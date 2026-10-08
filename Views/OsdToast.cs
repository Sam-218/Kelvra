using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using static Kelvra.NativeMethods;

namespace Kelvra;

/// <summary>
/// A short message drawn over the game (top centre of the screen the game is on), e.g. "Benchmark started". Windows
/// toasts are held back while a game runs fullscreen, so shortcuts confirm themselves this way. Click-through, never
/// takes focus, fades out after a few seconds; a new message replaces the old one.
/// </summary>
public sealed class OsdToast : Window
{
    private static OsdToast? _current;
    private readonly DispatcherTimer _timer;

    private OsdToast(string title, string text, Color accent, TimeSpan duration)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(accent),
        });
        if (text.Length > 0)
            stack.Children.Add(new TextBlock
            {
                Text = text,
                FontSize = 13,
                Margin = new Thickness(0, 3, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xEB)),
                MaxWidth = 520,
                TextWrapping = TextWrapping.Wrap,
            });
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE8, 0x10, 0x10, 0x14)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x90, accent.R, accent.G, accent.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18, 10, 18, 12),
            Child = stack,
        };
        FontFamily = new FontFamily("Segoe UI");

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetExStyle(hwnd, WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TRANSPARENT, true);
        };
        Loaded += (_, _) => PlaceOverGame();

        _timer = new DispatcherTimer { Interval = duration };
        _timer.Tick += (_, _) =>
        {
            _timer.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(350));
            fade.Completed += (_, _) => Close();
            BeginAnimation(OpacityProperty, fade);
        };
        Closed += (_, _) =>
        {
            _timer.Stop();
            if (_current == this) _current = null;
        };
    }

    /// <summary>Shows a message over the game. <paramref name="accent"/> null = Kelvra's accent colour.</summary>
    public static void Notify(string title, string text = "", Color? accent = null, double seconds = 3.5)
    {
        _current?.Close();
        var color = accent ?? (Application.Current.TryFindResource("AccentBrush") is SolidColorBrush b ? b.Color : Color.FromRgb(0xF5, 0xA5, 0x24));
        _current = new OsdToast(title, text, color, TimeSpan.FromSeconds(seconds));
        _current.Show();
        _current._timer.Start();
    }

    public static readonly Color Red = Color.FromRgb(0xFF, 0x50, 0x46);
    public static readonly Color Green = Color.FromRgb(0x3D, 0xDC, 0x84);

    /// <summary>Top centre of the monitor the foreground window (the game) is on, in physical pixels converted to WPF units.</summary>
    private void PlaceOverGame()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var screen = System.Windows.Forms.Screen.FromHandle(GetForegroundWindow()).Bounds;
        var source = PresentationSource.FromVisual(this);
        double scaleX = source?.CompositionTarget?.TransformToDevice.M11 ?? 1;
        double scaleY = source?.CompositionTarget?.TransformToDevice.M22 ?? 1;
        double widthPx = ActualWidth * scaleX;
        int x = (int)(screen.Left + (screen.Width - widthPx) / 2);
        int y = screen.Top + (int)(60 * scaleY);
        const uint SWP_NOSIZE_NOACTIVATE = SWP_NOSIZE | SWP_NOACTIVATE;
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE_NOACTIVATE);
    }
}
