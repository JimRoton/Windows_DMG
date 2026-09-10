namespace Dmg.Core.Imaging;

/// <summary>
/// A <see cref="DmgError"/> that had to be thrown because it happened inside a
/// <see cref="Stream"/> method, which has nowhere to put a
/// <see cref="Result{T}"/>.
/// </summary>
/// <remarks>
/// <para>
/// Everywhere else in this codebase an expected failure - a corrupt chunk table,
/// a codec this build does not implement - is a <see cref="Result{T}"/> value, and
/// exceptions are reserved for bugs. <see cref="DmgBlockStream"/> is the one place
/// that rule cannot hold: <see cref="Stream.Read(Span{byte})"/> returns an
/// <see cref="int"/>, and the whole point of the seam is that consumers see an
/// ordinary <see cref="Stream"/> rather than something DMG-shaped.
/// </para>
/// <para>
/// So the failure is thrown instead, and it carries the <see cref="DmgError"/>
/// intact. It derives from <see cref="IOException"/> because that is what a
/// <see cref="Stream"/> consumer already catches, and it exposes
/// <see cref="Error"/> so the CLI can recover the original
/// <see cref="DmgExitCode"/> rather than flattening every read failure into
/// "internal error".
/// </para>
/// <para>
/// <b>Opening is still a <see cref="Result{T}"/>.</b> Only failures discovered
/// during a read - which is to say, only failures that could not have been found
/// at open time - arrive as this exception.
/// </para>
/// </remarks>
public sealed class DmgStreamException : IOException
{
    /// <summary>Wraps an error, using its message as the exception message.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is null.</exception>
    public DmgStreamException(DmgError error)
        : base(Describe(error))
    {
        Error = error;
    }

    /// <summary>Wraps an error, keeping the exception that produced it.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="error"/> is null.</exception>
    public DmgStreamException(DmgError error, Exception? innerException)
        : base(Describe(error), innerException)
    {
        Error = error;
    }

    /// <summary>
    /// Reconstructs from a message alone. Present because
    /// <see cref="IOException"/> offers it; the error is synthesised as an
    /// internal one, so prefer the <see cref="DmgError"/> constructors.
    /// </summary>
    public DmgStreamException(string message)
        : base(message)
    {
        Error = DmgError.Internal(message);
    }

    /// <summary>As <see cref="DmgStreamException(string)"/>, keeping an inner exception.</summary>
    public DmgStreamException(string message, Exception? innerException)
        : base(message, innerException)
    {
        Error = DmgError.Internal(message, innerException?.Message);
    }

    /// <summary>An exception with neither a message nor a meaningful error.</summary>
    public DmgStreamException()
        : this("A read from a DMG block stream failed.")
    {
    }

    /// <summary>The failure, with its exit code and detail intact.</summary>
    public DmgError Error { get; }

    /// <summary>The exit code this failure maps to.</summary>
    public DmgExitCode Code => Error.Code;

    private static string Describe(DmgError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return error.ToString();
    }
}
