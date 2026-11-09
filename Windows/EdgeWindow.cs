using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace TodoEdge
{
    /// <summary>
    /// The small icon docked to a screen edge. Collapsed it is a thin bar; on hover it grows into a round button.
    /// Drag it to move it along any edge of any monitor. Click to open the list. Right-click for options.
    /// </summary>
    public sealed class EdgeWindow : Window
    {
        private readonly AppController _c;
        private readonly Grid _root = new Grid();
        private readonly Border _hit = new Border();
        private readonly Border _bar = new Border();
        private readonly Border _bubble = new Border();
        private readonly TextBlock _glyph = new TextBlock();
        private readonly Border _badge = new Border();
        private readonly TextBlock _badgeText = new TextBlock();
        private readonly RotateTransform _spin = new RotateTransform();
        private readonly DispatcherTimer _collapseTimer;
        private readonly DispatcherTimer _hoverOpenTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };

        private static readonly Color AccentColour = Color.FromRgb(0x25, 0x63, 0xEB);
        private static readonly Color AlertColour = Color.FromRgb(0xDC, 0x26, 0x26);

        private bool _pressed, _dragged, _panelOpen, _busy, _expanded;
        private Point _downScreen;

        public string Edge { get; private set; } = "Top";
        public IntPtr Handle => new WindowInteropHelper(this).Handle;

        public EdgeWindow(AppController controller)
        {
            _c = controller;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            Width = 64;
            Height = 64;
            Title = "TodoEdge";
            ShowActivated = false;

            // Near-invisible strip along the edge that catches the hover (fully transparent pixels let clicks pass through).
            _hit.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));

            _bar.CornerRadius = new CornerRadius(3);
            _bar.Background = new SolidColorBrush(AccentColour);
            _bar.Opacity = 0.85;

            _glyph.Text = ""; // check mark
            _glyph.FontFamily = (FontFamily)Application.Current.Resources["IconFont"];
            _glyph.FontSize = 19;
            _glyph.Foreground = Brushes.White;
            _glyph.HorizontalAlignment = HorizontalAlignment.Center;
            _glyph.VerticalAlignment = VerticalAlignment.Center;
            _glyph.RenderTransformOrigin = new Point(0.5, 0.5);
            _glyph.RenderTransform = _spin;

            _badgeText.FontSize = 10.5;
            _badgeText.FontWeight = FontWeights.Bold;
            _badgeText.Foreground = Brushes.White;
            _badgeText.HorizontalAlignment = HorizontalAlignment.Center;
            _badgeText.VerticalAlignment = VerticalAlignment.Center;
            _badge.Child = _badgeText;
            _badge.MinWidth = 18;
            _badge.Height = 18;
            _badge.Padding = new Thickness(4, 0, 4, 0);
            _badge.CornerRadius = new CornerRadius(9);
            _badge.Background = new SolidColorBrush(AlertColour);
            _badge.BorderBrush = Brushes.White;
            _badge.BorderThickness = new Thickness(1.5);
            _badge.HorizontalAlignment = HorizontalAlignment.Right;
            _badge.VerticalAlignment = VerticalAlignment.Top;
            _badge.Margin = new Thickness(0, -5, -5, 0);
            _badge.Visibility = Visibility.Collapsed;

            var bubbleContent = new Grid();
            bubbleContent.Children.Add(_glyph);
            bubbleContent.Children.Add(_badge);

            _bubble.Width = 44;
            _bubble.Height = 44;
            _bubble.CornerRadius = new CornerRadius(22);
            _bubble.Background = new LinearGradientBrush(Color.FromRgb(0x3B, 0x82, 0xF6), AccentColour, 90);
            _bubble.Child = bubbleContent;
            _bubble.Cursor = Cursors.Hand;
            _bubble.Effect = new DropShadowEffect { BlurRadius = 10, ShadowDepth = 1.5, Opacity = 0.35 };
            _bubble.Visibility = Visibility.Hidden;
            _bubble.ToolTip = "Click: to-do list   Drag: move   Right-click: options";
            ToolTipService.SetInitialShowDelay(_bubble, 900);

            _root.Children.Add(_hit);
            _root.Children.Add(_bar);
            _root.Children.Add(_bubble);
            Content = _root;

            _root.AllowDrop = true;
            _root.MouseEnter += (s, e) =>
            {
                Expand();
                if (_c.Settings.OpenOnHover) { _hoverOpenTimer.Stop(); _hoverOpenTimer.Start(); }
            };
            _root.MouseLeave += (s, e) => { _hoverOpenTimer.Stop(); _collapseTimer.Start(); };
            _hoverOpenTimer.Tick += (s, e) =>
            {
                _hoverOpenTimer.Stop();
                if (_root.IsMouseOver && !_pressed && !_busy && Mouse.LeftButton == MouseButtonState.Released)
                    _c.ShowPanel(hover: true);
            };
            _root.DragEnter += (s, e) => Expand();
            _root.DragLeave += (s, e) => _collapseTimer.Start();
            // Always ask for Copy, so Outlook never treats a drop as a move.
            _root.DragOver += (s, e) => { e.Effects = DragDropEffects.Copy; e.Handled = true; };
            _root.Drop += (s, e) => { e.Effects = DragDropEffects.Copy; _ = _c.FromDataAsync(e.Data); };

            _bubble.MouseLeftButtonDown += OnDown;
            _bubble.MouseMove += OnMove;
            _bubble.MouseLeftButtonUp += OnUp;
            _bubble.ContextMenu = BuildMenu();

            _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
            _collapseTimer.Tick += (s, e) =>
            {
                _collapseTimer.Stop();
                if (!_root.IsMouseOver && !_panelOpen && !_busy && !(_bubble.ContextMenu?.IsOpen ?? false)) Collapse();
            };

            SourceInitialized += (s, e) => Native.MakeToolWindow(Handle);
            ApplyEdge("Top");
        }

        private ContextMenu BuildMenu()
        {
            var menu = new ContextMenu();
            menu.Items.Add(MakeItem("Open to-do list", () => _c.ShowPanel()));
            menu.Items.Add(MakeItem("Add from screenshot", () => _ = _c.CaptureAsync(), "Ctrl+Shift+T"));
            menu.Items.Add(MakeItem("Add from clipboard", () => _ = _c.FromClipboardAsync(), "Ctrl+Shift+Y"));
            menu.Items.Add(MakeItem("Dictate to-dos", () => _c.StartDictation(), "Ctrl+Shift+D"));
            menu.Items.Add(MakeItem("Add item", () => _c.ShowPanel(focusAdd: true)));
            menu.Items.Add(new Separator());
            menu.Items.Add(MakeItem("Settings", () => _c.OpenSettings()));
            menu.Items.Add(MakeItem("Quit TodoEdge", () => _c.Quit()));
            menu.Closed += (s, e) => _collapseTimer.Start();
            return menu;
        }

        private static MenuItem MakeItem(string header, Action action, string? gesture = null)
        {
            var item = new MenuItem { Header = header };
            if (gesture != null) item.InputGestureText = gesture;
            item.Click += (s, e) => action();
            return item;
        }

        // ---------- show / hide ----------

        private void Expand()
        {
            _collapseTimer.Stop();
            if (_expanded) return;
            _expanded = true;
            _bar.Visibility = Visibility.Hidden;
            _bubble.Visibility = Visibility.Visible;
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140));
            var grow = new DoubleAnimation(0.6, 1, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut }
            };
            var scale = new ScaleTransform(1, 1);
            _bubble.RenderTransformOrigin = new Point(0.5, 0.5);
            _bubble.RenderTransform = scale;
            _bubble.BeginAnimation(OpacityProperty, fade);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        }

        private void Collapse()
        {
            if (!_expanded) return;
            _expanded = false;
            _bubble.Visibility = Visibility.Hidden;
            _bar.Visibility = Visibility.Visible;
        }

        public void SetPanelOpen(bool open)
        {
            _panelOpen = open;
            if (open) Expand();
            else _collapseTimer.Start();
        }

        public void SetBusy(bool busy)
        {
            _busy = busy;
            if (busy)
            {
                Expand();
                _glyph.Text = ""; // sync arrows
                _spin.BeginAnimation(RotateTransform.AngleProperty,
                    new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
            }
            else
            {
                _spin.BeginAnimation(RotateTransform.AngleProperty, null);
                _spin.Angle = 0;
                _glyph.Text = "";
                _collapseTimer.Start();
            }
        }

        public void UpdateBadge(int openCount, bool anyOverdue)
        {
            _badge.Visibility = openCount > 0 ? Visibility.Visible : Visibility.Collapsed;
            _badgeText.Text = openCount > 99 ? "99+" : openCount.ToString();
            _badge.Background = new SolidColorBrush(anyOverdue ? AlertColour : Color.FromRgb(0x11, 0x18, 0x27));
            _bar.Background = new SolidColorBrush(anyOverdue ? AlertColour : AccentColour);
        }

        // ---------- click vs drag ----------

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            _hoverOpenTimer.Stop();
            _pressed = true;
            _dragged = false;
            _downScreen = PointToScreen(e.GetPosition(this));
            _bubble.CaptureMouse();
            e.Handled = true;
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            if (!_pressed || e.LeftButton != MouseButtonState.Pressed) return;
            Point p = PointToScreen(e.GetPosition(this));
            if (Math.Abs(p.X - _downScreen.X) + Math.Abs(p.Y - _downScreen.Y) < 8) return;

            _pressed = false;
            _dragged = true;
            _bubble.ReleaseMouseCapture();
            _c.HidePanel();
            try { DragMove(); } catch (InvalidOperationException) { }
            Snap();
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (!_pressed) return;
            _pressed = false;
            _bubble.ReleaseMouseCapture();
            if (!_dragged) _c.TogglePanel();
            e.Handled = true;
        }

        // ---------- docking ----------

        public void RestorePosition()
        {
            var s = _c.Settings;
            if (s.PosX.HasValue && s.PosY.HasValue)
            {
                PlaceCentreAt(s.PosX.Value, s.PosY.Value);
            }
            else
            {
                var wa = WF.Screen.PrimaryScreen!.WorkingArea;
                PlaceCentreAt(wa.Left + wa.Width / 2, wa.Top);
            }
            Snap();
        }

        private void PlaceCentreAt(int cx, int cy)
        {
            Native.GetWindowRect(Handle, out var r);
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, cx - r.Width / 2, cy - r.Height / 2, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }

        /// <summary>Moves the window flush to the nearest edge of the monitor it is on.</summary>
        public void Snap()
        {
            Native.GetWindowRect(Handle, out var r);
            int w = r.Width, h = r.Height;
            int cx = r.Left + w / 2, cy = r.Top + h / 2;
            var wa = WF.Screen.FromPoint(new SD.Point(cx, cy)).WorkingArea;

            int dTop = Math.Abs(cy - wa.Top), dBottom = Math.Abs(wa.Bottom - cy);
            int dLeft = Math.Abs(cx - wa.Left), dRight = Math.Abs(wa.Right - cx);
            int min = Math.Min(Math.Min(dTop, dBottom), Math.Min(dLeft, dRight));

            string edge;
            int x, y;
            if (min == dTop) { edge = "Top"; y = wa.Top; x = Clamp(cx - w / 2, wa.Left, wa.Right - w); }
            else if (min == dBottom) { edge = "Bottom"; y = wa.Bottom - h; x = Clamp(cx - w / 2, wa.Left, wa.Right - w); }
            else if (min == dLeft) { edge = "Left"; x = wa.Left; y = Clamp(cy - h / 2, wa.Top, wa.Bottom - h); }
            else { edge = "Right"; x = wa.Right - w; y = Clamp(cy - h / 2, wa.Top, wa.Bottom - h); }

            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            ApplyEdge(edge);

            _c.Settings.Edge = edge;
            _c.Settings.PosX = x + w / 2;
            _c.Settings.PosY = y + h / 2;
            _c.Settings.Save();
        }

        private static int Clamp(int v, int lo, int hi) => hi < lo ? lo : Math.Max(lo, Math.Min(hi, v));

        private void ApplyEdge(string edge)
        {
            Edge = edge;
            bool horizontal = edge == "Top" || edge == "Bottom";

            _hit.Width = horizontal ? 52 : 14;
            _hit.Height = horizontal ? 14 : 52;
            _bar.Width = horizontal ? 40 : 6;
            _bar.Height = horizontal ? 6 : 40;

            var ha = edge == "Left" ? HorizontalAlignment.Left : edge == "Right" ? HorizontalAlignment.Right : HorizontalAlignment.Center;
            var va = edge == "Top" ? VerticalAlignment.Top : edge == "Bottom" ? VerticalAlignment.Bottom : VerticalAlignment.Center;

            foreach (var el in new FrameworkElement[] { _hit, _bar, _bubble })
            {
                el.HorizontalAlignment = ha;
                el.VerticalAlignment = va;
            }

            _hit.Margin = new Thickness(0);
            _bar.Margin = edge switch
            {
                "Top" => new Thickness(0, 2, 0, 0),
                "Bottom" => new Thickness(0, 0, 0, 2),
                "Left" => new Thickness(2, 0, 0, 0),
                _ => new Thickness(0, 0, 2, 0)
            };
            _bubble.Margin = edge switch
            {
                "Top" => new Thickness(0, 6, 0, 0),
                "Bottom" => new Thickness(0, 0, 0, 6),
                "Left" => new Thickness(6, 0, 0, 0),
                _ => new Thickness(0, 0, 6, 0)
            };
        }
    }
}
