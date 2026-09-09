# ADR-004 — `System.IO.Stream` as the seam; Strategy for codecs

**Status:** Proposed · 9 September 2026

## Context

Something has to separate "the DMG problem" from "everything that consumes decoded
sectors" — the VHD writer, `extract`, `verify`'s hasher, and any future head. The
obvious move is to define an interface of our own, e.g. `IBlockDevice`.

Separately, chunks come in seven compression types and more may be added.

## Decision

1. The seam is **`DmgBlockStream : System.IO.Stream`** — the BCL abstraction, not a
   bespoke interface.
2. Codecs are **Strategy implementations in a registry**:
   `Dictionary<uint, IChunkDecoder>` keyed by `EntryType`.

## Why Stream rather than IBlockDevice

Every consumer we will ever write already speaks `Stream`. `SHA256.HashData`
accepts one. A file copy accepts one. A test harness accepts one. An
`IBlockDevice` would require an adapter at every single one of those boundaries and
would buy nothing that `Stream` does not already provide — it is seekable, it has a
length, and it reads into a `Span<byte>`.

The one argument for a custom interface is expressing sector alignment in the type
system. That is not worth an adapter at every boundary; a documented invariant and
an assertion cover it.

## Why Strategy for codecs

```csharp
public interface IChunkDecoder
{
    uint EntryType { get; }
    Result<int> Decode(ReadOnlySpan<byte> source, Span<byte> destination);
}
```

Adding LZFSE later is one new class and one registration line. The alternative — a
`switch` on `EntryType` inside the read path — puts the decision in the hot path
and requires finding and editing that `switch` every time. The registry also gives
`dmg info` a free capability: it can report which codecs an image uses, and which
of those are supported, without decoding anything.

The registry *is* the factory. There is no abstract factory hierarchy; a dictionary
lookup is the whole mechanism.

## Consequences

- `DmgBlockStream` must honour the full `Stream` contract, including partial reads
  and reads that span chunk boundaries. That is a real correctness surface and it
  gets its own stories (S5.1, S5.4).
- `CanWrite` is `false`, permanently. Writes to the image are out of scope; writes
  to the mounted volume go through Windows into the VHD.
- An unsupported `EntryType` is a *decode-time* `Result` failure, not a parse-time
  one, so `dmg info` works on images that cannot be mounted.
- The decoder receives a destination span sized from the chunk descriptor, which is
  where the decompression-bomb cap is enforced — one place, not seven.
