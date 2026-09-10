namespace Dmg.Core.Containers;

/// <summary>
/// A file format this tool can recognise but will not open, together with the
/// sentence the user gets told about it.
/// </summary>
/// <param name="Format">The family the signature belongs to.</param>
/// <param name="Name">
/// The format's ordinary name - "Apple installer package (.pkg)", "ZIP archive".
/// Short enough to sit inside a sentence.
/// </param>
/// <param name="Message">
/// The whole user-facing sentence, which says what the file is and, where there is
/// one, what to do with it instead.
/// </param>
/// <param name="Detail">
/// The evidence: which bytes, at which offset, led to this conclusion. Verbose
/// output only, but it is what makes a wrong answer diagnosable.
/// </param>
/// <param name="Code">
/// The exit code a refusal carries. Nearly always
/// <see cref="DmgExitCode.UnsupportedFormat"/>; the encrypted formats are the
/// exception and carry <see cref="DmgExitCode.DecryptionFailed"/>.
/// </param>
public sealed record ImageFormatSignature(
    ImageFormat Format,
    string Name,
    string Message,
    string Detail,
    DmgExitCode Code = DmgExitCode.UnsupportedFormat)
{
    /// <summary>Renders this signature as the failure the chain returns.</summary>
    public DmgError ToError() => new(Code, Message, Detail);
}
