using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using static Kelvra.NativeMethods;
using Screen = System.Windows.Forms.Screen;

namespace Kelvra;

/// <summary>
/// Transparent, always-on-top, click-through window for one <see cref="OverlayProfile"/>. The content is drawn by
/// <see cref="OverlayView"/>; this class adds locking, dragging, resizing and pinning to a monitor's corner or edge.
/// </summary>
public sealed class OverlayWindow : Window
{
    private readonly OverlayProfile _p;
    private readonly OverlayView _view;
    private readonly Rectangle _dash = new() { StrokeDashArray = new DoubleCollection { 4, 3 }, StrokeThickness = 1.5, IsHitTestVisible = false };
    private IntPtr _hwnd;
    private bool _placing;
    private bool _resizing;

    public OverlayWindow(OverlayProfile profile, SensorStore store)
    {
        _p = profile;
        _view = new OverlayView(profile, store, preview: false);

        Title = $"Kelvra overlay – {profile.Name}";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        var host = new Grid();
        host.Children.Add(_view);
        host.Children.Add(_dash);
        Content = host;

        (Left, Top) = ClampToScreen(profile.X, profile.Y);

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            SetExStyle(_hwnd, WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, true);
            HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
            ApplyLock();
            PlaceAtAnchor();
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (_p.Locked) return;
            if (e.ClickCount == 2)
            {
                // Double-click: back to fit-to-content
                _p.Width = _p.Height = 0;
                Rebuild();
                return;
            }
            if (_p.Anchor != OverlayAnchor.Custom) _p.Anchor = OverlayAnchor.Custom; // dragging frees it from the corner
            DragMove();
        };
        LocationChanged += (_, _) =>
        {
            if (_p.Locked || _placing || _p.Anchor != OverlayAnchor.Custom) return;
            _p.X = Math.Round(Left);
            _p.Y = Math.Round(Top);
        };
        SizeChanged += (_, _) => PlaceAtAnchor(); // anchored to the right/bottom: grow towards the screen's middle
        SystemEvents.DisplaySettingsChanged += OnDisplaysChanged;
        _p.Changed += OnProfileChanged;
        Closed += (_, _) =>
        {
            _p.Changed -= OnProfileChanged;
            SystemEvents.DisplaySettingsChanged -= OnDisplaysChanged;
        };

        Rebuild();
    }

    private void OnDisplaysChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(PlaceAtAnchor);

    private void OnProfileChanged(object? sender, string property)
    {
        switch (property)
        {
            case nameof(OverlayProfile.X) or nameof(OverlayProfile.Y) or nameof(OverlayProfile.Enabled)
                or nameof(OverlayProfile.Width) or nameof(OverlayProfile.Height):
                return;
            case nameof(OverlayProfile.Anchor) or nameof(OverlayProfile.EdgeMargin) or nameof(OverlayProfile.Monitor):
                PlaceAtAnchor();
                return;
            case nameof(OverlayProfile.Locked):
                ApplyLock();
                Rebuild();
                return;
            default:
                Rebuild();
                return;
        }
    }

    private void ApplyLock()
    {
        if (_hwnd != IntPtr.Zero) SetExStyle(_hwnd, WS_EX_TRANSPARENT, _p.Locked);
        Cursor = _p.Locked ? null : Cursors.SizeAll;
        ResizeMode = _p.Locked ? ResizeMode.NoResize : ResizeMode.CanResize;
    }

    private const int WM_NCHITTEST = 0x0084, WM_SIZING = 0x0214, WM_EXITSIZEMOVE = 0x0232;

    /// <summary>Unlocked: the outer 8 px act as resize borders. After a resize the size is kept in the profile.</summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST when !_p.Locked:
                long lp = lParam.ToInt64();
                var pt = PointFromScreen(new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF)));
                const double edge = 8;
                bool l = pt.X < edge, r = pt.X > ActualWidth - edge, t = pt.Y < edge, b = pt.Y > ActualHeight - edge;
                int hit = (t, b, l, r) switch
                {
                    (true, _, true, _) => 13,  // HTTOPLEFT
                    (true, _, _, true) => 14,  // HTTOPRIGHT
                    (_, true, true, _) => 16,  // HTBOTTOMLEFT
                    (_, true, _, true) => 17,  // HTBOTTOMRIGHT
                    (true, _, _, _) => 12,     // HTTOP
                    (_, true, _, _) => 15,     // HTBOTTOM
                    (_, _, true, _) => 10,     // HTLEFT
                    (_, _, _, true) => 11,     // HTRIGHT
                    _ => 0,
                };
                if (hit == 0) break;
                handled = true;
                return new IntPtr(hit);
            case WM_SIZING:
                _resizing = true;
                break;
            case WM_EXITSIZEMOVE when _resizing:
                _resizing = false;
                SizeToContent = SizeToContent.Manual;
                _p.Width = Math.Round(ActualWidth);
                _p.Height = Math.Round(ActualHeight);
                break;
        }
        return IntPtr.Zero;
    }

    public void Rebuild()
    {
        _view.Rebuild(unlocked: !_p.Locked);
        Opacity = _p.Opacity / 100.0;

        _dash.RadiusX = _dash.RadiusY = _p.CornerRadius;
        _dash.Stroke = ColorUtil.Brush(_p.AccentColor, Color.FromRgb(255, 170, 40));
        _dash.Visibility = _p.Locked ? Visibility.Collapsed : Visibility.Visible;

        // Can be dragged bigger than the content, never smaller (the hint line just trims)
        MinWidth = _view.ContentSize.Width;
        MinHeight = _view.ContentSize.Height;
        if (_p.Width > 0 && _p.Height > 0)
        {
            SizeToContent = SizeToContent.Manual;
            Width = _p.Width;
            Height = _p.Height;
        }
        else
        {
            SizeToContent = SizeToContent.WidthAndHeight;
        }
        Dispatcher.BeginInvoke(PlaceAtAnchor, System.Windows.Threading.DispatcherPriority.Loaded);
    }

    public void UpdateValues()
    {
        _view.UpdateValues();
        // Some fullscreen-borderless games push themselves above us; reassert topmost.
        if (_hwnd != IntPtr.Zero)
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// Pinned overlays: puts the window in the chosen corner/edge of the chosen monitor (whole screen, not the work area,
    /// because games cover the taskbar). Done in physical pixels so mixed-DPI setups land exactly.
    /// </summary>
    private void PlaceAtAnchor()
    {
        if (_p.Anchor == OverlayAnchor.Custom || _hwnd == IntPtr.Zero || !GetWindowRect(_hwnd, out var rect)) return;
        var screen = Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _p.Monitor) ?? Screen.PrimaryScreen;
        if (screen == null) return;
        var (x, y) = AnchorPosition(_p.Anchor, screen.Bounds.Left, screen.Bounds.Top, screen.Bounds.Width, screen.Bounds.Height,
                                    rect.Right - rect.Left, rect.Bottom - rect.Top, _p.EdgeMargin * VisualTreeHelper.GetDpi(this).DpiScaleX);
        _placing = true;
        try
        {
            SetWindowPos(_hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
        }
        finally
        {
            _placing = false;
        }
    }

    /// <summary>Top-left pixel for a window of w×h in a screen at (sx, sy) of sw×sh, <paramref name="margin"/> px from the edges.</summary>
    internal static (int X, int Y) AnchorPosition(OverlayAnchor anchor, int sx, int sy, int sw, int sh, int w, int h, double margin)
    {
        int m = (int)Math.Round(margin);
        int left = sx + m, center = sx + (sw - w) / 2, right = sx + sw - w - m;
        int top = sy + m, middle = sy + (sh - h) / 2, bottom = sy + sh - h - m;
        return anchor switch
        {
            OverlayAnchor.TopCenter => (center, top),
            OverlayAnchor.TopRight => (right, top),
            OverlayAnchor.MiddleLeft => (left, middle),
            OverlayAnchor.MiddleRight => (right, middle),
            OverlayAnchor.BottomLeft => (left, bottom),
            OverlayAnchor.BottomCenter => (center, bottom),
            OverlayAnchor.BottomRight => (right, bottom),
            _ => (left, top),
        };
    }

    private static (double, double) ClampToScreen(double x, double y)
    {
        double left = SystemParameters.VirtualScreenLeft, top = SystemParameters.VirtualScreenTop;
        double right = left + SystemParameters.VirtualScreenWidth - 60;
        double bottom = top + SystemParameters.VirtualScreenHeight - 40;
        bool offscreen = x < left || y < top || x > right || y > bottom;
        return offscreen ? (20, 20) : (x, y);
    }
}
