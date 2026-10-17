using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Kelvra;

/// <summary>
/// Colour swatch + hex box; the swatch opens a full colour picker (any colour, plus presets). Value is a "#RRGGBB"
/// string ("#AARRGGBB" values keep their alpha). While you drag in the picker the value follows ~20 times a second
/// (each change redraws overlays); Esc in the picker puts back the colour it opened with.
/// </summary>
public sealed class ColorField : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(ColorField),
        new FrameworkPropertyMetadata("#FFFFFF", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    /// <summary>Quick picks for overlay colours: greys, then the colour wheel.</summary>
    private static readonly (string Hex, string Name)[] OverlayPalette =
    {
        ("#FFFFFF", "White"), ("#E6E6EB", "Light grey"), ("#9CA3AF", "Grey"), ("#4B5563", "Dark grey"), ("#101014", "Near black"), ("#000000", "Black"),
        ("#00FF90", "Mint"), ("#22C55E", "Green"), ("#A3E635", "Lime"), ("#FACC15", "Yellow"), ("#FFAA28", "Amber"), ("#F97316", "Orange"),
        ("#EF4444", "Red"), ("#EC4899", "Pink"), ("#A855F7", "Purple"), ("#6366F1", "Indigo"), ("#3B82F6", "Blue"), ("#06B6D4", "Cyan"),
    };

    private readonly Border _swatch;
    private readonly TextBox _hex;
    private readonly Popup _popup;
    private readonly ColorPicker _picker = new();
    private readonly DispatcherTimer _throttle = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private Color? _pending;
    private string _openedWith = "";
    private DateTime _popupClosedAt;

    /// <summary>The value changed (typed, picked, or set from outside).</summary>
    public event Action<string>? ValueChanged;

    public ColorField()
    {
        _swatch = new Border
        {
            Width = 20,
            Height = 20,
            Margin = new Thickness(6, 0, 0, 0),
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = "Pick any colour",
            Focusable = true,
        };
        _swatch.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        System.Windows.Automation.AutomationProperties.SetName(_swatch, "Pick a colour");
        _swatch.MouseLeftButtonUp += (_, _) => Open();
        _swatch.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space)) return;
            Open();
            e.Handled = true;
        };

        // the hex text sits in the same box as the swatch: a bare text box, the box's border shows focus
        var bare = new ControlTemplate(typeof(TextBox));
        var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
        host.SetValue(FocusableProperty, false);
        host.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        bare.VisualTree = host;
        _hex = new TextBox
        {
            Template = bare,
            Background = Brushes.Transparent,
            Margin = new Thickness(8, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            FontSize = 12,
            MaxLength = 9,
            CharacterCasing = CharacterCasing.Upper,
        };
        _hex.SetResourceReference(TextBox.FontFamilyProperty, "MonoFont");
        System.Windows.Automation.AutomationProperties.SetName(_hex, "Colour as hex");
        _hex.LostKeyboardFocus += (_, _) => CommitHex();
        _hex.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) CommitHex();
        };

        _picker.SetPresets(OverlayPalette.Select(p => (ColorUtil.Parse(p.Hex, Colors.White), p.Name)));
        _picker.ColorChanged += c =>
        {
            _pending = c;
            if (!_throttle.IsEnabled) _throttle.Start();
        };
        _picker.Committed += Flush;
        _throttle.Tick += (_, _) => Flush();

        var popupBorder = new Border
        {
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Child = _picker,
        };
        popupBorder.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        popupBorder.SetResourceReference(Border.BorderBrushProperty, "LineBrush");

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(_hex, 1);
        row.Children.Add(_swatch);
        row.Children.Add(_hex);
        var box = new Border
        {
            Height = 32,
            MinWidth = 104,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            Child = row,
        };
        box.SetResourceReference(Border.BackgroundProperty, "InputBrush");
        box.SetResourceReference(Border.BorderBrushProperty, "Line2Brush");
        _hex.GotKeyboardFocus += (_, _) => box.SetResourceReference(Border.BorderBrushProperty, "AccentBrush");
        _hex.LostKeyboardFocus += (_, _) => box.SetResourceReference(Border.BorderBrushProperty, "Line2Brush");

        _popup = new Popup
        {
            PlacementTarget = box,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 4,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = popupBorder,
        };
        _popup.Closed += (_, _) =>
        {
            Flush();
            _popupClosedAt = DateTime.Now;
        };
        _popup.PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            _pending = null;
            _throttle.Stop();
            Value = _openedWith; // Esc: back to how it was
            _popup.IsOpen = false;
            e.Handled = true;
        };

        row.Children.Add(_popup);
        Content = box;

        Refresh();
    }

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    /// <summary>Quick picks in the picker (default: greys and a colour wheel for overlays).</summary>
    public IEnumerable<(Color Color, string Name)> Presets
    {
        set => _picker.SetPresets(value);
    }

    private void Open()
    {
        // Clicking the swatch while open closes the popup first; don't instantly reopen it.
        if (_popup.IsOpen || (DateTime.Now - _popupClosedAt).TotalMilliseconds < 250) return;
        _openedWith = Value;
        var current = ColorUtil.Parse(Value, Colors.White);
        _picker.Before = current;
        _picker.Color = current;
        _popup.IsOpen = true;
    }

    /// <summary>Hands the picker's latest colour on (keeping an alpha the value had).</summary>
    private void Flush()
    {
        _throttle.Stop();
        if (_pending is not Color c) return;
        _pending = null;
        byte alpha = ColorUtil.Parse(Value, Colors.White).A;
        string hex = alpha == 255 ? ColorMath.ToHex(c) : $"#{alpha:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
        if (!string.Equals(hex, Value, StringComparison.OrdinalIgnoreCase)) Value = hex;
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var field = (ColorField)d;
        field.Refresh();
        field.ValueChanged?.Invoke(field.Value);
    }

    private void Refresh()
    {
        _swatch.Background = ColorUtil.Brush(Value, Colors.Transparent);
        if (!_hex.IsKeyboardFocused) _hex.Text = Value;
    }

    private void CommitHex()
    {
        string text = _hex.Text.Trim();
        if (!text.StartsWith('#')) text = "#" + text;
        if (ColorUtil.IsValidHex(text)) Value = text.ToUpperInvariant();
        else _hex.Text = Value;
    }
}

/// <summary>Binds RadioButtons to an enum/string: IsChecked = (value == ConverterParameter).</summary>
public sealed class MatchConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not true || parameter == null) return Binding.DoNothing;
        string text = parameter.ToString()!;
        if (targetType.IsEnum) return Enum.Parse(targetType, text);
        var type = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return type == typeof(string) ? text : System.Convert.ChangeType(text, type, CultureInfo.InvariantCulture); // ints, doubles …
    }
}
