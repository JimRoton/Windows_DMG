namespace Dmg.Core.Containers;

/// <summary>
/// One link in the chain that answers "what is this file?".
/// </summary>
/// <remarks>
/// <para>
/// A chain of responsibility rather than a single <c>switch</c> because the formats
/// are recognised by evidence of very different kinds - a magic number at offset
/// zero, a trailer at the end, the absence of anything at all - and because the
/// order in which that evidence is weighed is itself a decision. An encrypted image
/// is checked before UDIF, so that an encrypted file that happens to end in
/// plausible bytes cannot be misread as a corrupt UDIF; raw is checked last of the
/// openable formats, because "no container" is only the right answer once the
/// containers have all declined.
/// </para>
/// <para>
/// <b>Recognising is not the same as opening.</b> <see cref="Recognises"/> means
/// "this file is mine to answer for", and a probe that recognises a file may still
/// return a failure from <see cref="Describe"/> - the encrypted probe always does,
/// and the UDIF probe does whenever the trailer it found is self-contradictory. The
/// split exists so that a file with a <c>koly</c> that does not parse gets "your
/// UDIF image is corrupt" and not "this is not a DMG": once a probe has claimed a
/// file, no later probe gets to guess at it.
/// </para>
/// <para>
/// <b>Probes are pure.</b> Everything they may look at has already been read into
/// <see cref="ImageProbeContext"/>. They do no I/O, they throw nothing, and they are
/// stateless singletons shared across callers.
/// </para>
/// </remarks>
public interface IImageFormatProbe
{
    /// <summary>The format this probe recognises, for diagnostics and for tests.</summary>
    ImageFormat Format { get; }

    /// <summary>The probe's name as it appears in <c>--verbose</c> output: "UDIF", "raw".</summary>
    string Name { get; }

    /// <summary>
    /// True when this probe recognises every file, whatever is in it. Exactly one
    /// probe in a chain may say true, and it must be the last one: it is what makes
    /// the chain total, so that "nothing matched" is still an answer with a name on
    /// it rather than a fall off the end.
    /// </summary>
    bool IsTerminal => false;

    /// <summary>
    /// Whether this probe claims the file - the chain stops at the first probe that
    /// says yes. Cheap, total, and never throws.
    /// </summary>
    /// <param name="image">The windows read from the file.</param>
    bool Recognises(ImageProbeContext image);

    /// <summary>
    /// Describes the file this probe has claimed, or explains why it cannot be
    /// opened.
    /// </summary>
    /// <param name="image">The same context <see cref="Recognises"/> was given.</param>
    /// <returns>
    /// A description for a format this build reads, or a failure carrying the exit
    /// code the refusal should produce. Called only after
    /// <see cref="Recognises"/> returned true.
    /// </returns>
    Result<ImageFormatDetection> Describe(ImageProbeContext image);
}
