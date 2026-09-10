namespace Dmg.Core.Containers;

/// <summary>
/// What a file handed to <c>dmg</c> turned out to be.
/// </summary>
/// <remarks>
/// <para>
/// This is the answer to "what am I looking at?", not "can I open it?". Several
/// members here are formats this tool will never read - <see cref="Ndif"/> and
/// <see cref="SparseBundle"/> among them - and they exist precisely so that a
/// refusal can name the thing. A user who typed the wrong filename is helped by
/// "that is an Apple installer package"; they are not helped by "not a DMG".
/// </para>
/// <para>
/// <see cref="Unrecognised"/> is deliberately zero, so a default-initialised value
/// claims nothing.
/// </para>
/// </remarks>
public enum ImageFormat
{
    /// <summary>Nothing in the file matched anything this tool knows about.</summary>
    Unrecognised = 0,

    /// <summary>A UDIF image: the <c>koly</c> trailer is present and parses.</summary>
    Udif,

    /// <summary>
    /// No container at all - the file is a bare stream of 512-byte sectors, which
    /// is what a decoded image looks like and what <c>dmg extract --format raw</c>
    /// produces.
    /// </summary>
    Raw,

    /// <summary>
    /// An encrypted image. The payload cannot be looked at without a passphrase, so
    /// nothing further can be said about what is inside it.
    /// </summary>
    Encrypted,

    /// <summary>
    /// NDIF - the Disk Copy 6 format that predates UDIF, usually arriving wrapped in
    /// MacBinary or AppleDouble because its contents live in a resource fork.
    /// </summary>
    Ndif,

    /// <summary>A single-file Apple sparse image, <c>.sparseimage</c>.</summary>
    SparseImage,

    /// <summary>A <c>.sparsebundle</c>: a directory of band files, not a file at all.</summary>
    SparseBundle,

    /// <summary>
    /// A format that is not a disk image in any sense - an installer package, an
    /// archive, a picture. Named so the user can see what they actually passed.
    /// </summary>
    Foreign,
}
