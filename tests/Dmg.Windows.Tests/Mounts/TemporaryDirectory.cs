namespace Dmg.Windows.Tests.Mounts;

/// <summary>
/// A scratch folder for one test, deleted when the test finishes.
/// </summary>
/// <remarks>
/// <para>
/// <b>No test may touch the real <c>%LOCALAPPDATA%\dmg</c>.</b> The registry is a
/// file the running user's actual mounts are recorded in; a test that wrote to it
/// would delete somebody's list of attached disks, and a test that read it would
/// pass or fail depending on what the developer happened to have mounted. Every
/// <see cref="Dmg.Windows.Mounts.MountRegistry"/> in these tests is constructed
/// with a path from here instead.
/// </para>
/// <para>
/// Deletion is best-effort. A leftover folder under the system temporary directory
/// is harmless, and throwing out of <see cref="Dispose"/> would replace a real
/// assertion failure with a cleanup one.
/// </para>
/// </remarks>
public sealed class TemporaryDirectory : IDisposable
{
    /// <summary>Creates the folder.</summary>
    public TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "dmg-tests",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(Path);
    }

    /// <summary>The folder, which exists for as long as this object is not disposed.</summary>
    public string Path { get; }

    /// <summary>A path inside the folder.</summary>
    public string File(string name) => System.IO.Path.Combine(Path, name);

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // Nothing worth failing a test over.
        }
    }
}
