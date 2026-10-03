using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace OneC.Desktop.Controls;

/// <summary>What one infobase card shows. Built by the Infobases page from the cloud record.</summary>
public sealed record InfobaseCardModel(
    string Key, string Name, string Meta,
    Pill SyncKind, string SyncTitle, string SyncDetail, double? Progress,
    Pill HereKind, string HereTitle, string HereDetail,
    RowCommand? Primary, IReadOnlyList<RowCommand> Menu)
{
    /// <summary>Opens the base's sync screen: a link under the sync column. Null: no link.</summary>
    public RowCommand? SyncOpen { get; init; }
}

/// <summary>
/// One infobase of the chosen company, as a card: who it is (name, 1C base, software), how far its
/// sync is (words, rows, a bar while it runs), whether this computer has it, and what to do next.
/// A company has a handful of infobases, each with a few states — a card reads that at a glance,
/// where a wide table of five columns left an empty sheet. Updated in place: <see cref="Update"/>
/// rewrites only text and colours, so polling never re-creates it.
/// </summary>
public sealed class InfobaseCard : Grid
{
    private readonly FontIcon _icon = new() { Glyph = "", FontSize = 18 };
    private readonly Border _iconTile;
    private readonly TextBlock _name = Text(16, bold: true);
    private readonly TextBlock _meta = Text(14);
    private readonly TextBlock _syncTitle = Text(14, bold: true);
    private readonly TextBlock _syncDetail = Text(14);
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Margin = new Thickness(0, 6, 0, 0) };
    private readonly HyperlinkButton _syncOpen = new() { Padding = new Thickness(0, 2, 0, 0), FontSize = 14, Visibility = Visibility.Collapsed };
    private readonly TextBlock _hereTitle = Text(14, bold: true);
    private readonly TextBlock _hereDetail = Text(14);
    // Fixed width, right-aligned: cards with and without a main button keep their columns in line.
    private readonly StackPanel _actions = new()
    {
        Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Right
    };
    private InfobaseCardModel? _model;

    public string Key { get; }

    public InfobaseCard(string key)
    {
        Key = key;
        CornerRadius = new CornerRadius(8);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(16, 14, 12, 14);

        _iconTile = new Border
        {
            Width = 40, Height = 40, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center,
            Child = _icon
        };
        var who = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { _name, _meta } };
        var sync = new StackPanel { Spacing = 2, Width = 260, VerticalAlignment = VerticalAlignment.Center, Children = { _syncTitle, _syncDetail, _progress, _syncOpen } };
        _syncOpen.Click += (_, _) => _model?.SyncOpen?.Run();
        var here = new StackPanel { Spacing = 2, Width = 210, VerticalAlignment = VerticalAlignment.Center, Children = { _hereTitle, _hereDetail } };

        var g = new Grid { ColumnSpacing = 16 };
        foreach (var w in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, new GridLength(160) })
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        Place(g, _iconTile, 0);
        Place(g, who, 1);
        Place(g, sync, 2);
        Place(g, here, 3);
        Place(g, _actions, 4);
        Children.Add(g);

        ActualThemeChanged += (_, _) => { if (_model is { } m) Paint(m); };
    }

    private static void Place(Grid g, FrameworkElement e, int column)
    {
        Grid.SetColumn(e, column);
        g.Children.Add(e);
    }

    private static TextBlock Text(double size, bool bold = false) => new()
    {
        FontSize = size,
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap
    };

    public void Update(InfobaseCardModel m)
    {
        bool actionsChanged = _model is null || _model.Primary?.Label != m.Primary?.Label ||
                              !_model.Menu.Select(x => x.Label).SequenceEqual(m.Menu.Select(x => x.Label));
        _model = m;
        Set(_name, m.Name);
        Set(_meta, m.Meta);
        Set(_syncDetail, m.SyncDetail);
        Set(_hereDetail, m.HereDetail);
        _progress.Visibility = m.Progress is null ? Visibility.Collapsed : Visibility.Visible;
        if (m.Progress is { } p) _progress.Value = p;
        _syncOpen.Visibility = m.SyncOpen is null ? Visibility.Collapsed : Visibility.Visible;
        if (m.SyncOpen is { } open && !Equals(_syncOpen.Content, open.Label))
        {
            _syncOpen.Content = open.Label;
            AutomationProperties.SetName(_syncOpen, $"{open.Label}: {m.Name}");
        }
        Paint(m);
        if (actionsChanged) BuildActions(m);
        AutomationProperties.SetName(this, $"{m.Name}: {m.SyncTitle}, {m.HereTitle}");
    }

    private static void Set(TextBlock t, string text)
    {
        if (t.Text == text) return;
        t.Text = text;
        ToolTipService.SetToolTip(t, text.Length > 0 ? text : null);
    }

    /// <summary>Colours follow this card's theme: status words carry their state's ink, with a dot.</summary>
    private void Paint(InfobaseCardModel m)
    {
        Background = ThemeBrushes.Get(this, "PanelBackgroundBrush");
        BorderBrush = ThemeBrushes.Get(this, "PanelBorderBrush");
        _iconTile.Background = ThemeBrushes.Get(this, "InfoPillBrush");
        _icon.Foreground = ThemeBrushes.Get(this, "InfoBrush");
        var muted = ThemeBrushes.Get(this, "TextFillColorSecondaryBrush");
        _meta.Foreground = _syncDetail.Foreground = _hereDetail.Foreground = muted;
        Status(_syncTitle, m.SyncKind, m.SyncTitle);
        Status(_hereTitle, m.HereKind, m.HereTitle);
    }

    private void Status(TextBlock t, Pill kind, string text)
    {
        t.Foreground = kind == Pill.Neutral ? ThemeBrushes.Get(this, "TextFillColorSecondaryBrush") : ThemeBrushes.Get(this, kind + "Brush");
        t.Inlines.Clear();
        t.Inlines.Add(new Run { Text = kind == Pill.Neutral ? "○  " : "●  ", FontSize = 10 });
        t.Inlines.Add(new Run { Text = text });
        ToolTipService.SetToolTip(t, text);
    }

    private void BuildActions(InfobaseCardModel m)
    {
        _actions.Children.Clear();
        if (m.Primary is { } p)
        {
            var b = new Button
            {
                Padding = new Thickness(12, 5, 14, 6), MinWidth = 96,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children = { new FontIcon { Glyph = p.Glyph, FontSize = 14 }, new TextBlock { Text = p.Label } }
                }
            };
            b.Click += (_, _) => _model?.Primary?.Run();
            AutomationProperties.SetName(b, p.Label);
            _actions.Children.Add(b);
        }
        if (m.Menu.Count == 0) return;
        var menu = new MenuFlyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight,
            ShouldConstrainToRootBounds = true,
            MenuFlyoutPresenterStyle = (Style)Application.Current.Resources["SolidMenuPresenterStyle"]
        };
        for (int i = 0; i < m.Menu.Count; i++)
        {
            int at = i;
            var cmd = m.Menu[i];
            var item = new MenuFlyoutItem { Text = cmd.Label, Icon = new FontIcon { Glyph = cmd.Glyph } };
            if (cmd.Destructive) item.Foreground = ThemeBrushes.Get(this, "BadBrush");
            item.Click += (_, _) => _model?.Menu.ElementAtOrDefault(at)?.Run();     // the current model's action
            menu.Items.Add(item);
        }
        var more = new Button
        {
            Width = 36, Height = 32, Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
            Content = new FontIcon { Glyph = "", FontSize = 16 }, Flyout = menu
        };
        ToolTipService.SetToolTip(more, "More actions");
        AutomationProperties.SetName(more, "More actions");
        _actions.Children.Add(more);
    }
}
