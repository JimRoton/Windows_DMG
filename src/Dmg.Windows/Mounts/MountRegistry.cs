using System.Text.Json;
using Dmg.Core;
using Dmg.Core.Diagnostics;

namespace Dmg.Windows.Mounts;

/// <summary>
/// What dmg believes is mounted: a JSON file under
/// <c>%LOCALAPPDATA%\dmg\mounts.json</c>.
/// </summary>
/// <remarks>
/// <para>
/// The registry exists so that <c>dmg list</c> and <c>dmg unmount</c> have
/// something to work from. A mounted DMG is a scratch VHD attached to a drive
/// letter, and nothing in Windows records which image it came from - so if this
/// file is not kept, a user who mounts three images has no way to say which letter
/// belongs to which, and no way to clean up the scratch files afterwards.
/// </para>
/// <para>
/// <b>A broken registry never breaks a command.</b> This file lives in a folder the
/// user can open, is written while a machine can lose power, and is read by a tool
/// whose whole job is other people's disk images. So every failure to read it -
/// missing, empty, truncated mid-write, not JSON, from a future version, full of
/// entries that make no sense - produces a warning and an empty list, and the
/// command carries on. <see cref="Read"/> has no failure path at all; it cannot
/// throw and it does not return a <see cref="Result{T}"/>, because there is no
/// decision for a caller to make.
/// </para>
/// <para>
/// The corollary is that the next write replaces whatever was there. That is
/// deliberate: a registry nobody can read is worth less than an accurate one
/// starting now, and leaving it in place would mean the file stays broken forever.
/// </para>
/// <para>
/// <b>Writes are serialised and atomic.</b> Two terminals mounting at the same
/// second must not each read the list and write back their own copy, so writers
/// take <see cref="RegistryLock"/> across the read-modify-write. The new file is
/// written beside the old one and moved into place, so a reader - or a power cut -
/// sees one whole file or the other, never half of each.
/// </para>
/// </remarks>
public sealed class MountRegistry
{
    /// <summary>The registry file's name.</summary>
    public const string FileName = "mounts.json";

    /// <summary>The lock file's name, beside the registry.</summary>
    public const string LockFileName = "mounts.lock";

    /// <summary>The folder under <c>%LOCALAPPDATA%</c> that holds both.</summary>
    public const string ApplicationFolderName = "dmg";

    /// <summary>The schema version this build writes and the only one it reads.</summary>
    public const int SchemaVersion = 1;

    /// <summary>
    /// A ceiling on how many entries will be read out of the file. Nobody has four
    /// thousand disk images mounted; a file that says so has been damaged or
    /// tampered with, and the point of the bound is that the damage costs a warning
    /// rather than an allocation.
    /// </summary>
    public const int MaximumRecords = 4096;

    /// <summary>A ceiling on the file itself, for the same reason: 8 MiB.</summary>
    public const long MaximumFileBytes = 8L * 1024 * 1024;

    private readonly IOutput? _output;
    private readonly TimeSpan _lockTimeout;

    /// <summary>
    /// Opens the registry in a given folder. The folder is created on first write,
    /// not here - reading a registry that does not exist yet is normal and must not
    /// have side effects.
    /// </summary>
    /// <param name="directoryPath">The folder holding <c>mounts.json</c>.</param>
    /// <param name="output">Where to report a damaged registry. Null says nothing.</param>
    /// <param name="lockTimeout">
    /// How long a writer waits for another process. Defaults to
    /// <see cref="DefaultLockTimeout"/>.
    /// </param>
    public MountRegistry(string directoryPath, IOutput? output = null, TimeSpan? lockTimeout = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);

        DirectoryPath = directoryPath;
        FilePath = Path.Combine(directoryPath, FileName);
        LockPath = Path.Combine(directoryPath, LockFileName);

        _output = output;
        _lockTimeout = lockTimeout ?? DefaultLockTimeout;
    }

    /// <summary>How long a writer waits for another process before giving up: five seconds.</summary>
    public static TimeSpan DefaultLockTimeout => TimeSpan.FromSeconds(5);

    /// <summary>The folder holding the registry.</summary>
    public string DirectoryPath { get; }

    /// <summary>The registry file.</summary>
    public string FilePath { get; }

    /// <summary>The lock file.</summary>
    public string LockPath { get; }

    /// <summary>
    /// The registry for the signed-in user: <c>%LOCALAPPDATA%\dmg</c>.
    /// </summary>
    /// <param name="output">Where to report a damaged registry.</param>
    /// <returns>
    /// The registry, or <see cref="DmgExitCode.InternalError"/> if the user has no
    /// local application data folder - which should not be possible, and is
    /// certainly not something the user can fix.
    /// </returns>
    public static Result<MountRegistry> ForCurrentUser(IOutput? output = null)
    {
        string? localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");

        if (string.IsNullOrWhiteSpace(localAppData))
        {
            localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return Result<MountRegistry>.Failure(DmgError.Internal(
                "dmg could not find the local application data folder, so it cannot record mounts.",
                "Neither %LOCALAPPDATA% nor SpecialFolder.LocalApplicationData is set."));
        }

        return Result<MountRegistry>.Success(
            new MountRegistry(Path.Combine(localAppData, ApplicationFolderName), output));
    }

    /// <summary>
    /// Everything the registry holds, in the order it was written.
    /// </summary>
    /// <remarks>
    /// Never throws and never fails. A registry that cannot be read is reported as a
    /// warning and comes back empty - see the class remarks.
    /// </remarks>
    public IReadOnlyList<MountRecord> Read()
    {
        Result<string> contents = ReadContents();

        if (!contents.TryGetValue(out string? json))
        {
            Report(contents.Error);
            return [];
        }

        if (json.Length == 0)
        {
            return [];
        }

        MountRegistryDocument? document;

        try
        {
            document = JsonSerializer.Deserialize(json, MountRegistryJson.Default.MountRegistryDocument);
        }
        catch (JsonException exception)
        {
            Report(Damaged(
                "it is not valid JSON",
                $"{exception.Message} Delete it, or unmount and mount again, to start a fresh one."));

            return [];
        }

        if (document is null)
        {
            Report(Damaged("it holds nothing", "The file deserialized to null."));
            return [];
        }

        if (document.Version != SchemaVersion)
        {
            // Not parsed hopefully. A future version may mean anything by these
            // fields, and quietly acting on a half-understood file is worse than
            // ignoring it.
            Report(Damaged(
                $"it was written by a different version of dmg (schema {document.Version})",
                $"This build reads schema {SchemaVersion}."));

            return [];
        }

        return Validated(document.Mounts ?? []);
    }

    /// <summary>
    /// Adds a mount.
    /// </summary>
    /// <param name="record">
    /// The mount to record. Its id is replaced if it is blank or already taken, so
    /// the returned record is the one that was actually stored.
    /// </param>
    /// <returns>The stored record, or why it could not be stored.</returns>
    public Result<MountRecord> Add(MountRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        Result validated = record.Validate();

        if (!validated.Ok && !string.IsNullOrWhiteSpace(record.Id))
        {
            return validated.CastFailure<MountRecord>();
        }

        return Update(records =>
        {
            MountRecord stored = record with { Id = UnusedId(records, record.Id) };
            Result recheck = stored.Validate();

            if (!recheck.Ok)
            {
                return recheck.CastFailure<MountRecord>();
            }

            records.Add(stored);

            return Result<MountRecord>.Success(stored);
        });
    }

    /// <summary>
    /// Removes a mount by id.
    /// </summary>
    /// <param name="id">The id from <see cref="MountRecord.Id"/>, matched case-insensitively.</param>
    /// <returns>
    /// True when an entry was removed, false when there was no such id - which is
    /// not an error: unmounting something that is already gone has got the user what
    /// they wanted.
    /// </returns>
    public Result<bool> Remove(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        return Update(records =>
            Result<bool>.Success(
                records.RemoveAll(record =>
                    string.Equals(record.Id, id, StringComparison.OrdinalIgnoreCase)) > 0));
    }

    /// <summary>
    /// Replaces the whole registry - what reconciliation needs when it has decided
    /// which mounts are still real.
    /// </summary>
    /// <param name="records">The new contents. Invalid entries are refused, not silently dropped.</param>
    public Result Replace(IReadOnlyList<MountRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        foreach (MountRecord record in records)
        {
            ArgumentNullException.ThrowIfNull(record, nameof(records));

            Result validated = record.Validate();

            if (!validated.Ok)
            {
                return validated;
            }
        }

        return Update(existing =>
        {
            existing.Clear();
            existing.AddRange(records);

            return Result<bool>.Success(true);
        }).Discard();
    }

    /// <summary>
    /// Runs a change under the writer lock and saves the result.
    /// </summary>
    private Result<T> Update<T>(Func<List<MountRecord>, Result<T>> change)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return Result<T>.Failure(
                DmgExitCode.MountFailed,
                $"dmg could not create '{DirectoryPath}' to record the mount.",
                exception.Message);
        }

        Result<RegistryLock> acquired = RegistryLock.Acquire(LockPath, _lockTimeout);

        if (!acquired.TryGetValue(out RegistryLock? held))
        {
            return acquired.CastFailure<T>();
        }

        using (held)
        {
            // Read inside the lock. Reading outside it is the bug this lock exists
            // to prevent: two writers would each see the list as it was before the
            // other one added to it.
            List<MountRecord> records = [.. Read()];

            Result<T> changed = change(records);

            if (!changed.Ok)
            {
                return changed;
            }

            Result written = Write(records);

            return written.Ok ? changed : written.CastFailure<T>();
        }
    }

    /// <summary>
    /// Serialises and replaces the file, via a temporary beside it so that a crash
    /// mid-write cannot leave half a registry.
    /// </summary>
    private Result Write(IReadOnlyList<MountRecord> records)
    {
        string temporaryPath = FilePath + ".tmp";

        try
        {
            string json = JsonSerializer.Serialize(
                new MountRegistryDocument(SchemaVersion, records),
                MountRegistryJson.Default.MountRegistryDocument);

            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, FilePath, overwrite: true);

            return Result.Success();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            TryDelete(temporaryPath);

            return Result.Failure(
                DmgExitCode.MountFailed,
                $"dmg could not write the mount registry at '{FilePath}'.",
                exception.Message);
        }
    }

    /// <summary>Reads the file's text, bounded, or says why it could not.</summary>
    private Result<string> ReadContents()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                // The ordinary state of a machine that has never mounted anything.
                // Not a warning, not an error, not worth a word.
                return Result<string>.Success(string.Empty);
            }

            using FileStream stream = new(
                FilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            if (stream.Length > MaximumFileBytes)
            {
                return Result<string>.Failure(Damaged(
                    "it is far larger than a mount registry could be",
                    $"{stream.Length} bytes; the ceiling is {MaximumFileBytes}."));
            }

            using StreamReader reader = new(stream);
            string json = reader.ReadToEnd();

            if (string.IsNullOrWhiteSpace(json))
            {
                // A zero-length file is what a crash between create and write leaves
                // behind. It is not an error to recover from, but it is worth
                // saying, because the mounts that were in it are gone.
                return Result<string>.Failure(Damaged("it is empty", "The file has no contents."));
            }

            return Result<string>.Success(json);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Result<string>.Failure(Damaged("it could not be opened", exception.Message));
        }
    }

    /// <summary>
    /// Keeps the entries that make sense and reports the ones that do not, one at a
    /// time - a single bad line costs the user that line and nothing else.
    /// </summary>
    private IReadOnlyList<MountRecord> Validated(IReadOnlyList<MountRecord> records)
    {
        if (records.Count > MaximumRecords)
        {
            Report(Damaged(
                "it lists more mounts than could possibly exist",
                $"{records.Count} entries; the ceiling is {MaximumRecords}."));

            return [];
        }

        List<MountRecord> kept = new(records.Count);
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (MountRecord? record in records)
        {
            if (record is null)
            {
                Report(Damaged("one of its entries is empty", "A null entry was ignored."));
                continue;
            }

            Result validated = record.Validate();

            if (!validated.Ok)
            {
                Report(validated.Error);
                continue;
            }

            if (!seen.Add(record.Id))
            {
                Report(DmgError.Internal(
                    $"A mount registry entry was ignored because the id '{record.Id}' appears twice.",
                    $"source='{record.SourcePath}'."));

                continue;
            }

            kept.Add(record);
        }

        return kept;
    }

    /// <summary>An id that is not already in use, keeping the caller's if it is free.</summary>
    private static string UnusedId(List<MountRecord> records, string? preferred)
    {
        string candidate = string.IsNullOrWhiteSpace(preferred) ? MountRecord.NewId() : preferred;

        // Eight hex characters over a handful of mounts collide about as often as
        // never, but "about as often as never" is what a retry loop is for.
        while (records.Exists(record =>
            string.Equals(record.Id, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = MountRecord.NewId();
        }

        return candidate;
    }

    private DmgError Damaged(string why, string detail) =>
        DmgError.Internal(
            $"The mount registry at '{FilePath}' was ignored because {why}. dmg is carrying on "
            + "with an empty list of mounts; anything already mounted stays mounted and will "
            + "need unmounting by hand.",
            detail);

    private void Report(DmgError error) => _output?.Warning(error.Message);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Nothing useful to do about a stray temporary file, and the failure
            // that brought us here is the one worth reporting.
        }
    }
}
