using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OneC.Desktop.Services;

namespace OneC.Desktop.Views;

public sealed partial class BrowsePage : Page
{
    // Common starting points; any valid 1C table name can be typed.
    private static readonly string[] KnownTables =
    {
        "Справочник.Номенклатура", "Справочник.Контрагенты", "Справочник.Организации",
        "Справочник.Склады", "Справочник.БанковскиеСчета", "Справочник.ДоговорыКонтрагентов",
        "Документ.ПоступлениеТоваровУслуг", "Документ.РеализацияТоваровУслуг",
        "Документ.СписаниеСРасчетногоСчета", "Документ.ПоступлениеНаРасчетныйСчет",
        "Документ.ОтчетОРозничныхПродажах", "РегистрБухгалтерии.Хозрасчетный"
    };

    private static readonly Dictionary<string, string> DefaultFields = new()
    {
        ["Справочник"] = "Наименование, Код",
        ["Документ"] = "Номер, Дата, Проведен",
        ["РегистрБухгалтерии"] = "Период, Регистратор, Сумма"
    };

    private CancellationTokenSource? _cts;

    public BrowsePage()
    {
        InitializeComponent();
        BaseBox.ItemsSource = App.Bases.Bases.Select(b => b.Name).ToList();
        if (App.Bases.Bases.Count > 0) BaseBox.SelectedIndex = 0;
        EntityBox.Text = App.Arg("entity") ?? KnownTables[0];
        FieldsBox.Text = App.Arg("fields") ?? DefaultFields["Справочник"];
        if (App.Arg("base") is string b && App.Bases.Bases.Any(x => x.Name == b)) BaseBox.SelectedItem = b;

        // --autorun: run the query once the engine is up (screenshots, smoke checks).
        if (Environment.GetCommandLineArgs().Contains("--autorun"))
            Loaded += async (_, _) =>
            {
                for (int i = 0; i < 240 && App.Supervisor.State != SupervisorState.Running; i++) await Task.Delay(250);
                Run_Click(this, new RoutedEventArgs());
            };
    }

    /// <summary>Opened for one base (an infobase card's Browse): start on it.</summary>
    protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string b && App.Bases.Bases.Any(x => x.Name == b)) BaseBox.SelectedItem = b;
    }

    private void Entity_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        sender.ItemsSource = KnownTables.Where(t => t.Contains(sender.Text, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void Entity_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        string table = (string)args.SelectedItem;
        string kind = table.Split('.')[0];
        if (DefaultFields.TryGetValue(kind, out var f)) FieldsBox.Text = f;
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (App.Supervisor.Client is not { } client)
        {
            Show(InfoBarSeverity.Warning, "The 1C engine is not running yet. Check the status bar.");
            return;
        }
        if (BaseBox.SelectedItem is not string baseName) { Show(InfoBarSeverity.Warning, "Choose an infobase."); return; }

        // Empty Columns = the usual columns for that kind of table (what the grey hint suggests).
        if (FieldsBox.Text.Trim().Length == 0 && DefaultFields.TryGetValue(EntityBox.Text.Split('.')[0].Trim(), out var usual))
            FieldsBox.Text = usual;
        var fields = FieldsBox.Text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (fields.Count == 0)
        {
            Table.Clear();                                     // no old rows under the warning
            Show(InfoBarSeverity.Warning, "Enter at least one column.");
            return;
        }
        string refs = RefsBox.SelectedIndex switch { 1 => "guid", 2 => "both", _ => "text" };
        int limit = double.IsNaN(LimitBox.Value) ? 100 : (int)LimitBox.Value;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        SetBusy(true);
        Message.IsOpen = false;
        Summary.Text = "Reading…";
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var r = await client.Read(baseName, EntityBox.Text.Trim(), fields, limit, refs, _cts.Token);
            var rows = r["rows"]!.AsArray();
            var data = new List<string?[]>(rows.Count);
            var numeric = new HashSet<int>(Enumerable.Range(0, fields.Count));
            foreach (var row in rows)
            {
                var cells = new string?[fields.Count];
                for (int i = 0; i < fields.Count; i++)
                {
                    var v = row![fields[i]];
                    cells[i] = Format(v);
                    if (v is not null && v.GetValueKind() != JsonValueKind.Number) numeric.Remove(i);
                }
                data.Add(cells);
            }
            Table.EmptyText = "The read returned no rows.";
            Table.SetData(fields, data, numeric);
            Summary.Text = $"{data.Count:N0} row{(data.Count == 1 ? "" : "s")} · {sw.ElapsedMilliseconds:N0} ms" +
                           (data.Count == limit ? " · limit reached, there may be more" : "");
        }
        catch (OperationCanceledException) { Summary.Text = "Stopped."; }
        // A failed read clears the table: rows left from the previous query would look like
        // the answer to this one.
        catch (EdgeException ex)
        {
            Failed();
            Show(InfoBarSeverity.Error, ex.Friendly);
        }
        catch (HttpRequestException ex)
        {
            Failed();
            Show(InfoBarSeverity.Error, "The engine is not reachable: " + ex.Message);
        }
        finally { SetBusy(false); }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private void Failed()
    {
        Summary.Text = "";
        Table.EmptyText = "No rows: the read failed.";
        Table.Clear();
    }

    /// <summary>Numbers in the user's culture, dates without the empty-date noise, links as "name (id)".</summary>
    private static string? Format(JsonNode? v)
    {
        if (v is null) return "";
        if (v is JsonObject o) return $"{o["text"]} ({o["ref"]})";
        switch (v.GetValueKind())
        {
            case JsonValueKind.Number:
                return v.GetValue<decimal>().ToString("#,0.##", CultureInfo.CurrentCulture);
            case JsonValueKind.True: return "Yes";
            case JsonValueKind.False: return "No";
            case JsonValueKind.String:
                string s = v.GetValue<string>();
                if (s.Length >= 19 && DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)
                    && s[4] == '-' && s[10] == 'T')
                    return d.Year <= 1 ? "" : d.TimeOfDay == TimeSpan.Zero ? d.ToString("d") : d.ToString("g");
                return s;
            default: return v.ToJsonString();
        }
    }

    private void SetBusy(bool busy)
    {
        Busy.IsActive = busy;
        RunButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
    }

    private void Show(InfoBarSeverity severity, string text)
    {
        Message.Severity = severity;
        Message.Message = text;
        Message.IsOpen = true;
    }
}
