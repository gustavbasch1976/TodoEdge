using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace TodoEdge
{
    /// <summary>Wires everything together: edge icon, panel, tray icon, hotkeys and the Claude round trip.</summary>
    public sealed class AppController : IDisposable
    {
        private const uint VK_T = 0x54;
        private const uint VK_Y = 0x59;
        private const uint VK_D = 0x44;

        public TaskStore Store { get; } = new TaskStore();
        public AppSettings Settings { get; private set; } = new AppSettings();

        private EdgeWindow _edge = null!;
        private TodoPanel _panel = null!;
        private WF.NotifyIcon? _tray;
        private HotkeyManager? _hotkeys;
        private DispatcherTimer? _refreshTimer;
        private DispatcherTimer? _watchdog;
        private bool _busy;

        public bool IsMouseOverEdge => _edge != null && _edge.IsMouseOver;
        public bool IsBusy => _busy;

        public void Start()
        {
            Paths.Ensure();
            Settings = AppSettings.Load();
            TagPalette.Tags = Settings.Tags;
            Store.Load();

            _edge = new EdgeWindow(this);
            _edge.Show();
            _edge.RestorePosition();

            _panel = new TodoPanel(this);

            Store.Changed += UpdateBadge;
            Store.Open.CollectionChanged += (s, e) => UpdateBadge();
            UpdateBadge();

            CreateTray();
            RegisterHotkeys();

            // Keep "Due today" and overdue colours correct as the day moves on.
            _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(5) };
            _refreshTimer.Tick += (s, e) => Store.RefreshAll();
            _refreshTimer.Start();

            // Keep the edge icon on screen through monitor changes, docking and sleep.
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
            Microsoft.Win32.SystemEvents.PowerModeChanged += OnPowerModeChanged;
            _watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            _watchdog.Tick += (s, e) => _edge.EnsureVisible();
            _watchdog.Start();

            if (string.IsNullOrEmpty(GetApiKey()))
                Balloon("TodoEdge is running", "Hover over the blue bar at the top of your screen. Add your Claude API key in Settings to read screenshots.");
        }

        private void RegisterHotkeys()
        {
            _hotkeys = new HotkeyManager(_edge);
            var failed = new List<string>();
            if (!_hotkeys.Register(Native.MOD_CONTROL | Native.MOD_SHIFT, VK_T, () => { _ = CaptureAsync(); }))
                failed.Add("Ctrl+Shift+T");
            if (!_hotkeys.Register(Native.MOD_CONTROL | Native.MOD_SHIFT, VK_Y, () => { _ = FromClipboardAsync(); }))
                failed.Add("Ctrl+Shift+Y");
            if (!_hotkeys.Register(Native.MOD_CONTROL | Native.MOD_SHIFT, VK_D, () => StartDictation()))
                failed.Add("Ctrl+Shift+D");
            if (failed.Count > 0)
                Balloon("Shortcut not available", string.Join(" and ", failed) + " is used by another app. Use the tray or edge icon menu instead.");
        }

        private void CreateTray()
        {
            SD.Icon icon;
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("icon.ico"))
                icon = stream != null ? new SD.Icon(stream) : SD.SystemIcons.Application;

            var menu = new WF.ContextMenuStrip();
            menu.Items.Add("Open to-do list", null, (s, e) => ShowPanel());
            menu.Items.Add("Add from screenshot (Ctrl+Shift+T)", null, (s, e) => { _ = CaptureAsync(); });
            menu.Items.Add("Add from clipboard (Ctrl+Shift+Y)", null, (s, e) => { _ = FromClipboardAsync(); });
            menu.Items.Add("Dictate to-dos (Ctrl+Shift+D)", null, (s, e) => StartDictation());
            menu.Items.Add("Move icon back to top of screen", null, (s, e) => ResetIconPosition());
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add("Settings", null, (s, e) => OpenSettings());
            menu.Items.Add("Open data folder", null, (s, e) => OpenFolder(Paths.Root));
            menu.Items.Add(new WF.ToolStripSeparator());
            menu.Items.Add("Quit", null, (s, e) => Quit());

            _tray = new WF.NotifyIcon
            {
                Icon = icon,
                Text = "TodoEdge",
                Visible = true,
                ContextMenuStrip = menu
            };
            _tray.MouseClick += (s, e) => { if (e.Button == WF.MouseButtons.Left) TogglePanel(); };
        }

        // ---------- panel ----------

        public void TogglePanel()
        {
            if (_panel.IsVisible && _panel.OpenedByHover) _panel.Pin(); // click after hover: keep it open
            else if (_panel.IsVisible) HidePanel();
            else ShowPanel();
        }

        public void ShowPanel(bool focusAdd = false, bool hover = false) =>
            _panel.ShowNear(_edge.Handle, _edge.Edge, focusAdd, hover);

        public void ShowPanelForDrop()
        {
            if (!_panel.IsVisible) ShowPanel(hover: true);
            _panel.ShowDropHint();
        }

        public IntPtr EdgeHandle => _edge.Handle;

        public void StartDictation()
        {
            ShowPanel(focusAdd: true);
            _panel.StartDictation();
        }

        public async Task FromSpokenOrTypedTextAsync(string text, bool dictated)
        {
            if (_busy || !EnsureKey()) return;
            await ProcessAsync(new CaptureInput { Kind = dictated ? "dictation" : "text", Text = text });
        }

        public void HidePanel()
        {
            if (_panel.IsVisible) _panel.Hide();
        }

        public void OnPanelVisibilityChanged(bool visible) => _edge.SetPanelOpen(visible);

        private void UpdateBadge()
        {
            int open = Store.Open.Count;
            bool overdue = Store.Open.Any(i => i.IsOverdue);
            _edge.UpdateBadge(open, overdue);
            if (_tray != null) _tray.Text = open == 0 ? "TodoEdge: all done" : $"TodoEdge: {open} to do";
        }

        private void OnDisplayChanged(object? sender, EventArgs e) =>
            _edge.Dispatcher.BeginInvoke(new Action(async () =>
            {
                await Task.Delay(1500); // let Windows finish rearranging the screens
                _edge.EnsureVisible(forceSnap: true);
            }));

        private void OnPowerModeChanged(object? sender, Microsoft.Win32.PowerModeChangedEventArgs e)
        {
            if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
            _edge.Dispatcher.BeginInvoke(new Action(async () =>
            {
                await Task.Delay(3000);
                _edge.EnsureVisible(forceSnap: true);
            }));
        }

        private void ResetIconPosition()
        {
            Settings.PosX = null;
            Settings.PosY = null;
            _edge.EnsureVisible();
            _edge.RestorePosition();
        }

        // ---------- capture sources ----------

        public async Task CaptureAsync()
        {
            if (_busy || !EnsureKey()) return;
            HidePanel();
            SD.Rectangle? area = await CaptureOverlay.SelectAsync();
            if (area == null) return;
            await Task.Delay(180); // let the overlay disappear from the screen
            byte[] png = ScreenCapture.Grab(area.Value);
            await ProcessAsync(new CaptureInput { Kind = "screenshot", Png = png });
        }

        public async Task FromClipboardAsync()
        {
            if (_busy || !EnsureKey()) return;
            CaptureInput? input = null;
            try
            {
                if (WF.Clipboard.ContainsImage())
                {
                    using var img = WF.Clipboard.GetImage();
                    if (img != null) input = new CaptureInput { Kind = "image", Png = ImageUtil.ToPng(img) };
                }
                if (input == null && WF.Clipboard.ContainsFileDropList())
                {
                    foreach (string? f in WF.Clipboard.GetFileDropList())
                    {
                        if (f == null) continue;
                        input = InputReader.FromFile(f);
                        if (input != null) break;
                    }
                }
                if (input == null && WF.Clipboard.ContainsText())
                {
                    string text = WF.Clipboard.GetText();
                    if (!string.IsNullOrWhiteSpace(text)) input = new CaptureInput { Kind = "text", Text = text };
                }
            }
            catch (Exception ex)
            {
                ShowError("Could not read the clipboard: " + ex.Message);
                return;
            }

            if (input == null)
            {
                Balloon("Clipboard is empty", "Copy some text, an image or a file first, then press Ctrl+Shift+Y.");
                return;
            }
            await ProcessAsync(input);
        }

        /// <summary>Handles anything dropped on the icon or the note. Reads the data straight away, while the drag is live.</summary>
        public async Task FromDataAsync(IDataObject data)
        {
            if (_busy) return;
            CaptureInput? input;
            try
            {
                input = await InputReader.FromDataAsync(data, message => SetBusy(true, message));
            }
            catch (Exception ex)
            {
                SetBusy(false, "");
                ShowError(ex.Message + "\n\nIf this keeps happening with an email, open it and press Ctrl+Shift+T to read it as a screenshot.");
                return;
            }
            SetBusy(false, "");

            if (input == null)
            {
                Balloon("Can't read that", "Drop " + InputReader.SupportedList + ".");
                return;
            }
            if (!EnsureKey()) return;
            await ProcessAsync(input);
        }

        // ---------- the Claude round trip ----------

        private async Task ProcessAsync(CaptureInput input)
        {
            if (_busy) return;
            string? key = GetApiKey();
            if (string.IsNullOrEmpty(key)) return;

            SetBusy(true, input.BusyMessage);
            List<ProposedTask> tasks;
            try
            {
                if (input.Png != null) input.Png = ImageUtil.PrepareForApi(input.Png);
                tasks = await ClaudeService.ExtractAsync(key, Settings.Model, input, Settings.MyName);
            }
            catch (Exception ex)
            {
                SetBusy(false, "");
                ShowError(ex.Message);
                return;
            }
            SetBusy(false, "");

            string? text = input.Text;
            string? shortText = text != null && text.Length > 4000 ? text.Substring(0, 4000) : text;

            if (tasks.Count == 0 && input.Kind == "dictation" && text != null)
            {
                Store.Add(new TodoItem { Title = text.Length > 200 ? text.Substring(0, 200) : text });
                ShowPanel();
                return;
            }
            if (tasks.Count == 0)
            {
                Balloon("No to-do items found", "Claude did not find anything for you to do in that. You can still add an item by hand.");
                return;
            }

            // A single item from your own words goes straight in; anything else gets a quick review.
            if (input.Kind == "dictation" && tasks.Count == 1)
            {
                var t = tasks[0];
                Store.Add(new TodoItem { Title = t.Title, Due = t.Due, Person = t.Person, Notes = t.Notes, SourceText = shortText });
                ShowPanel();
                return;
            }

            var review = new ReviewWindow(tasks, input.Png, Settings.Tags, input.SourceName);
            if (review.ShowDialog() != true || review.Selected.Count == 0) return;

            // Keep the original (screenshot, email, PDF...) so "Show source" can open it.
            string? sourceFile = null;
            byte[]? keep = input.OriginalBytes ?? input.Png;
            if (keep != null)
            {
                string ext = input.OriginalBytes != null ? (input.OriginalExtension ?? ".bin") : ".png";
                string stem = input.SourceName != null ? SafeName(Path.GetFileNameWithoutExtension(input.SourceName)) : "capture";
                sourceFile = DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + stem + ext;
                File.WriteAllBytes(Path.Combine(Paths.Sources, sourceFile), keep);
            }

            foreach (var r in review.Selected)
            {
                Store.Open.Add(new TodoItem
                {
                    Title = r.Title.Trim(),
                    Due = r.ParseDue(),
                    Person = r.Person,
                    Notes = r.Notes,
                    Tag = r.Tag == ReviewItem.NoTag ? null : r.Tag,
                    SourceImage = sourceFile,
                    SourceText = sourceFile == null ? shortText : null
                });
            }
            Store.Save();
            ShowPanel();
        }

        private static string SafeName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Length > 50 ? name.Substring(0, 50) : name;
        }

        private void SetBusy(bool busy, string message)
        {
            _busy = busy;
            _edge.SetBusy(busy);
            _panel.SetBusy(busy, message);
        }

        public string? GetApiKey()
        {
            string? key = Settings.ApiKey;
            if (string.IsNullOrWhiteSpace(key)) key = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");
            return string.IsNullOrWhiteSpace(key) ? null : key;
        }

        private bool EnsureKey()
        {
            if (GetApiKey() != null) return true;
            var answer = MessageBox.Show("TodoEdge needs a Claude API key to read screenshots and text. Open Settings now?",
                "TodoEdge", MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (answer == MessageBoxResult.Yes) OpenSettings();
            return GetApiKey() != null;
        }

        // ---------- misc ----------

        public void OpenSettings()
        {
            HidePanel();
            var window = new SettingsWindow(Settings);
            if (window.ShowDialog() == true)
            {
                Settings.Save();
                TagPalette.Tags = Settings.Tags;
                foreach (var i in Store.Open) i.Tag = i.Tag; // refresh tag colours
            }
        }

        public void ShowSource(TodoItem item)
        {
            if (!string.IsNullOrEmpty(item.SourceImage))
            {
                string path = Path.Combine(Paths.Sources, item.SourceImage);
                if (File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    return;
                }
            }
            if (!string.IsNullOrEmpty(item.SourceText))
            {
                string shown = item.SourceText.Length > 2500 ? item.SourceText.Substring(0, 2500) + "\n..." : item.SourceText;
                MessageBox.Show(shown, "Source of: " + item.Title, MessageBoxButton.OK, MessageBoxImage.None);
                return;
            }
            MessageBox.Show("The source for this item is no longer available.", "TodoEdge");
        }

        private static void OpenFolder(string path)
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
        }

        private void Balloon(string title, string text) =>
            _tray?.ShowBalloonTip(5000, title, text, WF.ToolTipIcon.Info);

        private static void ShowError(string message) =>
            MessageBox.Show(message, "TodoEdge", MessageBoxButton.OK, MessageBoxImage.Warning);

        public void Quit()
        {
            Dispose();
            Application.Current.Shutdown();
        }

        public void Dispose()
        {
            _refreshTimer?.Stop();
            _watchdog?.Stop();
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            Microsoft.Win32.SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _hotkeys?.Dispose();
            _hotkeys = null;
            if (_tray != null)
            {
                _tray.Visible = false;
                _tray.Dispose();
                _tray = null;
            }
        }
    }
}
