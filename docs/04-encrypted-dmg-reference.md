# Encrypted DMG Reference — `encrcdsa` v2

Working notes for implementers. As with the container format, **verify every offset
against a real image** before relying on it. `tools/make-fixtures.sh` produces
encrypted fixtures with `hdiutil create -encryption AES-256`.

All multi-byte integers are **big-endian**.

---

## 1. Where the encryption sits

This is the fact that shapes the design. In an `encrcdsa` v2 image the encryption
header is at the **front** of the file, and the entire UDIF container — data fork,
property list, koly trailer — lives *inside* the encrypted payload.

```
┌──────────────────────────────┐  offset 0
│  encrcdsa header (~ 4 KiB)   │   ← plaintext
├──────────────────────────────┤  header.DataOffset
│                              │
│  AES-CBC ciphertext          │   ← decrypts to a complete, ordinary .dmg
│  in fixed-size blocks        │
│                              │
└──────────────────────────────┘
```

So decryption is a **decorator on the byte source**, not a mode inside the parser:

```
FileStream ──▶ EncryptedBlockStream ──▶ UdifReader ──▶ DmgBlockStream
```

`UdifReader` receives a plain seekable `Stream`, finds the koly trailer at the end
of *that stream's* length, and never learns encryption exists. This is the whole
argument for the pattern.

---

## 2. Header layout

Magic `encrcdsa` = `0x65 6E 63 72 63 64 73 61` at offset 0.

> **Corrected 2026-09-10 against `fixtures/generated/exfat-enc256.dmg` and
> `exfat-enc128.dmg`.** The table below originally had every field from `0x010`
> onward shifted four bytes too high — it allowed six words between `EncIvSize`
> and the UUID where the format has five — and it flattened the key material into
> the fixed header. It does not live there: the header ends with a **key count**
> and a **key-pointer table**, and the KDF parameters live in a separate
> descriptor the table points at. The old table also claimed a 4096-byte
> `BlockSize`; `hdiutil` writes **512**. Every offset below was read off bytes
> Apple wrote, and each variable-length field's container size is confirmed by
> where its zero padding starts.

### 2.1 Fixed header

| Offset | Size | Field | Observed |
| --- | --- | --- | --- |
| `0x000` | 8 | Signature | `encrcdsa` |
| `0x008` | 4 | Version | 2 |
| `0x00C` | 4 | EncIvSize | 16 — the per-block AES IV |
| `0x010` | 4 | EncMode | 5 (`CSSM_ALGMODE_CBC_IV8`) |
| `0x014` | 4 | EncAlgorithm | `0x80000001` — Apple's `CSSM_ALGID_AES` |
| `0x018` | 4 | **EncKeyBits** | 256 in enc256, 128 in enc128 |
| `0x01C` | 4 | PrngAlgorithm | 91 |
| `0x020` | 4 | PrngKeySize | 160 |
| `0x024` | 16 | Uuid | |
| `0x034` | 4 | **BlockSize** | **512**, not 4096 |
| `0x038` | 8 | **DataSize** | plaintext length; 12 582 912 in both fixtures |
| `0x040` | 8 | **DataOffset** | 122 368; `DataOffset + DataSize == fileLength` exactly |
| `0x048` | 4 | **KeyCount** | 1 |
| `0x04C` | 20 × KeyCount | Key-pointer table | see below |

There is no `EncPadding` word. That was the phantom field that shifted everything
else; `EncKeyBits` is at `0x018`, which is the single most load-bearing correction
here — reading 128/256 from the wrong offset silently mis-sizes the AES key.

### 2.2 Key-pointer table

One 20-byte entry per key, starting at `0x04C`:

| Offset | Size | Field | Observed |
| --- | --- | --- | --- |
| `+0x00` | 4 | Type | 1 = wrapped with a passphrase-derived key |
| `+0x04` | 8 | Offset | `0x60` — absolute file offset of the descriptor |
| `+0x0C` | 8 | Size | 616 |

Type 1 is the only kind this build unwraps. A certificate- or keychain-unlocked
image carries a different type and is refused by name (exit 3), not reported as a
wrong passphrase.

### 2.3 Key descriptor

At the offset the table gives — `0x60` in every image seen so far, but the pointer
is authoritative:

| Offset | Size | Field | Observed |
| --- | --- | --- | --- |
| `+0x00` | 4 | KdfAlgorithm | 103 = PBKDF2 |
| `+0x04` | 4 | KdfPrngAlgorithm | **0**, not 1 |
| `+0x08` | 4 | **KdfIterationCount** | 500 000 / 555 555 — calibrated per machine |
| `+0x0C` | 4 | KdfSaltLen | 20 |
| `+0x10` | 32 | **KdfSalt** | first `KdfSaltLen` bytes significant, rest zero |
| `+0x30` | 4 | BlobEncIvSize | 8 |
| `+0x34` | 32 | **BlobEncIv** | first `BlobEncIvSize` bytes significant |
| `+0x54` | 4 | BlobEncKeyBits | 192 |
| `+0x58` | 4 | BlobEncAlgorithm | `0x80000001` = **AES**, not 17 = 3DES |
| `+0x5C` | 4 | BlobEncPadding | 7 = PKCS#7 |
| `+0x60` | 4 | BlobEncMode | 6 = `CSSM_ALGMODE_CBCPadIV8`, not 2 |
| `+0x64` | 4 | **EncryptedKeyblobSize** | 64 (AES-256) / 48 (AES-128) |
| `+0x68` | descriptor size − `0x68` | **EncryptedKeyblob** | 512-byte container |

The header region is padded out; `DataOffset` is authoritative for where ciphertext
begins — do not assume a fixed header size.

---

## 3. Key unwrap

> **Corrected 2026-09-10.** The wrapping cipher is **AES-192-CBC**, not 3DES. The
> old text followed the 10.5-era reverse-engineering notes, which record
> `BlobEncAlgorithm = 17`; current `hdiutil` writes `0x80000001`, Apple's
> vendor-defined `CSSM_ALGID_AES`. The blob sizes prove it independently — 52
> bytes of key material padded to 64, and 36 padded to 48. PKCS#7 to a multiple of
> 8 would have produced 56 and 40. Only a 16-byte block lands on 64 and 48.
> 3DES-wrapped images from 10.5 still exist, so both ciphers are implemented; the
> header says which.

```
 1.  derived = PBKDF2-HMAC-SHA1(passphrase,
                                salt       = KdfSalt[0 .. KdfSaltLen],
                                iterations = KdfIterationCount,
                                outputLen  = BlobEncKeyBits / 8)      // 24 bytes

 2.  keyblob = AES-192-CBC-decrypt(key = derived,
                                   iv  = BlobEncIv[0 .. BlobEncIvSize] zero-extended
                                         to the cipher's block size,
                                   ciphertext = EncryptedKeyblob)

 3.  aesKey  = keyblob[0 .. EncKeyBits/8]                  // 16 or 32 bytes
     hmacKey = keyblob[EncKeyBits/8 .. +20]                // 20 bytes, HMAC-SHA1
```

`BlobEncIvSize` is 8 even though the cipher's block is 16. Apple zero-extends it.
Getting the IV wrong would corrupt only the first plaintext block — which is where
the AES key lives, so it cannot pass unnoticed.

The unwrapped blob is
`[aesKey][hmacKey (20)]["CKIE"][0x00]` and then PKCS#7 padding. The four-byte
marker is present in both fixtures but is not required by this implementation;
only the padding and the length are checked, so a future `hdiutil` that drops it
still opens.

**The passphrase may include its terminator.** `hdiutil -stdinpass` keys the image
off everything it read from standard input, newline included. Every image made by
a script that pipes `printf '%s\n' "$PASS"` — which is every fixture in this
repository — therefore has a passphrase one byte longer than the one its author
typed. The unwrap tries the passphrase as given and then, only if that fails,
once more with a single `\n` appended. Without that retry those images cannot be
opened with the passphrase their author believes they set.

.NET equivalents, all in the BCL:

```csharp
byte[] derived = Rfc2898DeriveBytes.Pbkdf2(
    passphrase, salt, iterations, HashAlgorithmName.SHA1, 24);

using var aes = Aes.Create();
aes.Key = derived;
byte[] keyblob = aes.DecryptCbc(wrapped, iv, PaddingMode.None);   // padding checked by hand
```

The padding is unpadded by hand rather than with `PaddingMode.PKCS7` so the
failure can come back as exit code 4 with a sentence about the passphrase. Left
to the BCL it arrives as "padding is invalid and cannot be removed", which is
true, unhelpful, and the most common thing a user of this tool will ever see.

---

## 4. Block decryption

Blocks are `BlockSize` bytes (4096) and each has its own IV derived from its index:

```
for block N (0-based, relative to DataOffset):
    iv        = HMAC-SHA1(hmacKey, BE32(N))[0 .. 16]
    plaintext = AES-CBC-decrypt(aesKey, iv, ciphertext[N])
```

Because the IV is a pure function of the block number, the stream is **randomly
seekable** — which is exactly what `UdifReader` needs to read a trailer at the end
of a multi-gigabyte file without decrypting everything before it.

```csharp
// Read a single 4096-byte plaintext block at index n.
Span<byte> ivFull = stackalloc byte[20];
Span<byte> counter = stackalloc byte[4];
BinaryPrimitives.WriteUInt32BigEndian(counter, (uint)n);
HMACSHA1.HashData(_hmacKey, counter, ivFull);
// iv = ivFull[..16]
```

The final block may be short: `DataSize` gives the true plaintext length, and the
stream must report that as `Length` rather than `ciphertextLength`.

---

## 5. Detecting a wrong passphrase

**This must not surface as "corrupt image".** It is the single most likely user
error and it needs its own exit code (4) and its own message.

Two checks, cheapest first:

1. **Keyblob padding.** After the 3DES decrypt, the keyblob's trailing bytes are
   PKCS#7-style padding. A wrong key produces bytes that are almost never valid
   padding. This catches nearly every wrong passphrase for the cost of one 3DES
   block.
2. **Plaintext sanity.** Decrypt the last block and look for the `koly` signature,
   or decrypt block 0 and check it is not high-entropy noise. This is the
   authoritative check.

Do both: (1) to fail fast, (2) to be certain.

---

## 6. Operational notes

- **3DES under FIPS policy.** `TripleDES.Create()` throws when the machine is in
  FIPS mode, because 3DES is not a FIPS-approved algorithm for new use. Catch that
  specific case and report it as such — "this machine's cryptography policy
  disallows Triple-DES, which Apple's key wrapping requires" — rather than as a
  generic crypto failure. There is no workaround short of implementing 3DES by
  hand, which we will not do.
- **Passphrase handling.** Held as `byte[]`, cleared with
  `CryptographicOperations.ZeroMemory` after derivation. Never logged, never in a
  crash report, never in the process title, never accepted as a command-line
  argument.
- **Iteration count is attacker-controlled.** A malicious image can specify
  2 000 000 000 PBKDF2 iterations as a denial of service. Cap it (say, 10 000 000)
  and report anything above the cap rather than hanging.
- **Legacy v1 (`cdsaencr`)** puts its header at the *end* of the file and uses a
  different structure. Detect it and report it as unsupported (exit 3). It is Mac
  OS X 10.4-era and vanishingly rare.

---

## 7. Hardening checklist

- [ ] `DataOffset` and `DataSize` bounded against the real file length
- [ ] `KdfIterationCount` capped; reject above the cap with a clear message
- [ ] `KdfSaltLen`, `BlobEncIvSize`, `EncryptedKeyblobSize` bounded against their
      containing fixed-size fields before slicing
- [ ] `EncKeyBits` restricted to {128, 256}; anything else is a reject, not a
      best-effort
- [ ] Passphrase buffer zeroed on every exit path, including exceptions
- [ ] Wrong-passphrase path is distinguishable from corrupt-image path in both the
      message and the exit code
