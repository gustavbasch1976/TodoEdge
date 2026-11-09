using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using WF = System.Windows.Forms;

namespace TodoEdge
{
    public partial class TodoPanel : Window
    {
        private readonly AppController _c;
        private readonly DispatcherTimer _undoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        private TodoItem? _undoItem;
        private int _undoIndex = -1;
        private readonly DispatcherTimer _leaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        private readonly DispatcherTimer _dictationTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(90) };
        private DateTime? _outsideSince;
        private bool _dictating;
        private IntPtr _anchor;

        /// <summary>True while the list was opened by hovering and you have not clicked into it yet.</summary>
        public bool OpenedByHover { get; private set; }
        private string _anchorEdge = "Top";

        public TodoPanel(AppController controller)
        {
            InitializeComponent();
            _c = controller;
            OpenList.ItemsSource = _c.Store.Open;
            DoneList.ItemsSource = _c.Store.Done;
            _c.Store.Changed += Refresh;
            Refresh();

            _undoTimer.Tick += (s, e) => HideUndo();
            _leaveTimer.Tick += LeaveTimer_Tick;
            _dictationTimeout.Tick += (s, e) => StopDictation(closeVoiceTyping: false);
            Activated += (s, e) => OpenedByHover = false; // clicking into the list keeps it open

            SourceInitialized += (s, e) => Native.MakeToolWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            Deactivated += (s, e) =>
            {
                if (_c.IsMouseOverEdge) return; // the edge icon's own click toggles us
                if (_dictating) return;          // the voice typing bar can take focus briefly
                CommitAllEdits();
                Hide();
            };
            PreviewKeyDown += OnPreviewKeyDown;
            SizeChanged += (s, e) => { if (IsVisible) Reposition(); };
            IsVisibleChanged += (s, e) => _c.OnPanelVisibilityChanged(IsVisible);
        }

        // ---------- showing ----------

        public void ShowNear(IntPtr anchorHwnd, string edge, bool focusAdd, bool hover = false)
        {
            _anchor = anchorHwnd;
            _anchorEdge = edge;
            if (!IsVisible)
            {
                OpenedByHover = hover;
                ShowActivated = !hover; // hover never steals focus from the app you are typing in
                Opacity = 0;
                Show();
                UpdateLayout();
                Reposition();
                Opacity = 1;
            }
            else
            {
                Reposition();
            }

            if (hover)
            {
                _outsideSince = null;
                _leaveTimer.Start();
                return;
            }
            Pin(focusAdd);
        }

        /// <summary>Keeps the list open until you click elsewhere.</summary>
        public void Pin(bool focusAdd = false)
        {
            OpenedByHover = false;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            Native.SetForegroundWindow(hwnd);
            Activate();
            if (focusAdd)
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => { AddBox.Focus(); Keyboard.Focus(AddBox); }));
        }

        private void LeaveTimer_Tick(object? sender, EventArgs e)
        {
            if (!IsVisible || !OpenedByHover) { _leaveTimer.Stop(); return; }
            if (IsMouseOver || _c.IsMouseOverEdge || _c.IsBusy || _dictating)
            {
                _outsideSince = null;
                return;
            }
            _outsideSince ??= DateTime.Now;
            if ((DateTime.Now - _outsideSince.Value).TotalMilliseconds > 600)
            {
                _leaveTimer.Stop();
                Hide();
            }
        }

        private void Reposition()
        {
            if (_anchor == IntPtr.Zero) return;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            Native.GetWindowRect(_anchor, out var icon);
            Native.GetWindowRect(hwnd, out var me);
            int w = me.Width, h = me.Height;
            int cx = icon.Left + icon.Width / 2, cy = icon.Top + icon.Height / 2;
            var wa = WF.Screen.FromPoint(new System.Drawing.Point(cx, cy)).WorkingArea;

            int x, y;
            switch (_anchorEdge)
            {
                case "Bottom": x = cx - w / 2; y = icon.Top - h + 8; break;
                case "Left": x = icon.Right - 8; y = cy - h / 2; break;
                case "Right": x = icon.Left - w + 8; y = cy - h / 2; break;
                default: x = cx - w / 2; y = icon.Bottom - 8; break;
            }
            x = Math.Max(wa.Left, Math.Min(x, wa.Right - w));
            y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - h));
            Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE);
        }

        // ---------- state ----------

        private void Refresh()
        {
            int open = _c.Store.Open.Count;
            CountText.Text = open == 0 ? "" : open.ToString();
            EmptyText.Visibility = open == 0 ? Visibility.Visible : Visibility.Collapsed;
            int done = _c.Store.Done.Count;
            DoneToggle.Visibility = done > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (done == 0) DoneScroll.Visibility = Visibility.Collapsed;
            DoneToggleText.Text = (DoneScroll.Visibility == Visibility.Visible ? "Hide completed" : "Show completed") + $" ({done})";
        }

        public void SetBusy(bool busy, string text)
        {
            BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            BusyText.Text = text;
        }

        // ---------- keyboard ----------

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            bool inTextBox = Keyboard.FocusedElement is TextBox;
            if (e.Key == Key.Escape && !inTextBox)
            {
                Hide();
                e.Handled = true;
            }
            else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && !inTextBox)
            {
                e.Handled = true;
                _ = _c.FromClipboardAsync();
            }
        }

        // ---------- adding ----------

        private void AddBox_TextChanged(object sender, TextChangedEventArgs e) =>
            AddPlaceholder.Visibility = AddBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        private void AddBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                bool useClaude = _dictating || Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
                if (useClaude) SendToClaude();
                else AddFromBox();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (_dictating) StopDictation(closeVoiceTyping: true);
                AddBox.Clear();
                Hide();
                e.Handled = true;
            }
        }

        private void AddButton_Click(object sender, RoutedEventArgs e)
        {
            if (AddBox.Text.Trim().Length == 0) { AddBox.Focus(); return; }
            if (_dictating) SendToClaude();
            else AddFromBox();
        }

        private void SendToClaude()
        {
            string text = AddBox.Text.Trim();
            StopDictation(closeVoiceTyping: false);
            if (text.Length == 0) return;
            AddBox.Clear();
            if (_c.GetApiKey() == null)
            {
                _c.Store.Add(new TodoItem { Title = text }); // no key: add as typed
                return;
            }
            _ = _c.FromSpokenOrTypedTextAsync(text, dictated: true); // typed or spoken, it is your own note
        }

        // ---------- dictation ----------

        private void Mic_Click(object sender, RoutedEventArgs e)
        {
            if (_dictating) StopDictation(closeVoiceTyping: true);
            else StartDictation();
        }

        public void StartDictation()
        {
            if (_dictating) return;
            Pin();
            _dictating = true;
            MicButton.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26));
            AddBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26));
            AddPlaceholder.Text = "Listening... speak, then press Enter";
            _dictationTimeout.Start();
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(async () =>
            {
                AddBox.Focus();
                Keyboard.Focus(AddBox);
                AddBox.CaretIndex = AddBox.Text.Length;
                await System.Threading.Tasks.Task.Delay(200);
                Native.ToggleVoiceTyping();
            }));
        }

        private void StopDictation(bool closeVoiceTyping)
        {
            if (!_dictating) return;
            _dictating = false;
            _dictationTimeout.Stop();
            MicButton.ClearValue(ForegroundProperty);
            AddBorder.BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD1, 0xD5, 0xDB));
            AddPlaceholder.Text = "Add a to-do, or Ctrl+Enter to let Claude sort it";
            if (closeVoiceTyping && AddBox.IsKeyboardFocused) Native.ToggleVoiceTyping();
        }

        private void AddFromBox()
        {
            string text = AddBox.Text.Trim();
            if (text.Length == 0) return;
            _c.Store.Add(new TodoItem { Title = text });
            AddBox.Clear();
            ListScroll.ScrollToEnd();
        }

        private void AddItemMenu_Click(object sender, RoutedEventArgs e)
        {
            AddBox.Focus();
            Keyboard.Focus(AddBox);
        }

        private void Capture_Click(object sender, RoutedEventArgs e) => _ = _c.CaptureAsync();
        private void Clipboard_Click(object sender, RoutedEventArgs e) => _ = _c.FromClipboardAsync();
        private void Settings_Click(object sender, RoutedEventArgs e) => _c.OpenSettings();

        private void ClearCompleted_Click(object sender, RoutedEventArgs e)
        {
            if (_c.Store.Done.Count == 0) return;
            _c.Store.ClearDone();
        }

        // ---------- completing / undo ----------

        private void Item_Checked(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not TodoItem item) return;
            // Let the tick render briefly, then remove the item.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(async () =>
            {
                await System.Threading.Tasks.Task.Delay(250);
                if (!_c.Store.Open.Contains(item)) return;
                int index = _c.Store.Complete(item);
                ShowUndo(item, index);
            }));
        }

        private void ShowUndo(TodoItem item, int index)
        {
            _undoItem = item;
            _undoIndex = index;
            UndoText.Text = "Done: " + item.Title;
            UndoBar.Visibility = Visibility.Visible;
            _undoTimer.Stop();
            _undoTimer.Start();
        }

        private void HideUndo()
        {
            _undoTimer.Stop();
            UndoBar.Visibility = Visibility.Collapsed;
            _undoItem = null;
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            if (_undoItem != null && _c.Store.Done.Contains(_undoItem))
                _c.Store.Restore(_undoItem, _undoIndex);
            HideUndo();
        }

        private void DoneItem_Unchecked(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is TodoItem item)
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => _c.Store.Restore(item)));
        }

        private void DoneToggle_Click(object sender, RoutedEventArgs e)
        {
            DoneScroll.Visibility = DoneScroll.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            Refresh();
        }

        // ---------- editing ----------

        private void Row_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ClickCount == 2 && (sender as FrameworkElement)?.Tag is TodoItem item)
            {
                CommitAllEdits();
                item.IsEditing = true;
                e.Handled = true;
            }
        }

        private void TitleBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (sender is TextBox box && box.IsVisible)
            {
                Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
                {
                    box.Focus();
                    Keyboard.Focus(box);
                    box.SelectAll();
                }));
            }
        }

        private void TitleBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox box || box.Tag is not TodoItem item) return;
            if (e.Key == Key.Enter)
            {
                CommitEdit(box, item);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
                item.IsEditing = false;
                e.Handled = true;
            }
        }

        private void TitleBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is TextBox box && box.Tag is TodoItem item && item.IsEditing) CommitEdit(box, item);
        }

        private void CommitEdit(TextBox box, TodoItem item)
        {
            if (box.Text.Trim().Length == 0)
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateTarget();
            else
                box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            item.IsEditing = false;
            _c.Store.Save();
        }

        private void CommitAllEdits()
        {
            foreach (var i in _c.Store.Open.Where(i => i.IsEditing).ToList()) i.IsEditing = false;
        }

        // ---------- per-item menu ----------

        private void Row_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            if (sender is not FrameworkElement fe || fe.Tag is not TodoItem item || fe.ContextMenu == null) return;
            var menu = fe.ContextMenu;
            menu.Items.Clear();
            bool isOpen = _c.Store.Open.Contains(item);

            if (isOpen)
            {
                menu.Items.Add(Item("Edit", () => { CommitAllEdits(); item.IsEditing = true; }));

                var due = new MenuItem { Header = "Due date" };
                due.Items.Add(Item("Today", () => SetDue(item, DateTime.Today)));
                due.Items.Add(Item("Tomorrow", () => SetDue(item, DateTime.Today.AddDays(1))));
                due.Items.Add(Item("Next Monday", () => SetDue(item, NextMonday())));
                due.Items.Add(Item("In a week", () => SetDue(item, DateTime.Today.AddDays(7))));
                due.Items.Add(new Separator());
                due.Items.Add(Item("No due date", () => SetDue(item, null)));
                menu.Items.Add(due);

                var tag = new MenuItem { Header = "Tag" };
                foreach (string t in _c.Settings.Tags)
                {
                    string name = t;
                    var mi = Item(name, () => { item.Tag = name; _c.Store.Save(); });
                    mi.IsChecked = string.Equals(item.Tag, name, StringComparison.OrdinalIgnoreCase);
                    tag.Items.Add(mi);
                }
                tag.Items.Add(new Separator());
                tag.Items.Add(Item("No tag", () => { item.Tag = null; _c.Store.Save(); }));
                menu.Items.Add(tag);

                menu.Items.Add(Item("Move to top", () => _c.Store.MoveToTop(item)));
            }
            else
            {
                menu.Items.Add(Item("Restore", () => _c.Store.Restore(item)));
            }

            var source = Item("Show source", () => _c.ShowSource(item));
            source.IsEnabled = item.HasSource;
            menu.Items.Add(source);
            if (!string.IsNullOrWhiteSpace(item.Notes))
            {
                var notes = new MenuItem { Header = "Note: " + Shorten(item.Notes!, 70), IsEnabled = false };
                menu.Items.Add(notes);
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Delete", () => _c.Store.Delete(item)));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Add item", () => AddItemMenu_Click(this, new RoutedEventArgs())));
            menu.Items.Add(Item("Add from screenshot", () => _ = _c.CaptureAsync(), "Ctrl+Shift+T"));
            menu.Items.Add(Item("Add from clipboard", () => _ = _c.FromClipboardAsync(), "Ctrl+Shift+Y"));
        }

        private void SetDue(TodoItem item, DateTime? date)
        {
            item.Due = date;
            _c.Store.Save();
        }

        private static DateTime NextMonday()
        {
            int days = ((int)DayOfWeek.Monday - (int)DateTime.Today.DayOfWeek + 7) % 7;
            return DateTime.Today.AddDays(days == 0 ? 7 : days);
        }

        private static string Shorten(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";

        private static MenuItem Item(string header, Action action, string? gesture = null)
        {
            var mi = new MenuItem { Header = header };
            if (gesture != null) mi.InputGestureText = gesture;
            mi.Click += (s, e) => action();
            return mi;
        }
    }
}
