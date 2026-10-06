using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace Kelvra;

/// <summary>
/// Draws one <see cref="OverlayProfile"/>: panel, headers, labels, values, bars and mini graphs, in the vertical,
/// one-line or grid layout. Used by the on-screen <see cref="OverlayWindow"/> and by the live preview in the editor,
/// so what you see while editing is exactly what appears over the game.
/// </summary>
public sealed class OverlayView : Border
{
    private readonly OverlayProfile _p;
    private readonly SensorStore _store;
    private readonly bool _preview;
    private readonly List<Cell> _cells = new();
    private readonly Dictionary<string, Brush> _brushes = new();
    private Brush _valueBrush = Brushes.White, _warmBrush = Brushes.Yellow, _hotBrush = Brushes.Red, _accent = Brushes.Orange;

    private sealed record Cell(OverlayItem Item, TextBlock? Label, TextBlock Value, ColumnDefinition? BarFill, ColumnDefinition? BarRest,
                               Border? BarFront, TrendChart? Graph);

    public OverlayView(OverlayProfile profile, SensorStore store, bool preview)
    {
        _p = profile;
        _store = store;
        _preview = preview;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
    }

    /// <summary>Smallest size that shows all content (the window can be dragged bigger, never smaller).</summary>
    public Size ContentSize { get; private set; }

    // ---------- building ----------

    /// <param name="unlocked">Adds the "drag to move" hint and keeps a little background so the window can be grabbed.</param>
    public void Rebuild(bool unlocked)
    {
        _brushes.Clear();
        var bg = ColorUtil.Parse(_p.BackgroundColor, Color.FromRgb(16, 16, 20));
        bg.A = (byte)Math.Round(_p.BackgroundOpacity * 2.55);
        if (unlocked && !_preview && bg.A < 40) bg.A = 40;

        _accent = BrushOf(_p.AccentColor, Color.FromRgb(255, 170, 40));
        var labelBrush = BrushOf(_p.LabelColor, Color.FromRgb(230, 230, 235));
        _valueBrush = BrushOf(_p.ValueColor, Color.FromRgb(0, 255, 144));
        _warmBrush = BrushOf(_p.WarmColor, Color.FromRgb(255, 210, 60));
        _hotBrush = BrushOf(_p.HotColor, Color.FromRgb(255, 80, 70));

        int pad = _p.Padding;
        Background = new SolidColorBrush(bg);
        CornerRadius = new CornerRadius(_p.CornerRadius);
        BorderBrush = BrushOf(_p.BorderColor, Colors.Transparent);
        BorderThickness = new Thickness(_p.BorderThickness);
        Padding = _p.Layout == OverlayLayout.Horizontal ? new Thickness(pad, pad / 2.0, pad, pad / 2.0) : new Thickness(pad, pad * 0.75, pad, pad * 0.85);
        Effect = TextEffect();

        TextElement.SetFontFamily(this, new FontFamily(_p.FontFamily));
        TextElement.SetFontSize(this, _p.FontSize);

        _cells.Clear();
        var panel = new StackPanel();
        if (unlocked && !_preview)
        {
            panel.Children.Add(new TextBlock
            {
                Text = $"✥ {_p.Name} — drag to move · drag edges to resize · double-click to fit{LockHint}",
                TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = $"Drag to move · drag the edges to resize · double-click to fit the content{LockHint}",
                Foreground = _accent,
                FontSize = Math.Max(10, _p.FontSize - 2),
                Margin = new Thickness(0, 0, 0, 6),
            });
        }

        UIElement content = _p.Items.Count == 0
            ? new TextBlock { Text = $"{_p.Name}: no sensors yet.\nAdd some in Kelvra → Overlays.", Foreground = labelBrush }
            : _p.Layout switch
            {
                OverlayLayout.Horizontal => BuildHorizontal(labelBrush),
                OverlayLayout.Grid => BuildGrid(labelBrush),
                _ => BuildVertical(labelBrush),
            };
        panel.Children.Add(content);
        Child = panel;
        UpdateValues();

        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double width = content.DesiredSize.Width + Padding.Left + Padding.Right + BorderThickness.Left * 2;
        Measure(new Size(width, double.PositiveInfinity));
        ContentSize = new Size(width, DesiredSize.Height);
    }

    private static string LockHint => App.Current.Settings.HotkeyLock is { Length: > 0 } key ? $" · {key} to lock" : " · lock it in Kelvra";

    /// <summary>Shadow/outline keeps text readable over bright scenes; Auto adds a shadow when the panel is mostly see-through.</summary>
    private Effect? TextEffect() => _p.TextEffect switch
    {
        OverlayTextEffect.None => null,
        OverlayTextEffect.Shadow => new DropShadowEffect { ShadowDepth = 1.5, BlurRadius = 3, Opacity = 0.9, Color = Colors.Black, RenderingBias = RenderingBias.Performance },
        OverlayTextEffect.Outline => new DropShadowEffect { ShadowDepth = 0, BlurRadius = 4, Opacity = 1, Color = Colors.Black, RenderingBias = RenderingBias.Quality },
        _ => _p.BackgroundOpacity < 45
            ? new DropShadowEffect { ShadowDepth = 1, BlurRadius = 3, Opacity = 0.9, Color = Colors.Black, RenderingBias = RenderingBias.Performance }
            : null,
    };

    private TextBlock Label(Brush brush, double size) => new()
    {
        Foreground = brush,
        FontWeight = Weight(_p.LabelWeight),
        FontSize = size,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private TextBlock Value(double size)
    {
        var tb = new TextBlock { FontWeight = Weight(_p.ValueWeight), FontSize = size, VerticalAlignment = VerticalAlignment.Center };
        if (_p.ValueFontFamily.Length > 0) tb.FontFamily = new FontFamily(_p.ValueFontFamily); // else inherits the label font
        return tb;
    }

    private TrendChart Graph(double height) => new()
    {
        Width = _p.GraphWidth,
        Height = height,
        AutoScale = true,
        ShowGrid = false,
        WindowSeconds = _p.GraphSeconds,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private UIElement BuildVertical(Brush labelBrush)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });               // label
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });               // graph
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = _p.ValuesRight ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
        double valueSize = _p.FontSize * _p.ValueScale;
        string? lastGroup = null;

        foreach (var item in _p.Items)
        {
            string group = GroupOf(item);
            if (_p.ShowHeaders && group != lastGroup)
            {
                AddRow(grid, new TextBlock
                {
                    Text = group,
                    Foreground = _accent,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, lastGroup == null ? 0 : 6 + _p.RowSpacing, 0, 1),
                }, span: true);
                lastGroup = group;
            }

            var value = Value(valueSize);
            value.Margin = new Thickness(item.HideLabel ? 0 : 18, _p.RowSpacing / 2.0, 0, _p.RowSpacing / 2.0);
            value.TextAlignment = _p.ValuesRight ? TextAlignment.Right : TextAlignment.Left;
            if (_p.ValuesRight) value.MinWidth = valueSize * 4.2; // stops the window from jittering as digits change

            TextBlock? label = null;
            int row;
            if (item.HideLabel)
            {
                row = AddRow(grid, value, span: true);
                value.HorizontalAlignment = _p.ValuesRight ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            }
            else
            {
                label = Label(labelBrush, _p.FontSize);
                row = AddRow(grid, label);
                Grid.SetColumn(value, 2);
                Grid.SetRow(value, row);
                grid.Children.Add(value);
            }

            TrendChart? graph = null;
            if (item.ShowGraph)
            {
                graph = Graph(Math.Max(10, valueSize * 1.1));
                graph.Margin = new Thickness(14, 0, 0, 0);
                Grid.SetColumn(graph, 1);
                Grid.SetRow(graph, row);
                grid.Children.Add(graph);
            }

            ColumnDefinition? fill = null, rest = null; Border? front = null;
            if (item.ShowBar)
            {
                var bar = MakeBar(labelBrush, out fill, out rest, out front);
                bar.Margin = new Thickness(0, 1, 0, 4);
                AddRow(grid, bar, span: true);
            }
            _cells.Add(new Cell(item, label, value, fill, rest, front, graph));
        }
        return grid;
    }

    private UIElement BuildHorizontal(Brush labelBrush)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        double valueSize = _p.FontSize * _p.ValueScale;
        string? lastGroup = null;

        foreach (var item in _p.Items)
        {
            string group = GroupOf(item);
            if (_p.ShowHeaders && group != lastGroup)
            {
                row.Children.Add(new TextBlock
                {
                    Text = group,
                    Foreground = _accent,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(lastGroup == null ? 0 : _p.ItemSpacing / 2.0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
                lastGroup = group;
            }

            var cell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, _p.ItemSpacing, 0) };
            TextBlock? label = null;
            if (!item.HideLabel)
            {
                label = Label(labelBrush, _p.FontSize);
                label.Margin = new Thickness(0, 0, 6, 0);
                cell.Children.Add(label);
            }
            var value = Value(valueSize);
            value.MinWidth = valueSize * 3.4;

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

            TrendChart? graph = null;
            if (item.ShowGraph)
            {
                graph = Graph(Math.Max(10, valueSize * 1.1));
                graph.Margin = new Thickness(6, 0, 0, 0);
                cell.Children.Add(graph);
            }

            row.Children.Add(cell);
            _cells.Add(new Cell(item, label, value, fill, rest, front, graph));
        }
        return row;
    }

    /// <summary>Tiles: small label on top, big value below, bar or graph under it.</summary>
    private UIElement BuildGrid(Brush labelBrush)
    {
        var grid = new Grid();
        int columns = Math.Min(_p.Columns, Math.Max(1, _p.Items.Count));
        for (int c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        double valueSize = _p.FontSize * _p.ValueScale * 1.35;
        double gap = _p.ItemSpacing;

        for (int i = 0; i < _p.Items.Count; i++)
        {
            var item = _p.Items[i];
            int r = i / columns, c = i % columns;
            if (c == 0) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var tile = new StackPanel { Margin = new Thickness(0, 0, c == columns - 1 ? 0 : gap, gap / 2 + _p.RowSpacing), MinWidth = valueSize * 3.6 };
            TextBlock? label = null;
            if (!item.HideLabel)
            {
                label = Label(labelBrush, Math.Max(8, _p.FontSize * 0.85));
                tile.Children.Add(label);
            }
            var value = Value(valueSize);
            tile.Children.Add(value);

            ColumnDefinition? fill = null, rest = null; Border? front = null;
            if (item.ShowBar)
            {
                var bar = MakeBar(labelBrush, out fill, out rest, out front);
                bar.Margin = new Thickness(0, 2, 0, 0);
                tile.Children.Add(bar);
            }
            TrendChart? graph = null;
            if (item.ShowGraph)
            {
                graph = Graph(Math.Max(12, _p.FontSize * 1.6));
                graph.Width = double.NaN; // as wide as the tile
                graph.HorizontalAlignment = HorizontalAlignment.Stretch;
                graph.Margin = new Thickness(0, 3, 0, 0);
                tile.Children.Add(graph);
            }

            Grid.SetRow(tile, r);
            Grid.SetColumn(tile, c);
            grid.Children.Add(tile);
            _cells.Add(new Cell(item, label, value, fill, rest, front, graph));
        }
        return grid;
    }

    private Grid MakeBar(Brush labelBrush, out ColumnDefinition fill, out ColumnDefinition rest, out Border front)
    {
        double height = _p.BarHeight > 0 ? _p.BarHeight : Math.Max(3, _p.FontSize / 4);
        var track = new Grid { Height = height, ClipToBounds = true };
        fill = new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) };
        rest = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        track.ColumnDefinitions.Add(fill);
        track.ColumnDefinitions.Add(rest);

        var bgBrush = labelBrush.Clone();
        bgBrush.Opacity = 0.18;
        var radius = new CornerRadius(height / 2);
        var back = new Border { Background = bgBrush, CornerRadius = radius };
        Grid.SetColumnSpan(back, 2);
        front = new Border { CornerRadius = radius };
        track.Children.Add(back);
        track.Children.Add(front);
        return track;
    }

    private static int AddRow(Grid grid, UIElement element, bool span = false)
    {
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        int row = grid.RowDefinitions.Count - 1;
        Grid.SetRow(element, row);
        if (span) Grid.SetColumnSpan(element, 3);
        grid.Children.Add(element);
        return row;
    }

    private string GroupOf(OverlayItem item) =>
        OverlayExtras.NameOf(item.SensorId) != null ? OverlayExtras.GroupOf(item.SensorId)
        : _store.ById.TryGetValue(item.SensorId, out var s) ? s.GroupName : item.HardwareName;

    private static FontWeight Weight(OverlayWeight w) => w switch
    {
        OverlayWeight.Bold => FontWeights.Bold,
        OverlayWeight.SemiBold => FontWeights.SemiBold,
        _ => FontWeights.Normal,
    };

    private Brush BrushOf(string? hex, Color fallback)
    {
        string key = hex ?? "";
        if (_brushes.TryGetValue(key, out var b)) return b;
        return _brushes[key] = ColorUtil.Brush(hex, fallback);
    }

    // ---------- live values ----------

    public void UpdateValues()
    {
        foreach (var cell in _cells)
        {
            _store.ById.TryGetValue(cell.Item.SensorId, out var s);
            string? extra = OverlayExtras.NameOf(cell.Item.SensorId);

            if (cell.Label != null)
            {
                string label = cell.Item.Label ?? s?.OverlayLabel ?? extra ?? (_store.Loaded ? "?" : "…");
                if (cell.Label.Text != label) cell.Label.Text = label;
            }

            string value = extra != null ? OverlayExtras.Text(cell.Item.SensorId, _p.ClockSeconds)
                : s == null ? "-" : OverlayFormat.Value(s.Type, s.Value, cell.Item.Decimals, _p.ShowUnits);
            if (cell.Value.Text != value) cell.Value.Text = value;

            var own = cell.Item.ValueColor is { } c ? BrushOf(c, Colors.White) : _valueBrush;
            int level = !_p.WarnColors || s == null ? 0 : OverlayFormat.Level(s.Value, _p.Limits(cell.Item, s.Type));
            var brush = level switch { 2 => _hotBrush, 1 => _warmBrush, _ => own };
            if (!ReferenceEquals(cell.Value.Foreground, brush)) cell.Value.Foreground = brush;

            if (cell.BarFill != null && cell.BarRest != null)
            {
                double f = s?.BarFraction ?? 0;
                cell.BarFill.Width = new GridLength(f, GridUnitType.Star);
                cell.BarRest.Width = new GridLength(1 - f, GridUnitType.Star);
                if (cell.BarFront != null) cell.BarFront.Background = _p.BarColor == OverlayBarColor.Accent ? _accent : brush;
            }

            if (cell.Graph != null)
            {
                if (cell.Graph.PrimaryId != cell.Item.SensorId && s != null) cell.Graph.PrimaryId = cell.Item.SensorId;
                if (!ReferenceEquals(cell.Graph.Stroke, brush)) cell.Graph.Stroke = brush;
            }
        }
    }
}
