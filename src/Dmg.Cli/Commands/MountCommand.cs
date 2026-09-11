using System.Text.Json;
using Dmg.Cli.Imaging;
using Dmg.Cli.Output;
using Dmg.Cli.Parsing;
using Dmg.Core;
using Dmg.Core.Crypto;
using Dmg.Core.Filesystems;
using Dmg.Core.Imaging;
using Dmg.Core.Vhd;
using Dmg.Windows.Elevation;
using Dmg.Windows.Mounts;
using Dmg.Windows.VirtualDisk;
using Dmg.Windows.Volumes;

namespace Dmg.Cli.Commands;

/// <summary>
/// <c>dmg mount IMAGE</c> - decode a volume to a scratch VHD and attach it as a
/// Windows drive.
/// </summary>
/// <remarks>
/// <para>
/// <b>The headline verb, and the most expensive one.</b> Every other verb reads or
/// decodes; this one also writes tens of gigabytes to scratch and asks Windows to
/// attach them. That is why the checks run cheapest first: opening and decrypting
/// the image, picking the volume, and a free-space precheck all cost at most a few
/// chunk decodes, and all run before the elevation check, which itself runs before
/// the VHD is written. A user who cannot mount at all - wrong passphrase, nothing
/// mountable, a shell that was never elevated - finds that out in well under a
/// second, not twenty minutes into a decode.
/// </para>
/// <para>
/// <b>One volume, not the whole disk.</b> <c>extract</c> writes the decoded disk
/// verbatim because a caller who wants the whole thing asked for it by name; a
/// mount is a single Windows-visible drive, so only the selected
/// <see cref="DiskVolume"/> - <see cref="VolumeMap.Select"/> picks it, the same way
/// <c>dmg info</c> would describe it - is windowed out of the decoded disk and
/// written to the scratch VHD. See <see cref="VolumeWindowStream"/>.
/// </para>
/// <para>
/// <b>Nothing is left behind on a failure that never attached.</b> Every exit
/// before <see cref="IVirtualDiskService.Attach"/> succeeds removes the scratch
/// directory - the half-written VHD included - the same way <c>extract</c> removes
/// a half-written output file. Once the disk is actually attached, the rule
/// reverses: the scratch VHD is now the backing store of a live volume, and no
/// failure past that point may delete it. A failure to find a volume or a drive
/// letter leaves the disk attached on purpose - <see cref="DriveLetterDiscovery"/>'s
/// own messages say so and point at <c>--letter</c> or <c>dmg unmount</c> - and a
/// failure to write the mount registry leaves it attached with a warning that
/// <c>dmg unmount</c> will not find it, because detaching a disk Windows just
/// attached over a bookkeeping failure would be worse than the bookkeeping failure.
/// </para>
/// <para>
/// <b>No self-elevation.</b> See <see cref="ElevationCheck"/>: this verb tells the
/// user to re-run elevated and exits 7, and never raises a UAC prompt itself.
/// </para>
/// </remarks>
public sealed class MountCommand : ICliCommand
{
    private readonly PassphraseReader _passphrases;
    private readonly IVirtualDiskService _virtualDisks;
    private readonly IPrivilegeService _privileges;
    private readonly IVolumeService _volumes;
    private readonly Func<Result<MountRegistry>> _openRegistry;
    private readonly IFreeSpaceProbe? _freeSpaceProbe;

    /// <summary>Builds the verb over the shipping Windows services.</summary>
    public MountCommand()
        : this(
            new PassphraseReader(),
            new WindowsVirtualDiskService(),
            new WindowsPrivilegeService(),
            new WindowsVolumeService())
    {
    }

    /// <summary>Builds the verb over substitutes - the constructor a test uses.</summary>
    /// <param name="passphrases">Supplies a passphrase when one is asked for.</param>
    /// <param name="virtualDisks">The virtdisk.dll port.</param>
    /// <param name="privileges">The process-token port the elevation check reads.</param>
    /// <param name="volumes">The volume-management port drive-letter discovery reads.</param>
    /// <param name="openRegistry">
    /// Opens the mount registry to record a successful mount. Defaults to
    /// <see cref="MountRegistry.ForCurrentUser"/>; a test substitutes a registry
    /// rooted at a temporary directory.
    /// </param>
    /// <param name="freeSpaceProbe">The volume probe for the free-space precheck; null for the real one.</param>
    public MountCommand(
        PassphraseReader passphrases,
        IVirtualDiskService virtualDisks,
        IPrivilegeService privileges,
        IVolumeService volumes,
        Func<Result<MountRegistry>>? openRegistry = null,
        IFreeSpaceProbe? freeSpaceProbe = null)
    {
        ArgumentNullException.ThrowIfNull(passphrases);
        ArgumentNullException.ThrowIfNull(virtualDisks);
        ArgumentNullException.ThrowIfNull(privileges);
        ArgumentNullException.ThrowIfNull(volumes);

        _passphrases = passphrases;
        _virtualDisks = virtualDisks;
        _privileges = privileges;
        _volumes = volumes;
        _openRegistry = openRegistry ?? (() => MountRegistry.ForCurrentUser());
        _freeSpaceProbe = freeSpaceProbe;
    }

    /// <inheritdoc />
    public string Verb => "mount";

    /// <inheritdoc />
    public string Summary => "Decode a volume and attach it as a Windows drive.";

    /// <inheritdoc />
    public CommandLineSpec Spec { get; } = new(
        "mount",
        [
            new OptionSpec(
                "partition",
                "Which partition to mount (see dmg info). Defaults to the image's single mountable volume.",
                'p',
                "N"),
            new OptionSpec(
                "rw",
                "Attach read-write instead of the default read-only."),
            new OptionSpec(
                "letter",
                "The drive letter to assign, instead of letting Windows choose one.",
                ValueName: "LETTER"),
            new OptionSpec(
                "scratch",
                "The directory to write the scratch VHD under, instead of the per-user default.",
                ValueName: "DIR"),
            new OptionSpec(
                "keep-scratch",
                "Leave the scratch VHD behind if the mount does not complete, instead of deleting it."),
            new OptionSpec(
                "dynamic",
                "Write the scratch VHD as a dynamic (allocate-on-demand) disk instead of the default fixed one."),
            CacheOption.Spec,
            new OptionSpec(
                "password-stdin",
                "Read the passphrase for an encrypted image from stdin."),
            new OptionSpec(
                "password-env",
                "Read the passphrase from an environment variable.",
                ValueName: "VAR"),
        ],
        ["IMAGE"],
        [
            "Picks the image's single mountable volume by default; an image with more than one "
            + "needs --partition N, the same number dmg info lists.",
            "Attaches read-only unless --rw is given. Windows assigns the drive letter unless "
            + "--letter is given, in which case dmg asks for that letter specifically.",
            "Needs an elevated shell: attaching a virtual disk needs the Manage Volume privilege, "
            + "and dmg checks for it before decoding anything rather than raising a UAC prompt "
            + "itself. Exits 7 with the remedy when the shell is not elevated.",
            "Decodes the volume to a scratch VHD first - not the whole image, just the volume "
            + "being mounted - and refuses before writing a byte if the scratch volume has no "
            + "room for it (exit 8).",
            "--dynamic makes the scratch VHD allocate-on-demand instead of fixed - the free-space "
            + "precheck accounts for that, asking only for the volume's real (sparse) size rather "
            + "than the fixed disk's worst case.",
            "A failure to find a volume or a drive letter, or to record the mount, leaves the disk "
            + "attached: the message says so and how to proceed. Only a failure before Windows "
            + "actually attaches the disk removes the scratch VHD.",
            "Progress goes to stderr as the VHD is written; the result line goes to stdout.",
        ]);

    /// <inheritdoc />
    public DmgExitCode Execute(CliContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        Result<ParsedArguments> parsed = ArgumentParser.Parse(Spec, context.Arguments);

        if (!parsed.TryGetValue(out ParsedArguments? arguments))
        {
            context.Output.Error(parsed.Error);

            return parsed.Error.Code;
        }

        if (arguments.Positionals.Count != 1)
        {
            context.Output.Error(DmgError.Usage(
                "'mount' needs an image to attach.",
                $"Usage: {Spec.UsageLine}"));

            return DmgExitCode.UsageError;
        }

        string imagePath = arguments.Positionals[0];

        Result<int?> partitionParsed = arguments.TryGetInt32("partition");

        if (!partitionParsed.TryGetValue(out int? partitionNumber))
        {
            context.Output.Error(partitionParsed.Error);

            return partitionParsed.Error.Code;
        }

        // Command-line validation happens here, before any work is attempted - a
        // typo in --letter must not cost a two-gigabyte decode before it is caught.
        string? requestedLetter = null;

        if (arguments.TryGetValue("letter", out string? letterText))
        {
            Result<string> letterParsed = DriveLetter.Parse(letterText);

            if (!letterParsed.TryGetValue(out requestedLetter))
            {
                context.Output.Error(letterParsed.Error);

                return letterParsed.Error.Code;
            }
        }

        bool readWrite = arguments.Has("rw");
        bool keepScratch = arguments.Has("keep-scratch");
        bool dynamic = arguments.Has("dynamic");
        arguments.TryGetValue("scratch", out string? scratchRoot);

        Result<long?> cache = CacheOption.BytesFor(arguments);

        if (!cache.TryGetValue(out long? cacheCapacityBytes))
        {
            context.Output.Error(cache.Error);

            return cache.Error.Code;
        }

        Result<PassphraseOptions> options = PassphraseOptionsOf(arguments);

        if (!options.TryGetValue(out PassphraseOptions? passphraseOptions))
        {
            context.Output.Error(options.Error);

            return options.Error.Code;
        }

        Passphrase? passphrase = null;

        try
        {
            if (passphraseOptions.Source != PassphraseSource.Unspecified)
            {
                Result<Passphrase> read = _passphrases.Read(passphraseOptions);

                if (!read.TryGetValue(out passphrase))
                {
                    context.Output.Error(read.Error);

                    return read.Error.Code;
                }
            }

            context.Output.Trace($"Reading {imagePath}");

            Result<OpenedImage> opened = OpenedImage.Open(imagePath, passphrase, cacheCapacityBytes: cacheCapacityBytes);

            if (!opened.TryGetValue(out OpenedImage? image))
            {
                context.Output.Error(opened.Error);

                return opened.Error.Code;
            }

            using (image)
            {
                return MountImage(
                    context,
                    image,
                    imagePath,
                    partitionNumber,
                    readWrite,
                    requestedLetter,
                    scratchRoot,
                    keepScratch,
                    dynamic);
            }
        }
        finally
        {
            passphrase?.Dispose();
        }
    }

    /// <summary>
    /// The sequence after the image is open: select the volume, check space and
    /// elevation, write the VHD, attach it, give it a letter, and record it - in
    /// that order, cheapest checks first.
    /// </summary>
    private DmgExitCode MountImage(
        CliContext context,
        OpenedImage image,
        string imagePath,
        int? partitionNumber,
        bool readWrite,
        string? requestedLetter,
        string? scratchRoot,
        bool keepScratch,
        bool dynamic)
    {
        Result<VolumeMap> mapped = VolumeMap.Read(image.Disk);

        if (!mapped.TryGetValue(out VolumeMap? map))
        {
            context.Output.Error(mapped.Error);

            return mapped.Error.Code;
        }

        Result<DiskVolume> selected = map.Select(partitionNumber);

        if (!selected.TryGetValue(out DiskVolume? volume))
        {
            context.Output.Error(selected.Error);

            return selected.Error.Code;
        }

        context.Output.Trace($"Selected {volume}");

        Result<ScratchSpace> createdScratch = ScratchSpace.Create(
            new ScratchOptions { Root = scratchRoot, KeepScratch = keepScratch });

        if (!createdScratch.TryGetValue(out ScratchSpace? scratch))
        {
            context.Output.Error(createdScratch.Error);

            return createdScratch.Error.Code;
        }

        // Set once Attach() succeeds. From that point the scratch VHD is the
        // backing store of a live, permanently-attached volume, and nothing in the
        // finally block below may remove it - see the class remarks.
        bool attached = false;

        VhdWriteOptions writeOptions = dynamic
            ? VhdWriteOptions.Default with { DiskType = VhdDiskType.Dynamic }
            : VhdWriteOptions.Default;

        // The map DmgSparseMap builds answers in sector-of-whole-disk terms, but
        // the VHD is written from a window that starts at volume.ByteOffset, not
        // byte zero of the disk - VhdWriter always asks a sparse map about offsets
        // relative to the start of what it is writing. OffsetSparseMap is the shim
        // that keeps those two coordinate spaces from being silently conflated,
        // which matters here: a wrong "yes" from the map is a block that is never
        // read and never written, not merely a slower conversion.
        IVhdSparseMap? sparseMap = dynamic && image.Disk is DmgBlockStream blockStream
            ? new OffsetSparseMap(DmgSparseMap.For(blockStream), volume.ByteOffset)
            : null;

        try
        {
            Result room = dynamic
                ? RequireDynamicRoom(scratch, volume.ByteLength, writeOptions.BlockSize, sparseMap, _freeSpaceProbe)
                : scratch.EnsureRoomFor(volume.ByteLength, _freeSpaceProbe);

            if (!room.Ok)
            {
                context.Output.Error(room.Error);

                return room.Error.Code;
            }

            Result elevated = ElevationCheck.RequireManageVolume(_privileges);

            if (!elevated.Ok)
            {
                context.Output.Error(elevated.Error);

                return elevated.Error.Code;
            }

            context.Output.Progress(
                $"Writing {ByteSize.Format((ulong)volume.ByteLength)} to '{scratch.VhdPath}'...");

            ProgressThrottle progress = new(context.Output);

            Result<VhdWriteResult> written = VhdWriter.WriteToFile(
                new VolumeWindowStream(image.Disk, volume.ByteOffset, volume.ByteLength),
                scratch.VhdPath,
                options: writeOptions,
                progress: new Progress<VhdWriteProgress>(
                    update => progress.Report(update.BytesWritten, update.TotalBytes)),
                freeSpaceProbe: _freeSpaceProbe,
                sparseMap: sparseMap);

            if (!written.Ok)
            {
                context.Output.Error(written.Error);

                return written.Error.Code;
            }

            VirtualDiskAccessMode mode = readWrite
                ? VirtualDiskAccessMode.ReadWrite
                : VirtualDiskAccessMode.ReadOnly;

            Result<IVirtualDiskHandle> openedDisk = _virtualDisks.Open(scratch.VhdPath, mode);

            if (!openedDisk.TryGetValue(out IVirtualDiskHandle? handle))
            {
                context.Output.Error(openedDisk.Error);

                return openedDisk.Error.Code;
            }

            VirtualDiskAttachment attachment;

            using (handle)
            {
                // NoDriveLetter when the user named one explicitly: Windows must not
                // race AssignDriveLetter with an automatic assignment of its own.
                VirtualDiskAttachOptions attachOptions = requestedLetter is null
                    ? VirtualDiskAttachOptions.Mount
                    : VirtualDiskAttachOptions.Mount with { NoDriveLetter = true };

                Result<VirtualDiskAttachment> attachResult = _virtualDisks.Attach(handle, attachOptions);

                if (!attachResult.TryGetValue(out VirtualDiskAttachment? madeAttachment))
                {
                    context.Output.Error(attachResult.Error);

                    return attachResult.Error.Code;
                }

                attachment = madeAttachment;
                attached = true;

                // The handle closes here. That does not detach the disk: the attach
                // above asked for a permanent lifetime, which is what lets `dmg
                // mount` attach a disk and then exit.
            }

            context.Output.Trace($"Attached as '{attachment.PhysicalPath}' ({attachment.ModeDescription}).");

            Result<string> letterResult = requestedLetter is null
                ? DriveLetterDiscovery.WaitForDriveLetter(_volumes, attachment.PhysicalPath, output: context.Output)
                : DriveLetterDiscovery.AssignDriveLetter(
                    _volumes,
                    attachment.PhysicalPath,
                    requestedLetter,
                    output: context.Output);

            if (!letterResult.TryGetValue(out string? driveLetter))
            {
                // Deliberately not "clean up and fail": the disk is already
                // attached, and the message above - DriveLetterDiscovery's own -
                // already says so and points at --letter or dmg unmount.
                // Detaching it here would contradict what the user was just told.
                context.Output.Error(letterResult.Error);

                return letterResult.Error.Code;
            }

            Result<MountRecord> recordBuilt = MountRecord.Create(
                imagePath,
                scratch.VhdPath,
                driveLetter,
                attachment.Mode);

            if (!recordBuilt.TryGetValue(out MountRecord? record))
            {
                context.Output.Error(recordBuilt.Error);

                return recordBuilt.Error.Code;
            }

            Result<MountRegistry> registryOpened = _openRegistry();

            if (!registryOpened.TryGetValue(out MountRegistry? registry))
            {
                context.Output.Error(UntrackedMount(registryOpened.Error, record, attachment));

                return registryOpened.Error.Code;
            }

            Result<MountRecord> added = registry.Add(record);

            if (!added.TryGetValue(out MountRecord? stored))
            {
                context.Output.Error(UntrackedMount(added.Error, record, attachment));

                return added.Error.Code;
            }

            Report(context, stored, attachment, volume, imagePath);

            return DmgExitCode.Success;
        }
        finally
        {
            // Everything up to and including the VHD write leaves debris - an empty
            // scratch directory, or a half-written VHD the writer already deleted -
            // that a failure must not leave lying around. Once Attach() has
            // succeeded the VHD is a live disk's backing store and this must do
            // nothing at all, whatever failed afterwards.
            if (!attached)
            {
                Result cleanup = scratch.Cleanup();

                if (!cleanup.Ok)
                {
                    context.Output.Warning(cleanup.Error.Message);
                }
            }
        }
    }

    /// <summary>
    /// The free-space precheck for a dynamic scratch VHD: the real (sparse) size
    /// when a map is available, the worst case otherwise - never the fixed disk's
    /// size, which a dynamic write does not need and should not have to insist on.
    /// </summary>
    private static Result RequireDynamicRoom(
        ScratchSpace scratch,
        long payloadBytes,
        int blockSize,
        IVhdSparseMap? sparseMap,
        IFreeSpaceProbe? probe)
    {
        Result<long> sized = VhdWriter.DynamicFileSizeFor(payloadBytes, blockSize, sparseMap);

        return sized.TryGetValue(out long required)
            ? FreeSpaceCheck.Require(scratch.VhdPath, required, probe)
            : sized.Discard();
    }

    /// <summary>
    /// Wraps a bookkeeping failure that happened after the disk was already
    /// attached, so the message tells the user what actually happened - a working
    /// mount dmg cannot find again - rather than only the bookkeeping error.
    /// </summary>
    private static DmgError UntrackedMount(DmgError original, MountRecord record, VirtualDiskAttachment attachment) =>
        new(
            original.Code,
            $"The image is attached at {record.DescribeDriveLetter()} ({attachment.ModeDescription}), but "
            + $"dmg could not record the mount: {original.Message} 'dmg unmount' will not find it; use "
            + $"Windows to detach '{attachment.PhysicalPath}' by hand if you need it gone.",
            original.Detail);

    private static void Report(
        CliContext context,
        MountRecord record,
        VirtualDiskAttachment attachment,
        DiskVolume volume,
        string imagePath)
    {
        if (context.Output.IsJson)
        {
            context.Output.WriteJson(JsonSerializer.Serialize(
                MountPayload.From(record, attachment, volume.Number),
                CliJson.Readable.MountPayload));

            return;
        }

        context.Output.WriteLine(
            $"Mounted '{imagePath}' (partition {volume.Number}) at {record.DescribeDriveLetter()}, "
            + $"{attachment.ModeDescription}. Id: {record.Id}. Unmount with 'dmg unmount {record.Id}'.");
    }

    /// <summary>
    /// Turns the parsed switches into the options <see cref="PassphraseReader"/>
    /// takes. The parser has already rejected an unknown option and caught a
    /// repeat, so what is left is the one rule it cannot know: the two sources are
    /// alternatives.
    /// </summary>
    private static Result<PassphraseOptions> PassphraseOptionsOf(ParsedArguments arguments)
    {
        bool stdin = arguments.Has("password-stdin");
        bool environment = arguments.TryGetValue("password-env", out string? variable);

        if (stdin && environment)
        {
            return Result<PassphraseOptions>.Failure(DmgError.Usage(
                $"{PassphraseOptions.StandardInputOption} and {PassphraseOptions.EnvironmentOption} "
                + "are alternatives. Give one or neither.",
                "A passphrase comes from exactly one place, so that it is obvious which."));
        }

        PassphraseSource source = stdin
            ? PassphraseSource.StandardInput
            : environment ? PassphraseSource.Environment : PassphraseSource.Unspecified;

        return Result<PassphraseOptions>.Success(
            new PassphraseOptions(source, variable, arguments.Positionals));
    }

    /// <summary>
    /// Shifts a sparse map's queries by a fixed offset, so a map built over an
    /// entire disk's chunk index can answer for a <see cref="VolumeWindowStream"/>
    /// that starts partway through it.
    /// </summary>
    /// <remarks>
    /// <see cref="DmgSparseMap"/> answers "is this range zero" in terms of the
    /// disk it was built from, sector zero being byte zero of that disk.
    /// <see cref="VhdWriter"/> always asks a sparse map about offsets relative to
    /// the start of whatever it is writing - see <see cref="IVhdSparseMap"/> - so
    /// when the thing being written is one volume partway into the disk, every
    /// query has to be translated back to the disk's own coordinates before the
    /// underlying map can answer it. Getting this wrong would not slow anything
    /// down; it would make the writer trust a "yes" that describes the wrong
    /// bytes, which is a corrupt VHD, not a slow one.
    /// </remarks>
    private sealed class OffsetSparseMap(IVhdSparseMap inner, long offset) : IVhdSparseMap
    {
        public bool IsKnownZero(long windowOffset, long length)
        {
            if (windowOffset < 0 || length < 0)
            {
                return false;
            }

            long absoluteOffset;

            try
            {
                absoluteOffset = checked(offset + windowOffset);
            }
            catch (OverflowException)
            {
                // False is always the safe answer - see IVhdSparseMap's remarks.
                return false;
            }

            return inner.IsKnownZero(absoluteOffset, length);
        }
    }

    /// <summary>
    /// A read-only, seekable window over one volume's bytes within the decoded
    /// disk, so <see cref="VhdWriter"/> - which always writes from byte zero to a
    /// stream's own <see cref="Stream.Length"/> - can write just the volume being
    /// mounted without a copy ever existing on top of <see cref="OpenedImage.Disk"/>.
    /// </summary>
    /// <remarks>
    /// Position 0 here is <see cref="DiskVolume.ByteOffset"/> on the inner stream,
    /// and this stream's <see cref="Length"/> is <see cref="DiskVolume.ByteLength"/>
    /// - never the inner stream's own length. A read or seek past the volume's end
    /// is clamped to it, the same way it would be if the volume genuinely were its
    /// own file. Any <see cref="DmgStreamException"/> the inner stream throws - a
    /// bad chunk, an unsupported codec - passes through unchanged, which is what
    /// lets <see cref="VhdWriter"/> report the real failure and exit code rather
    /// than a generic I/O error.
    /// </remarks>
    private sealed class VolumeWindowStream(Stream inner, long offset, long length) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => inner.Position - offset;
            set => inner.Position = checked(offset + value);
        }

        public override int Read(byte[] buffer, int offset2, int count) =>
            Read(buffer.AsSpan(offset2, count));

        public override int Read(Span<byte> buffer)
        {
            long remaining = length - Position;
            int wanted = (int)Math.Clamp(Math.Min(buffer.Length, remaining), 0, buffer.Length);

            return wanted <= 0 ? 0 : inner.Read(buffer[..wanted]);
        }

        public override long Seek(long seekOffset, SeekOrigin origin)
        {
            long target = origin switch
            {
                SeekOrigin.Begin => seekOffset,
                SeekOrigin.Current => Position + seekOffset,
                SeekOrigin.End => length + seekOffset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin), origin, "Unknown SeekOrigin."),
            };

            Position = target;

            return target;
        }

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset2, int count) => throw new NotSupportedException();
    }
}
