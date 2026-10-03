using System.Text.Json;
using OneC.CloudStub;

// OneC.CloudStub --port 55971 --phone +998900000001 --password <test password>
// A local cloud for trying the desktop app's login and linking without touching prod. Point the
// app at it with %LOCALAPPDATA%\AIBA\Connector\cloud.json:
//   { "label": "Local stub", "apiBase": "http://127.0.0.1:55971/api/v1", "oneCBase": "http://127.0.0.1:55971/api/v2" }
// Runs until stdin closes or Ctrl+C.
string? Arg(string k) { int i = Array.IndexOf(args, "--" + k); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

if (Arg("phone") is not { } phone || Arg("password") is not { } password)
{
    Console.Error.WriteLine("usage: OneC.CloudStub --phone +998... --password <test password> [--port N]");
    return 1;
}

await using var stub = new CloudStub();
stub.AddUser(new CloudStub.User("101", phone, password, "Test", "User"));
stub.AddCompany(new CloudStub.Company("501", "Test company A", "300000001", true, "101"));
stub.AddCompany(new CloudStub.Company("502", "Test company B", "300000002", false, "101"));
// As if the old Connector had added them: one base this computer has, one it doesn't.
stub.AddRecord("101", "501", "Kansler buxgalteriya", "kansler", totalCount: 48210, percentage: 100);
stub.AddRecord("101", "501", "Old office", "OldOffice", "venkon", totalCount: 9000, percentage: 37.5);
stub.AddRecord("101", "501", "Broken base", "Broken", lastError: "1C ulanish xatosi: User identification failed");
await stub.StartAsync(int.TryParse(Arg("port"), out int p) ? p : 0);
Console.WriteLine(JsonSerializer.Serialize(new { @event = "ready", apiBase = stub.ApiBase, oneCBase = stub.OneCBase }));

var done = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.TrySetResult(); };
_ = Task.Run(() => { while (Console.In.ReadLine() is not null) { } done.TrySetResult(); });
await done.Task;
return 0;
