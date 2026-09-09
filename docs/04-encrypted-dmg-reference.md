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

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| `0x000` | 8 | Signature | `encrcdsa` |
| `0x008` | 4 | Version | 2 |
| `0x00C` | 4 | EncIvSize | 16 |
| `0x010` | 4 | EncMode | |
| `0x014` | 4 | EncAlgorithm | |
| `0x018` | 4 | EncPadding | |
| `0x01C` | 4 | EncKeyBits | 128 or 256 |
| `0x020` | 4 | PrngAlgorithm | |
| `0x024` | 4 | PrngKeySize | |
| `0x028` | 16 | Uuid | |
| `0x038` | 4 | **BlockSize** | 4096 in practice |
| `0x03C` | 8 | **DataSize** | plaintext length |
| `0x044` | 8 | **DataOffset** | where ciphertext starts |
| `0x04C` | 4 | KdfAlgorithm | 103 = PBKDF2 |
| `0x050` | 4 | KdfPrngAlgorithm | 1 = HMAC-SHA1 |
| `0x054` | 4 | **KdfIterationCount** | typically 250 000 |
| `0x058` | 4 | KdfSaltLen | 20 |
| `0x05C` | 32 | **KdfSalt** | first `KdfSaltLen` bytes are significant |
| `0x07C` | 4 | BlobEncIvSize | 8 |
| `0x080` | 32 | **BlobEncIv** | first `BlobEncIvSize` bytes; the 3DES IV |
| `0x0A0` | 4 | BlobEncKeyBits | 192 |
| `0x0A4` | 4 | BlobEncAlgorithm | 17 = 3DES |
| `0x0A8` | 4 | BlobEncPadding | |
| `0x0AC` | 4 | BlobEncMode | 2 = CBC |
| `0x0B0` | 4 | **EncryptedKeyblobSize** | |
| `0x0B4` | 48 | **EncryptedKeyblob** | up to `EncryptedKeyblobSize` bytes |

The header region is padded out; `DataOffset` is authoritative for where ciphertext
begins — do not assume a fixed header size.

---

## 3. Key unwrap

```
 1.  derived = PBKDF2-HMAC-SHA1(passphrase,
                                salt       = KdfSalt[0 .. KdfSaltLen],
                                iterations = KdfIterationCount,
                                outputLen  = BlobEncKeyBits / 8)      // 24 bytes

 2.  keyblob = 3DES-EDE-CBC-decrypt(key = derived,
                                    iv  = BlobEncIv[0 .. BlobEncIvSize],
                                    ciphertext = EncryptedKeyblob)

 3.  aesKey  = keyblob[0 .. EncKeyBits/8]                  // 16 or 32 bytes
     hmacKey = keyblob[EncKeyBits/8 .. +20]                // 20 bytes, HMAC-SHA1
```

.NET equivalents, all in the BCL:

```csharp
using var kdf = new Rfc2898DeriveBytes(passphrase, salt, iterations, HashAlgorithmName.SHA1);
byte[] derived = kdf.GetBytes(24);

using var des = TripleDES.Create();
des.Mode = CipherMode.CBC;
des.Padding = PaddingMode.None;      // handle padding ourselves; see below
byte[] keyblob = des.CreateDecryptor(derived, blobIv).TransformFinalBlock(blob, 0, blob.Length);
```

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
