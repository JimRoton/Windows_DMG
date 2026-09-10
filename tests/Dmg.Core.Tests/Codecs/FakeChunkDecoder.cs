using Dmg.Core.Codecs;

namespace Dmg.Core.Tests.Codecs;

/// <summary>
/// A decoder the registry tests can point at whatever behaviour they need: fill the
/// output, under-fill it, claim to have written more than it did, or fail outright.
/// It exists so the registry's own rules can be tested without dragging a real codec -
/// and a real codec's bugs - into the test.
/// </summary>
internal sealed class FakeChunkDecoder : IChunkDecoder
{
    internal FakeChunkDecoder(uint entryType, string name = "fake")
    {
        EntryType = entryType;
        Name = name;
    }

    /// <summary>How many bytes to claim to have written. Null means "as many as I filled".</summary>
    internal int? ClaimedWritten { get; set; }

    /// <summary>How many bytes of the destination to actually fill. Null means all of it.</summary>
    internal int? FillCount { get; set; }

    /// <summary>When set, the decoder returns this failure instead of decoding.</summary>
    internal DmgError? Failure { get; set; }

    /// <summary>The length of the destination the registry handed over on the last call.</summary>
    internal int LastDestinationLength { get; private set; } = -1;

    /// <summary>The length of the source the registry handed over on the last call.</summary>
    internal int LastSourceLength { get; private set; } = -1;

    /// <summary>How many times the registry called this decoder.</summary>
    internal int CallCount { get; private set; }

    public uint EntryType { get; }

    public string Name { get; }

    public bool ReadsDataFork => true;

    public Result<int> Decode(ReadOnlySpan<byte> source, Span<byte> destination)
    {
        CallCount++;
        LastSourceLength = source.Length;
        LastDestinationLength = destination.Length;

        if (Failure is not null)
        {
            return Result<int>.Failure(Failure);
        }

        int fill = Math.Min(FillCount ?? destination.Length, destination.Length);
        destination[..fill].Fill(0xAB);

        return Result<int>.Success(ClaimedWritten ?? fill);
    }
}
