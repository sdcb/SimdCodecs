namespace Sdcb.SimdCodecs.Tests;

/// <summary>
/// The entry gate for an external-oracle test: skip when the oracle is missing,
/// <b>unless</b> the environment says it must be there — in which case a missing
/// oracle is a failure.
/// <para>
/// This exists because of the failure mode called out in <c>AGENTS.md</c>: <i>a
/// silently skipped oracle is not a passing oracle.</i> Every oracle harness in
/// this repo degrades to a skip so a clean clone still goes green, which is
/// right for a dev box and actively misleading in CI — a workflow that installs
/// the tool and then quietly skips the tests anyway looks identical to one that
/// runs them. Setting <c>REQUIRE_ORACLES=1</c> (CI does) converts that silence
/// into a red build.
/// </para>
/// <para>
/// All three external oracles are wired up this way — <see cref="Jbig2Oracle"/>,
/// <see cref="OpenJpegOracle"/>, and jpegenc (via
/// <c>JpegEncoderOracleTests.RequireOracle</c>) — and CI installs or builds each
/// one, so none can go quietly missing.
/// </para>
/// <para>
/// The motivating bug: an oracle guard written as
/// <c>if (exe is null) { WriteLine(...); return; }</c> <b>passes</b>, so hundreds
/// of test cases once reported success in CI while asserting nothing at all.
/// </para>
/// </summary>
internal static class OracleGate
{
    /// <summary>Set to <c>1</c>/<c>true</c> in CI so a missing oracle fails instead of skipping.</summary>
    public const string RequireVariable = "REQUIRE_ORACLES";

    /// <summary>True when the environment demands that oracles actually run.</summary>
    public static bool OraclesRequired =>
        Environment.GetEnvironmentVariable(RequireVariable) is "1" or "true" or "TRUE";

    /// <summary>
    /// Continues when <paramref name="available"/>; otherwise fails (when
    /// <see cref="OraclesRequired"/>) or skips with a reported reason.
    /// </summary>
    /// <param name="available">Whether the oracle could be resolved.</param>
    /// <param name="oracle">The oracle's name, for the message.</param>
    /// <param name="reason">Why it is unavailable, including how to install it.</param>
    public static void RequireOrSkip(bool available, string oracle, string reason)
    {
        if (available) return;

        if (OraclesRequired)
        {
            Assert.Fail(
                $"{oracle} is unavailable but {RequireVariable} is set, so it was expected to run. {reason}");
        }

        Assert.Skip($"{oracle} unavailable — skipping. {reason}");
    }
}
