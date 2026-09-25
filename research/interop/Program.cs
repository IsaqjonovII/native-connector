using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using OneC.Interop;

// Research harness #2: activation (reg-free COM, mixed 1C versions) and interop style
// (dynamic vs hand-rolled IDispatch). Read-only unless a mode says otherwise; every document
// it creates carries the AIBA_INTEROP_ marker and nothing without that marker is ever touched.

static class P
{
    public const string MarkerPrefix = "AIBA_INTEROP_";
    public static string Marker = MarkerPrefix + DateTime.Now.ToString("MMddHHmmss");

    public static readonly Dictionary<string, string> Versions = new()
    {
        ["8.3.15"] = @"C:\Program Files\1cv8\8.3.15.1565\bin\comcntr.dll",
        ["8.3.18"] = @"C:\Program Files\1cv8\8.3.18.1289\bin\comcntr.dll",
    };

    static string Arg(string[] a, string k)
    {
        for (int i = 0; i < a.Length - 1; i++) if (a[i] == "--" + k) return a[i + 1];
        return null;
    }
    static bool Flag(string[] a, string k) => a.Contains("--" + k);

    static (string name, string cs) LoadCfg(string path)
    {
        var r = JsonDocument.Parse(File.ReadAllText(path)).RootElement;
        return (r.GetProperty("baseName").GetString(), r.GetProperty("connectionString").GetString());
    }

    static void Rel(object o)
    {
        try { if (o != null && Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o); } catch { }
    }

    static int Main(string[] argv)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string mode = argv.Length > 0 && !argv[0].StartsWith("--") ? argv[0] : "help";
        string cfg = Arg(argv, "cfg") ?? @"D:\aiba\research\1c-adapter\csharp-worker\bilim.json";
        string ver = Arg(argv, "ver") ?? "8.3.15";
        // --dll lets a version that is not installed be requested, to see how that fails.
        string dll = Arg(argv, "dll");
        if (dll != null) Versions[ver] = dll;

        try
        {
            switch (mode)
            {
                case "act": SxsDir = Arg(argv, "sxsdir") ?? "temp"; return Act(cfg, ver, Arg(argv, "how") ?? "direct");
                case "matrix": return Matrix(argv);
                case "mixed": return Mixed(argv);
                case "typeinfo": return TypeInfo(cfg, ver);
                case "cmp": return Compare(cfg, ver, Arg(argv, "style") ?? "both", Flag(argv, "write"));
                case "err": return Errors(cfg, ver, Arg(argv, "style") ?? "both");
                case "conc": return Conc(cfg, ver, int.Parse(Arg(argv, "k") ?? "4"), int.Parse(Arg(argv, "r") ?? "30"), Arg(argv, "style") ?? "both");
                case "cleanup": return Cleanup(cfg, ver);
                default:
                    Console.WriteLine("modes: act | matrix | mixed | typeinfo | cmp | err | cleanup");
                    Console.WriteLine("  --cfg <json> --ver 8.3.15|8.3.18 --how registry|direct|sxs --style dyn|disp|both [--write]");
                    return 1;
            }
        }
        catch (OneCException oe) { Console.WriteLine("ONEC " + oe.Describe()); return 2; }
        catch (Exception ex) { Console.WriteLine("FATAL " + ex.GetType().Name + ": " + ex.Message); return 2; }
    }

    // ---------------- activation ----------------

    static object Create(string how, string ver, out long ms, out string note)
    {
        note = "";
        var sw = Stopwatch.StartNew();
        object c = how switch
        {
            "registry" => Activation.CreateViaRegistry(),
            "direct" => Activation.CreateFromPath(Versions[ver]),
            "sxs" => Sxs(ver, out note),
            _ => throw new ArgumentException("how = registry|direct|sxs")
        };
        ms = sw.ElapsedMilliseconds;
        return c;
    }

    public static string SxsDir = "temp";

    static object Sxs(string ver, out string note)
    {
        string bin = Path.GetDirectoryName(Versions[ver])!;
        // "bin"  — manifest sits next to comcntr.dll, the layout SxS documents.
        // "temp" — manifest elsewhere, bin only named through lpAssemblyDirectory.
        string dir = SxsDir == "bin" ? bin : Path.Combine(Path.GetTempPath(), "onec-sxs-" + ver);
        Directory.CreateDirectory(dir);
        string man = Path.Combine(dir, "comcntr.manifest");
        File.WriteAllText(man, $@"<?xml version=""1.0"" encoding=""UTF-8"" standalone=""yes""?>
<assembly xmlns=""urn:schemas-microsoft-com:asm.v1"" manifestVersion=""1.0"">
  <assemblyIdentity type=""win32"" name=""OneC.comcntr.{ver}"" version=""1.0.0.0"" processorArchitecture=""amd64""/>
  <file name=""comcntr.dll"">
    <comClass clsid=""{{181E893D-73A4-4722-B61D-D604B3D67D47}}""
              threadingModel=""Both""
              progid=""V83.COMConnector""/>
  </file>
</assembly>", new UTF8Encoding(false));
        return Activation.CreateViaSxS(man, bin, out note);
    }

    static void ReportModules(string tag)
    {
        Console.WriteLine($"  [{tag}] comcntr modules mapped in this process:");
        var mods = Activation.LoadedComcntrModules();
        if (mods.Count == 0) Console.WriteLine("    (none)");
        foreach (var (p, v) in mods) Console.WriteLine($"    {v,-16} {p}");
    }

    static int Act(string cfg, string ver, string how)
    {
        var (name, cs) = LoadCfg(cfg);
        Disp.Context = name; Disp.Version = ver;
        Console.WriteLine($"activate how={how} ver={ver} base={name} ({(cs.StartsWith("File=") ? "FILE" : "SERVER")})");

        object conn = null, session = null;
        try
        {
            conn = Create(how, ver, out long actMs, out string note);
            Console.WriteLine($"  activation OK in {actMs} ms {note}");
            ReportModules("after activate");

            var sw = Stopwatch.StartNew();
            session = Disp.Call(conn, "Connect", cs);
            Console.WriteLine($"  Connect OK in {sw.ElapsedMilliseconds} ms");
            ReportModules("after connect");

            string pv = Activation.PlatformVersionFromConnection(session);
            Console.WriteLine($"  1C reports ВерсияПриложения = {pv}");
            Console.WriteLine($"  VERDICT: requested {ver}, 1C says {pv}, match={pv.StartsWith(ver)}");

            var p = Process.GetCurrentProcess(); p.Refresh();
            Console.WriteLine($"  rss={p.WorkingSet64 / 1024 / 1024}MB pid={p.Id}");
            return 0;
        }
        finally { Rel(session); Rel(conn); }
    }

    static int Matrix(string[] argv)
    {
        string[] cfgs = (Arg(argv, "cfgs") ??
            @"D:\aiba\research\1c-adapter\csharp-worker\bilim.json;D:\aiba\research\1c-adapter\csharp-worker\config.local.json")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        // Each cell runs in its own child process: a comcntr, once loaded, stays for the
        // process lifetime, so in-process cells would contaminate each other.
        Console.WriteLine("how       ver      base            result");
        foreach (string how in new[] { "registry", "direct", "sxs" })
            foreach (string ver in Versions.Keys)
                foreach (string cfg in cfgs)
                {
                    var (name, cs) = LoadCfg(cfg);
                    var psi = new ProcessStartInfo(Environment.ProcessPath!)
                    {
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        StandardOutputEncoding = Encoding.UTF8
                    };
                    foreach (var a in new[] { "act", "--cfg", cfg, "--ver", ver, "--how", how, "--sxsdir", "bin" }) psi.ArgumentList.Add(a);
                    var pr = Process.Start(psi)!;
                    string o = pr.StandardOutput.ReadToEnd() + pr.StandardError.ReadToEnd();
                    pr.WaitForExit();
                    string verdict = o.Contains("VERDICT")
                        ? o.Split('\n').First(l => l.Contains("VERDICT")).Trim()
                        : (o.Split('\n').FirstOrDefault(l => l.StartsWith("ONEC") || l.StartsWith("FATAL"))?.Trim()
                           ?? $"exit {pr.ExitCode}");
                    if (verdict.Length > 150) verdict = verdict[..150];
                    Console.WriteLine($"{how,-9} {ver,-8} {name,-15} {verdict}");
                }
        return 0;
    }

    /// <summary>Both platform versions activated inside ONE process, at the same time.</summary>
    static int Mixed(string[] argv)
    {
        string fileCfg = Arg(argv, "cfg") ?? @"D:\aiba\research\1c-adapter\csharp-worker\bilim.json";
        var (name, cs) = LoadCfg(fileCfg);
        Console.WriteLine($"mixed-version in one process, base={name}");

        var objs = new List<object>();
        foreach (var ver in Versions.Keys)
        {
            try
            {
                var sw = Stopwatch.StartNew();
                object c = Activation.CreateFromPath(Versions[ver]);
                Console.WriteLine($"  {ver}: connector created in {sw.ElapsedMilliseconds} ms");
                objs.Add(c);
                ReportModules("after " + ver);
                var s = Disp.Call(c, "Connect", cs);
                Console.WriteLine($"  {ver}: Connect OK, 1C says {Activation.PlatformVersionFromConnection(s)}");
                objs.Add(s);
            }
            catch (Exception ex)
            {
                var oe = ex as OneCException ?? Disp.FromClr(ex, "mixed", ver);
                Console.WriteLine($"  {ver}: FAILED {oe.Describe()}");
            }
        }
        ReportModules("final");
        var pr0 = Process.GetCurrentProcess(); pr0.Refresh();
        var onec = pr0.Modules.Cast<ProcessModule>()
                     .Where(m => m.FileName.Contains(@"\1cv8\", StringComparison.OrdinalIgnoreCase))
                     .Select(m => m.ModuleName).OrderBy(x => x).ToList();
        Console.WriteLine($"  1cv8 modules mapped ({onec.Count}): " + string.Join(" ", onec));
        var p = Process.GetCurrentProcess(); p.Refresh();
        Console.WriteLine($"  rss={p.WorkingSet64 / 1024 / 1024}MB");
        objs.Reverse();
        foreach (var o in objs) Rel(o);
        return 0;
    }

    // ---------------- type information ----------------

    static int TypeInfo(string cfg, string ver)
    {
        var (name, cs) = LoadCfg(cfg);
        object conn = Activation.CreateFromPath(Versions[ver]);
        object sess = null;
        try
        {
            Probe("connector", conn);
            sess = Disp.Call(conn, "Connect", cs);
            Probe("connection", sess);
            var q = Disp.Call(sess, "NewObject", "Запрос");
            Probe("Запрос", q);
            Rel(q);

            Console.WriteLine("\nname resolution (GetIDsOfNames) on the connection:");
            foreach (var n in new[] { "NewObject", "СоздатьОбъект", "Метаданные", "Metadata",
                                      "Документы", "Documents", "Справочники", "Catalogs",
                                      "ПолучитьCOMОбъект", "NotAMember" })
                Console.WriteLine($"  {n,-22} {(Disp.TryDispId(sess, n, out int id) ? "dispid " + id : "NOT FOUND")}");
            return 0;
        }
        finally { Rel(sess); Rel(conn); }
    }

    static void Probe(string tag, object o)
    {
        var d = (IDispatchRaw)o;
        int hr = d.GetTypeInfoCount(out int count);
        Console.WriteLine($"{tag}: GetTypeInfoCount hr=0x{hr:X8} count={count}");
        if (hr >= 0 && count > 0)
        {
            int hr2 = d.GetTypeInfo(0, 0, out IntPtr ti);
            Console.WriteLine($"  GetTypeInfo hr=0x{hr2:X8} ptr={(ti == IntPtr.Zero ? "null" : "yes")}");
            if (ti != IntPtr.Zero) Marshal.Release(ti);
        }
        IntPtr unk = Marshal.GetIUnknownForObject(o);
        var iidPci = new Guid("B196B283-BAB4-101A-B69C-00AA00341D07");
        bool hasPci = Marshal.QueryInterface(unk, in iidPci, out IntPtr pci) >= 0 && pci != IntPtr.Zero;
        Console.WriteLine($"  is IProvideClassInfo: {hasPci}");
        if (hasPci)
        {
            var p = (IProvideClassInfo)Marshal.GetObjectForIUnknown(pci);
            int hr3 = p.GetClassInfo(out IntPtr ti2);
            Console.WriteLine($"    GetClassInfo hr=0x{hr3:X8} ptr={(ti2 == IntPtr.Zero ? "null" : "yes")}");
            if (ti2 != IntPtr.Zero) DumpTypeInfo(ti2);
            Marshal.FinalReleaseComObject(p);
            Marshal.Release(pci);
        }
        if (hr >= 0 && count > 0 && d.GetTypeInfo(0, 0, out IntPtr tiA) >= 0 && tiA != IntPtr.Zero)
            DumpTypeInfo(tiA);
        Marshal.Release(unk);
    }

    [ComImport, Guid("B196B283-BAB4-101A-B69C-00AA00341D07"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IProvideClassInfo { [PreserveSig] int GetClassInfo(out IntPtr ti); }

    static void DumpTypeInfo(IntPtr tiPtr)
    {
        var ti = (System.Runtime.InteropServices.ComTypes.ITypeInfo)Marshal.GetObjectForIUnknown(tiPtr);
        try
        {
            ti.GetTypeAttr(out IntPtr pAttr);
            var attr = Marshal.PtrToStructure<System.Runtime.InteropServices.ComTypes.TYPEATTR>(pAttr);
            Console.WriteLine($"    typeattr: kind={attr.typekind} funcs={attr.cFuncs} vars={attr.cVars} guid={attr.guid}");
            var names = new List<string>();
            for (int i = 0; i < attr.cFuncs && i < 40; i++)
            {
                ti.GetFuncDesc(i, out IntPtr pf);
                var fd = Marshal.PtrToStructure<System.Runtime.InteropServices.ComTypes.FUNCDESC>(pf);
                var nm = new string[1]; ti.GetNames(fd.memid, nm, 1, out _);
                names.Add($"{nm[0]}({fd.cParams})");
                ti.ReleaseFuncDesc(pf);
            }
            Console.WriteLine("    members: " + string.Join(", ", names));
            ti.ReleaseTypeAttr(pAttr);
        }
        catch (Exception ex) { Console.WriteLine("    typeinfo dump failed: " + ex.Message); }
        finally { Marshal.FinalReleaseComObject(ti); Marshal.Release(tiPtr); }
    }

    // ---------------- dynamic vs disp comparison ----------------

    sealed class Step
    {
        public string Name;
        public bool Ok;
        public long Ms;
        public string Detail = "";
    }

    static void Show(string style, List<Step> steps)
    {
        Console.WriteLine($"\n=== {style} ===");
        foreach (var s in steps)
            Console.WriteLine($"  {(s.Ok ? "ok  " : "FAIL")} {s.Name,-26} {s.Ms,6}ms  {s.Detail}");
    }

    static Step Timed(string name, Func<string> f)
    {
        var sw = Stopwatch.StartNew();
        try { string d = f(); return new Step { Name = name, Ok = true, Ms = sw.ElapsedMilliseconds, Detail = d ?? "" }; }
        catch (OneCException oe) { return new Step { Name = name, Ok = false, Ms = sw.ElapsedMilliseconds, Detail = oe.Describe() }; }
        catch (Exception ex)
        {
            var oe = Disp.FromClr(ex, name, "");
            return new Step { Name = name, Ok = false, Ms = sw.ElapsedMilliseconds, Detail = ex.GetType().Name + " | " + oe.Describe() };
        }
    }

    const string DocQuery =
        "ВЫБРАТЬ ПЕРВЫЕ 1 Ссылка, Комментарий ИЗ Документ.{0} ГДЕ Проведен = ИСТИНА УПОРЯДОЧИТЬ ПО Дата УБЫВ";

    static int Compare(string cfg, string ver, string style, bool write)
    {
        var (name, cs) = LoadCfg(cfg);
        Disp.Context = name; Disp.Version = ver;
        Console.WriteLine($"compare base={name} ver={ver} write={write}");

        if (style is "dyn" or "both") Show("A. dynamic", RunDynamic(cs, ver, write));
        if (style is "disp" or "both") Show("C. IDispatch wrapper", RunDisp(cs, ver, write));
        return 0;
    }

    static string DiscoverDocType(object sess, bool useDisp)
    {
        // Any document type that has a Комментарий attribute and at least one posted document.
        foreach (var t in new[] { "РеализацияТоваровУслуг", "ПоступлениеТоваровУслуг",
                                  "СписаниеСРасчетногоСчета", "ПоступлениеНаРасчетныйСчет",
                                  "ОтчетОРозничныхПродажах", "АвансовыйОтчет" })
        {
            try
            {
                object q = useDisp ? Disp.Call(sess, "NewObject", "Запрос") : ((dynamic)sess).NewObject("Запрос");
                try
                {
                    string sql = string.Format(DocQuery, t);
                    if (useDisp) { Disp.Set(q, "Текст", sql); }
                    else { ((dynamic)q).Текст = sql; }
                    object res = useDisp ? Disp.Call(q, "Выполнить") : ((dynamic)q).Выполнить();
                    try
                    {
                        object sel = useDisp ? Disp.Call(res, "Выбрать") : ((dynamic)res).Выбрать();
                        try
                        {
                            bool got = useDisp ? (bool)Disp.Call(sel, "Следующий") : (bool)((dynamic)sel).Следующий();
                            if (got) return t;
                        }
                        finally { Rel(sel); }
                    }
                    finally { Rel(res); }
                }
                finally { Rel(q); }
            }
            catch { }
        }
        return null;
    }

    // ---- A. dynamic ----

    static List<Step> RunDynamic(string cs, string ver, bool write)
    {
        var steps = new List<Step>();
        object conn = null; dynamic sess = null;
        var keep = new List<object>();
        try
        {
            steps.Add(Timed("activate", () => { conn = Activation.CreateFromPath(Versions[ver]); return "direct path"; }));
            steps.Add(Timed("Connect", () => { sess = ((dynamic)conn).Connect(cs); return "session up"; }));
            steps.Add(Timed("metadata", () => { dynamic m = sess.Метаданные; dynamic d = m.Документы; int n = (int)d.Количество(); Rel(d); Rel(m); return n + " document types"; }));

            string docType = null;
            steps.Add(Timed("discover doc type", () => { docType = DiscoverDocType(sess, false); return docType ?? "none"; }));

            dynamic q = null;
            steps.Add(Timed("query create", () => { q = sess.NewObject("Запрос"); return ""; }));
            keep.Add(q);
            steps.Add(Timed("property write (Текст)", () => { q.Текст = string.Format(DocQuery, docType ?? "РеализацияТоваровУслуг"); return ""; }));

            dynamic res = null, sel = null;
            steps.Add(Timed("query Execute", () => { res = q.Выполнить(); return ""; }));
            keep.Add(res);
            steps.Add(Timed("selection", () => { sel = res.Выбрать(); return ""; }));
            keep.Add(sel);
            steps.Add(Timed("traverse", () => ((bool)sel.Следующий()).ToString()));
            steps.Add(Timed("property read (by name)", () => { object v = sel.Комментарий; return "\"" + (v?.ToString() ?? "") + "\""; }));
            steps.Add(Timed("property read (Get(0))", () => { object v = sel.Get(0); return v?.ToString() ?? "null"; }));

            steps.Add(Timed("invalid query", () =>
            {
                dynamic bq = sess.NewObject("Запрос");
                try { bq.Текст = "ВЫБРАТЬ ЭтойКолонкиНет ИЗ Документ.НетТакогоДокумента"; bq.Выполнить(); return "NO ERROR RAISED"; }
                finally { Rel(bq); }
            }));
            steps.Add(Timed("missing member", () => { object v = ((dynamic)sess).ЭтогоЧленаНет; return "NO ERROR"; }));
            steps.Add(Timed("bad arg type", () => { dynamic bq = sess.NewObject("Запрос"); try { bq.Текст = 12345; return "accepted int"; } finally { Rel(bq); } }));

            if (write && docType != null) DynWrite(sess, docType, steps);
            return steps;
        }
        finally
        {
            keep.Reverse();
            foreach (var o in keep) Rel(o);
            Rel(sess); Rel(conn);
        }
    }

    static void DynWrite(dynamic sess, string docType, List<Step> steps)
    {
        dynamic src = null, obj = null;
        try
        {
            steps.Add(Timed("find source doc", () =>
            {
                dynamic q = sess.NewObject("Запрос");
                try
                {
                    q.Текст = string.Format(DocQuery, docType);
                    dynamic r = q.Выполнить(); dynamic s = r.Выбрать();
                    try { if (!(bool)s.Следующий()) return "none"; src = s.Ссылка; return docType; }
                    finally { Rel(s); Rel(r); }
                }
                finally { Rel(q); }
            }));
            steps.Add(Timed("doc create (clone)", () =>
            {
                dynamic o = src.ПолучитьОбъект();
                try { obj = o.Скопировать(); } finally { Rel(o); }
                obj.Дата = DateTime.Now;
                obj.Комментарий = Marker + " dyn";
                obj.Записать();
                return "№" + obj.Номер;
            }));
            steps.Add(Timed("doc update", () =>
            {
                obj.Комментарий = Marker + " dyn upd";
                obj.Записать();
                return "ok";
            }));
        }
        finally { Rel(obj); Rel(src); }
    }

    // ---- C. IDispatch wrapper ----

    static List<Step> RunDisp(string cs, string ver, bool write)
    {
        var steps = new List<Step>();
        object conn = null, sess = null;
        var keep = new List<object>();
        try
        {
            steps.Add(Timed("activate", () => { conn = Activation.CreateFromPath(Versions[ver]); return "direct path"; }));
            steps.Add(Timed("Connect", () => { sess = Disp.Call(conn, "Connect", cs); return "session up"; }));
            steps.Add(Timed("metadata", () =>
            {
                object m = Disp.Get(sess, "Метаданные");
                try { object d = Disp.Get(m, "Документы"); try { return Convert.ToInt32(Disp.Call(d, "Количество")) + " document types"; } finally { Rel(d); } }
                finally { Rel(m); }
            }));

            string docType = null;
            steps.Add(Timed("discover doc type", () => { docType = DiscoverDocType(sess, true); return docType ?? "none"; }));

            object q = null, res = null, sel = null;
            steps.Add(Timed("query create", () => { q = Disp.Call(sess, "NewObject", "Запрос"); return ""; }));
            keep.Add(q);
            steps.Add(Timed("property write (Текст)", () => { Disp.Set(q, "Текст", string.Format(DocQuery, docType ?? "РеализацияТоваровУслуг")); return ""; }));
            steps.Add(Timed("query Execute", () => { res = Disp.Call(q, "Выполнить"); return ""; }));
            keep.Add(res);
            steps.Add(Timed("selection", () => { sel = Disp.Call(res, "Выбрать"); return ""; }));
            keep.Add(sel);
            steps.Add(Timed("traverse", () => Disp.Call(sel, "Следующий").ToString()));
            steps.Add(Timed("property read (by name)", () => "\"" + (Disp.Get(sel, "Комментарий")?.ToString() ?? "") + "\""));
            steps.Add(Timed("property read (Get(0))", () => Disp.Call(sel, "Get", 0)?.ToString() ?? "null"));

            steps.Add(Timed("invalid query", () =>
            {
                object bq = Disp.Call(sess, "NewObject", "Запрос");
                try { Disp.Set(bq, "Текст", "ВЫБРАТЬ ЭтойКолонкиНет ИЗ Документ.НетТакогоДокумента"); Disp.Call(bq, "Выполнить"); return "NO ERROR RAISED"; }
                finally { Rel(bq); }
            }));
            steps.Add(Timed("missing member", () => { Disp.Get(sess, "ЭтогоЧленаНет"); return "NO ERROR"; }));
            steps.Add(Timed("bad arg type", () => { object bq = Disp.Call(sess, "NewObject", "Запрос"); try { Disp.Set(bq, "Текст", 12345); return "accepted int"; } finally { Rel(bq); } }));

            if (write && docType != null) DispWrite(sess, docType, steps);
            return steps;
        }
        finally
        {
            keep.Reverse();
            foreach (var o in keep) Rel(o);
            Rel(sess); Rel(conn);
        }
    }

    static void DispWrite(object sess, string docType, List<Step> steps)
    {
        object src = null, obj = null;
        try
        {
            steps.Add(Timed("find source doc", () =>
            {
                object q = Disp.Call(sess, "NewObject", "Запрос");
                try
                {
                    Disp.Set(q, "Текст", string.Format(DocQuery, docType));
                    object r = Disp.Call(q, "Выполнить"); object s = Disp.Call(r, "Выбрать");
                    try { if (!(bool)Disp.Call(s, "Следующий")) return "none"; src = Disp.Get(s, "Ссылка"); return docType; }
                    finally { Rel(s); Rel(r); }
                }
                finally { Rel(q); }
            }));
            steps.Add(Timed("doc create (clone)", () =>
            {
                object o = Disp.Call(src, "ПолучитьОбъект");
                try { obj = Disp.Call(o, "Скопировать"); } finally { Rel(o); }
                Disp.Set(obj, "Дата", DateTime.Now);
                Disp.Set(obj, "Комментарий", Marker + " disp");
                Disp.Call(obj, "Записать");
                return "№" + Disp.Get(obj, "Номер");
            }));
            steps.Add(Timed("doc update", () =>
            {
                Disp.Set(obj, "Комментарий", Marker + " disp upd");
                Disp.Call(obj, "Записать");
                return "ok";
            }));
            steps.Add(Timed("post (may be textless)", () =>
            {
                object mode = Disp.Get(Disp.Get(sess, "РежимЗаписиДокумента"), "Проведение");
                try { Disp.Call(obj, "Записать", mode); return "posted"; } finally { Rel(mode); }
            }));
        }
        finally { Rel(obj); Rel(src); }
    }

    // ---------------- error matrix ----------------

    static int Errors(string cfg, string ver, string style)
    {
        var (name, cs) = LoadCfg(cfg);
        Disp.Context = name; Disp.Version = ver;
        object conn = Activation.CreateFromPath(Versions[ver]);
        Console.WriteLine($"error matrix base={name} ver={ver}");
        var rows = new List<Step>();

        rows.Add(Timed("bad credentials", () =>
        {
            string bad = System.Text.RegularExpressions.Regex.Replace(cs, "Pwd=[^;]*", "Pwd=definitely-wrong");
            if (!bad.Contains("Pwd=")) bad += "Pwd=definitely-wrong;";
            object s = Disp.Call(conn, "Connect", bad); Rel(s); return "CONNECTED ANYWAY";
        }));
        rows.Add(Timed("missing base", () =>
        {
            object s = Disp.Call(conn, "Connect", @"File=""D:\1C\no-such-base-here"";"); Rel(s); return "CONNECTED ANYWAY";
        }));
        rows.Add(Timed("garbage conn string", () => { object s = Disp.Call(conn, "Connect", "this is not a connection string"); Rel(s); return "CONNECTED ANYWAY"; }));
        rows.Add(Timed("unknown server", () => { object s = Disp.Call(conn, "Connect", @"Srvr=""no-such-host-xyz"";Ref=""NOPE"";"); Rel(s); return "CONNECTED ANYWAY"; }));
        rows.Add(Timed("connector missing member", () => { Disp.Get(conn, "НетТакогоСвойства"); return "NO ERROR"; }));

        object sess = null;
        rows.Add(Timed("good connect", () => { sess = Disp.Call(conn, "Connect", cs); return "ok"; }));
        if (sess != null)
        {
            rows.Add(Timed("invalid query text", () =>
            {
                object q = Disp.Call(sess, "NewObject", "Запрос");
                try { Disp.Set(q, "Текст", "ЭТО НЕ ЗАПРОС"); Disp.Call(q, "Выполнить"); return "NO ERROR"; }
                finally { Rel(q); }
            }));
            rows.Add(Timed("unknown metadata object", () =>
            {
                object d = Disp.Get(sess, "Документы");
                try { Disp.Get(d, "НетТакогоДокумента"); return "NO ERROR"; } finally { Rel(d); }
            }));
            rows.Add(Timed("NewObject unknown type", () => { object o = Disp.Call(sess, "NewObject", "НетТакогоТипа"); Rel(o); return "NO ERROR"; }));
            rows.Add(Timed("use released session", () =>
            {
                object s2 = Disp.Call(conn, "Connect", cs);
                Marshal.FinalReleaseComObject(s2);
                try { Disp.Get(s2, "Метаданные"); return "NO ERROR"; } catch (InvalidComObjectException) { throw; }
            }));
        }

        foreach (var r in rows) Console.WriteLine($"  {(r.Ok ? "ok  " : "err ")} {r.Name,-26} {r.Ms,6}ms  {r.Detail}");

        // same list through dynamic, for the exception-quality column
        if (style is "dyn" or "both")
        {
            Console.WriteLine("\n  --- same failures through C# dynamic ---");
            dynamic dc = conn;
            foreach (var (label, act) in new (string, Action)[]
            {
                ("bad credentials", () => { var s = dc.Connect(System.Text.RegularExpressions.Regex.Replace(cs, "Pwd=[^;]*", "Pwd=definitely-wrong")); Rel(s); }),
                ("missing base",    () => { var s = dc.Connect(@"File=""D:\1C\no-such-base-here"";"); Rel(s); }),
                ("connector missing member", () => { var _ = dc.НетТакогоСвойства; }),
            })
            {
                try { act(); Console.WriteLine($"  dyn {label,-26} NO ERROR"); }
                catch (Exception ex)
                {
                    int hr = ex.HResult;
                    Console.WriteLine($"  dyn {label,-26} {ex.GetType().Name} hr=0x{hr:X8} :: {(ex.Message ?? "").Split('\n')[0]}");
                }
            }
        }

        Rel(sess); Rel(conn);
        return 0;
    }

    // ---------------- concurrency / crash behaviour ----------------

    /// <summary>
    /// K threads, each on its own session over one connector, reading the same way the old
    /// harness did — including the index-based Get(0) that used to take the process down with
    /// 0xC0000005. Run once per style so the two can be blamed separately.
    /// </summary>
    static int Conc(string cfg, string ver, int k, int reps, string style)
    {
        var (name, cs) = LoadCfg(cfg);
        Disp.Context = name; Disp.Version = ver;
        object conn = Activation.CreateFromPath(Versions[ver]);
        Console.WriteLine($"conc base={name} ver={ver} k={k} reps={reps} style={style}");

        foreach (string st in style == "both" ? new[] { "dyn", "disp" } : new[] { style })
        {
            var errs = new System.Collections.Concurrent.ConcurrentBag<string>();
            int ok = 0;
            var sw = Stopwatch.StartNew();
            var threads = new List<Thread>();
            for (int t = 0; t < k; t++)
            {
                var th = new Thread(() =>
                {
                    object sess = null;
                    try
                    {
                        sess = st == "dyn" ? ((dynamic)conn).Connect(cs) : Disp.Call(conn, "Connect", cs);
                        for (int i = 0; i < reps; i++)
                        {
                            object q = null, r = null, s = null;
                            try
                            {
                                if (st == "dyn")
                                {
                                    dynamic dq = ((dynamic)sess).NewObject("Запрос"); q = dq;
                                    dq.Текст = "ВЫБРАТЬ ПЕРВЫЕ 20 Наименование ИЗ Справочник.Номенклатура";
                                    dynamic dr = dq.Выполнить(); r = dr;
                                    dynamic ds = dr.Выбрать(); s = ds;
                                    while ((bool)ds.Следующий()) { var _ = ds.Get(0); }
                                }
                                else
                                {
                                    q = Disp.Call(sess, "NewObject", "Запрос");
                                    Disp.Set(q, "Текст", "ВЫБРАТЬ ПЕРВЫЕ 20 Наименование ИЗ Справочник.Номенклатура");
                                    r = Disp.Call(q, "Выполнить");
                                    s = Disp.Call(r, "Выбрать");
                                    while ((bool)Disp.Call(s, "Следующий")) { var _ = Disp.Call(s, "Get", 0); }
                                }
                                Interlocked.Increment(ref ok);
                            }
                            catch (Exception ex) { errs.Add(ex.GetType().Name + ": " + (ex.Message ?? "").Split('\n')[0]); }
                            finally { Rel(s); Rel(r); Rel(q); }
                        }
                    }
                    catch (Exception ex) { errs.Add("SESSION " + ex.GetType().Name + ": " + ex.Message); }
                    finally { Rel(sess); }
                });
                th.SetApartmentState(ApartmentState.MTA);
                threads.Add(th); th.Start();
            }
            foreach (var th in threads) th.Join();
            var p = Process.GetCurrentProcess(); p.Refresh();
            Console.WriteLine($"  {st,-5} ok={ok}/{k * reps} wall={sw.ElapsedMilliseconds}ms " +
                              $"{ok * 1000.0 / Math.Max(1, sw.ElapsedMilliseconds):F1} op/s rss={p.WorkingSet64 / 1024 / 1024}MB " +
                              $"errs={errs.Count}");
            foreach (var e in errs.Distinct().Take(3)) Console.WriteLine("       " + e);
        }
        Rel(conn);
        return 0;
    }

    // ---------------- cleanup ----------------

    static int Cleanup(string cfg, string ver)
    {
        var (name, cs) = LoadCfg(cfg);
        object conn = Activation.CreateFromPath(Versions[ver]);
        object sess = Disp.Call(conn, "Connect", cs);
        int marked = 0;
        try
        {
            foreach (var t in new[] { "РеализацияТоваровУслуг", "ПоступлениеТоваровУслуг",
                                      "СписаниеСРасчетногоСчета", "ПоступлениеНаРасчетныйСчет",
                                      "ОтчетОРозничныхПродажах", "АвансовыйОтчет" })
            {
                object q = null, r = null, s = null;
                try
                {
                    q = Disp.Call(sess, "NewObject", "Запрос");
                    Disp.Set(q, "Текст", $"ВЫБРАТЬ Ссылка, Комментарий ИЗ Документ.{t} ГДЕ Комментарий ПОДОБНО \"{P.MarkerPrefix}%\"");
                    r = Disp.Call(q, "Выполнить"); s = Disp.Call(r, "Выбрать");
                    while ((bool)Disp.Call(s, "Следующий"))
                    {
                        string c = Disp.Get(s, "Комментарий")?.ToString() ?? "";
                        if (!c.StartsWith(MarkerPrefix, StringComparison.Ordinal)) continue;
                        object refx = Disp.Get(s, "Ссылка");
                        object o = Disp.Call(refx, "ПолучитьОбъект");
                        try { Disp.Call(o, "Удалить"); marked++; Console.WriteLine($"  deleted {t} :: {c}"); }
                        catch (OneCException oe) { Console.WriteLine($"  keep {t} :: {oe.Message}"); }
                        finally { Rel(o); Rel(refx); }
                    }
                }
                catch { }
                finally { Rel(s); Rel(r); Rel(q); }
            }
            Console.WriteLine($"  total removed: {marked}");
            return 0;
        }
        finally { Rel(sess); Rel(conn); }
    }
}
