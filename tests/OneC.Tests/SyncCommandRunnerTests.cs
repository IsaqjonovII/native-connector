using System.Text.Json.Nodes;
using OneC.Ipc;
using OneC.Supervisor;
using Xunit;

namespace OneC.Tests;

/// <summary>R9: which host operation a cloud command becomes, and which outcomes are reported vs left for a retry.</summary>
public sealed class SyncCommandRunnerTests
{
    [Theory]
    [InlineData("document.create", Ops.Create, "docType,body,post")]
    [InlineData("document.update", Ops.Update, "docType,ref,fields,post,autoUnpost,expected,expectedVersion")]
    [InlineData("document.post", Ops.Post, "docType,ref")]
    [InlineData("document.unpost", Ops.Unpost, "docType,ref")]
    [InlineData("document.markDeleted", Ops.MarkDeleted, "docType,ref")]
    [InlineData("catalog.create", Ops.CatalogCreate, "catalog,body")]
    [InlineData("catalog.update", Ops.CatalogUpdate, "catalog,ref,expected,set")]
    public void EveryNormalPrimitiveMapsToItsHostOperationAndOnlyItsArguments(string kind, string op, string keys)
    {
        var all = new JsonObject
        {
            ["docType"] = "D", ["catalog"] = "C", ["ref"] = "r", ["body"] = new JsonObject(), ["fields"] = new JsonObject(), ["post"] = true,
            ["autoUnpost"] = true, ["expected"] = new JsonObject(), ["expectedVersion"] = "AAA=", ["set"] = new JsonObject(),
            ["exchange"] = true, ["hard"] = true
        };
        var m = CommandRunner.Map(kind, all);
        Assert.NotNull(m);
        Assert.Equal(op, m.Value.Op);
        Assert.Equal(keys.Split(',').Order(), m.Value.Args.Select(kv => kv.Key).Order());   // never exchange, never hard
    }

    [Theory]
    [InlineData("document.delete")]
    [InlineData("catalog.delete")]
    [InlineData("procedure.call")]
    [InlineData("")]
    public void AnythingElseIsNotRun(string kind) => Assert.Null(CommandRunner.Map(kind, new JsonObject { ["docType"] = "D", ["ref"] = "r" }));

    [Fact]
    public void OnlyAnAnswerTheCallerCanActOnIsReported()
    {
        Assert.True(CommandRunner.Retryable(IpcError.Of(Layers.Transport, "pipe broke")));
        Assert.True(CommandRunner.Retryable(IpcError.Of(Layers.Timeout, "deadline")));
        Assert.True(CommandRunner.Retryable(IpcError.Of(Layers.Busy, "gate full")));
        Assert.True(CommandRunner.Retryable(IpcError.Of(Layers.Runtime, "lock conflict") with { Retryable = true }));
        Assert.True(CommandRunner.Retryable(IpcError.Of(Layers.Host, "host died") with { HostFatal = true }));
        Assert.False(CommandRunner.Retryable(IpcError.Of(Layers.Validation, "bad body")));
        Assert.False(CommandRunner.Retryable(IpcError.Of(Layers.Host, "not AIBA's", kind: ErrorKinds.Forbidden)));
        Assert.False(CommandRunner.Retryable(IpcError.Of(Layers.Runtime, "1C refused the posting")));
    }
}
