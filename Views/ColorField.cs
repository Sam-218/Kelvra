using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Kelvra;

/// <summary>Colour swatch + hex box with a preset palette popup. Value is a "#RRGGBB" string.</summary>
public sealed class ColorField : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(string), typeof(ColorField),
        new FrameworkPropertyMetadata("#FFFFFF", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    private static readonly string[] Palette =
    {
        "#FFFFFF", "#E6E6EB", "#9CA3AF", "#4B5563", "#101014", "#000000",
        "#00FF90", "#22C55E", "#A3E635", "#FACC15", "#FFAA28", "#F97316",
        "#EF4444", "#EC4899", "#A855F7", "#6366F1", "#3B82F6", "#06B6D4",
    };

    private readonly Border _swatch;
    private readonly TextBox _hex;
    private readonly Popup _popup;
    private DateTime _popupClosedAt;

    public ColorField()
    {
        _swatch = new Border
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(7),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = "Pick a colour",
        };
        _swatch.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
        _swatch.MouseLeftButtonUp += (_, _) =>
        {
            // Clicking the swatch while open closes the popup first; don't instantly reopen it.
            if ((DateTime.Now - _popupClosedAt).TotalMilliseconds < 250) return;
            _popup!.IsOpen = true;
        };

        _hex = new TextBox { Width = 96, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _hex.LostKeyboardFocus += (_, _) => CommitHex();
        _hex.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) CommitHex();
        };

        var grid = new UniformGrid { Columns = 6 };
        foreach (var hex in Palette)
        {
            var chip = new Border
            {
                Width = 24,
                Height = 24,
                Margin = new Thickness(3),
                CornerRadius = new CornerRadius(6),
                Background = ColorUtil.Brush(hex, Colors.White),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                ToolTip = hex,
            };
            chip.SetResourceReference(Border.BorderBrushProperty, "LineBrush");
            chip.MouseLeftButtonUp += (_, _) =>
            {
                Value = hex;
                _popup!.IsOpen = false;
            };
            grid.Children.Add(chip);
        }

        var popupBorder = new Border
        {
            Padding = new Thickness(8),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(1),
            Child = grid,
        };
        popupBorder.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        popupBorder.SetResourceReference(Border.BorderBrushProperty, "LineBrush");

        _popup = new Popup
        {
            PlacementTarget = _swatch,
            Placement = PlacementMode.Bottom,
            VerticalOffset = 4,
            StaysOpen = false,
            AllowsTransparency = true,
            PopupAnimation = PopupAnimation.Fade,
            Child = popupBorder,
        };
        _popup.Closed += (_, _) => _popupClosedAt = DateTime.Now;

        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(_swatch);
        panel.Children.Add(_hex);
        panel.Children.Add(_popup);
        Content = panel;

        Refresh();
    }

    public string Value
    {
        get => (string)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ColorField)d).Refresh();

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
