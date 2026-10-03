using Xunit;

namespace OneC.Tests;

/// <summary>
/// The release gate: <c>AIBA_TEST_GATE=1</c>. Outside it, a test whose environment is missing (no
/// live bases, no local backend/1c, a table this base does not have) returns early and counts as
/// passed — the convention for a developer run without 1C. In the gate that would be a test that
/// "passed" without testing anything (2026-10-01: the two Python-target tests did exactly that in a
/// 414/414 run), so it fails instead, with the reason. The live fixture applies the same rule to
/// the bases themselves (<see cref="LiveFixture"/>).
/// </summary>
internal static class Gate
{
    public static bool On => Environment.GetEnvironmentVariable("AIBA_TEST_GATE") == "1";

    /// <summary>True: skip (outside the gate). In the gate a missing precondition fails the test.</summary>
    public static bool Skip(bool missing, string why)
    {
        if (!missing) return false;
        Assert.False(On, "release gate (AIBA_TEST_GATE=1): " + why);
        return true;
    }
}
