using Dmg.Core;
using Dmg.Windows.Projection;

namespace Dmg.Windows.Tests.Projection;

/// <summary>
/// The ProjFS <c>HRESULT</c> mapping: the part of the projection path that can be
/// proven on any machine.
/// </summary>
/// <remarks>
/// <para>
/// The ProjFS calls themselves only run on Windows, and nothing in this repository
/// can exercise them elsewhere. That is exactly why the interpretation of their
/// failures lives in a pure class - so the question "what does dmg tell the user
/// when this goes wrong" has an answer that is tested, even though the going-wrong
/// cannot be reproduced here.
/// </para>
/// <para>
/// The case worth most is the feature being switched off. ProjFS is an optional
/// Windows component that is not on by default, so it is the most likely first
/// experience of this verb, and the one where a raw HRESULT would be least
/// forgivable.
/// </para>
/// </remarks>
public sealed class ProjectionErrorsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void NonNegativeHresultsAreSuccess(int hresult) =>
        Assert.True(ProjectionErrors.Succeeded(hresult));

    [Theory]
    [InlineData(ProjectionErrors.AccessDenied)]
    [InlineData(ProjectionErrors.DirectoryNotEmpty)]
    [InlineData(ProjectionErrors.NotSupported)]
    public void NegativeHresultsAreFailures(int hresult) =>
        Assert.False(ProjectionErrors.Succeeded(hresult));

    [Fact]
    public void MappingASuccessCodeIsARejectedRequest()
    {
        // Asking what a success "means" is a bug at the call site, not a failure to
        // describe. It throws rather than inventing an error.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ProjectionErrors.FromNative(
                ProjectionErrors.Success,
                ProjectionOperation.StartVirtualizing,
                @"C:\projection"));
    }

    [Fact]
    public void AFeatureThatIsNotInstalledIsReportedAsSomethingToTurnOn()
    {
        DmgError error = ProjectionErrors.FeatureUnavailable("ProjectedFSLib.dll was not found.");

        // Unsupported, not MountFailed: the image is fine and dmg is fine; this
        // machine simply cannot do it yet. Same code HFS+ gets, for the same reason.
        Assert.Equal(DmgExitCode.UnsupportedFormat, error.Code);

        // The message has to carry the actual command, because "enable ProjFS" is
        // not something a user can act on without looking it up.
        Assert.Contains("Client-ProjFS", error.Message, StringComparison.Ordinal);
        Assert.Contains("Enable-WindowsOptionalFeature", error.Message, StringComparison.Ordinal);
        Assert.Contains("ProjectedFSLib.dll", error.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void ARootThatAlreadyHasFilesInItIsAUsageError()
    {
        DmgError error = ProjectionErrors.FromNative(
            ProjectionErrors.DirectoryNotEmpty,
            ProjectionOperation.MarkDirectory,
            @"C:\projection");

        Assert.Equal(DmgExitCode.UsageError, error.Code);
        Assert.Contains("empty", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"C:\projection", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARootThatIsAlreadyAProjectionSaysSo()
    {
        DmgError error = ProjectionErrors.FromNative(
            ProjectionErrors.ReparsePointEncountered,
            ProjectionOperation.MarkDirectory,
            @"C:\projection");

        Assert.Equal(DmgExitCode.UsageError, error.Code);
        Assert.Contains("already a projection root", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AVolumeThatCannotHostAProjectionNamesTheRequirement()
    {
        DmgError error = ProjectionErrors.FromNative(
            ProjectionErrors.NotSupported,
            ProjectionOperation.StartVirtualizing,
            @"Z:\projection");

        Assert.Equal(DmgExitCode.UnsupportedFormat, error.Code);
        Assert.Contains("NTFS", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AccessDeniedIsAMountFailureThatSuggestsSomewhereWritable()
    {
        DmgError error = ProjectionErrors.FromNative(
            ProjectionErrors.AccessDenied,
            ProjectionOperation.MarkDirectory,
            @"C:\Windows\projection");

        Assert.Equal(DmgExitCode.MountFailed, error.Code);
        Assert.Contains("write to", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParametersWeBuiltOurselvesComeBackAsOurBug()
    {
        // E_INVALIDARG means dmg assembled the structure wrongly. Blaming the user's
        // image for that would send them chasing a file that is fine.
        DmgError error = ProjectionErrors.FromNative(
            ProjectionErrors.InvalidParameter,
            ProjectionOperation.WritePlaceholderInfo,
            @"C:\projection");

        Assert.Equal(DmgExitCode.InternalError, error.Code);
        Assert.Contains("bug in dmg", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnrecognisedHresultStillGetsAnExitCodeAndTheRawValue()
    {
        DmgError error = ProjectionErrors.FromNative(
            unchecked((int)0x8007DEAD),
            ProjectionOperation.WriteFileData,
            @"C:\projection");

        Assert.Equal(DmgExitCode.MountFailed, error.Code);
        Assert.Contains("0x8007DEAD", error.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ProjectionOperation.MarkDirectory, "PrjMarkDirectoryAsPlaceholder")]
    [InlineData(ProjectionOperation.StartVirtualizing, "PrjStartVirtualizing")]
    [InlineData(ProjectionOperation.WriteFileData, "PrjWriteFileData")]
    [InlineData(ProjectionOperation.FillDirEntryBuffer, "PrjFillDirEntryBuffer")]
    public void TheDetailNamesTheCallThatFailed(ProjectionOperation operation, string expected)
    {
        // The detail is what a bug report needs: which call, and what it returned.
        DmgError error = ProjectionErrors.FromNative(
            ProjectionErrors.AccessDenied,
            operation,
            @"C:\projection");

        Assert.Contains(expected, error.Detail, StringComparison.Ordinal);
    }
}
