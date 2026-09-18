using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace AibaShell.Services;

public sealed class MockOneCService : IOneCService, ITickable
{
    private readonly Random _rng = new(1337);
    private readonly ILogService _log;

    public ObservableCollection<InfoBase> InfoBases { get; } = new();
    public ObservableCollection<DocumentRow> Documents { get; } = new();
    public ObservableCollection<ReportRow> Reports { get; } = new();

    public MockOneCService(ILogService log)
    {
        _log = log;
        Seed();
    }

    private void Seed()
    {
        Add(new InfoBase
        {
            Name = "signum",
            Kind = "Server",
            Server = "win-11-2070",
            Database = "signum_prod",
            Organization = "SIGNUM SAVDO MCHJ",
            OneCVersion = "8.3.24.1548",
            ComVersion = "8.3.24",
            State = "Syncing",
            Status = StatusKind.Busy,
            ComState = "Connected",
            ComStatus = StatusKind.Healthy,
            Worker = "oscript-1",
            WorkerPid = 18244,
            WorkerPort = 55899,
            WorkerMemoryMb = 412,
            RowsPerSecond = 118,
            RowsRead = 2_481_390,
            Progress = 62,
            CurrentObject = "AccumulationRegister.RoznichnayaVyruchka",
            RequestsServed = 12_884,
            QueueDepth = 2,
            WorkerUptime = TimeSpan.FromMinutes(94)
        });

        Add(new InfoBase
        {
            Name = "Kanstik",
            Kind = "Server",
            Server = "win-11-2070",
            Database = "kanstik",
            Organization = "KANSTIK SAVDO",
            OneCVersion = "8.3.15.1830",
            ComVersion = "8.3.15",
            State = "Connected",
            Status = StatusKind.Healthy,
            ComState = "Connected",
            Worker = "oscript-2",
            WorkerPid = 18310,
            WorkerPort = 55901,
            WorkerMemoryMb = 268,
            RowsRead = 18_402_775,
            Progress = 100,
            CurrentObject = "idle",
            RequestsServed = 61_005,
            QueueDepth = 0,
            WorkerUptime = TimeSpan.FromHours(7)
        });

        Add(new InfoBase
        {
            Name = "MIREL",
            Kind = "File",
            Database = @"D:\1C\bases\MIREL",
            Organization = "MIREL TRADE",
            OneCVersion = "8.3.22.2411",
            ComVersion = "8.3.22",
            State = "Connecting",
            Status = StatusKind.Busy,
            ComState = "Cold start",
            ComStatus = StatusKind.Warning,
            Worker = "oscript-3",
            WorkerPid = 18422,
            WorkerPort = 55902,
            WorkerMemoryMb = 96,
            CurrentObject = "opening COM connection",
            WorkerUptime = TimeSpan.FromSeconds(6),
            LastSync = DateTimeOffset.Now.AddHours(-2)
        });

        Add(new InfoBase
        {
            Name = "Accounting-Test",
            Kind = "File",
            Database = @"D:\1C\bases\acc-test",
            Organization = "AIBA TEST",
            OneCVersion = "8.3.24.1548",
            ComVersion = "8.3.24",
            State = "Idle",
            Status = StatusKind.Idle,
            ComState = "Disconnected",
            ComStatus = StatusKind.Idle,
            Worker = "none",
            WorkerPid = 0,
            WorkerPort = 0,
            WorkerMemoryMb = 0,
            CurrentObject = "idle",
            LastSync = DateTimeOffset.Now.AddDays(-1),
            WorkerUptime = TimeSpan.Zero
        });

        Add(new InfoBase
        {
            Name = "Production-01",
            Kind = "Server",
            Server = "1c-srv-01",
            Database = "prod01",
            Organization = "AIBA GROUP",
            OneCVersion = "8.3.25.1394",
            ComVersion = "8.3.24",
            State = "Error",
            Status = StatusKind.Error,
            ComState = "Version mismatch",
            ComStatus = StatusKind.Error,
            Worker = "oscript-4",
            WorkerPid = 18510,
            WorkerPort = 55903,
            WorkerMemoryMb = 74,
            CurrentObject = "halted",
            Problem = "COM client 8.3.24 cannot attach to server 8.3.25. Register comcntr 8.3.25 or pin comVersion for this base.",
            LastSync = DateTimeOffset.Now.AddHours(-5),
            WorkerUptime = TimeSpan.FromMinutes(3)
        });

        Add(new InfoBase
        {
            Name = "kansler_uz",
            Kind = "1uz (SQL)",
            Server = "kassa2",
            Database = "kansler_uz",
            Organization = "KANSLER UZ",
            OneCVersion = "8.3.21.1644",
            ComVersion = "direct SQL",
            State = "Syncing",
            Status = StatusKind.Busy,
            ComState = "Bulk SQL",
            Worker = "1uz-adapter",
            WorkerPid = 19004,
            WorkerPort = 55900,
            WorkerMemoryMb = 540,
            RowsPerSecond = 9_140,
            RowsRead = 44_128_006,
            Progress = 31,
            CurrentObject = "_AccRgAT1 keyset page 812",
            RequestsServed = 904,
            QueueDepth = 1,
            WorkerUptime = TimeSpan.FromMinutes(22)
        });

        Add(new InfoBase
        {
            Name = "diet-bistro",
            Kind = "File",
            Database = @"\\kassa2\bases\bistro",
            Organization = "DIET BISTRO",
            OneCVersion = "8.3.20.2290",
            ComVersion = "8.3.20",
            State = "Connected",
            Status = StatusKind.Healthy,
            ComState = "Connected",
            Worker = "oscript-1",
            WorkerPid = 18244,
            WorkerPort = 55899,
            WorkerMemoryMb = 132,
            RowsRead = 220_884,
            Progress = 100,
            RequestsServed = 3_302,
            WorkerUptime = TimeSpan.FromMinutes(94)
        });

        Add(new InfoBase
        {
            Name = "hoomo-retail",
            Kind = "Server",
            Server = "1c-srv-02",
            Database = "hoomo",
            Organization = "HOOMO RETAILER",
            OneCVersion = "8.3.24.1548",
            ComVersion = "8.3.24",
            State = "Connected",
            Status = StatusKind.Healthy,
            ComState = "Connected",
            Worker = "oscript-2",
            WorkerPid = 18310,
            WorkerPort = 55901,
            WorkerMemoryMb = 201,
            RowsRead = 6_770_210,
            Progress = 100,
            RequestsServed = 25_771,
            WorkerUptime = TimeSpan.FromHours(7)
        });

        SeedDocuments();
        SeedReports();
    }

    private void Add(InfoBase b) => InfoBases.Add(b);

    private void SeedDocuments()
    {
        DocumentRow D(string n, string t, string b, string c, string a, string s, StatusKind k, string m) =>
            new() { Number = n, Type = t, Base = b, Counterparty = c, Amount = a, State = s, Status = k, Marker = m };

        Documents.Add(D("SP-000418", "Spisanie s rascheetnogo scheta", "signum", "UZBEKTELEKOM AJ", "14 820 000", "Posted", StatusKind.Healthy, "AIBA_BANK_48210"));
        Documents.Add(D("SP-000419", "Postuplenie na raschetnyy schet", "signum", "MIREL TRADE", "3 200 000", "Posted", StatusKind.Healthy, "AIBA_BANK_48211"));
        Documents.Add(D("AO-000077", "Avansovyy otchet", "Kanstik", "Karimov A.", "1 145 500", "Draft", StatusKind.Warning, "AIBA_AVANS_77"));
        Documents.Add(D("RT-002913", "Otchet o roznichnyh prodazhah", "hoomo-retail", "Store #4", "28 004 200", "Posted", StatusKind.Healthy, "AIBA_RETAIL_2913"));
        Documents.Add(D("RL-000512", "Realizatsiya tovarov i uslug", "signum", "SIGNUM SAVDO", "9 400 000", "Posted", StatusKind.Healthy, "AIBA_SOTUV_512"));
        Documents.Add(D("SP-000420", "Spisanie s rascheetnogo scheta", "Production-01", "SQB BANK", "780 000", "Rejected", StatusKind.Error, "AIBA_BANK_48212"));
        Documents.Add(D("PT-000188", "Postuplenie tovarov", "Kanstik", "GLOBAL SUPPLY", "51 220 000", "Draft", StatusKind.Warning, "AIBA_PURCH_188"));
        Documents.Add(D("RT-002914", "Otchet o roznichnyh prodazhah", "diet-bistro", "Bistro POS", "4 118 000", "Queued", StatusKind.Busy, "AIBA_RETAIL_2914"));
    }

    private void SeedReports()
    {
        ReportRow R(string n, string k, string b, string p, string t, string s, StatusKind st) =>
            new() { Name = n, Kind = k, Base = b, Period = p, Built = t, State = s, Status = st };

        Reports.Add(R("Sverka razvernutoe", "Reconciliation", "signum", "2026-08", "4.1 s", "Ready", StatusKind.Healthy));
        Reports.Add(R("Retail revenue by tender", "Retail", "hoomo-retail", "2026-09-01..13", "12.8 s", "Ready", StatusKind.Healthy));
        Reports.Add(R("Cashflow (DDS) daily", "Cashflow", "Kanstik", "2026-09", "running", "Building", StatusKind.Busy));
        Reports.Add(R("AR by SchetDtKod", "Receivables", "signum", "2026-08", "2.4 s", "Ready", StatusKind.Healthy));
        Reports.Add(R("Warehouse stock (Ombor)", "Stock", "Kanstik", "snapshot", "failed", "COM timeout", StatusKind.Error));
        Reports.Add(R("Retail sverka 1C vs Hoomo", "Reconciliation", "hoomo-retail", "2026-09-12", "38.2 s", "Ready", StatusKind.Healthy));
    }

    public InfoBase? Find(string id) => InfoBases.FirstOrDefault(b => b.Id == id);

    public void Connect(InfoBase b)
    {
        b.State = "Connecting";
        b.Status = StatusKind.Busy;
        b.ComState = "Cold start";
        b.ComStatus = StatusKind.Warning;
        b.Worker = "oscript-" + _rng.Next(1, 5);
        b.WorkerPid = _rng.Next(18000, 19999);
        b.WorkerPort = 55899 + _rng.Next(0, 5);
        b.CurrentObject = "opening COM connection";
        b.WorkerUptime = TimeSpan.Zero;
        b.Problem = "";
        _log.Write(LogLevel.Info, "1C", "Connecting to " + b.Name + " (" + b.Location + ")");
    }

    public void Disconnect(InfoBase b)
    {
        b.State = "Idle";
        b.Status = StatusKind.Idle;
        b.ComState = "Disconnected";
        b.ComStatus = StatusKind.Idle;
        b.Worker = "none";
        b.WorkerPid = 0;
        b.WorkerMemoryMb = 0;
        b.RowsPerSecond = 0;
        b.CurrentObject = "idle";
        _log.Write(LogLevel.Warning, "1C", "Disconnected " + b.Name);
    }

    public void RestartWorker(InfoBase b)
    {
        b.WorkerPid = _rng.Next(18000, 19999);
        b.WorkerUptime = TimeSpan.Zero;
        b.WorkerMemoryMb = 64;
        b.RequestsServed = 0;
        b.QueueDepth = 0;
        b.ComState = "Cold start";
        b.ComStatus = StatusKind.Warning;
        b.Problem = "";
        _log.Write(LogLevel.Warning, "Router", "Worker restarted for " + b.Name + " (pid " + b.WorkerPid + ")");
    }

    public void Tick(int tick)
    {
        foreach (var b in InfoBases)
        {
            if (b.WorkerPid != 0)
            {
                b.WorkerUptime += TimeSpan.FromSeconds(1);
                b.WorkerMemoryMb = Math.Max(48, b.WorkerMemoryMb + (_rng.NextDouble() - 0.45) * 6);
            }

            if (b.Status == StatusKind.Busy && b.State == "Syncing")
            {
                b.RowsPerSecond = Math.Max(20, b.RowsPerSecond + (_rng.NextDouble() - 0.5) * b.RowsPerSecond * 0.12);
                b.RowsRead += (long)b.RowsPerSecond;
                b.Progress = Math.Min(99.4, b.Progress + 0.12);
                b.RequestsServed += _rng.Next(1, 6);
                b.QueueDepth = _rng.Next(0, 4);
                b.LastSync = DateTimeOffset.Now;
            }
            else if (b.State == "Connecting")
            {
                // Cold COM attach is 6-8 s in the real adapter; mirror that here.
                if (b.WorkerUptime.TotalSeconds > 7)
                {
                    b.State = "Connected";
                    b.Status = StatusKind.Healthy;
                    b.ComState = "Connected";
                    b.ComStatus = StatusKind.Healthy;
                    b.CurrentObject = "idle";
                    b.LastSync = DateTimeOffset.Now;
                    _log.Write(LogLevel.Info, "COM", "Connected to " + b.Name + " in " + (int)b.WorkerUptime.TotalSeconds + "s");
                }
            }

            b.Raise(nameof(InfoBase.LastSyncText));
        }
    }
}
