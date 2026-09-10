namespace Dmg.Core.Containers;

/// <summary>
/// The ordered chain that turns a file into an answer: encrypted, UDIF, raw, or a
/// named refusal.
/// </summary>
/// <remarks>
/// <para>
/// <b>The order is the design.</b> Encrypted first, because ciphertext can imitate
/// anything and a wrong guess about it produces a confidently wrong message. UDIF
/// next, because it is the only format here with a real signature. Raw after that,
/// because "no container" is only credible once the containers have declined. The
/// terminal last, because every file must get an answer and the answer must have a
/// name in it.
/// </para>
/// <para>
/// <b>The first probe to claim a file owns the outcome.</b> If it then fails - the
/// encrypted probe always does, the UDIF probe does on a damaged trailer - the
/// chain returns that failure rather than trying the next probe. Falling through on
/// failure would mean a corrupt UDIF eventually being reinterpreted as a raw sector
/// stream, which is the one outcome worse than any error: it would mount, and the
/// contents would be wrong.
/// </para>
/// <para>
/// Nothing here throws for anything a file can do. Construction throws, because a
/// chain assembled without a terminal or with two of them is a wiring bug in this
/// codebase and not something an image can cause.
/// </para>
/// </remarks>
public sealed class ImageFormatProbeChain
{
    private readonly IImageFormatProbe[] _probes;

    /// <summary>
    /// Builds a chain. The probes are tried in the order given.
    /// </summary>
    /// <param name="probes">
    /// The probes, ending with exactly one whose <see cref="IImageFormatProbe.IsTerminal"/>
    /// is true.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="probes"/>, or one of them, is null.</exception>
    /// <exception cref="ArgumentException">
    /// The chain is empty, has no terminal, has more than one, or does not end with
    /// it. All four are wiring bugs.
    /// </exception>
    public ImageFormatProbeChain(IEnumerable<IImageFormatProbe> probes)
    {
        ArgumentNullException.ThrowIfNull(probes);

        _probes = [.. probes];

        if (_probes.Length == 0)
        {
            throw new ArgumentException("A probe chain needs at least a terminal probe.", nameof(probes));
        }

        int terminals = 0;

        foreach (IImageFormatProbe probe in _probes)
        {
            ArgumentNullException.ThrowIfNull(probe, nameof(probes));

            if (probe.IsTerminal)
            {
                terminals++;
            }
        }

        if (terminals != 1)
        {
            throw new ArgumentException(
                $"A probe chain needs exactly one terminal probe; this one has {terminals}. "
                + "Without it a file can fall off the end unanswered; with two, the second is dead code.",
                nameof(probes));
        }

        if (!_probes[^1].IsTerminal)
        {
            throw new ArgumentException(
                $"The terminal probe must be last; '{_probes[^1].Name}' is. Everything after a "
                + "terminal is unreachable.",
                nameof(probes));
        }
    }

    /// <summary>
    /// The chain every caller should use: encrcdsa, then UDIF, then raw, then the
    /// terminal that names what is left.
    /// </summary>
    public static ImageFormatProbeChain Default { get; } = new(
    [
        EncryptedImageProbe.Instance,
        UdifImageProbe.Instance,
        RawImageProbe.Instance,
        UnknownImageProbe.Instance,
    ]);

    /// <summary>The probes, in the order they are tried.</summary>
    public IReadOnlyList<IImageFormatProbe> Probes => _probes;

    /// <summary>
    /// Identifies the image at <paramref name="path"/>, including the cases where
    /// there is no file there to open.
    /// </summary>
    /// <param name="path">A path to an image file, or to something that is not one.</param>
    /// <remarks>
    /// The directory case is handled here rather than by a probe because a probe
    /// takes a stream and a directory cannot be opened as one. It matters:
    /// <c>.sparsebundle</c> is a directory, and shell completion walks users into it
    /// constantly. "That is a sparse bundle" is worth reaching for.
    /// </remarks>
    public Result<ImageFormatDetection> Identify(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        try
        {
            if (Directory.Exists(path))
            {
                ImageFormatSignature? bundle = ImageFormatSignatures.IdentifySparseBundle(path);

                return Result<ImageFormatDetection>.Failure(bundle?.ToError() ?? new DmgError(
                    DmgExitCode.UsageError,
                    $"'{path}' is a folder, not a disk image file.",
                    "Directory.Exists returned true."));
            }

            if (!File.Exists(path))
            {
                // A sparse bundle that has been moved or deleted still deserves its
                // own message: the path shape says what was meant.
                ImageFormatSignature? bundle = ImageFormatSignatures.IdentifySparseBundle(path);

                return Result<ImageFormatDetection>.Failure(bundle?.ToError() ?? new DmgError(
                    DmgExitCode.UsageError,
                    $"There is no file at '{path}'.",
                    "File.Exists returned false."));
            }

            using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            return Identify(stream, path);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or NotSupportedException
                or ArgumentException)
        {
            // Not an internal error: the path came from a user, and a path that
            // cannot be opened is a thing the user needs to fix rather than a bug
            // to report.
            return Result<ImageFormatDetection>.Failure(
                DmgExitCode.UsageError,
                $"Could not open '{path}'.",
                exception.Message);
        }
    }

    /// <summary>
    /// Identifies the image in <paramref name="stream"/>.
    /// </summary>
    /// <param name="stream">A readable, seekable stream over the whole file.</param>
    /// <param name="sourcePath">
    /// Where the stream came from, when the caller knows. Used only in messages and
    /// for the sparse-bundle check.
    /// </param>
    public Result<ImageFormatDetection> Identify(Stream stream, string? sourcePath = null)
    {
        Result<ImageProbeContext> context = ImageProbeContext.Create(stream, sourcePath);

        return context.TryGetValue(out ImageProbeContext? image)
            ? Identify(image)
            : context.CastFailure<ImageFormatDetection>();
    }

    /// <summary>
    /// Runs the chain over a context that has already been read - the overload tests
    /// use, and the one to call when the windows are in hand already.
    /// </summary>
    /// <param name="image">The windows read from the file.</param>
    public Result<ImageFormatDetection> Identify(ImageProbeContext image)
    {
        ArgumentNullException.ThrowIfNull(image);

        foreach (IImageFormatProbe probe in _probes)
        {
            if (probe.Recognises(image))
            {
                return probe.Describe(image);
            }
        }

        // Unreachable: the constructor guarantees a terminal probe, and a terminal
        // probe recognises everything. Returned rather than thrown because a broken
        // invariant should still leave the process with a usable exit code.
        return Result<ImageFormatDetection>.Failure(DmgError.Internal(
            "No probe claimed the image, which should be impossible.",
            $"Chain: {string.Join(" -> ", _probes.Select(probe => probe.Name))}."));
    }
}
