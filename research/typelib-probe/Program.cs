using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

// Reads comcntr.dll's embedded type library straight from the file (REGKIND_NONE — nothing is
// registered) and prints every interface with its vtable slots, so the connector's dual
// interface can be called by vtable without the registered typelib IDispatch depends on.

const int REGKIND_NONE = 2;
[DllImport("oleaut32", CharSet = CharSet.Unicode, PreserveSig = false)]
static extern ITypeLib LoadTypeLibEx(string file, int regKind);

string path = args.Length > 0 ? args[0] : @"C:\Program Files\1cv8\8.3.15.1565\bin\comcntr.dll";
var tl = LoadTypeLibEx(path, REGKIND_NONE);
tl.GetLibAttr(out IntPtr pla);
var la = Marshal.PtrToStructure<TYPELIBATTR>(pla);
Console.WriteLine($"{path}\ntypelib {la.guid} v{la.wMajorVerNum}.{la.wMinorVerNum}");
tl.ReleaseTLibAttr(pla);

for (int i = 0; i < tl.GetTypeInfoCount(); i++)
{
    tl.GetTypeInfo(i, out ITypeInfo ti);
    tl.GetDocumentation(i, out string name, out _, out _, out _);
    ti.GetTypeAttr(out IntPtr pa);
    var ta = Marshal.PtrToStructure<TYPEATTR>(pa);
    Console.WriteLine($"\n[{ta.typekind}] {name} {ta.guid} funcs={ta.cFuncs} flags={ta.wTypeFlags} cbVft={ta.cbSizeVft}");

    // For a dual interface's dispatch view, the vtable half lives in the related TKIND_INTERFACE.
    ITypeInfo view = ti;
    if (ta.typekind == TYPEKIND.TKIND_DISPATCH && (ta.wTypeFlags & TYPEFLAGS.TYPEFLAG_FDUAL) != 0)
    {
        ti.GetRefTypeOfImplType(-1, out int href);
        ti.GetRefTypeInfo(href, out view);
        Console.WriteLine("  (dual: showing the vtable interface)");
    }
    view.GetTypeAttr(out IntPtr pva);
    var va = Marshal.PtrToStructure<TYPEATTR>(pva);
    for (int f = 0; f < va.cFuncs; f++)
    {
        view.GetFuncDesc(f, out IntPtr pfd);
        var fd = Marshal.PtrToStructure<FUNCDESC>(pfd);
        var names = new string[fd.cParams + 1];
        view.GetNames(fd.memid, names, names.Length, out int got);
        Console.WriteLine($"  slot {fd.oVft / IntPtr.Size,2} {fd.invkind,-22} {names[0]}({string.Join(", ", names.Skip(1).Take(got - 1))}) " +
                          $"params={fd.cParams} ret={(VarEnum)fd.elemdescFunc.tdesc.vt}");
        view.ReleaseFuncDesc(pfd);
    }
    view.ReleaseTypeAttr(pva);
    ti.ReleaseTypeAttr(pa);
}
