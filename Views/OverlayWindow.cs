using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using static Kelvra.NativeMethods;

namespace Kelvra;

/// <summary>
/// Transparent, always-on-top, click-through OSD for one <see cref="OverlayProfile"/>.
/// Built in code so the layout can switch between vertical and horizontal freely.
/// </summary>
public sealed class OverlayWindow : Window
{
    private static readonly Color WarmColor = Color.FromRgb(255, 210, 60);
    private static readonly Color HotColor = Color.FromRgb(255, 80, 70);

    private readonly OverlayProfile _p;
    private readonly SensorStore _store;
    private readonly Border _root = new();
    private readonly Rectangle _dash = new() { StrokeDashArray = new DoubleCollection { 4, 3 }, StrokeThickness = 1.5, IsHitTestVisible = false };
    private readonly List<Cell> _cells = new();
    private IntPtr _hwnd;

    private Brush _valueBrush = Brushes.White;
    private readonly Brush _warmBrush = Frozen(WarmColor);
    private readonly Brush _hotBrush = Frozen(HotColor);

    private sealed record Cell(OverlayItem Item, TextBlock Label, TextBlock Value, ColumnDefinition? BarFill, ColumnDefinition? BarRest, Border? BarFront);

    public OverlayWindow(OverlayProfile profile, SensorStore store)
    {
        _p = profile;
        _store = store;

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
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var host = new Grid();
        host.Children.Add(_root);
        host.Children.Add(_dash);
        Content = host;

        (Left, Top) = ClampToScreen(profile.X, profile.Y);

        SourceInitialized += (_, _) =>
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            SetExStyle(_hwnd, WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, true);
            HwndSource.FromHwnd(_hwnd).AddHook(WndProc);
            ApplyLock();
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (_p.Locked) return;
            if (e.ClickCount == 2)
            {
                // Double-click: back to fit-to-content
                _p.Width = _p.Height = 0;
                Rebuild();
            }
            else DragMove();
        };
        LocationChanged += (_, _) =>
        {
            if (_p.Locked) return;
            _p.X = Math.Round(Left);
            _p.Y = Math.Round(Top);
        };
        _p.Changed += OnProfileChanged;
        Closed += (_, _) => _p.Changed -= OnProfileChanged;

        Rebuild();
    }

    private void OnProfileChanged(object? sender, string property)
    {
        switch (property)
        {
            case nameof(OverlayProfile.X) or nameof(OverlayProfile.Y) or nameof(OverlayProfile.Enabled)
                or nameof(OverlayProfile.Width) or nameof(OverlayProfile.Height):
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
    private bool _resizing;

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

    // ---------- building ----------

    public void Rebuild()
    {
        var bg = ColorUtil.Parse(_p.BackgroundColor, Color.FromRgb(16, 16, 20));
        bg.A = (byte)Math.Round(_p.BackgroundOpacity * 2.55);
        // Keep a sliver of alpha so the unlocked window can still be grabbed with the mouse
        if (!_p.Locked && bg.A < 40) bg.A = 40;

        var accent = ColorUtil.Brush(_p.AccentColor, Color.FromRgb(255, 170, 40));
        var labelBrush = ColorUtil.Brush(_p.LabelColor, Color.FromRgb(230, 230, 235));
        _valueBrush = ColorUtil.Brush(_p.ValueColor, Color.FromRgb(0, 255, 144));

        _root.Background = new SolidColorBrush(bg);
        _root.CornerRadius = new CornerRadius(_p.CornerRadius);
        _root.Padding = _p.Layout == OverlayLayout.Vertical ? new Thickness(12, 9, 12, 10) : new Thickness(12, 6, 12, 6);
        _root.Effect = _p.BackgroundOpacity < 45
            ? new DropShadowEffect { ShadowDepth = 1, BlurRadius = 3, Opacity = 0.9, Color = Colors.Black, RenderingBias = RenderingBias.Performance }
            : null;

        _dash.RadiusX = _dash.RadiusY = _p.CornerRadius;
        _dash.Stroke = accent;
        _dash.Visibility = _p.Locked ? Visibility.Collapsed : Visibility.Visible;

        FontFamily = new FontFamily(_p.FontFamily);
        FontSize = _p.FontSize;

        _cells.Clear();
        var panel = new StackPanel();

        if (!_p.Locked)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"✥ {_p.Name} — drag to move · drag edges to resize · double-click to fit · Ctrl+Shift+L to lock",
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = "Drag to move · drag the edges to resize · double-click to fit the content · Ctrl+Shift+L to lock",
                Foreground = accent,
                FontSize = Math.Max(10, _p.FontSize - 2),
                Margin = new Thickness(0, 0, 0, 6),
            });
        }

        UIElement content = _p.Items.Count == 0
            ? new TextBlock { Text = $"{_p.Name}: no sensors yet.\nAdd some in Kelvra → Overlays.", Foreground = labelBrush }
            : _p.Layout == OverlayLayout.Vertical ? BuildVertical(labelBrush, accent) : BuildHorizontal(labelBrush, accent);
        panel.Children.Add(content);

        _root.Child = panel;
        UpdateValues();

        // Can be dragged bigger than the content, never smaller (the hint line just trims)
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        MinWidth = content.DesiredSize.Width + _root.Padding.Left + _root.Padding.Right;
        _root.Measure(new Size(MinWidth, double.PositiveInfinity));
        MinHeight = _root.DesiredSize.Height;

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
    }

    private UIElement BuildVertical(Brush labelBrush, Brush accent)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        string? lastGroup = null;

        foreach (var item in _p.Items)
        {
            string group = GroupOf(item);
            if (_p.ShowHeaders && group != lastGroup)
            {
                var header = new TextBlock
                {
                    Text = group,
                    Foreground = accent,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, lastGroup == null ? 0 : 6, 0, 1),
                };
                AddRow(grid, header, span: true);
                lastGroup = group;
            }

            var label = new TextBlock { Foreground = labelBrush, VerticalAlignment = VerticalAlignment.Center };
            var value = new TextBlock
            {
                FontWeight = FontWeights.SemiBold,
                TextAlignment = TextAlignment.Right,
                MinWidth = _p.FontSize * 4.8, // stops the window from jittering as digits change
                Margin = new Thickness(18, 0, 0, 0),
            };
            int row = AddRow(grid, label);
            Grid.SetColumn(value, 1);
            Grid.SetRow(value, row);
            grid.Children.Add(value);

            ColumnDefinition? fill = null, rest = null; Border? front = null;
            if (item.ShowBar)
            {
                var bar = MakeBar(labelBrush, out fill, out rest, out front);
                bar.Margin = new Thickness(0, 1, 0, 4);
                AddRow(grid, bar, span: true);
            }
            _cells.Add(new Cell(item, label, value, fill, rest, front));
        }
        return grid;
    }

    private UIElement BuildHorizontal(Brush labelBrush, Brush accent)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        string? lastGroup = null;

        foreach (var item in _p.Items)
        {
            string group = GroupOf(item);
            if (_p.ShowHeaders && group != lastGroup)
            {
                row.Children.Add(new TextBlock
                {
                    Text = group,
                    Foreground = accent,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(lastGroup == null ? 0 : 8, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                lastGroup = group;
            }

            var label = new TextBlock { Foreground = labelBrush, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
            var value = new TextBlock { FontWeight = FontWeights.SemiBold, MinWidth = _p.FontSize * 3.6, VerticalAlignment = VerticalAlignment.Center };

            var cell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 14, 0) };
            cell.Children.Add(label);

            ColumnDefinition? fill = null, rest = null; Border? front = null;
            if (item.ShowBar)
            {
                var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                stack.Children.Add(value);
                var bar = MakeBar(labelBrush, out fill, out rest, out front);
                bar.Margin = new Thickness(0, 1, 0, 0);
                stack.Children.Add(bar);
                cell.Children.Add(stack);
            }
            else
            {
                cell.Children.Add(value);
            }

            row.Children.Add(cell);
            _cells.Add(new Cell(item, label, value, fill, rest, front));
        }
        return row;
    }

    private Grid MakeBar(Brush labelBrush, out ColumnDefinition fill, out ColumnDefinition rest, out Border front)
    {
        var track = new Grid { Height = Math.Max(3, _p.FontSize / 4), ClipToBounds = true };
        fill = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
        rest = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        track.ColumnDefinitions.Add(fill);
        track.ColumnDefinitions.Add(rest);

        var bgBrush = labelBrush.Clone();
        bgBrush.Opacity = 0.18;
        var back = new Border { Background = bgBrush, CornerRadius = new CornerRadius(2) };
        Grid.SetColumnSpan(back, 2);
        front = new Border { CornerRadius = new CornerRadius(2) };
        track.Children.Add(back);
        track.Children.Add(front);
        return track;
    }

    private static int AddRow(Grid grid, UIElement element, bool span = false)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        int row = grid.RowDefinitions.Count - 1;
        Grid.SetRow(element, row);
        if (span) Grid.SetColumnSpan(element, 2);
        grid.Children.Add(element);
        return row;
    }

    private string GroupOf(OverlayItem item) =>
        OverlayExtras.NameOf(item.SensorId) != null ? OverlayExtras.GroupOf(item.SensorId)
        : _store.ById.TryGetValue(item.SensorId, out var s) ? s.GroupName : item.HardwareName;

    // ---------- live values ----------

    public void UpdateValues()
    {
        foreach (var cell in _cells)
        {
            _store.ById.TryGetValue(cell.Item.SensorId, out var s);
            string? extra = OverlayExtras.NameOf(cell.Item.SensorId);

            string label = cell.Item.Label ?? s?.OverlayLabel ?? extra ?? (_store.Loaded ? "?" : "…");
            if (cell.Label.Text != label) cell.Label.Text = label;

            string value = extra != null ? OverlayExtras.Text(cell.Item.SensorId, _p.ClockSeconds) : s?.ValueText ?? "-";
            if (cell.Value.Text != value) cell.Value.Text = value;

            var brush = !_p.WarnColors || s == null ? _valueBrush
                : s.Level switch { 2 => _hotBrush, 1 => _warmBrush, _ => _valueBrush };
            if (!ReferenceEquals(cell.Value.Foreground, brush)) cell.Value.Foreground = brush;

            if (cell.BarFill != null && cell.BarRest != null)
            {
                double f = s?.BarFraction ?? 0;
                cell.BarFill.Width = new GridLength(f, GridUnitType.Star);
                cell.BarRest.Width = new GridLength(1 - f, GridUnitType.Star);

                if (cell.BarFront != null) cell.BarFront.Background = brush;

            }
        }

        // Some fullscreen-borderless games push themselves above us; reassert topmost.
        if (_hwnd != IntPtr.Zero)
            SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private static (double, double) ClampToScreen(double x, double y)
    {
        double left = SystemParameters.VirtualScreenLeft, top = SystemParameters.VirtualScreenTop;
        double right = left + SystemParameters.VirtualScreenWidth - 60;
        double bottom = top + SystemParameters.VirtualScreenHeight - 40;
        bool offscreen = x < left || y < top || x > right || y > bottom;
        return offscreen ? (20, 20) : (x, y);
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
