using OneC.SyncState;

// SyncStateChild <db> — commits cursor + work item pairs forever, printing "committed N" after each.
using var db = SyncDb.Open(args[0]);
for (int i = 1; ; i++)
{
    db.Write(tx =>
    {
        tx.UpsertWork(new WorkRequest("b", "T", "k" + i, WorkFlags.Changed, 1));
        tx.SetCursor("b", "cursor-" + i, null);
    });
    Console.WriteLine("committed " + i);
    Console.Out.Flush();
}
