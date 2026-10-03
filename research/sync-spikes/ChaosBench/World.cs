using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneC.Sync.Source;

namespace ChaosBench;

/// <summary>The fake 1C's data: catalog items and posted documents with movement lines. Kept in one JSON file.</summary>
public sealed class World
{
    public sealed class Item { public string Name { get; set; } = ""; public long V { get; set; } }
    public sealed class Doc { public string Date { get; set; } = ""; public decimal Sum { get; set; } public long V { get; set; } public List<decimal> Lines { get; set; } = new(); }

    public Dictionary<string, Item> Cats { get; set; } = new();
    public Dictionary<string, Doc> Docs { get; set; } = new();
    public int Next { get; set; } = 1;
    public long Mutations { get; set; }

    public static string G(int i) => new Guid(i, 0, 0, new byte[8]).ToString("D");
    public static string Version(long v) => Convert.ToBase64String(BitConverter.GetBytes(v).Reverse().ToArray());

    public static World Load(string path) => JsonSerializer.Deserialize<World>(File.ReadAllText(path))!;

    /// <summary>Atomic: a reader sees the old or the new world, never half.</summary>
    public void Save(string path)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this));
        for (int attempt = 0; ; attempt++)
        {
            try { File.Move(tmp, path, overwrite: true); return; }
            catch (Exception e) when (e is UnauthorizedAccessException or IOException && attempt < 200) { Thread.Sleep(10); }   // the engine is reading it
        }
    }

    public static World Seed(int cats, int docs, Random r)
    {
        var w = new World();
        for (int i = 0; i < cats; i++) w.Cats[G(w.Next++)] = new Item { Name = "item " + i, V = 1 };
        for (int i = 0; i < docs; i++) w.Docs[G(w.Next++)] = NewDoc(r, i);
        return w;
    }

    private static Doc NewDoc(Random r, int i)
    {
        var lines = Enumerable.Range(0, r.Next(1, 6)).Select(_ => Math.Round((decimal)r.NextDouble() * 1000, 2)).ToList();
        return new Doc { Date = new DateTime(2026, 1, 1).AddMinutes(i * 7).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture), Sum = lines.Sum(), V = 1, Lines = lines };
    }

    /// <summary>One change a user could make, and the log events 1C writes for it.</summary>
    public List<(string Event, string Meta, string Ref)> Mutate(Random r)
    {
        Mutations++;
        var ev = new List<(string, string, string)>();
        switch (r.Next(6))
        {
            case 0: // rename an item
            {
                var k = Cats.Keys.ElementAt(r.Next(Cats.Count));
                Cats[k].Name = "renamed " + Mutations; Cats[k].V++;
                ev.Add(("_$Data$_.Update", "Справочник.Cat", k));
                break;
            }
            case 1: // new item
            {
                var k = G(Next++);
                Cats[k] = new Item { Name = "new " + Mutations, V = 1 };
                ev.Add(("_$Data$_.New", "Справочник.Cat", k));
                break;
            }
            case 2: // delete an item
            {
                if (Cats.Count < 10) goto case 1;
                var k = Cats.Keys.ElementAt(r.Next(Cats.Count));
                Cats.Remove(k);
                ev.Add(("_$Data$_.Delete", "Справочник.Cat", k));
                break;
            }
            case 3: // repost with other lines: fewer, more, same keys new amounts (F1)
            {
                var k = Docs.Keys.ElementAt(r.Next(Docs.Count));
                var d = Docs[k];
                d.Lines = Enumerable.Range(0, r.Next(0, 6)).Select(_ => Math.Round((decimal)r.NextDouble() * 1000, 2)).ToList();
                d.Sum = d.Lines.Sum(); d.V++;
                ev.Add(("_$Data$_.Update", "Документ.Doc", k));
                ev.Add(("_$Data$_.Post", "Документ.Doc", k));
                ev.Add(("_$Data$_.Update", "РегистрБухгалтерии.Acc", k));
                break;
            }
            case 4: // new posted document
            {
                var k = G(Next++);
                Docs[k] = NewDoc(r, Next);
                ev.Add(("_$Data$_.New", "Документ.Doc", k));
                ev.Add(("_$Data$_.Post", "Документ.Doc", k));
                ev.Add(("_$Data$_.Update", "РегистрБухгалтерии.Acc", k));
                break;
            }
            default: // delete a document (its movements go with it)
            {
                if (Docs.Count < 10) goto case 4;
                var k = Docs.Keys.ElementAt(r.Next(Docs.Count));
                Docs.Remove(k);
                ev.Add(("_$Data$_.Delete", "Документ.Doc", k));
                ev.Add(("_$Data$_.Update", "РегистрБухгалтерии.Acc", k));
                break;
            }
        }
        return ev;
    }

    // ---------------- rows in the host's shapes ----------------

    public static JsonObject CatRow(string id, Item it) => new() { ["id"] = id, ["name"] = it.Name, ["deletionMark"] = false, ["dataVersion"] = Version(it.V) };

    public static JsonObject DocRow(string id, Doc d) => new()
    {
        ["id"] = id, ["number"] = id[..8], ["date"] = d.Date, ["posted"] = true, ["Сумма"] = d.Sum, ["dataVersion"] = Version(d.V)
    };

    public static IEnumerable<JsonObject> Moves(string id, Doc d) => d.Lines.Select((a, i) => new JsonObject
    {
        ["Период"] = d.Date, ["recorderRef"] = id, ["lineNo"] = i + 1, ["НомерСтроки"] = i + 1, ["Сумма"] = a
    });
}

/// <summary>1C's event log in its own format (lgf dictionary + lgp records), appended by the mutator.</summary>
public static class LogWriter
{
    public static readonly string[] Events = { "_$Data$_.New", "_$Data$_.Update", "_$Data$_.Post", "_$Data$_.Unpost", "_$Data$_.Delete" };
    public static readonly string[] Metas = { "Справочник.Cat", "Документ.Doc", "РегистрБухгалтерии.Acc" };
    private const string File1 = "20260930000000.lgp";

    public static void Create(string dir)
    {
        Directory.CreateDirectory(dir);
        var lgf = new StringBuilder("﻿1CV8LOG(ver 2.0)\r\n11111111-2222-3333-4444-555555555555\r\n\r\n");
        for (int i = 0; i < Events.Length; i++) lgf.Append($"{{4,\"{Events[i]}\",{i + 1}}},\r\n");
        for (int i = 0; i < Metas.Length; i++) lgf.Append($"{{5,{Guid.NewGuid()},\"{Metas[i]}\",{i + 1}}},\r\n");
        File.WriteAllText(Path.Combine(dir, "1Cv8.lgf"), lgf.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(dir, File1), "﻿1CV8LOG(ver 2.0)\r\n11111111-2222-3333-4444-555555555555\r\n\r\n", new UTF8Encoding(false));
    }

    public static void Append(string dir, IEnumerable<(string Event, string Meta, string Ref)> events)
    {
        var sb = new StringBuilder();
        foreach (var (e, m, r) in events)
        {
            var p = r.Replace("-", "");
            string hex = p[16..20] + p[20..32] + p[12..16] + p[8..12] + p[0..8];
            sb.Append($"{{20260930120000,U,\r\n{{0,0}},1,1,1,1,{Array.IndexOf(Events, e) + 1},I,\"\",{Array.IndexOf(Metas, m) + 1},\r\n{{\"R\",214:{hex}}},\"\",0,0,0,2,0,\r\n{{0}}\r\n}},\r\n");
        }
        File.AppendAllText(Path.Combine(dir, File1), sb.ToString(), new UTF8Encoding(false));
    }
}
