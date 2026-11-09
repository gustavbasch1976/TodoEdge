using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace TodoEdge
{
    /// <summary>Dims every screen and lets you drag a box around the area to read. A single click takes the whole monitor.</summary>
    public sealed class CaptureOverlay : Window
    {
        private readonly Canvas _canvas = new Canvas();
        private readonly Path _dim = new Path();
        private readonly Rectangle _frame = new Rectangle();
        private readonly Border _hint = new Border();
        private Point _start;
        private bool _dragging;
        private SD.Rectangle? _result;
        private bool _closing;

        private CaptureOverlay()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)); // alpha 1 so the whole area takes clicks
            Topmost = true;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            Cursor = Cursors.Cross;
            Title = "TodoEdge capture";

            _dim.Fill = new SolidColorBrush(Color.FromArgb(0x70, 0, 0, 0));
            _dim.IsHitTestVisible = false;
            _frame.Stroke = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6));
            _frame.StrokeThickness = 2;
            _frame.Visibility = Visibility.Collapsed;
            _frame.IsHitTestVisible = false;

            _hint.Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x11, 0x18, 0x27));
            _hint.CornerRadius = new CornerRadius(6);
            _hint.Padding = new Thickness(10, 6, 10, 6);
            _hint.IsHitTestVisible = false;
            _hint.Child = new TextBlock
            {
                Text = "Drag around the message.  Click = whole screen.  Esc = cancel.",
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12.5
            };

            _canvas.Children.Add(_dim);
            _canvas.Children.Add(_frame);
            _canvas.Children.Add(_hint);
            Content = _canvas;
            UpdateDim(Rect.Empty);

            SourceInitialized += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                Native.MakeToolWindow(hwnd);
                var vs = WF.SystemInformation.VirtualScreen; // physical pixels, all monitors
                Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, vs.Left, vs.Top, vs.Width, vs.Height, Native.SWP_SHOWWINDOW);
            };
            Loaded += (s, e) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                Native.SetForegroundWindow(hwnd);
                Activate();
                Focus();
                MoveHint(Mouse.GetPosition(this));
            };

            KeyDown += (s, e) => { if (e.Key == Key.Escape) Finish(null); };
            MouseRightButtonDown += (s, e) => Finish(null);
            MouseLeftButtonDown += OnDown;
            MouseMove += OnMove;
            MouseLeftButtonUp += OnUp;
            Deactivated += (s, e) => { if (!_dragging && IsLoaded) Finish(null); };
        }

        public static Task<SD.Rectangle?> SelectAsync()
        {
            var tcs = new TaskCompletionSource<SD.Rectangle?>();
            var overlay = new CaptureOverlay();
            overlay.Closed += (s, e) => tcs.TrySetResult(overlay._result);
            overlay.Show();
            return tcs.Task;
        }

        private void OnDown(object sender, MouseButtonEventArgs e)
        {
            _start = e.GetPosition(this);
            _dragging = true;
            _hint.Visibility = Visibility.Collapsed;
            CaptureMouse();
        }

        private void OnMove(object sender, MouseEventArgs e)
        {
            var p = e.GetPosition(this);
            if (!_dragging)
            {
                MoveHint(p);
                return;
            }
            var r = new Rect(_start, p);
            Canvas.SetLeft(_frame, r.X);
            Canvas.SetTop(_frame, r.Y);
            _frame.Width = r.Width;
            _frame.Height = r.Height;
            _frame.Visibility = Visibility.Visible;
            UpdateDim(r);
        }

        private void OnUp(object sender, MouseButtonEventArgs e)
        {
            if (!_dragging) return;
            _dragging = false;
            ReleaseMouseCapture();

            var end = e.GetPosition(this);
            // PointToScreen returns physical pixels, which is what screen capture needs.
            Point a = PointToScreen(_start);
            Point b = PointToScreen(end);
            int x1 = (int)Math.Round(Math.Min(a.X, b.X)), y1 = (int)Math.Round(Math.Min(a.Y, b.Y));
            int x2 = (int)Math.Round(Math.Max(a.X, b.X)), y2 = (int)Math.Round(Math.Max(a.Y, b.Y));

            if (x2 - x1 < 10 || y2 - y1 < 10)
            {
                // Treated as a click: capture the monitor under the cursor.
                var screen = WF.Screen.FromPoint(new SD.Point((int)b.X, (int)b.Y));
                Finish(screen.Bounds);
            }
            else
            {
                Finish(new SD.Rectangle(x1, y1, x2 - x1, y2 - y1));
            }
        }

        private void MoveHint(Point p)
        {
            Canvas.SetLeft(_hint, p.X + 18);
            Canvas.SetTop(_hint, p.Y + 18);
        }

        private void UpdateDim(Rect hole)
        {
            var all = new RectangleGeometry(new Rect(-20000, -20000, 60000, 60000));
            _dim.Data = hole.IsEmpty
                ? all
                : new CombinedGeometry(GeometryCombineMode.Exclude, all, new RectangleGeometry(hole));
        }

        private void Finish(SD.Rectangle? result)
        {
            if (_closing) return;
            _closing = true;
            _result = result;
            Close();
        }
    }
}
