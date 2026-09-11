using Dmg.Core.Diagnostics;

namespace Dmg.Cli.Output;

/// <summary>
/// Throttles a byte-count progress report to one line per whole percentage
/// point, so a multi-gigabyte transfer does not flood stderr with a line per
/// megabyte read or written.
/// </summary>
/// <remarks>
/// <para>
/// The same throttle backs every verb that reports progress by bytes moved -
/// <c>verify</c>, <c>extract</c> and <c>mount</c> - so the three cannot drift
/// into three different ideas of what "one update" means. Before S9.11 each
/// verb carried its own copy of this exact class.
/// </para>
/// <para>
/// <b>What this class is not responsible for.</b> One line per update, always
/// to stderr, silenced entirely at <see cref="Verbosity.Quiet"/> - that
/// contract belongs to <see cref="IOutput.Progress"/> itself, which every
/// caller of this class already goes through. This class decides only how
/// often a line is worth sending and what it says; whether it is sent at all
/// - and where - is <see cref="IOutput"/>'s call, not this one's, which is what
/// keeps the two testable independently.
/// </para>
/// </remarks>
public sealed class ProgressThrottle
{
    private readonly IOutput _output;
    private int _lastPercent = -1;

    /// <summary>Creates a throttle that reports through <paramref name="output"/>.</summary>
    public ProgressThrottle(IOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        _output = output;
    }

    /// <summary>
    /// Reports <paramref name="done"/> of <paramref name="total"/> bytes as a
    /// percentage. A no-op unless the whole percentage point has changed since
    /// the last report that actually went out, so a caller can call this once
    /// per buffer read without checking anything itself.
    /// </summary>
    /// <param name="done">Bytes moved so far.</param>
    /// <param name="total">The total this operation expects to move.</param>
    public void Report(long done, long total)
    {
        int percent = total > 0 ? (int)(done * 100 / total) : 100;

        if (percent == _lastPercent)
        {
            return;
        }

        _lastPercent = percent;
        _output.Progress($"{ByteSize.Format((ulong)done)} / {ByteSize.Format((ulong)total)} ({percent}%)");
    }
}
