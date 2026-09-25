namespace OneC.Interop;

/// <summary>
/// Member DISPIDs of ONE object, resolved once and reused while that object lives — for hot
/// loops such as reading the same columns of a <c>Выборка</c> on every row, where name lookup
/// was half the COM crossings.
///
/// This is not the global cache <see cref="Dispatch"/> refuses to keep: DISPIDs collide across
/// 1C types, but the key here is the object itself, and an object does not renumber its own
/// members. Do not share a memo between objects.
/// </summary>
public sealed class DispatchMemo
{
    private readonly object _target;
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);

    public DispatchMemo(object target) => _target = target;

    public object Target => _target;

    public object? Get(string member, ErrorContext ctx) =>
        Dispatch.InvokeResolved(_target, Id(member, ctx), member, Dispatch.PropertyGet, ctx, Array.Empty<object?>());

    public object? Call(string member, ErrorContext ctx, params object?[] args) =>
        Dispatch.InvokeResolved(_target, Id(member, ctx), member, Dispatch.MethodOrGet, ctx, args);

    public bool CallBool(string member, ErrorContext ctx, params object?[] args) => Convert.ToBoolean(Call(member, ctx, args));

    public bool GetBool(string member, ErrorContext ctx) => Convert.ToBoolean(Get(member, ctx));

    private int Id(string member, ErrorContext ctx)
    {
        if (!_ids.TryGetValue(member, out int id))
            _ids[member] = id = Dispatch.Resolve(_target, member, ctx);
        return id;
    }
}
