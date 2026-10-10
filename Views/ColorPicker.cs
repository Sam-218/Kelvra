using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace Kelvra;

/// <summary>
/// A full colour picker: a saturation / brightness square, a hue strip, R / G / B and hex fields, the colour before
/// and now, and preset swatches. Drag in the square or the strip (or use the arrow keys there, Shift for bigger steps),
/// or type exact values (↑ / ↓ in the R / G / B fields step them).
/// </summary>
public sealed class ColorPicker : UserControl
{
    private const double SquareW = 232, SquareH = 156, StripW = 18;

    private double _h, _s, _v = 1;
    private Color _color = Colors.White;

    private readonly SolidColorBrush _hueBrush = new(Colors.Red);
    private readonly Grid _square, _strip;
    private readonly Ellipse _squareThumb;
    private readonly TranslateTransform _stripThumbMove = new();
    private readonly Border _before, _after;
    private readonly TextBox _r, _g, _b, _hex;
    private readonly WrapPanel _presets = new() { Margin = new Thickness(-3, 0, -3, 0) };
    private readonly TextBlock _presetsLabel;

    /// <summary>The colour changed – while dragging, on every move.</summary>
    public event Action<Color>? ColorChanged;

    /// <summary>A drag ended, a value was typed or a preset picked: a good moment to apply it for real.</summary>
    public event Action? Committed;

    public ColorPicker()
    {
        // Saturation left → right, brightness top → bottom, over the current hue
        _square = new Grid { Width = SquareW, Height = SquareH, Cursor = Cursors.Cross, Focusable = true, Background = Brushes.Transparent };
        AutomationName(_square, "Saturation and brightness (arrow keys)");
        _square.Children.Add(new Rectangle { Fill = _hueBrush, RadiusX = 6, RadiusY = 6 });
        _square.Children.Add(new Rectangle { Fill = new LinearGradientBrush(Colors.White, Color.FromArgb(0, 255, 255, 255), 0), RadiusX = 6, RadiusY = 6 });
        _square.Children.Add(new Rectangle { Fill = new LinearGradientBrush(Color.FromArgb(0, 0, 0, 0), Colors.Black, 90), RadiusX = 6, RadiusY = 6 });
        _squareThumb = new Ellipse
        {
            Width = 14, Height = 14, Stroke = Brushes.White, StrokeThickness = 2, IsHitTestVisible = false,
            Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.8 },
        };
        var thumbs = new Canvas { IsHitTestVisible = false };
        thumbs.Children.Add(_squareThumb);
        _square.Children.Add(thumbs);
        Drag(_square, p =>
        {
            _s = Math.Clamp(p.X / SquareW, 0, 1);
            _v = 1 - Math.Clamp(p.Y / SquareH, 0, 1);
        });
        _square.KeyDown += (_, e) =>
        {
            double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 0.1 : 0.01;
            switch (e.Key)
            {
                case Key.Left: _s = Math.Max(0, _s - step); break;
                case Key.Right: _s = Math.Min(1, _s + step); break;
                case Key.Up: _v = Math.Min(1, _v + step); break;
                case Key.Down: _v = Math.Max(0, _v - step); break;
                default: return;
            }
            e.Handled = true;
            FromHsv(commit: true);
        };

        // Hue, red at the top through the rainbow back to red
        var rainbow = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        for (int i = 0; i <= 6; i++) rainbow.GradientStops.Add(new GradientStop(ColorMath.FromHsv(i * 60, 1, 1), i / 6.0));
        _strip = new Grid { Width = StripW, Height = SquareH, Cursor = Cursors.Hand, Focusable = true, Background = Brushes.Transparent, Margin = new Thickness(10, 0, 0, 0) };
        AutomationName(_strip, "Hue (arrow keys)");
        _strip.Children.Add(new Rectangle { Fill = rainbow, RadiusX = 4, RadiusY = 4 });
        _strip.Children.Add(new Border
        {
            Height = 7, Margin = new Thickness(-3, -3, -3, 0), VerticalAlignment = VerticalAlignment.Top, CornerRadius = new CornerRadius(3),
            BorderBrush = Brushes.White, BorderThickness = new Thickness(2), IsHitTestVisible = false, RenderTransform = _stripThumbMove,
            Effect = new DropShadowEffect { BlurRadius = 3, ShadowDepth = 0, Opacity = 0.8 },
        });
        Drag(_strip, p => _h = Math.Clamp(p.Y / SquareH, 0, 1) * 359.99);
        _strip.KeyDown += (_, e) =>
        {
            double step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
            switch (e.Key)
            {
                case Key.Up: _h = Math.Max(0, _h - step); break;
                case Key.Down: _h = Math.Min(359.99, _h + step); break;
                default: return;
            }
            e.Handled = true;
            FromHsv(commit: true);
        };

        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(_square);
        top.Children.Add(_strip);

        // Before | now, then the exact values
        _before = new Border { Width = 26, CornerRadius = new CornerRadius(6, 0, 0, 6), ToolTip = "Before" };
        _after = new Border { Width = 26, CornerRadius = new CornerRadius(0, 6, 6, 0), ToolTip = "Now" };
        var compare = new StackPanel { Orientation = Orientation.Horizontal, Height = 30, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 10, 0) };
        compare.Children.Add(_before);
        compare.Children.Add(_after);

        _r = Channel("R");
        _g = Channel("G");
        _b = Channel("B");
        _hex = new TextBox { Width = 76 };
        AutomationName(_hex, "Hex");
        _hex.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) CommitHex();
        };
        _hex.LostKeyboardFocus += (_, _) => CommitHex();

        var fields = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        fields.Children.Add(compare);
        fields.Children.Add(Labelled("R", _r));
        fields.Children.Add(Labelled("G", _g));
        fields.Children.Add(Labelled("B", _b));
        fields.Children.Add(Labelled("Hex", _hex));

        _presetsLabel = Label("Presets");
        _presetsLabel.Margin = new Thickness(0, 12, 0, 4);

        var root = new StackPanel();
        root.Children.Add(top);
        root.Children.Add(fields);
        root.Children.Add(_presetsLabel);
        root.Children.Add(_presets);
        Content = root;
        Show();
    }

    /// <summary>The colour shown. Setting it doesn't raise <see cref="ColorChanged"/>.</summary>
    public Color Color
    {
        get => _color;
        set => FromRgb(value, raise: false);
    }

    /// <summary>The colour before this pick, shown beside the new one.</summary>
    public Color Before
    {
        set => _before.Background = Brush(value);
    }

    /// <summary>Quick picks under the fields (empty: no presets row).</summary>
    public void SetPresets(IEnumerable<(Color Color, string Name)> presets)
    {
        _presets.Children.Clear();
        foreach (var (color, name) in presets)
        {
            var chip = new Border
            {
                Width = 22, Height = 22, Margin = new Thickness(3), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1),
                Background = Brush(color), Cursor = Cursors.Hand, ToolTip = $"{name} · {ColorMath.ToHex(color)}",
            };
            chip.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            AutomationName(chip, name);
            chip.MouseLeftButtonUp += (_, _) =>
            {
                FromRgb(color, raise: true);
                Committed?.Invoke();
            };
            _presets.Children.Add(chip);
        }
        _presetsLabel.Visibility = _presets.Children.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------- state ----------

    /// <summary>From the square / strip: the colour follows the HSV values.</summary>
    private void FromHsv(bool commit)
    {
        _color = ColorMath.FromHsv(_h, _s, _v);
        Show();
        ColorChanged?.Invoke(_color);
        if (commit) Committed?.Invoke();
    }

    /// <summary>From typed values or a preset. Greys and black have no hue of their own: the strip keeps the last one.</summary>
    private void FromRgb(Color c, bool raise)
    {
        c.A = 255;
        var (h, s, v) = ColorMath.ToHsv(c);
        if (s > 0 && v > 0) _h = h;
        if (v > 0) _s = s;
        _v = v;
        _color = c;
        Show();
        if (raise) ColorChanged?.Invoke(_color);
    }

    private void Show()
    {
        _hueBrush.Color = ColorMath.FromHsv(_h, 1, 1);
        Canvas.SetLeft(_squareThumb, _s * SquareW - 7);
        Canvas.SetTop(_squareThumb, (1 - _v) * SquareH - 7);
        _stripThumbMove.Y = _h / 360 * SquareH;
        _after.Background = Brush(_color);
        foreach (var (box, value) in new[] { (_r, _color.R), (_g, _color.G), (_b, _color.B) })
            if (!box.IsKeyboardFocused) box.Text = value.ToString(CultureInfo.InvariantCulture);
        if (!_hex.IsKeyboardFocused) _hex.Text = ColorMath.ToHex(_color);
    }

    // ---------- input ----------

    /// <summary>Mouse down, drag and up in the square or the strip.</summary>
    private void Drag(FrameworkElement area, Action<Point> apply)
    {
        area.MouseLeftButtonDown += (_, e) =>
        {
            area.Focus();
            area.CaptureMouse();
            apply(e.GetPosition(area));
            FromHsv(commit: false);
            e.Handled = true;
        };
        area.MouseMove += (_, e) =>
        {
            if (!area.IsMouseCaptured) return;
            apply(e.GetPosition(area));
            FromHsv(commit: false);
        };
        area.MouseLeftButtonUp += (_, e) =>
        {
            if (!area.IsMouseCaptured) return;
            area.ReleaseMouseCapture();
            e.Handled = true;
            Committed?.Invoke();
        };
    }

    private TextBox Channel(string name)
    {
        var box = new TextBox { Width = 46, HorizontalContentAlignment = HorizontalAlignment.Right };
        AutomationName(box, name switch { "R" => "Red", "G" => "Green", _ => "Blue" });
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) CommitChannels();
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Up or Key.Down) || !int.TryParse(box.Text, out int v)) return;
            int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
            box.Text = Math.Clamp(v + (e.Key == Key.Up ? step : -step), 0, 255).ToString(CultureInfo.InvariantCulture);
            box.CaretIndex = box.Text.Length;
            CommitChannels();
            e.Handled = true;
        };
        box.LostKeyboardFocus += (_, _) => CommitChannels();
        return box;
    }

    private void CommitChannels()
    {
        static byte? Read(TextBox t) => int.TryParse(t.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? (byte)Math.Clamp(v, 0, 255) : null;
        if (Read(_r) is byte r && Read(_g) is byte g && Read(_b) is byte b)
        {
            var c = Color.FromRgb(r, g, b);
            if (c != _color)
            {
                FromRgb(c, raise: true);
                Committed?.Invoke();
                return;
            }
        }
        Show(); // put back what's valid
    }

    private void CommitHex()
    {
        string text = _hex.Text.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        if (Accents.IsCustom(text))
        {
            var c = ColorUtil.Parse(text, _color);
            if (c != _color)
            {
                FromRgb(c, raise: true);
                Committed?.Invoke();
                return;
            }
        }
        Show();
    }

    // ---------- bits ----------

    private static StackPanel Labelled(string label, FrameworkElement field)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 6, 0) };
        panel.Children.Add(Label(label));
        panel.Children.Add(field);
        return panel;
    }

    private static TextBlock Label(string text)
    {
        var t = new TextBlock { Text = text, FontSize = 11, Margin = new Thickness(1, 0, 0, 3) };
        t.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        return t;
    }

    private static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private static void AutomationName(DependencyObject o, string name) => System.Windows.Automation.AutomationProperties.SetName(o, name);
}
