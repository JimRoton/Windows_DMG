using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Dmg.Core;
using Dmg.Core.Projection;
using Dmg.Core.Vhd;

namespace Dmg.Windows.Projection;

/// <summary>
/// The real <see cref="IProjectionService"/>: the Windows Projected File System,
/// driven through <c>ProjectedFSLib.dll</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the first callback interop in the repository.</b> Everything else
/// that touches Windows - <c>virtdisk.dll</c>, volume enumeration, the token query
/// - is a synchronous call that returns a number. ProjFS inverts that: it calls
/// back into this process, on threads it owns, for as long as a projection is
/// running. Three consequences follow, and all three shape this file.
/// </para>
/// <para>
/// <b>The entry points cannot capture anything.</b> Under NativeAOT a native
/// callback must be an <see cref="UnmanagedCallersOnlyAttribute"/> static method,
/// which rules out lambdas, instance methods and closures. The instance a callback
/// belongs to is therefore found in <see cref="Instances"/>, keyed by the token
/// handed to <c>PrjStartVirtualizing</c> as its <c>instanceContext</c> and given
/// back on every callback. The table is static and lives as long as the process.
/// </para>
/// <para>
/// <b>No exception may escape.</b> An exception unwinding into native code is
/// undefined behaviour - not a stack trace, not a crash dump, just whatever
/// happens next. Every callback body is wrapped, and anything unexpected becomes
/// <c>E_FAIL</c>. That is also why the content is wrapped in
/// <see cref="SynchronizedProjectedContent"/> before it is ever reachable from a
/// callback: several of these can be in flight at once.
/// </para>
/// <para>
/// <b>None of this is exercised outside Windows.</b> The decisions that could be
/// tested elsewhere were deliberately put elsewhere - <see cref="ProjectionErrors"/>
/// for what a failure means, <see cref="ExFatProjectedContent"/> for what a path
/// resolves to. What is left here is the part that genuinely needs the operating
/// system, and it arrives unverified until it runs on a real machine.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed unsafe class WindowsProjectionService : IProjectionService
{
    /// <summary><c>HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER)</c>: the enumeration buffer is full.</summary>
    private const int InsufficientBuffer = unchecked((int)0x8007007A);

    /// <summary><c>E_FAIL</c>, for anything this code did not anticipate.</summary>
    private const int Fail = unchecked((int)0x80004005);

    /// <summary><c>PRJ_CB_DATA_FLAG_ENUM_RESTART_SCAN</c>.</summary>
    private const uint RestartScan = 0x00000001;

    /// <summary>Every running projection, by the token ProjFS hands back to us.</summary>
    /// <remarks>
    /// Static because the callbacks are static and have nowhere else to look. The
    /// token is an ordinary counter rather than a pointer to managed memory: a
    /// pointer would have to be pinned for the projection's whole life, and an
    /// integer that means nothing outside this table cannot be dereferenced by
    /// accident.
    /// </remarks>
    private static readonly ConcurrentDictionary<nint, ProjectionInstance> Instances = new();

    private static long _nextToken;

    /// <inheritdoc />
    public Result EnsureAvailable()
    {
        // The cheapest exported function in the library. What is being tested is
        // not the answer but whether the call resolves at all: on a machine where
        // the optional feature is off, the library is not there to bind to.
        try
        {
            _ = ProjFsNative.PrjDoesNameContainWildCards("*");

            return Result.Success();
        }
        catch (DllNotFoundException exception)
        {
            return Result.Failure(ProjectionErrors.FeatureUnavailable(
                $"ProjectedFSLib.dll could not be loaded: {exception.Message}"));
        }
        catch (EntryPointNotFoundException exception)
        {
            return Result.Failure(ProjectionErrors.FeatureUnavailable(
                $"ProjectedFSLib.dll is present but incomplete: {exception.Message}"));
        }
    }

    /// <inheritdoc />
    public Result<IProjectionSession> Start(ProjectionOptions options, IProjectedContent content)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(content);

        Result available = EnsureAvailable();

        if (!available.Ok)
        {
            return available.CastFailure<IProjectionSession>();
        }

        Result<bool> prepared = PrepareRoot(options.RootPath);

        if (!prepared.TryGetValue(out bool rootCreated))
        {
            return prepared.CastFailure<IProjectionSession>();
        }

        Guid instanceId = Guid.NewGuid();

        int marked = ProjFsNative.PrjMarkDirectoryAsPlaceholder(
            options.RootPath,
            targetPathName: null,
            versionInfo: nint.Zero,
            in instanceId);

        // Already a projection root is not a failure to recover from here - the
        // caller is told to pick another folder - but it is worth not treating a
        // second run in the same place as a crash.
        if (!ProjectionErrors.Succeeded(marked))
        {
            return Result<IProjectionSession>.Failure(ProjectionErrors.FromNative(
                marked,
                ProjectionOperation.MarkDirectory,
                options.RootPath));
        }

        // Everything a callback can reach is registered before the projection can
        // possibly call back, and the content is serialised on the way in.
        nint token = (nint)Interlocked.Increment(ref _nextToken);
        ProjectionInstance instance = new(new SynchronizedProjectedContent(content));

        Instances[token] = instance;

        ProjFsNative.PrjCallbacks callbacks = new()
        {
            StartDirectoryEnumerationCallback =
                (nint)(delegate* unmanaged[Stdcall]<ProjFsNative.PrjCallbackData*, Guid*, int>)&StartEnumeration,
            GetDirectoryEnumerationCallback =
                (nint)(delegate* unmanaged[Stdcall]<ProjFsNative.PrjCallbackData*, Guid*, nint, nint, int>)&GetEnumeration,
            EndDirectoryEnumerationCallback =
                (nint)(delegate* unmanaged[Stdcall]<ProjFsNative.PrjCallbackData*, Guid*, int>)&EndEnumeration,
            GetPlaceholderInfoCallback =
                (nint)(delegate* unmanaged[Stdcall]<ProjFsNative.PrjCallbackData*, int>)&GetPlaceholderInfo,
            GetFileDataCallback =
                (nint)(delegate* unmanaged[Stdcall]<ProjFsNative.PrjCallbackData*, ulong, uint, int>)&GetFileData,
        };

        int started = ProjFsNative.PrjStartVirtualizing(
            options.RootPath,
            in callbacks,
            token,
            options: nint.Zero,
            out nint rawContext);

        // Wrapped before anything else can happen, so there is no window in which
        // the context exists and nothing owns it.
        SafeProjectionContextHandle context = new(rawContext);

        if (!ProjectionErrors.Succeeded(started))
        {
            context.Dispose();
            Instances.TryRemove(token, out _);

            return Result<IProjectionSession>.Failure(ProjectionErrors.FromNative(
                started,
                ProjectionOperation.StartVirtualizing,
                options.RootPath));
        }

        instance.Context = context;

        return Result<IProjectionSession>.Success(
            new WindowsProjectionSession(options, token, context, rootCreated));
    }

    /// <summary>
    /// Makes sure the root exists and is empty, which ProjFS requires and which is
    /// worth refusing early rather than as an HRESULT.
    /// </summary>
    /// <returns>
    /// True when this call created the directory, false when it adopted one that
    /// was already there and empty. That decides what cleanup may remove: a folder
    /// dmg made is dmg's to delete, and one the user made is theirs to keep.
    /// </returns>
    private static Result<bool> PrepareRoot(string rootPath)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return Result<bool>.Failure(DmgError.Usage(
                "A projection needs a directory to appear under.",
                "No root path was given."));
        }

        try
        {
            if (File.Exists(rootPath))
            {
                return Result<bool>.Failure(DmgError.Usage(
                    $"'{rootPath}' is a file, not a directory.",
                    "A projection root has to be a folder."));
            }

            if (Directory.Exists(rootPath))
            {
                if (Directory.EnumerateFileSystemEntries(rootPath).Any())
                {
                    return Result<bool>.Failure(DmgError.Usage(
                        $"'{rootPath}' already has files in it. A projection root must be empty, "
                        + "so that everything appearing there comes from the image.",
                        "Choose an empty folder, or one that does not exist yet."));
                }

                return Result<bool>.Success(false);
            }

            Directory.CreateDirectory(rootPath);

            return Result<bool>.Success(true);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return Result<bool>.Failure(DmgError.Usage(
                $"'{rootPath}' cannot be used as a projection root.",
                exception.Message));
        }
    }

    /// <summary>Looks up the instance a callback belongs to.</summary>
    private static ProjectionInstance? InstanceFor(ProjFsNative.PrjCallbackData* data) =>
        data is not null && Instances.TryGetValue(data->InstanceContext, out ProjectionInstance? instance)
            ? instance
            : null;

    /// <summary>The relative path a callback is asking about, or the empty string for the root.</summary>
    private static string PathOf(ProjFsNative.PrjCallbackData* data) =>
        data->FilePathName == nint.Zero
            ? string.Empty
            : Marshal.PtrToStringUni(data->FilePathName) ?? string.Empty;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int StartEnumeration(ProjFsNative.PrjCallbackData* data, Guid* enumerationId)
    {
        try
        {
            ProjectionInstance? instance = InstanceFor(data);

            if (instance is null)
            {
                return Fail;
            }

            instance.Enumerations[*enumerationId] = new EnumerationState(PathOf(data));

            return ProjectionErrors.Success;
        }
        catch
        {
            return Fail;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EndEnumeration(ProjFsNative.PrjCallbackData* data, Guid* enumerationId)
    {
        try
        {
            InstanceFor(data)?.Enumerations.TryRemove(*enumerationId, out _);

            return ProjectionErrors.Success;
        }
        catch
        {
            return Fail;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetEnumeration(
        ProjFsNative.PrjCallbackData* data,
        Guid* enumerationId,
        nint searchExpression,
        nint dirEntryBufferHandle)
    {
        try
        {
            ProjectionInstance? instance = InstanceFor(data);

            if (instance is null
                || !instance.Enumerations.TryGetValue(*enumerationId, out EnumerationState? state))
            {
                return Fail;
            }

            string? pattern = searchExpression == nint.Zero
                ? null
                : Marshal.PtrToStringUni(searchExpression);

            // A restart means the caller is asking from the top again; the snapshot
            // is re-taken so a listing cannot show half of one moment and half of
            // another.
            if ((data->Flags & RestartScan) != 0)
            {
                state.Reset();
            }

            if (state.Items is null)
            {
                Result<IReadOnlyList<ProjectedItem>> listed = instance.Content.List(state.Directory);

                if (!listed.TryGetValue(out IReadOnlyList<ProjectedItem>? items))
                {
                    return ProjectionErrors.FileNotFound;
                }

                // ProjFS expects entries in its own sort order, and gets it wrong
                // quietly if they arrive in another.
                state.Items = [.. items.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase)];
            }

            while (state.Cursor < state.Items.Count)
            {
                ProjectedItem item = state.Items[state.Cursor];

                if (pattern is { Length: > 0 } && !ProjFsNative.PrjFileNameMatch(item.Name, pattern))
                {
                    state.Cursor++;
                    continue;
                }

                ProjFsNative.PrjFileBasicInfo info = BasicInfoFor(item);

                int filled = ProjFsNative.PrjFillDirEntryBuffer(item.Name, in info, dirEntryBufferHandle);

                if (filled == InsufficientBuffer)
                {
                    // Not a failure: ProjFS will ask again, and the cursor is left
                    // on this entry so the next call resumes rather than repeats.
                    return ProjectionErrors.Success;
                }

                if (!ProjectionErrors.Succeeded(filled))
                {
                    return filled;
                }

                state.Cursor++;
            }

            return ProjectionErrors.Success;
        }
        catch
        {
            return Fail;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetPlaceholderInfo(ProjFsNative.PrjCallbackData* data)
    {
        try
        {
            ProjectionInstance? instance = InstanceFor(data);

            if (instance is null)
            {
                return Fail;
            }

            string path = PathOf(data);
            Result<ProjectedItem?> found = instance.Content.Find(path);

            if (!found.TryGetValue(out ProjectedItem? item) || item is null)
            {
                // Not being there is an ordinary answer. The shell asks about
                // desktop.ini and friends constantly.
                return ProjectionErrors.FileNotFound;
            }

            ProjFsNative.PrjPlaceholderInfo placeholder = default;
            placeholder.FileBasicInfo = BasicInfoFor(item);

            int written = ProjFsNative.PrjWritePlaceholderInfo(
                data->NamespaceVirtualizationContext,
                path,
                in placeholder,
                (uint)Unsafe.SizeOf<ProjFsNative.PrjPlaceholderInfo>());

            return written;
        }
        catch
        {
            return Fail;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetFileData(ProjFsNative.PrjCallbackData* data, ulong byteOffset, uint length)
    {
        nint buffer = nint.Zero;

        try
        {
            ProjectionInstance? instance = InstanceFor(data);

            if (instance is null)
            {
                return Fail;
            }

            if (length == 0)
            {
                return ProjectionErrors.Success;
            }

            Result<byte[]> read = instance.Content.Read(PathOf(data), (long)byteOffset, (int)length);

            if (!read.TryGetValue(out byte[]? bytes))
            {
                return ProjectionErrors.FileNotFound;
            }

            if (bytes.Length == 0)
            {
                return ProjectionErrors.Success;
            }

            // The buffer has to come from ProjFS: it writes it to the volume
            // directly, and the alignment is the filesystem's, not something a
            // managed array can be relied on to satisfy.
            buffer = ProjFsNative.PrjAllocateAlignedBuffer(
                data->NamespaceVirtualizationContext,
                (nuint)bytes.Length);

            if (buffer == nint.Zero)
            {
                return ProjectionErrors.OutOfMemory;
            }

            Marshal.Copy(bytes, 0, buffer, bytes.Length);

            return ProjFsNative.PrjWriteFileData(
                data->NamespaceVirtualizationContext,
                in data->DataStreamId,
                buffer,
                byteOffset,
                (uint)bytes.Length);
        }
        catch
        {
            return Fail;
        }
        finally
        {
            if (buffer != nint.Zero)
            {
                ProjFsNative.PrjFreeAlignedBuffer(buffer);
            }
        }
    }

    /// <summary>Turns one projected item into what ProjFS wants to be told about it.</summary>
    private static ProjFsNative.PrjFileBasicInfo BasicInfoFor(ProjectedItem item)
    {
        ProjFsNative.PrjFileBasicInfo info = default;

        info.IsDirectory = item.IsDirectory;
        info.FileSize = item.IsDirectory ? 0 : item.Length;
        info.CreationTime = ToFileTime(item.Created);
        info.LastWriteTime = ToFileTime(item.Modified);
        info.ChangeTime = info.LastWriteTime;
        info.LastAccessTime = info.LastWriteTime;

        uint attributes = item.IsDirectory
            ? ProjFsNative.FileAttributeDirectory
            : 0;

        // Everything projected is read-only: this tool has never written into an
        // image and a projection is not where that starts.
        attributes |= ProjFsNative.FileAttributeReadOnly;

        if (item.IsHidden)
        {
            attributes |= ProjFsNative.FileAttributeHidden;
        }

        info.FileAttributes = attributes;

        return info;
    }

    /// <summary>
    /// A Windows file time, or zero for "not recorded" - which is what an exFAT
    /// entry with no stamp should produce rather than a date in 1601.
    /// </summary>
    private static long ToFileTime(DateTimeOffset? moment)
    {
        if (moment is not { } value)
        {
            return 0;
        }

        try
        {
            return value.UtcDateTime.ToFileTimeUtc();
        }
        catch (ArgumentOutOfRangeException)
        {
            // A stamp before 1601. The image said something impossible; saying
            // nothing is better than refusing to list the file.
            return 0;
        }
    }

    /// <summary>One running projection, as the static callbacks see it.</summary>
    private sealed class ProjectionInstance(IProjectedContent content)
    {
        internal IProjectedContent Content { get; } = content;

        internal ConcurrentDictionary<Guid, EnumerationState> Enumerations { get; } = new();

        internal SafeProjectionContextHandle? Context { get; set; }
    }

    /// <summary>
    /// One directory enumeration in progress: the snapshot and how far through it
    /// ProjFS has been told.
    /// </summary>
    /// <remarks>
    /// The snapshot matters. <c>GetDirectoryEnumeration</c> is called repeatedly
    /// until the buffer stops filling, and re-listing on each call would let a
    /// directory that changed underneath show some entries twice and others not at
    /// all. The image is read-only so it cannot change, but the invariant is worth
    /// holding anyway.
    /// </remarks>
    private sealed class EnumerationState(string directory)
    {
        internal string Directory { get; } = directory;

        internal IReadOnlyList<ProjectedItem>? Items { get; set; }

        internal int Cursor { get; set; }

        internal void Reset()
        {
            Items = null;
            Cursor = 0;
        }
    }

    /// <summary>A running projection, owned by whoever started it.</summary>
    private sealed class WindowsProjectionSession(
        ProjectionOptions options,
        nint token,
        SafeProjectionContextHandle context,
        bool rootCreated) : IProjectionSession
    {
        private bool _stopped;

        public string RootPath => options.RootPath;

        public bool IsRunning => !_stopped;

        public Result Stop()
        {
            if (_stopped)
            {
                // Stopping twice is not an error; the second call did nothing.
                return Result.Success();
            }

            _stopped = true;

            // The handle's release calls PrjStopVirtualizing. The instance stays
            // reachable until after that returns, because a callback may still be
            // in flight as the projection is torn down.
            context.Dispose();
            Instances.TryRemove(token, out _);

            // Only now. Deleting while ProjFS is still serving the root invites it
            // to materialise the very entries being removed.
            return Cleanup();
        }

        public void Dispose() => Stop();

        /// <summary>
        /// Removes what the projection left on disk.
        /// </summary>
        /// <remarks>
        /// <para>
        /// ProjFS leaves a placeholder for every item that was looked at and a full
        /// copy of every file that was opened, and none of that goes away when the
        /// projection stops. For an encrypted image those copies are plaintext, so
        /// this is not tidying - it is the difference between serving an image
        /// without copying it and quietly decrypting parts of it onto the user's
        /// disk.
        /// </para>
        /// <para>
        /// Every entry is checked with <see cref="ScratchLayout.IsWithin"/> before
        /// it is deleted, the same guard the scratch directory uses. The root itself
        /// is removed only when dmg created it: adopting a folder the user made and
        /// then deleting it would be a surprise, so that one is emptied and left.
        /// </para>
        /// <para>
        /// A failure here is reported as a failure, not swallowed. "The projection
        /// stopped but there is still decrypted content in that folder" is
        /// something the user has to be told.
        /// </para>
        /// </remarks>
        private Result Cleanup()
        {
            if (options.KeepContents)
            {
                return Result.Success();
            }

            try
            {
                if (!Directory.Exists(options.RootPath))
                {
                    return Result.Success();
                }

                foreach (string entry in Directory.EnumerateFileSystemEntries(options.RootPath))
                {
                    if (!ScratchLayout.IsWithin(options.RootPath, entry))
                    {
                        return Result.Failure(DmgError.Internal(
                            "Refusing to delete something that is not inside the projection root.",
                            $"root '{options.RootPath}', entry '{entry}'"));
                    }

                    if (Directory.Exists(entry))
                    {
                        Directory.Delete(entry, recursive: true);
                    }
                    else
                    {
                        File.Delete(entry);
                    }
                }

                if (rootCreated)
                {
                    Directory.Delete(options.RootPath, recursive: false);
                }

                return Result.Success();
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                return Result.Failure(new DmgError(
                    DmgExitCode.MountFailed,
                    $"The projection stopped, but '{options.RootPath}' could not be cleared. "
                    + "Anything that was opened while it ran is still there, decrypted. Close "
                    + "whatever is using that folder and delete it by hand.",
                    exception.Message));
            }
        }
    }
}
