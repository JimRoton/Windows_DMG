# ADR-005 — Encryption as a Stream decorator, not a parser mode

**Status:** Proposed · 9 September 2026

## Context

In an `encrcdsa` v2 image the encryption header is at the front of the file and the
*entire* UDIF container — data fork, property list, koly trailer — lives inside the
encrypted payload. Two ways to handle it:

- a flag threaded through `UdifReader` and everything it calls, or
- a decorating `Stream` that presents the decrypted view.

## Decision

**A decorator.**

```
FileStream ──▶ EncryptedBlockStream ──▶ UdifReader ──▶ DmgBlockStream
```

`UdifReader` receives a plain seekable `Stream`, finds the koly trailer at the end
of *that stream's* `Length`, and never learns that encryption exists.

## Why this works

Apple's block IV is a pure function of the block index:

```
iv = HMAC-SHA1(hmacKey, BE32(blockNumber))[0..16]
```

That makes the decrypted view **randomly seekable** with no state, which is what
lets the decorator be a real `Stream` rather than a forward-only filter. Without
that property this decision would not be available — reading a trailer at the end
of a multi-gigabyte file would mean decrypting everything before it.

The alternative — a mode flag — would put an `if (encrypted)` at every read site in
the container parser, and each one would be a place to get the offset arithmetic
wrong. The decorator has exactly one place where ciphertext becomes plaintext.

## Consequences

- `EncryptedBlockStream.Length` must report `header.DataSize` (the plaintext
  length), not the ciphertext length. Getting this wrong puts the koly search in
  the wrong place and produces a confusing "not a DMG" error on a perfectly good
  image.
- Reads that are not block-aligned require decrypting the containing 4096-byte
  block and slicing. A one-block cache makes sequential reads cheap; this is a
  smaller and separate concern from the chunk cache above it.
- **Wrong-passphrase detection belongs here**, at construction time, not to the
  parser. If `EncryptedBlockStream` cannot be constructed, the failure is exit 4
  ("wrong passphrase") and never exit 9 ("corrupt image"). This distinction is the
  single most user-visible consequence of the design and it has its own story (S4.7).
- **3DES may be unavailable.** `TripleDES.Create()` throws under machine FIPS
  policy. That specific failure is caught and reported as a policy problem with no
  workaround, rather than as a generic crypto error.
- Passphrases live in a `byte[]` inside this class and are zeroed after key
  derivation on every exit path. There is no `--password` command-line option, by
  design — see [the CLI design, §2](../02-cli-design.md#dmg-mount).
