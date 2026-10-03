using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace OneC.Desktop.Controls;

/// <summary>
/// The one table used by every screen. Built in code so the header and every row share the
/// exact same column widths — the WinUI prototype had header/row misalignment, no horizontal
/// scroll and silent truncation; this control exists so those bugs cannot come back.
///
///  - columns are sized once per data set from the header and a sample of values;
///  - the whole table scrolls horizontally, rows scroll (virtualized) vertically;
///  - every cell trims with an ellipsis and shows its full text as a tooltip;
///  - rows are zebra-striped with no row borders (house style);
///  - Ctrl+C copies the selected rows as tab-separated text (pastes into Excel);
///  - a refresh with the same columns patches the list instead of rebuilding it: new rows slide
///    in, gone rows slide out, a changed row has its cells rewritten in place, unchanged rows are
///    not touched (user: the 3 s polling made the whole list flicker).
/// </summary>
/// <summary>
/// Status colours (Theme.xaml: &lt;kind&gt;Brush for the ink, &lt;kind&gt;PillBrush for the fill).
/// Neutral is drawn without a fill: a column of grey "Not connected" pills is noise.
/// </summary>
public enum Pill { Ok, Warn, Bad, Info, Neutral }

/// <summary>
/// An action on one row. The first one not marked <see cref="MenuOnly"/> is a button on the row;
/// the others sit in the row's "…" menu (rare and destructive ones belong there).
/// </summary>
public sealed record RowCommand(string Label, string Glyph, Action Run, bool Destructive = false, bool MenuOnly = false);

public sealed class ResultTable : Grid
{
    private readonly ScrollViewer _scroll;
    private readonly Grid _inner;
    private readonly Border _headerBand;
    private readonly Grid _header;
    private readonly ListView _list;
    private readonly StackPanel _emptyPanel;
    private readonly FontIcon _emptyIcon;
    private readonly TextBlock _emptyTitle;
    private readonly TextBlock _empty;
    private readonly Button _emptyButton;

    private IReadOnlyList<string> _columns = Array.Empty<string>();
    private double[] _widths = Array.Empty<double>();   // what the content needs
    private double[] _eff = Array.Empty<double>();      // plus the viewport's spare width on the fill column
    private HashSet<int> _numeric = new();

    private const double RowHeight = 40;
    private const double RowInset = 4;                  // the Windows 11 list item margin, each side
    private const double CellPad = 14;
    private const double ActionsWidth = 156;            // "Connect" or "Test" + "…"

    /// <summary>The column that reads first (a name): semibold. -1 for none (data read from 1C).</summary>
    public int PrimaryColumn { get; set; } = -1;

    /// <summary>Secondary columns (types, paths, codes): muted, so the primary one stands out.</summary>
    public HashSet<int> MutedColumns { get; } = new();

    /// <summary>The column that takes the spare width, so rows span the panel. -1: the last one.</summary>
    public int FillColumn { get; set; } = -1;

    /// <summary>Row actions, shown on the row itself. Null: no actions column.</summary>
    public Func<string?[], IReadOnlyList<RowCommand>>? CommandsFor { get; set; }

    /// <summary>Empty state: an icon, a title, <see cref="EmptyText"/>, and an optional button.</summary>
    public string EmptyTitle
    {
        get => _emptyTitle.Text;
        set { _emptyTitle.Text = value; _emptyTitle.Visibility = value.Length > 0 ? Visibility.Visible : Visibility.Collapsed; }
    }

    public string EmptyGlyph
    {
        get => _emptyIcon.Glyph;
        set { _emptyIcon.Glyph = value; _emptyIcon.Visibility = value.Length > 0 ? Visibility.Visible : Visibility.Collapsed; }
    }

    private Action? _emptyRun;

    public void SetEmptyAction(string? label, Action? run)
    {
        _emptyRun = run;
        _emptyButton.Content = label;
        _emptyButton.Visibility = label is { Length: > 0 } && run is not null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>One list item. Stable across refreshes, so selection and scroll position survive.</summary>
    private sealed class Line
    {
        public string?[] Cells = Array.Empty<string?>();
    }
    private readonly ObservableCollection<Line> _items = new();

    public event EventHandler<int>? RowInvoked;
    public event EventHandler? SelectionChanged;
    public int SelectedIndex => _list.SelectedIndex;

    /// <summary>Selects one row (used by <c>--select</c> for screenshots and scripted checks).</summary>
    public void Select(int index)
    {
        if (index >= 0 && index < _items.Count) _list.SelectedIndex = index;
    }
    public IReadOnlyList<string?[]> Rows => _items.Select(l => l.Cells).ToList();

    /// <summary>
    /// What identifies a row across refreshes (e.g. the base name), so a changed row is updated in
    /// place. Without it a row is identified by its whole content: a change reads as gone + new.
    /// </summary>
    public Func<string?[], string>? RowKey { get; set; }

    public string EmptyText { get => _empty.Text; set => _empty.Text = value; }

    public ResultTable()
    {
        // The header lines up with the rows, which the list insets by RowInset on each side.
        _header = new Grid { Height = RowHeight, Margin = new Thickness(RowInset, 0, RowInset, 0) };
        // Hidden until there are columns: no empty band above a "nothing read yet" message.
        _headerBand = new Border { Child = _header, BorderThickness = new Thickness(0, 0, 0, 1), Visibility = Visibility.Collapsed };

        _list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Extended,
            IsItemClickEnabled = true,
            ItemTemplate = (DataTemplate)XamlReader.Load(
                $"<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid Height='{RowHeight}'/></DataTemplate>"),
            ItemContainerStyle = RowStyle()
        };
        _list.ContainerContentChanging += OnContainerContentChanging;
        _list.ItemClick += (_, e) => RowInvoked?.Invoke(this, _items.IndexOf((Line)e.ClickedItem));
        _list.KeyDown += OnKeyDown;
        _list.SelectionChanged += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);

        // Left-aligned and at least as wide as the viewport: a narrow table must not float in
        // the middle of the panel, and the header band / zebra rows must span the full width.
        _inner = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        _inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _inner.Children.Add(_headerBand);
        SetRow(_list, 1);
        _inner.Children.Add(_list);

        // Horizontal scrolling for the whole table; vertical scrolling stays in the ListView
        // so row virtualization keeps working for 100 000-row reads.
        _scroll = new ScrollViewer
        {
            HorizontalScrollMode = ScrollMode.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollMode = ScrollMode.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _inner
        };

        _emptyIcon = new FontIcon { Glyph = "", FontSize = 32, Margin = new Thickness(0, 0, 0, 4), Visibility = Visibility.Collapsed };
        _emptyTitle = new TextBlock
        {
            FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed
        };
        _empty = new TextBlock { Text = "No data", FontSize = 14, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
        _emptyButton = new Button
        {
            Style = (Style)Application.Current.Resources["AccentButtonStyle"],
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed
        };
        _emptyButton.Click += (_, _) => _emptyRun?.Invoke();
        _emptyPanel = new StackPanel
        {
            Spacing = 6, MaxWidth = 420, Margin = new Thickness(24),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Children = { _emptyIcon, _emptyTitle, _empty, _emptyButton }
        };

        Children.Add(_scroll);
        Children.Add(_emptyPanel);
        _scroll.SizeChanged += (_, _) =>
        {
            _inner.MinWidth = _scroll.ViewportWidth;
            ApplyWidths();
        };

        // Brushes follow THIS element's theme, and follow it again if it changes.
        Loaded += (_, _) => ApplyThemeBrushes();
        ActualThemeChanged += (_, _) => ApplyThemeBrushes();
    }

    private Brush? _zebra;

    private void ApplyThemeBrushes()
    {
        _headerBand.Background = ThemeBrushes.Get(this, "TableHeaderBrush");
        _headerBand.BorderBrush = ThemeBrushes.Get(this, "DividerBrush");
        _empty.Foreground = ThemeBrushes.Get(this, "TextFillColorSecondaryBrush");
        _emptyIcon.Foreground = ThemeBrushes.Get(this, "TextFillColorTertiaryBrush");
        _zebra = ThemeBrushes.Get(this, "SubtleRowBrush");
        // Re-realize rows with the new stripe; a theme switch must not drop the selected row.
        int sel = _list.SelectedIndex;
        _list.ItemsSource = null;
        _list.ItemsSource = _items;
        if (sel >= 0 && sel < _items.Count) _list.SelectedIndex = sel;
    }

    private static Style RowStyle()
    {
        var s = new Style(typeof(ListViewItem));
        s.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        s.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, RowHeight));
        s.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        s.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Stretch));
        return s;
    }

    /// <summary>
    /// Replaces the data. <paramref name="numericColumns"/> are right-aligned (amounts).
    /// Keeps the selection by index when the shape is unchanged — used by auto-refreshing
    /// screens so a refresh does not throw the user's place away.
    /// </summary>
    /// <param name="remeasure">With <paramref name="keepLayout"/>: size the columns to this data afresh
    /// (a new filter or search) instead of only letting them grow (polling); rows are still patched.</param>
    public void SetData(IReadOnlyList<string> columns, IReadOnlyList<string?[]> rows,
                        IEnumerable<int>? numericColumns = null, bool keepLayout = false, bool remeasure = false)
    {
        bool sameShape = keepLayout && columns.SequenceEqual(_columns);
        var numeric = numericColumns?.ToHashSet() ?? new HashSet<int>();

        // Re-measure every time; on a refresh of the same shape only let columns grow, so
        // live-updating screens neither truncate new, longer values nor jitter.
        var measured = MeasureWidths(columns, rows);
        // A pill carries a dot, padding and a margin: ~34 px more than its text.
        _pillColumns = new HashSet<int>();
        if (PillFor is { } pillFor)
            for (int c = 0; c < measured.Length; c++)
                if (rows.Take(300).Any(r => c < r.Length && r[c] is { Length: > 0 } s && pillFor(c, s) is not null))
                {
                    // Capped: a long status (an error message) trims in its pill; the tooltip has it all.
                    measured[c] = Math.Min(measured[c] + 34, 300);
                    _pillColumns.Add(c);
                }
        // Semibold is wider.
        if (PrimaryColumn >= 0 && PrimaryColumn < measured.Length) measured[PrimaryColumn] = Math.Min(measured[PrimaryColumn] * 1.08, 520);
        var widths = sameShape && !remeasure && _widths.Length == measured.Length
            ? measured.Select((w, i) => Math.Max(w, _widths[i])).ToArray()
            : measured;

        // Same columns: new widths are applied in place and the rows patched. New columns (or a
        // new data set, keepLayout false): lay out again.
        bool relayout = !sameShape || !numeric.SetEquals(_numeric);
        bool resized = !widths.SequenceEqual(_widths);
        _columns = columns;
        _widths = widths;
        _numeric = numeric;
        if (!relayout)
        {
            if (resized) ApplyWidths();
            Patch(rows);
        }
        else
        {
            int keepSel = sameShape ? _list.SelectedIndex : -1;
            _eff = Effective();
            BuildHeader();
            // No columns (cleared, failed read, engine off): no empty header band above the message.
            _headerBand.Visibility = columns.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            _inner.Width = TotalWidth();
            _list.ItemsSource = null;                         // one reset, not one event per row
            _items.Clear();
            foreach (var r in rows) _items.Add(new Line { Cells = r });
            _list.ItemsSource = _items;
            if (keepSel >= 0 && keepSel < _items.Count) _list.SelectedIndex = keepSel;
        }
        _emptyPanel.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private double ActionsW => CommandsFor is null ? 0 : ActionsWidth;
    private double TotalWidth() => _eff.Sum() + ActionsW + 2 * RowInset;

    /// <summary>
    /// Content widths fitted to the viewport. Spare width goes to the fill column. When the viewport
    /// is too narrow and the table has row actions, text columns give way instead — the widest
    /// secondary ones first, the primary (name) column last, each down to a floor (the text trims,
    /// its tooltip keeps it whole) — so the actions never slide out of sight behind a horizontal
    /// scroll. Status (pill) columns keep their width. Without actions the table scrolls sideways.
    /// </summary>
    private double[] Effective()
    {
        var e = (double[])_widths.Clone();
        if (e.Length == 0 || _scroll.ViewportWidth <= 0) return e;
        int f = FillColumn >= 0 && FillColumn < e.Length ? FillColumn : e.Length - 1;
        double spare = _scroll.ViewportWidth - 2 * RowInset - e.Sum() - ActionsW;
        if (spare > 0) { e[f] += Math.Floor(spare); return e; }
        if (CommandsFor is null) return e;

        double deficit = -spare;
        double Floor(int c) => c == PrimaryColumn ? 170 : 110;
        foreach (bool primaryPass in new[] { false, true })
            while (deficit > 0.5)
            {
                int widest = -1;
                for (int c = 0; c < e.Length; c++)
                    if ((c == PrimaryColumn) == primaryPass && !_pillColumns.Contains(c) && e[c] > Floor(c) &&
                        (widest < 0 || e[c] > e[widest])) widest = c;
                if (widest < 0) break;                       // nothing left in this pass
                double take = Math.Min(deficit, e[widest] - Floor(widest));
                e[widest] -= take;
                deficit -= take;
            }
        for (int c = 0; c < e.Length; c++) e[c] = Math.Floor(e[c]);
        return e;
    }

    /// <summary>Columns that hold status pills in the current data; they keep their width.</summary>
    private HashSet<int> _pillColumns = new();

    /// <summary>The window changed width: the fill column takes the difference, in place — no rebuild.</summary>
    private void ApplyWidths()
    {
        var eff = Effective();
        if (eff.SequenceEqual(_eff)) return;
        _eff = eff;
        for (int c = 0; c < _eff.Length && c < _header.ColumnDefinitions.Count; c++)
            _header.ColumnDefinitions[c].Width = new GridLength(_eff[c]);
        _inner.Width = TotalWidth();
        foreach (var g in RealizedRows().Select(x => x.Grid))
            for (int c = 0; c < _eff.Length && c < g.ColumnDefinitions.Count; c++)
                g.ColumnDefinitions[c].Width = new GridLength(_eff[c]);
    }

    /// <summary>The rows that exist on screen right now (the list virtualizes the rest).</summary>
    private IEnumerable<(int Index, Grid Grid)> RealizedRows()
    {
        foreach (var child in _list.ItemsPanelRoot?.Children ?? (IEnumerable<UIElement>)Array.Empty<UIElement>())
            if (child is ListViewItem { ContentTemplateRoot: Grid g } c && _list.IndexFromContainer(c) is var i and >= 0)
                yield return (i, g);
    }

    /// <summary>
    /// Brings the list to <paramref name="rows"/> with the fewest changes: gone rows removed, new
    /// ones inserted, moved ones moved (each animated by the list), changed ones rewritten in
    /// place without re-creating their row. Unchanged rows are not touched at all.
    /// </summary>
    private void Patch(IReadOnlyList<string?[]> rows)
    {
        string Key(string?[] r) => RowKey?.Invoke(r) ?? string.Join('\u001f', r);
        var keys = rows.Select(Key).ToList();
        var wanted = keys.ToHashSet();
        bool shifted = false;

        for (int i = _items.Count - 1; i >= 0; i--)
            if (!wanted.Contains(Key(_items[i].Cells))) { _items.RemoveAt(i); shifted = true; }

        for (int i = 0; i < rows.Count; i++)
        {
            if (i >= _items.Count || Key(_items[i].Cells) != keys[i])
            {
                int j = -1;
                for (int k = i + 1; k < _items.Count; k++)
                    if (Key(_items[k].Cells) == keys[i]) { j = k; break; }
                if (j >= 0) _items.Move(j, i);
                else _items.Insert(i, new Line { Cells = rows[i] });
                shifted = true;
            }
            var line = _items[i];
            if (line.Cells.SequenceEqual(rows[i])) continue;
            line.Cells = rows[i];
            // Realized rows are rewritten directly; the others are filled when they scroll in.
            if (_list.ContainerFromItem(line) is ListViewItem { ContentTemplateRoot: Grid g }) Fill(g, line.Cells);
        }
        while (_items.Count > rows.Count) { _items.RemoveAt(_items.Count - 1); shifted = true; }

        // Rows below an insert or removal moved by one: fix their stripe.
        if (shifted)
            foreach (var (i, g) in RealizedRows())
                g.Background = i % 2 == 1 ? _zebra ??= ThemeBrushes.Get(this, "SubtleRowBrush") : null;
    }

    public void Clear() => SetData(Array.Empty<string>(), Array.Empty<string?[]>());

    /// <summary>~7.8 px per character at 14 px Segoe UI, plus the cell padding; header and first 300 rows.</summary>
    private static double[] MeasureWidths(IReadOnlyList<string> columns, IReadOnlyList<string?[]> rows)
    {
        var w = new double[columns.Count];
        for (int c = 0; c < columns.Count; c++)
        {
            int chars = columns[c].Length + 1;
            for (int r = 0; r < Math.Min(rows.Count, 300); r++)
                chars = Math.Max(chars, rows[r].Length > c ? rows[r][c]?.Length ?? 0 : 0);
            w[c] = Math.Clamp(chars * 7.8 + 2 * CellPad, 80, 480);
        }
        return w;
    }

    private void BuildHeader()
    {
        _header.Children.Clear();
        _header.ColumnDefinitions.Clear();
        for (int c = 0; c < _columns.Count; c++)
        {
            _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_eff[c]) });
            var t = new TextBlock
            {
                Text = _columns[c],
                Style = (Style)Application.Current.Resources["TableHeaderTextStyle"],
                Margin = new Thickness(CellPad, 0, CellPad, 0),
                TextAlignment = _numeric.Contains(c) ? TextAlignment.Right : TextAlignment.Left
            };
            ToolTipService.SetToolTip(t, _columns[c]);
            SetColumn(t, c);
            _header.Children.Add(t);
        }
        if (CommandsFor is not null) _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ActionsW) });
    }

    /// <summary>Fills a recycled row container. Cells are reused, never rebuilt per scroll.</summary>
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.ItemContainer.ContentTemplateRoot is not Grid g) return;
        Fill(g, ((Line)args.Item).Cells);
        g.Background = args.ItemIndex % 2 == 1 ? _zebra ??= ThemeBrushes.Get(this, "SubtleRowBrush") : null;
        args.Handled = true;
    }

    /// <summary>Writes one row's cells (and its action buttons) into its (possibly recycled) grid.</summary>
    private void Fill(Grid g, string?[] row)
    {
        int n = _columns.Count + (CommandsFor is null ? 0 : 1);
        if (g.ColumnDefinitions.Count != n || !WidthsMatch(g))
        {
            g.Children.Clear();
            g.ColumnDefinitions.Clear();
            for (int c = 0; c < _columns.Count; c++)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_eff[c]) });
                // Each cell is a host border around its text, so a status cell can become a pill.
                var t = new TextBlock
                {
                    Style = (Style)Application.Current.Resources["CellTextStyle"],
                    TextAlignment = _numeric.Contains(c) ? TextAlignment.Right : TextAlignment.Left,
                    FontWeight = c == PrimaryColumn ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                    IsTextSelectionEnabled = false
                };
                var host = new Border { Child = t };
                SetColumn(host, c);
                g.Children.Add(host);
            }
            if (CommandsFor is not null)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ActionsW) });
                var actions = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0)
                };
                SetColumn(actions, _columns.Count);
                g.Children.Add(actions);
            }
        }

        for (int c = 0; c < _columns.Count; c++)
        {
            var host = (Border)g.Children[c];
            var t = (TextBlock)host.Child;
            string text = c < row.Length ? row[c] ?? "" : "";
            if (text.Length > 0 && PillFor?.Invoke(c, text) is { } pill) ShowPill(host, t, text, pill);
            else ShowText(host, t, text, MutedColumns.Contains(c));
            ToolTipService.SetToolTip(t, string.IsNullOrEmpty(text) ? null : text);
        }
        if (CommandsFor is not null) ShowCommands((StackPanel)g.Children[_columns.Count], CommandsFor(row));
    }

    /// <summary>One command as a button on the row, the others in a "…" menu.</summary>
    private void ShowCommands(StackPanel panel, IReadOnlyList<RowCommand> commands)
    {
        panel.Children.Clear();
        var inline = commands.FirstOrDefault(c => !c.MenuOnly);
        if (inline is not null)
        {
            var button = new Button
            {
                Padding = new Thickness(10, 4, 12, 5), MinHeight = 0, FontSize = 14,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 6,
                    Children = { new FontIcon { Glyph = inline.Glyph, FontSize = 14 }, new TextBlock { Text = inline.Label } }
                }
            };
            button.Click += (_, _) => inline.Run();
            AutomationProperties.SetName(button, inline.Label);
            panel.Children.Add(button);
        }
        var rest = commands.Where(c => c != inline).ToList();
        if (rest.Count == 0) return;

        var menu = new MenuFlyout
        {
            Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight,
            ShouldConstrainToRootBounds = true,
            MenuFlyoutPresenterStyle = (Style)Application.Current.Resources["SolidMenuPresenterStyle"]
        };
        foreach (var cmd in rest)
        {
            var item = new MenuFlyoutItem { Text = cmd.Label, Icon = new FontIcon { Glyph = cmd.Glyph } };
            if (cmd.Destructive) item.Foreground = ThemeBrushes.Get(this, "BadBrush");
            item.Click += (_, _) => cmd.Run();
            menu.Items.Add(item);
        }
        var more = new Button
        {
            Width = 34, Height = 30, Padding = new Thickness(0), MinHeight = 0,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), BorderThickness = new Thickness(0),
            Content = new FontIcon { Glyph = "", FontSize = 16 }, Flyout = menu        // "More"
        };
        ToolTipService.SetToolTip(more, "More actions");
        AutomationProperties.SetName(more, "More actions");
        panel.Children.Add(more);
    }

    /// <summary>
    /// Which cells are statuses, and how they read: column index + cell text → pill kind, or
    /// null for plain text. Statuses as coloured pills, not grey words (house UI rules).
    /// </summary>
    public Func<int, string, Pill?>? PillFor { get; set; }

    private void ShowText(Border host, TextBlock t, string text, bool muted)
    {
        host.Background = null;
        host.Padding = new Thickness(0);
        host.Margin = new Thickness(0);
        host.CornerRadius = new CornerRadius(0);
        host.HorizontalAlignment = HorizontalAlignment.Stretch;
        host.VerticalAlignment = VerticalAlignment.Stretch;
        t.Margin = new Thickness(CellPad, 0, CellPad, 0);
        if (muted) t.Foreground = ThemeBrushes.Get(this, "TextFillColorSecondaryBrush");
        else t.ClearValue(TextBlock.ForegroundProperty);
        t.Text = text;
    }

    private void ShowPill(Border host, TextBlock t, string text, Pill pill)
    {
        // Neutral is a state, not news: its dot and words only, no fill.
        bool quiet = pill == Pill.Neutral;
        host.Background = quiet ? null : ThemeBrushes.Get(this, pill + "PillBrush");
        host.Padding = quiet ? new Thickness(0) : new Thickness(8, 3, 10, 4);
        host.Margin = quiet ? new Thickness(CellPad, 0, CellPad, 0) : new Thickness(CellPad - 8, 0, 8, 0);
        host.CornerRadius = new CornerRadius(12);
        host.HorizontalAlignment = HorizontalAlignment.Left;
        host.VerticalAlignment = VerticalAlignment.Center;
        t.Margin = new Thickness(0);
        t.Foreground = ThemeBrushes.Get(this, pill + "Brush");
        t.Inlines.Clear();
        t.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = quiet ? "○  " : "●  ", FontSize = 9 });
        t.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = text });
    }

    private bool WidthsMatch(Grid g)
    {
        for (int c = 0; c < _eff.Length && c < g.ColumnDefinitions.Count; c++)
            if (Math.Abs(g.ColumnDefinitions[c].Width.Value - _eff[c]) > 0.5) return false;
        return true;
    }

    private void OnKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                        .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!ctrl || e.Key != Windows.System.VirtualKey.C || _list.SelectedItems.Count == 0) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Join('\t', _columns));
        foreach (var item in _list.SelectedItems.Cast<Line>())
            sb.AppendLine(string.Join('\t', item.Cells.Select(v => (v ?? "").Replace('\t', ' ').Replace('\n', ' '))));
        var dp = new DataPackage();
        dp.SetText(sb.ToString());
        Clipboard.SetContent(dp);
        e.Handled = true;
    }
}
