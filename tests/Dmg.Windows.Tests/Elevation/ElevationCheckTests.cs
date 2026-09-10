using Dmg.Core;
using Dmg.Windows.Elevation;
using Dmg.Windows.Tests.Fakes;
using Dmg.Windows.VirtualDisk;

namespace Dmg.Windows.Tests.Elevation;

/// <summary>
/// The elevation check is the first thing a mount does and the last thing anybody
/// wants to discover late. These tests hold it to three promises: it asks about the
/// right thing, it answers before any work happens, and when the answer is no it
/// says exactly what to do - without ever offering to do it.
/// </summary>
public sealed class ElevationCheckTests
{
    [Fact]
    public void AnElevatedShellPasses()
    {
        Result result = ElevationCheck.RequireManageVolume(FakePrivilegeService.Elevated());

        Assert.True(result.Ok, result.Ok ? "" : result.Error.ToString());
        Assert.Equal(DmgExitCode.Success, result.ExitCode);
    }

    [Fact]
    public void APrivilegeThatIsHeldButSwitchedOffStillPasses()
    {
        // The case a naive check gets wrong. Windows hands most privileges over
        // disabled, including this one, and a process can switch its own on without
        // a prompt - so "disabled" means yes. Reporting "you are not elevated" to
        // somebody sitting in an elevated shell is both wrong and unactionable.
        FakePrivilegeService token = new FakePrivilegeService()
            .Holding(WindowsPrivilege.ManageVolume, PrivilegeState.Disabled);

        Assert.True(ElevationCheck.RequireManageVolume(token).Ok);
    }

    [Fact]
    public void AnEnabledPrivilegePasses()
    {
        FakePrivilegeService token = new FakePrivilegeService()
            .Holding(WindowsPrivilege.ManageVolume, PrivilegeState.Enabled);

        Assert.True(ElevationCheck.RequireManageVolume(token).Ok);
    }

    [Fact]
    public void AnUnelevatedShellIsRefusedWithExitSeven()
    {
        Result result = ElevationCheck.RequireManageVolume(FakePrivilegeService.Unelevated());

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.ElevationRequired, result.Error.Code);
        Assert.Equal(7, (int)result.ExitCode);
    }

    [Fact]
    public void TheCheckAsksAboutManageVolumeAndNothingElse()
    {
        FakePrivilegeService token = FakePrivilegeService.Unelevated();

        ElevationCheck.RequireManageVolume(token);

        Assert.Equal(WindowsPrivilege.ManageVolume, Assert.Single(token.Calls));
        Assert.Equal("SeManageVolumePrivilege", WindowsPrivilege.ManageVolume);
    }

    [Fact]
    public void TheCheckIsOneQueryAndNothingElse()
    {
        // The whole reason it can run before any work: one system call, no file I/O,
        // nothing to undo if it fails.
        FakePrivilegeService token = FakePrivilegeService.Elevated();

        ElevationCheck.RequireManageVolume(token);

        Assert.Single(token.Calls);
    }

    [Fact]
    public void TheRefusalUsesTheDisplayNameAUserWouldRecogniseFromWindows()
    {
        // Nobody finds "SeManageVolumePrivilege" in secpol.msc. The console only
        // ever shows "Perform volume maintenance tasks".
        DmgError error = Refusal();

        Assert.Contains(
            WindowsPrivilege.ManageVolumeDisplayName,
            error.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TheRefusalStatesTheExactRemedy()
    {
        DmgError error = Refusal();

        Assert.Contains("Run as administrator", error.Message, StringComparison.Ordinal);
        Assert.Contains("Windows Terminal", error.Message, StringComparison.Ordinal);
        Assert.Contains("run the same command again", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRefusalSaysDmgWillNotRaiseAUacPrompt()
    {
        // Not decoration. A user who is told to open an elevated shell will wonder
        // why the tool did not just ask, and the answer - that a CLI which pops a
        // consent dialog breaks every unattended script that calls it - belongs in
        // front of them rather than in a design document.
        DmgError error = Refusal();

        Assert.Contains("will not raise a UAC prompt", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRefusalSaysNothingHasBeenWrittenYet()
    {
        DmgError error = Refusal();

        Assert.Contains("before doing any work", error.Message, StringComparison.Ordinal);
        Assert.Contains("no files have been written", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRefusalIsWordedIdenticallyToTheOneWindowsItselfProvokes()
    {
        // Two paths reach the same wall: this pre-flight check, and virtdisk.dll
        // returning ERROR_ACCESS_DENIED to somebody who got past it. A user who
        // hits both must not be told two different things.
        DmgError error = Refusal();

        Assert.EndsWith(VirtualDiskErrors.ElevationRemedy, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDetailNamesThePrivilegeAndTheStateForABugReport()
    {
        DmgError error = Refusal();

        Assert.Contains(WindowsPrivilege.ManageVolume, error.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("Absent", error.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void AFailureToReadTheTokenIsNotReportedAsMissingElevation()
    {
        // "You are not elevated" would be a guess, and a wrong one: nobody knows
        // what the token says. A broken query is a bug in dmg and exits as one.
        FakePrivilegeService token = new()
        {
            QueryFailure = DmgError.Internal("The token could not be read.", "test"),
        };

        Result result = ElevationCheck.RequireManageVolume(token);

        Assert.False(result.Ok);
        Assert.Equal(DmgExitCode.InternalError, result.Error.Code);
    }

    [Fact]
    public void AnUnrelatedPrivilegeDoesNotSatisfyTheCheck()
    {
        FakePrivilegeService token = new FakePrivilegeService()
            .Holding("SeBackupPrivilege", PrivilegeState.Enabled);

        Assert.False(ElevationCheck.RequireManageVolume(token).Ok);
    }

    [Fact]
    public void PrivilegeNamesAreMatchedTheWayWindowsMatchesThem()
    {
        // LookupPrivilegeValue is case-sensitive, so the fake is too, so a typo in a
        // constant fails here rather than on a user's machine.
        FakePrivilegeService token = new FakePrivilegeService()
            .Holding("semanagevolumeprivilege", PrivilegeState.Enabled);

        Assert.False(ElevationCheck.RequireManageVolume(token).Ok);
    }

    [Theory]
    [InlineData(PrivilegeState.Absent, false)]
    [InlineData(PrivilegeState.Disabled, true)]
    [InlineData(PrivilegeState.Enabled, true)]
    public void HeldMeansPresentRatherThanSwitchedOn(PrivilegeState state, bool held) =>
        Assert.Equal(held, ElevationCheck.IsHeld(state));

    [Fact]
    public void AbsentIsTheDefaultSoAnUnsetStateDenies() =>
        Assert.Equal(PrivilegeState.Absent, default);

    [Fact]
    public void QueryReportsTheStateWithoutDecidingAnything()
    {
        FakePrivilegeService token = FakePrivilegeService.Elevated();

        Result<PrivilegeState> result = ElevationCheck.Query(token, WindowsPrivilege.ManageVolume);

        Assert.True(result.TryGetValue(out PrivilegeState state));
        Assert.Equal(PrivilegeState.Disabled, state);
    }

    [Fact]
    public void AnyNamedPrivilegeCanBeChecked()
    {
        FakePrivilegeService token = new FakePrivilegeService()
            .Holding("SeBackupPrivilege", PrivilegeState.Enabled);

        Assert.True(ElevationCheck.Require(token, "SeBackupPrivilege").Ok);
        Assert.False(ElevationCheck.Require(token, "SeRestorePrivilege").Ok);
    }

    [Fact]
    public void ANullServiceIsARejectedArgument() =>
        Assert.Throws<ArgumentNullException>(() => ElevationCheck.RequireManageVolume(null!));

    [Fact]
    public void ABlankPrivilegeNameIsARejectedArgument() =>
        Assert.Throws<ArgumentException>(() =>
            ElevationCheck.Require(FakePrivilegeService.Unelevated(), "  "));

    [Fact]
    public void TheErrorFactoryRejectsABlankName() =>
        Assert.Throws<ArgumentException>(() => ElevationCheck.NotHeld(""));

    [Fact]
    public void AnUnknownPrivilegeStillGetsAUsableMessage()
    {
        // No display name for it, so the raw name has to do - but the remedy is
        // still there, which is the part the user acts on.
        DmgError error = ElevationCheck.NotHeld("SeSomethingElsePrivilege");

        Assert.Equal(DmgExitCode.ElevationRequired, error.Code);
        Assert.Contains("SeSomethingElsePrivilege", error.Message, StringComparison.Ordinal);
        Assert.EndsWith(VirtualDiskErrors.ElevationRemedy, error.Message, StringComparison.Ordinal);
    }

    private static DmgError Refusal()
    {
        Result result = ElevationCheck.RequireManageVolume(FakePrivilegeService.Unelevated());

        Assert.False(result.Ok);

        return result.Error;
    }
}
