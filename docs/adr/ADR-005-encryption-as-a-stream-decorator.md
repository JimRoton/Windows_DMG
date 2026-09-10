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

---

## Correction — 10 September 2026

The decorator decision below stands and was validated in implementation (S4.1–S4.5).
Two factual claims in it did **not** survive contact with real images, and are
corrected here rather than silently edited above.

**1. The wrapping cipher is AES-192-CBC, not 3DES.** This ADR originally treated
3DES as the key-wrapping algorithm and spent a paragraph on `TripleDES.Create()`
failing under machine FIPS policy. That was drawn from 10.5-era reverse-engineering
notes, which record `BlobEncAlgorithm = 17`. Current `hdiutil` writes `0x80000001`
— Apple's vendor-defined `CSSM_ALGID_AES`. The blob sizes prove it independently:
52 bytes of key material padded to 64, and 36 padded to 48. PKCS#7 to a multiple
of 8 would have produced 56 and 40; only a 16-byte block lands on 64 and 48.

3DES-wrapped images from 10.5 do still exist, so both ciphers are implemented and
the header decides which. **The FIPS concern is therefore a legacy-image-only
concern, not a main-path one** — it no longer affects images `hdiutil` produces today.

**2. `BlockSize` is 512, not 4096.** The header layout in
[04-encrypted-dmg-reference.md](../04-encrypted-dmg-reference.md) had every field
from `0x010` onward shifted four bytes too high, because of a phantom `EncPadding`
word. Most damagingly that put `EncKeyBits` at the wrong offset, which silently
mis-sizes the AES key. The key material also does not live in the fixed header at
all: the header ends with a key count and a key-pointer table, and the KDF
parameters live in a descriptor that table points at.

**3. A finding neither document anticipated: the passphrase may include its
terminator.** `hdiutil -stdinpass` keys the image off *everything it read from
standard input*, newline included. Any image created by a script that pipes
`printf '%s\n' "$PASS"` — which is every fixture in this repository, and a great
many real images — has a passphrase one byte longer than the one its author typed.
The unwrap tries the passphrase as given, then once more with a trailing `\n`.
Without that retry, those images cannot be opened with the passphrase their author
believes they set.

All three were found by decoding bytes Apple actually wrote. They are the reason
both format documents carry a "verify against a real image" warning at the top.
