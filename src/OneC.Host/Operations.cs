using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using OneC.Interop;
using OneC.Ipc;
using OneC.Sessions;

namespace OneC.Host;

/// <summary>
/// Executes one IPC request against this host's services and turns every outcome — result,
/// 1C error, validation failure, timeout, cancellation — into an <see cref="IpcResponse"/>.
/// Nothing escapes as an exception: the caller's id always resolves (IPC_CONTRACT.md §5).
/// </summary>
public sealed class Operations
{
    private readonly SessionManager _m;
    private readonly ReadService _reads;
    private readonly CatalogReadService _catalogs;
    private readonly DocumentReadService _documents;
    private readonly RegisterReadService _registers;
    private readonly SlicePlanner _slices;
    private readonly WriteService _writes;
    private readonly DocumentWriter _documentWriter;
    private readonly DateTime _startedUtc = DateTime.UtcNow;

    public Operations(SessionManager m, string writePrefix = "AIBA_")
    {
        _m = m;
        _reads = new ReadService(m);
        _catalogs = new CatalogReadService(m);
        _documents = new DocumentReadService(m);
        _registers = new RegisterReadService(m);
        _slices = new SlicePlanner(m);
        _writes = new WriteService(m, writePrefix);
        _documentWriter = new DocumentWriter(_writes);
    }

    public IpcResponse Execute(IpcRequest req, CancellationToken cancel)
    {
        var sw = Stopwatch.StartNew();
        using var deadline = new CancellationTokenSource(req.EffectiveDeadlineMs);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancel);
        var ct = linked.Token;
        try
        {
            var result = Dispatch(req, ct);
            return IpcResponse.Success(req.Id, result, sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancel.IsCancellationRequested)
        {
            return IpcResponse.Failure(req.Id, IpcError.Of(Layers.Timeout,
                $"deadline of {req.EffectiveDeadlineMs} ms passed"), sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            return IpcResponse.Failure(req.Id, IpcError.Of(Layers.Cancelled, "cancelled by the caller"), sw.ElapsedMilliseconds);
        }
        catch (OneCException oe)
        {
            return IpcResponse.Failure(req.Id, FromOneC(oe), sw.ElapsedMilliseconds);
        }
        catch (WriteRejected wr)
        {
            return IpcResponse.Failure(req.Id, IpcError.Of(wr.Unprocessable ? Layers.Host : Layers.Validation, wr.Message,
                                                           kind: wr.Unprocessable ? ErrorKinds.Unprocessable : null)
                                               with { Data = WriteErrorData(null, null, wr.Diagnostics) }, sw.ElapsedMilliseconds);
        }
        catch (DocumentWriteFailed wf)
        {
            return IpcResponse.Failure(req.Id, FromOneC(wf.OneC) with
            {
                Message = wf.Message,
                Data = WriteErrorData(wf.Document, wf.WriteMode, wf.Diagnostics)
            }, sw.ElapsedMilliseconds);
        }
        // InvalidOperationException only when System.Text.Json raised it (a wrongly typed
        // argument); anywhere else it is a host bug and must not be reported as bad input.
        catch (Exception ex) when (ex is ArgumentException or FormatException or JsonException ||
                                   (ex is InvalidOperationException && ex.Source == "System.Text.Json"))
        {
            return IpcResponse.Failure(req.Id, IpcError.Of(Layers.Validation, ex.Message), sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            return IpcResponse.Failure(req.Id, IpcError.Of(Layers.Host, $"{ex.GetType().Name}: {ex.Message}"), sw.ElapsedMilliseconds);
        }
    }

    private JsonNode? Dispatch(IpcRequest r, CancellationToken ct)
    {
        var a = r.Args ?? new JsonObject();
        return r.Op switch
        {
            Ops.Health => IpcJson.ToNode(new
            {
                pid = Environment.ProcessId,
                comcntr = _m.ComcntrPath,
                comcntrVersion = PlatformCatalog.FileVersionOf(_m.ComcntrPath),
                uptimeMs = (long)(DateTime.UtcNow - _startedUtc).TotalMilliseconds
            }),
            Ops.Stats => Stats(),
            Ops.Sweep => IpcJson.ToNode(new { retired = _m.SweepIdle() }),
            Ops.Version => IpcJson.ToNode(new
            {
                platformVersion = _m.Use(Base(r), c => c.Error.PlatformVersion, ct)
            }),
            Ops.Read => ReadOp(r, a, ct),
            Ops.Catalog => CatalogOp(r, a, ct),
            Ops.Document => DocumentOp(r, a, ct),
            Ops.Register => RegisterOp(r, a, ct),
            Ops.Slices => SlicesOp(r, a, ct),
            Ops.Test => TestOp(r, ct),
            Ops.Create => CreateOp(r, a, ct),
            Ops.Update => UpdateOp(r, a, ct),
            Ops.Post => PostOp(r, a, ct),
            Ops.Unpost => IpcJson.ToNode(new
            {
                id = _writes.Unpost(Base(r), Str(a, "docType"), Str(a, "ref"), ct).Ref, unposted = true
            }),
            Ops.MarkDeleted => Write(_writes.MarkForDeletion(Base(r), Str(a, "docType"), Str(a, "ref"),
                                                             a["mark"]?.GetValue<bool>() ?? true, ct)),
            // The old DELETE (main.os:17593): a deletion mark unless hard=true.
            Ops.Delete => a["hard"]?.GetValue<bool>() == false
                ? IpcJson.ToNode(new { id = _writes.MarkForDeletion(Base(r), Str(a, "docType"), Str(a, "ref"), true, ct).Ref, marked = true })
                : IpcJson.ToNode(new { id = _writes.Delete(Base(r), Str(a, "docType"), Str(a, "ref"), ct).Ref, deleted = true }),
            Ops.FindOwned => IpcJson.ToNode(new { refs = _writes.FindOwned(Base(r), Str(a, "docType"), Str(a, "prefix"), ct) }),
            _ => throw new ArgumentException($"unknown op '{r.Op}'")
        };
    }

    private JsonNode? ReadOp(IpcRequest r, JsonObject a, CancellationToken ct)
    {
        var q = new ReadQuery
        {
            Entity = Str(a, "entity"),
            Fields = a["fields"]?.AsArray().Select(n => n!.GetValue<string>()).ToArray()
                     ?? throw new ArgumentException("'fields' missing"),
            Limit = a["limit"]?.GetValue<int>() ?? 100,
            OrderBy = a["orderBy"]?.GetValue<string>(),
            Descending = a["desc"]?.GetValue<bool>() ?? false,
            From = OptDate(a, "from"),
            To = OptDate(a, "to"),
            Refs = a["refs"]?.GetValue<string>() switch
            {
                null or "text" => RefMode.Text,
                "guid" => RefMode.Guid,
                "both" => RefMode.Both,
                var other => throw new ArgumentException($"refs must be text|guid|both, got '{other}'")
            }
        };
        var res = _reads.Read(Base(r), q, ct);
        return IpcJson.ToNode(new { rows = res.Rows, sessionId = res.SessionId });
    }

    /// <summary>
    /// Catalog rows in the old adapter's shape (D33). With <c>id</c>: one element; otherwise a
    /// page — keyset when <c>after</c> is given, offset otherwise.
    /// </summary>
    private JsonNode? CatalogOp(IpcRequest r, JsonObject a, CancellationToken ct)
    {
        string catalog = Str(a, "catalog");
        if (a["id"]?.GetValue<string>() is { Length: > 0 } id)
            return IpcJson.ToNode(new { row = _catalogs.ById(Base(r), catalog, id, ct) });

        var page = _catalogs.List(Base(r), new CatalogQuery
        {
            Catalog = catalog,
            Limit = a["limit"]?.GetValue<int>() ?? 100,
            Offset = a["offset"]?.GetValue<int>() ?? 0,
            Fields = a["fields"] is JsonArray f ? f.Select(n => n!.GetValue<string>()).ToArray() : null,
            Filters = a["filters"] is JsonObject fo ? Scalars(fo, "filter") : null,
            After = a["after"]?.GetValue<string>(),
            SkipTotal = a["skipTotal"]?.GetValue<bool>() ?? false
        }, ct);
        return IpcJson.ToNode(new
        {
            rows = page.Rows, totalCount = page.TotalCount, next = page.Next,
            ignoredFields = page.IgnoredFields, sessionId = page.SessionId
        });
    }

    /// <summary>
    /// Document rows in the old adapter's shapes (D33): <c>id</c> → one document, <c>ids</c> →
    /// a batch (both with every tabular section), otherwise a list page.
    /// </summary>
    private JsonNode? DocumentOp(IpcRequest r, JsonObject a, CancellationToken ct)
    {
        string document = Str(a, "document");
        if (a["id"]?.GetValue<string>() is { Length: > 0 } id)
            return IpcJson.ToNode(new { row = _documents.ById(Base(r), document, id, ct) });
        if (a["ids"] is JsonArray ids)
            return IpcJson.ToNode(new { rows = _documents.ByIds(Base(r), document, ids.Select(n => n!.GetValue<string>()).ToList(), ct) });

        var page = _documents.List(Base(r), new DocumentQuery
        {
            Document = document,
            Limit = a["limit"]?.GetValue<int>() ?? 100,
            Offset = a["offset"]?.GetValue<int>() ?? 0,
            Fields = a["fields"] is JsonArray f ? f.Select(n => n!.GetValue<string>()).ToArray() : null,
            Filters = a["filters"] is JsonObject fo ? Scalars(fo, "filter") : null,
            CursorDate = OptDate(a, "cursorDate"),
            Order = a["order"]?.GetValue<string>() ?? "desc",
            After = a["after"]?.GetValue<string>(),
            Tabular = a["tabular"]?.GetValue<bool>() ?? true,
            From = OptDate(a, "from"),
            To = OptDate(a, "to"),
            SkipTotal = a["skipTotal"]?.GetValue<bool>() ?? false
        }, ct);
        return IpcJson.ToNode(new
        {
            rows = page.Rows, totalCount = page.TotalCount, nextCursorDate = page.NextCursorDate,
            nextCursorSkip = page.NextCursorSkip, next = page.Next, ignoredFields = page.IgnoredFields,
            document = page.Document, sessionId = page.SessionId
        });
    }

    /// <summary>
    /// Balanced period slices for a cold read (D35): kind = information | accumulation |
    /// accounting | document, name, parts, from?, to?. Walk each slice with the matching read
    /// (register: cursorDate = from, to = to; document: from/to window) on its own request.
    /// </summary>
    private JsonNode? SlicesOp(IpcRequest r, JsonObject a, CancellationToken ct)
    {
        string name = Str(a, "name");
        var (table, field) = Str(a, "kind") switch
        {
            "information" or "info" => ($"РегистрСведений.{name}", "Период"),
            "accumulation" => ($"РегистрНакопления.{name}", "Период"),
            "accounting" => ($"РегистрБухгалтерии.{name}", "Период"),
            "document" => ($"Документ.{name}", "Дата"),
            var other => throw new ArgumentException($"kind must be information|accumulation|accounting|document, got '{other}'")
        };
        var slices = _slices.Plan(Base(r), table, field, a["parts"]?.GetValue<int>() ?? 4, OptDate(a, "from"), OptDate(a, "to"), ct);
        return IpcJson.ToNode(new { slices = slices.Select(s => new { from = s.From, to = s.To, rows = s.Rows }) });
    }

    /// <summary>Register rows in the old adapter's shape (D34); kind = information | accumulation | accounting.</summary>
    private JsonNode? RegisterOp(IpcRequest r, JsonObject a, CancellationToken ct)
    {
        var kind = Str(a, "kind") switch
        {
            "information" or "info" => RegisterKind.Information,
            "accumulation" => RegisterKind.Accumulation,
            "accounting" => RegisterKind.Accounting,
            var other => throw new ArgumentException($"kind must be information|accumulation|accounting, got '{other}'")
        };
        var page = _registers.List(Base(r), new RegisterQuery
        {
            Kind = kind,
            Register = Str(a, "register"),
            Limit = a["limit"]?.GetValue<int>() ?? 100,
            Offset = a["offset"]?.GetValue<int>() ?? 0,
            CursorDate = OptDate(a, "cursorDate"),
            Order = a["order"]?.GetValue<string>() ?? "desc",
            To = OptDate(a, "to"),
            SkipTotal = a["skipTotal"]?.GetValue<bool>() ?? false,
            Recorder = a["recorderId"]?.GetValue<string>() is { Length: > 0 } rid ? (Str(a, "recorderDocument"), rid) : null
        }, ct);
        return IpcJson.ToNode(new
        {
            rows = page.Rows, totalCount = page.TotalCount, nextCursorDate = page.NextCursorDate,
            nextCursorSkip = page.NextCursorSkip, hasMore = page.HasMore, sessionId = page.SessionId
        });
    }

    /// <summary>
    /// Connection test with a metadata probe — the old adapter's check that the session is
    /// alive, not just that Connect returned (main.os:2048 ТестСоединение.Метаданные.Имя).
    /// </summary>
    private JsonNode? TestOp(IpcRequest r, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var info = _m.Use(Base(r), ctx =>
        {
            // Fully qualified: this class's own Dispatch(...) method hides the Dispatch class.
            using var scope = new ComScope();
            var md = scope.Track(OneC.Interop.Dispatch.Get(ctx.Connection, "Метаданные", ctx.Error), "Метаданные");
            return new
            {
                configuration = OneC.Interop.Dispatch.GetString(md, "Имя", ctx.Error),
                synonym = OneC.Interop.Dispatch.GetString(md, "Синоним", ctx.Error),
                configurationVersion = OneC.Interop.Dispatch.GetString(md, "Версия", ctx.Error),
                platformVersion = ctx.Error.PlatformVersion
            };
        }, ct);
        return IpcJson.ToNode(new
        {
            info.configuration, info.synonym, info.configurationVersion, info.platformVersion,
            ms = sw.ElapsedMilliseconds
        });
    }

    /// <summary>
    /// A document from the old adapter's create body (D38): <c>docType, body, post?=true,
    /// exchange?</c>. <c>exchange=true</c> is refused — exchange mode posts without movements
    /// and is never used by this host (workspace rule, quirk Q1).
    /// </summary>
    private JsonNode? CreateOp(IpcRequest r, JsonObject a, CancellationToken ct)
    {
        if (a["exchange"]?.GetValue<bool>() == true)
            throw new ArgumentException("exchange=true is not supported: exchange mode marks a document posted without its movements (D38)");
        var body = a["body"] as JsonObject ?? throw new ArgumentException("'body' must be the document as a JSON object");
        var c = _documentWriter.Create(Base(r), Str(a, "docType"), body, a["post"]?.GetValue<bool>() ?? true, ct);
        return IpcJson.ToNode(new
        {
            id = c.Id,
            number = c.Number,
            date = c.Date.ToString("yyyy-MM-ddTHH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
            posted = c.Posted,
            created = c.Created,
            idempotent = c.Idempotent,
            document = c.Document,
            requestedDocumentName = c.RequestedDocument,
            fillDiagnostics = c.Diagnostics.Count == 0 ? null : c.Diagnostics,
            sessionId = c.SessionId
        });
    }

    /// <summary>The old PUT (D38): <c>docType, ref, fields{}, post?, autoUnpost?=false</c>.</summary>
    private JsonNode? UpdateOp(IpcRequest r, JsonObject a, CancellationToken ct)
    {
        if (a["exchange"]?.GetValue<bool>() == true)
            throw new ArgumentException("exchange=true is not supported (D38)");
        var fields = a["fields"] as JsonObject ?? throw new ArgumentException("'fields' must be an object of the attributes to change");
        var u = _documentWriter.Update(Base(r), Str(a, "docType"), Str(a, "ref"), fields,
                                       a["post"]?.GetValue<bool>(), a["autoUnpost"]?.GetValue<bool>() ?? false, ct);
        return IpcJson.ToNode(new
        {
            id = u.Id, updated = true, posted = u.Posted,
            autoUnposted = u.WasPosted && !u.Posted, reposted = u.WasPosted && u.Posted,
            fillDiagnostics = u.Diagnostics.Count == 0 ? null : u.Diagnostics, sessionId = u.SessionId
        });
    }

    private JsonNode? PostOp(IpcRequest r, JsonObject a, CancellationToken ct)
    {
        if (a["exchange"]?.GetValue<bool>() == true)
            throw new ArgumentException("exchange=true is not supported: exchange mode marks a document posted without its movements (D38)");
        var w = _writes.Post(Base(r), Str(a, "docType"), Str(a, "ref"), ct);
        return IpcJson.ToNode(new { id = w.Ref, posted = true, number = w.Number, sessionId = w.SessionId });
    }

    private static JsonNode WriteErrorData(string? document, string? writeMode, IReadOnlyList<WriteDiagnostic> diagnostics) =>
        IpcJson.ToNode(new { documentName = document, writeMode, fillDiagnostics = diagnostics })!;

    private static JsonNode? Write(WriteResult w) =>
        IpcJson.ToNode(new { @ref = w.Ref, number = w.Number, kind = w.Kind.ToString(), sessionId = w.SessionId });

    private JsonNode? Stats()
    {
        var s = ResourceSampler.Take("stats", _m.BudgetUsed, collect: false);
        var pools = _m.Stats();
        return IpcJson.ToNode(new
        {
            workingSetMb = s.WorkingSetMb, privateMb = s.PrivateBytesMb, managedMb = s.ManagedHeapMb,
            threads = s.Threads, handles = s.Handles, cpuSeconds = s.CpuSeconds,
            comRefsLive = s.LiveComRefs, sessions = _m.BudgetUsed, inUse = pools.Sum(p => p.InUse), pools
        });
    }

    // ---------------- argument helpers ----------------

    private static string Base(IpcRequest r) =>
        string.IsNullOrWhiteSpace(r.Base) ? throw new ArgumentException("'base' missing") : r.Base;

    private static string Str(JsonObject a, string k) =>
        a[k]?.GetValue<string>() is { Length: > 0 } s ? s : throw new ArgumentException($"'{k}' missing");

    private static DateTime? OptDate(JsonObject a, string k) =>
        a[k] is JsonNode n ? DateTime.Parse(n.GetValue<string>(), null, System.Globalization.DateTimeStyles.RoundtripKind) : null;

    /// <summary>JSON scalars → CLR scalars. Strings that are ISO dates stay strings.</summary>
    private static Dictionary<string, object?> Scalars(JsonObject o, string what)
    {
        var d = new Dictionary<string, object?>();
        foreach (var (k, v) in o)
        {
            d[k] = v switch
            {
                null => null,
                JsonValue jv when jv.TryGetValue(out bool b) => b,
                JsonValue jv when jv.TryGetValue(out long l) => l,
                JsonValue jv when jv.TryGetValue(out decimal m) => m,
                JsonValue jv when jv.TryGetValue(out string? s) => s,
                _ => throw new ArgumentException($"{what} '{k}': only scalar values are allowed")
            };
        }
        return d;
    }

    // ---------------- error mapping ----------------

    public static IpcError FromOneC(OneCException oe)
    {
        string? kind =
            oe.Layer == OneCLayer.Host && oe.Message.Contains("not created by AIBA", StringComparison.Ordinal) ? ErrorKinds.Forbidden
            : oe.Layer == OneCLayer.Host && (oe.Message.Contains("not found", StringComparison.Ordinal) ||
                                             oe.Message.Contains("not registered", StringComparison.Ordinal)) ? ErrorKinds.NotFound
            : null;
        return new IpcError
        {
            Layer = oe.Layer.ToString(),
            Message = oe.Message,
            Hr = $"0x{oe.Hr:X8}",
            OneCCode = oe.OneCCode,
            Scode = $"0x{oe.SCode:X8}",
            Source = oe.OneCSource,
            Op = oe.Operation,
            Member = oe.Member,
            Base = oe.BaseName,
            PlatformVersion = oe.PlatformVersion,
            Retryable = oe.IsRetryable,
            SessionFatal = oe.IsSessionFatal,
            HostFatal = oe.IsHostFatal,
            Category = oe.Category,
            Kind = kind
        };
    }
}
