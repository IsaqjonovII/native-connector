using System;
using System.Collections.ObjectModel;
using System.Linq;

namespace AibaShell.Services;

public sealed class MockBankService : IBankService, ITickable
{
    private readonly Random _rng = new(4242);
    private readonly ILogService _log;

    public ObservableCollection<BankConnection> Banks { get; } = new();

    public int WarningCount => Banks.Count(b => b.Status is StatusKind.Warning or StatusKind.Error);

    public MockBankService(ILogService log)
    {
        _log = log;

        Add("Kapitalbank", "KAPB", 3, "Connected", StatusKind.Healthy, "statement pulled, 38 rows", 4);
        Add("SQB", "SQB", 2, "Needs login", StatusKind.Warning, "session expired - operator must re-auth", 96);
        Add("Ipak Yo'li", "IPAK", 5, "Syncing", StatusKind.Busy, "curl_cffi desktop refresh, page 3/7", 0);
        Add("Hamkorbank", "HMKB", 1, "Connected", StatusKind.Healthy, "idle", 11);
        Add("Xalq Banki", "XALQ", 2, "Connected", StatusKind.Healthy, "idle", 22);
        Add("Ipoteka Bank", "IPOT", 1, "Error", StatusKind.Error, "403 from statement endpoint (WAF)", 140);
        Add("Trustbank", "TRST", 1, "Connected", StatusKind.Healthy, "idle", 31);
        Add("Turonbank", "TURN", 2, "Not configured", StatusKind.Idle, "no key bound", 0);
        Add("Mikrokreditbank", "MKB", 1, "Connected", StatusKind.Healthy, "idle", 9);
        Add("OFB", "OFB", 1, "Connected", StatusKind.Healthy, "idle", 17);
    }

    private void Add(string name, string code, int accounts, string state, StatusKind status, string detail, int minutesAgo)
    {
        Banks.Add(new BankConnection
        {
            Name = name,
            Code = code,
            Accounts = accounts,
            State = state,
            Status = status,
            Detail = detail,
            LastSync = DateTimeOffset.Now.AddMinutes(-minutesAgo)
        });
    }

    public void Reconnect(BankConnection bank)
    {
        bank.State = "Syncing";
        bank.Status = StatusKind.Busy;
        bank.Detail = "re-authenticating";
        _log.Write(LogLevel.Info, "Bank", bank.Name + ": reconnect requested");
    }

    public void Tick(int tick)
    {
        foreach (var b in Banks)
        {
            if (b.Status == StatusKind.Busy && _rng.NextDouble() > 0.93)
            {
                b.State = "Connected";
                b.Status = StatusKind.Healthy;
                b.Detail = "statement pulled, " + _rng.Next(4, 90) + " rows";
                b.LastSync = DateTimeOffset.Now;
                _log.Write(LogLevel.Info, "Bank", b.Name + " sync finished");
            }
            b.Raise(nameof(BankConnection.LastSyncText));
        }
    }
}
