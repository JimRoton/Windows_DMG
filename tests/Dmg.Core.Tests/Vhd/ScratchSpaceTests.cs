using Dmg.Core.Vhd;

namespace Dmg.Core.Tests.Vhd;

/// <summary>
/// Covers scratch directory management: paths built only from generated mount
/// ids, a directory that really is created, and cleanup that happens unless
/// <c>--keep-scratch</c> says otherwise.
/// </summary>
/// <remarks>
/// Every test that touches the filesystem does so under its own temporary root
/// and removes it afterwards, so the suite never writes to the real
/// <c>%LOCALAPPDATA%\dmg\scratch</c> and two runs cannot collide.
/// </remarks>
public sealed class ScratchSpaceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "dmg-scratch-tests",
        Guid.NewGuid().ToString("N"));

    private ScratchOptions Options(bool keep = false) => new()
    {
        Root = _root,
        KeepScratch = keep,
    };

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void TheDefaultRootIsLocalAppDataDmgScratch()
    {
        string root = ScratchLayout.DefaultRoot();

        Assert.True(Path.IsPathFullyQualified(root), $"'{root}' should be absolute.");
        Assert.Equal("scratch", Path.GetFileName(root));
        Assert.Equal("dmg", Path.GetFileName(Path.GetDirectoryName(root)));

        string localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);

        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            // On Windows this folder is %LOCALAPPDATA%; the same code resolves the
            // platform equivalent on the Mac the suite runs on.
            Assert.Equal(
                Path.Combine(localAppData, "dmg", "scratch"),
                root);
        }
    }

    [Fact]
    public void CreatesADirectoryNamedAfterAGeneratedMountId()
    {
        using ScratchSpace scratch = Create();

        Assert.True(Directory.Exists(scratch.Directory));
        Assert.True(scratch.Id.IsValid);
        Assert.Equal(scratch.Id.Value, Path.GetFileName(scratch.Directory));
        Assert.True(ScratchLayout.IsWithin(_root, scratch.Directory));
    }

    [Fact]
    public void NamesTheVhdAfterTheMountIdAndNothingElse()
    {
        using ScratchSpace scratch = Create();

        Assert.Equal(scratch.Id.Value + ".vhd", Path.GetFileName(scratch.VhdPath));
        Assert.Equal(scratch.Directory, Path.GetDirectoryName(scratch.VhdPath));
        Assert.True(ScratchLayout.IsWithin(scratch.Directory, scratch.VhdPath));
    }

    [Fact]
    public void GivesTwoConcurrentMountsSeparateDirectories()
    {
        using ScratchSpace first = Create();
        using ScratchSpace second = Create();

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.Directory, second.Directory);
        Assert.True(Directory.Exists(first.Directory));
        Assert.True(Directory.Exists(second.Directory));
    }

    [Fact]
    public void DeletesTheDirectoryAndItsContentsOnDispose()
    {
        string directory;

        using (ScratchSpace scratch = Create())
        {
            directory = scratch.Directory;
            File.WriteAllBytes(scratch.VhdPath, new byte[4096]);
            File.WriteAllText(Path.Combine(directory, "leftover.tmp"), "x");
        }

        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void KeepsTheDirectoryWhenKeepScratchIsSet()
    {
        string directory;

        using (ScratchSpace scratch = Create(keep: true))
        {
            directory = scratch.Directory;
            File.WriteAllBytes(scratch.VhdPath, new byte[512]);

            Assert.True(scratch.KeepScratch);
        }

        Assert.True(Directory.Exists(directory), "--keep-scratch must leave the directory behind.");
        Assert.True(File.Exists(Path.Combine(directory, Path.GetFileName(directory) + ".vhd")));
    }

    [Fact]
    public void CleanupIsIdempotentAndReportsSuccess()
    {
        ScratchSpace scratch = Create();

        Assert.True(scratch.Cleanup().Ok);
        Assert.True(scratch.IsCleanedUp);
        Assert.Null(scratch.CleanupError);

        Assert.True(scratch.Cleanup().Ok);

        scratch.Dispose();
        scratch.Dispose();

        Assert.False(Directory.Exists(scratch.Directory));
    }

    [Fact]
    public void CleanupSucceedsWithoutDeletingAnythingWhenTheDirectoryIsKept()
    {
        using ScratchSpace scratch = Create(keep: true);

        Assert.True(scratch.Cleanup().Ok);
        Assert.True(Directory.Exists(scratch.Directory));
        Assert.False(scratch.IsCleanedUp);
    }

    [Fact]
    public void CleanupSucceedsWhenSomethingElseAlreadyRemovedTheDirectory()
    {
        using ScratchSpace scratch = Create();

        Directory.Delete(scratch.Directory, recursive: true);

        Assert.True(scratch.Cleanup().Ok);
        Assert.Null(scratch.CleanupError);
    }

    [Fact]
    public void CreatesMissingParentDirectories()
    {
        string deep = Path.Combine(_root, "one", "two", "three");

        using ScratchSpace scratch = Assert.IsType<ScratchSpace>(
            ScratchSpace.Create(new ScratchOptions { Root = deep }).GetValueOrDefault());

        Assert.True(Directory.Exists(scratch.Directory));
        Assert.True(ScratchLayout.IsWithin(deep, scratch.Directory));
    }

    [Fact]
    public void ReportsAFailureRatherThanThrowingWhenTheRootCannotBeCreated()
    {
        string file = Path.Combine(_root, "not-a-directory");
        Directory.CreateDirectory(_root);
        File.WriteAllText(file, "occupied");

        Result<ScratchSpace> created = ScratchSpace.Create(new ScratchOptions { Root = file });

        Assert.False(created.Ok);
        Assert.Equal(DmgExitCode.InternalError, created.Error.Code);
    }

    [Fact]
    public void ScratchPathsAreBuiltOnlyFromAMountId()
    {
        // There is no API that takes a name from the image: DirectoryFor and
        // FileNameFor accept a MountId and nothing else, and a default MountId -
        // the only one an unchecked caller could conjure - is refused.
        Assert.Throws<ArgumentException>(() => ScratchLayout.DirectoryFor(_root, default));
        Assert.Throws<ArgumentException>(() => ScratchLayout.FileNameFor(default));

        MountId id = MountId.New();

        Assert.Equal(id.Value + ".vhd", ScratchLayout.FileNameFor(id));
        Assert.Equal(
            Path.Combine(Path.GetFullPath(_root), id.Value, id.Value + ".vhd"),
            ScratchLayout.VhdPathFor(_root, id));
    }

    [Theory]
    [InlineData("child", true)]
    [InlineData("child/grandchild", true)]
    [InlineData("", false)]
    [InlineData("..", false)]
    [InlineData("../sibling", false)]
    [InlineData("child/../..", false)]
    public void IsWithinAnswersForResolvedPathsNotForTextPrefixes(string relative, bool expected)
    {
        string candidate = relative.Length == 0
            ? _root
            : Path.Combine(_root, relative.Replace('/', Path.DirectorySeparatorChar));

        Assert.Equal(expected, ScratchLayout.IsWithin(_root, candidate));
    }

    [Fact]
    public void IsWithinIsNotFooledByASiblingWithTheSamePrefix()
    {
        Assert.False(ScratchLayout.IsWithin(_root, _root + "-evil"));
    }

    private ScratchSpace Create(bool keep = false)
    {
        Result<ScratchSpace> created = ScratchSpace.Create(Options(keep));

        Assert.True(created.TryGetValue(out ScratchSpace? scratch), "Creating scratch under a temp root should work.");

        return scratch;
    }
}
