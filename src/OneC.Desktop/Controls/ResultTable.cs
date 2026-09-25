using Microsoft.UI.Xaml;
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
///  - Ctrl+C copies the selected rows as tab-separated text (pastes into Excel).
/// </summary>
public sealed class ResultTable : Grid
{
    private readonly ScrollViewer _scroll;
    private readonly Grid _inner;
    private readonly Grid _header;
    private readonly ListView _list;
    private readonly TextBlock _empty;

    private IReadOnlyList<string> _columns = Array.Empty<string>();
    private double[] _widths = Array.Empty<double>();
    private IReadOnlyList<string?[]> _rows = Array.Empty<string?[]>();
    private readonly HashSet<int> _numeric = new();

    public event EventHandler<int>? RowInvoked;
    public event EventHandler? SelectionChanged;
    public int SelectedIndex => _list.SelectedIndex;
    public IReadOnlyList<string?[]> Rows => _rows;

    public string EmptyText { get => _empty.Text; set => _empty.Text = value; }

    public ResultTable()
    {
        _header = new Grid { Height = 32 };

        _list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Extended,
            IsItemClickEnabled = true,
            ItemTemplate = (DataTemplate)XamlReader.Load(
                "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'><Grid Height='30'/></DataTemplate>"),
            ItemContainerStyle = RowStyle()
        };
        _list.ContainerContentChanging += OnContainerContentChanging;
        _list.ItemClick += (_, e) => RowInvoked?.Invoke(this, _rows is List<string?[]> l ? l.IndexOf((string?[])e.ClickedItem) : -1);
        _list.KeyDown += OnKeyDown;
        _list.SelectionChanged += (_, _) => SelectionChanged?.Invoke(this, EventArgs.Empty);

        // Left-aligned and at least as wide as the viewport: a narrow table must not float in
        // the middle of the panel, and the header band / zebra rows must span the full width.
        _inner = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        _inner.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _inner.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _inner.Children.Add(_header);
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

        _empty = new TextBlock
        {
            Text = "No data",
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(24),
            Visibility = Visibility.Visible
        };

        Children.Add(_scroll);
        Children.Add(_empty);
        _scroll.SizeChanged += (_, _) => _inner.MinWidth = _scroll.ViewportWidth;

        // Brushes follow THIS element's theme, and follow it again if it changes.
        Loaded += (_, _) => ApplyThemeBrushes();
        ActualThemeChanged += (_, _) => ApplyThemeBrushes();
    }

    private Brush? _zebra;

    private void ApplyThemeBrushes()
    {
        _header.Background = ThemeBrushes.Get(this, "TableHeaderBrush");
        _empty.Foreground = ThemeBrushes.Get(this, "TextFillColorSecondaryBrush");
        _zebra = ThemeBrushes.Get(this, "SubtleRowBrush");
        if (_rows.Count > 0) _list.ItemsSource = null;          // re-realize rows with the new stripe
        _list.ItemsSource = _rows.Count > 0 ? _rows : null;
    }

    private static Style RowStyle()
    {
        var s = new Style(typeof(ListViewItem));
        s.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        s.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 30.0));
        s.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        s.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Stretch));
        return s;
    }

    /// <summary>
    /// Replaces the data. <paramref name="numericColumns"/> are right-aligned (amounts).
    /// Keeps the selection by index when the shape is unchanged — used by auto-refreshing
    /// screens so a refresh does not throw the user's place away.
    /// </summary>
    public void SetData(IReadOnlyList<string> columns, IReadOnlyList<string?[]> rows,
                        IEnumerable<int>? numericColumns = null, bool keepLayout = false)
    {
        bool sameShape = keepLayout && columns.SequenceEqual(_columns);
        int keepSel = sameShape ? _list.SelectedIndex : -1;

        _columns = columns;
        _rows = rows is List<string?[]> ? rows : rows.ToList();
        _numeric.Clear();
        if (numericColumns is not null) foreach (var i in numericColumns) _numeric.Add(i);

        // Re-measure every time; on a refresh of the same shape only let columns grow, so
        // live-updating screens neither truncate new, longer values nor jitter.
        var measured = MeasureWidths(columns, _rows);
        _widths = sameShape && _widths.Length == measured.Length
            ? measured.Select((w, i) => Math.Max(w, _widths[i])).ToArray()
            : measured;
        BuildHeader();
        // No columns (cleared, failed read, engine off): no empty header band above the message.
        _header.Visibility = columns.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _inner.Width = _widths.Sum() + 16;

        _list.ItemsSource = _rows;
        if (keepSel >= 0 && keepSel < _rows.Count) _list.SelectedIndex = keepSel;
        _empty.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Clear() => SetData(Array.Empty<string>(), Array.Empty<string?[]>());

    /// <summary>~7.2 px per character at 13 px Segoe UI, from the header and the first 300 rows.</summary>
    private static double[] MeasureWidths(IReadOnlyList<string> columns, IReadOnlyList<string?[]> rows)
    {
        var w = new double[columns.Count];
        for (int c = 0; c < columns.Count; c++)
        {
            int chars = columns[c].Length + 2;
            for (int r = 0; r < Math.Min(rows.Count, 300); r++)
                chars = Math.Max(chars, rows[r].Length > c ? rows[r][c]?.Length ?? 0 : 0);
            w[c] = Math.Clamp(chars * 7.2 + 24, 72, 460);
        }
        return w;
    }

    private void BuildHeader()
    {
        _header.Children.Clear();
        _header.ColumnDefinitions.Clear();
        for (int c = 0; c < _columns.Count; c++)
        {
            _header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_widths[c]) });
            var t = new TextBlock
            {
                Text = _columns[c],
                Style = (Style)Application.Current.Resources["TableHeaderTextStyle"],
                Margin = new Thickness(12, 0, 12, 0),
                TextAlignment = _numeric.Contains(c) ? TextAlignment.Right : TextAlignment.Left
            };
            ToolTipService.SetToolTip(t, _columns[c]);
            SetColumn(t, c);
            _header.Children.Add(t);
        }
    }

    /// <summary>Fills a recycled row container. Cells are reused, never rebuilt per scroll.</summary>
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.ItemContainer.ContentTemplateRoot is not Grid g) return;
        var row = (string?[])args.Item;

        if (g.ColumnDefinitions.Count != _columns.Count || !WidthsMatch(g))
        {
            g.Children.Clear();
            g.ColumnDefinitions.Clear();
            for (int c = 0; c < _columns.Count; c++)
            {
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(_widths[c]) });
                var t = new TextBlock
                {
                    Style = (Style)Application.Current.Resources["CellTextStyle"],
                    Margin = new Thickness(12, 0, 12, 0),
                    TextAlignment = _numeric.Contains(c) ? TextAlignment.Right : TextAlignment.Left,
                    IsTextSelectionEnabled = false
                };
                SetColumn(t, c);
                g.Children.Add(t);
            }
        }

        for (int c = 0; c < _columns.Count; c++)
        {
            var t = (TextBlock)g.Children[c];
            string text = c < row.Length ? row[c] ?? "" : "";
            t.Text = text;
            ToolTipService.SetToolTip(t, string.IsNullOrEmpty(text) ? null : text);
        }

        g.Background = args.ItemIndex % 2 == 1 ? _zebra ??= ThemeBrushes.Get(this, "SubtleRowBrush") : null;
        args.Handled = true;
    }

    private bool WidthsMatch(Grid g)
    {
        for (int c = 0; c < _widths.Length; c++)
            if (Math.Abs(g.ColumnDefinitions[c].Width.Value - _widths[c]) > 0.5) return false;
        return true;
    }

    private void OnKeyDown(object sender, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
    {
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
                        .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (!ctrl || e.Key != Windows.System.VirtualKey.C || _list.SelectedItems.Count == 0) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Join('\t', _columns));
        foreach (var item in _list.SelectedItems.Cast<string?[]>())
            sb.AppendLine(string.Join('\t', item.Select(v => (v ?? "").Replace('\t', ' ').Replace('\n', ' '))));
        var dp = new DataPackage();
        dp.SetText(sb.ToString());
        Clipboard.SetContent(dp);
        e.Handled = true;
    }
}
