# UDIF Container Format Reference

Working notes for implementers. Field offsets here come from public
reverse-engineering work and from QEMU's `block/dmg.c`; **verify every offset
against a real image before relying on it in code** — `tools/make-fixtures.sh`
produces images for exactly that purpose.

All multi-byte integers in UDIF are **big-endian**.

---

## 1. Overall shape

A UDIF file is read from the back.

```
┌────────────────────────────────────────────┐  offset 0
│  Data fork — the compressed chunk payload  │
│  (chunks are addressed by absolute offset, │
│   not stored in any particular order)      │
├────────────────────────────────────────────┤  XMLOffset
│  XML property list                         │
│    resource-fork → blkx → [ mish, ... ]    │
├────────────────────────────────────────────┤  filesize - 512
│  koly trailer (512 bytes)                  │
└────────────────────────────────────────────┘  EOF
```

Read order: koly → plist → blkx entries → mish blocks → chunk descriptors.

---

## 2. The koly trailer

512 bytes at `filesize - 512`. Magic `koly` = `0x6B6F6C79`.

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| `0x000` | 4 | Signature | `koly` |
| `0x004` | 4 | Version | 4 for everything modern |
| `0x008` | 4 | HeaderSize | 512 |
| `0x00C` | 4 | Flags | |
| `0x010` | 8 | RunningDataForkOffset | |
| `0x018` | 8 | DataForkOffset | usually 0 |
| `0x020` | 8 | DataForkLength | |
| `0x028` | 8 | RsrcForkOffset | usually 0 in v4 |
| `0x030` | 8 | RsrcForkLength | usually 0 in v4 |
| `0x038` | 4 | SegmentNumber | **0**, not 1, on a single-part image — verified |
| `0x03C` | 4 | SegmentCount | **0**, not 1, on a single-part image — verified |
| `0x040` | 16 | SegmentID | |
| `0x050` | 4 | DataChecksumType | |
| `0x054` | 4 | DataChecksumSize | bits |
| `0x058` | 128 | DataChecksum | |
| `0x0D8` | 8 | **XMLOffset** | where the plist starts |
| `0x0E0` | 8 | **XMLLength** | |
| `0x0E8` | 120 | Reserved | |
| `0x160` | 4 | ChecksumType | |
| `0x164` | 4 | ChecksumSize | |
| `0x168` | 128 | Checksum | |
| `0x1E8` | 4 | ImageVariant | |
| `0x1EC` | 8 | **SectorCount** | total decoded size ÷ 512 |
| `0x1F4` | 12 | Reserved | |

**Validation before anything else:** signature matches; `HeaderSize == 512`;
`XMLOffset + XMLLength <= filesize - 512`; `DataForkOffset + DataForkLength <=
filesize`; `SectorCount * 512` does not overflow.

The `XMLOffset + XMLLength <= filesize - 512` bound must be **inclusive**: in an
hdiutil-produced image the plist ends exactly where the trailer begins, so a
strict `<` rejects every real image. Verified against a UDZO fixture.

A file with no `koly` at all is `UnsupportedFormat` (3) — it may simply be some
other format. A file that says `koly` and then contradicts itself is
`CorruptImage` (9). An unrecognised `Version` is `UnsupportedFormat`.

---

## 3. The property list

`XMLOffset` points at an Apple XML plist (`<!DOCTYPE plist ...>`). The structure of
interest:

```xml
<dict>
  <key>resource-fork</key>
  <dict>
    <key>blkx</key>
    <array>
      <dict>
        <key>Attributes</key> <string>0x0050</string>
        <key>CFName</key>     <string>Protective Master Boot Record (MBR : 0)</string>
        <key>Data</key>       <data>bWlzaAAAAAE...</data>   <!-- base64 mish block -->
        <key>ID</key>         <string>-1</string>
        <key>Name</key>       <string>Protective Master Boot Record (MBR : 0)</string>
      </dict>
      ...
    </array>
    <key>plst</key> <array>...</array>   <!-- ignore -->
  </dict>
</dict>
```

Only a small subset of plist needs supporting: `dict`, `array`, `key`, `string`,
`data`, `integer`. `System.Xml.Linq` handles the parse; the base64 in `<data>` may
contain whitespace and newlines, which `Convert.FromBase64String` rejects — strip
whitespace first.

One `blkx` entry usually exists per partition, plus entries for the protective MBR
and any free space.

---

## 4. The mish block

Each `<data>` payload decodes to a mish block. Magic `mish` = `0x6D697368`.

| Offset | Size | Field | Notes |
| --- | --- | --- | --- |
| `0x00` | 4 | Signature | `mish` |
| `0x04` | 4 | Version | 1 |
| `0x08` | 8 | **FirstSectorNumber** | where this region starts on the decoded disk |
| `0x10` | 8 | **SectorCount** | how many sectors this region covers |
| `0x18` | 8 | DataOffset | usually 0 |
| `0x20` | 4 | BuffersNeeded | |
| `0x24` | 4 | BlockDescriptors | |
| `0x28` | 24 | Reserved | |
| `0x40` | 32 | Checksum block | type, size, then the value |
| `0x?` | 4 | **NumberOfBlockChunks** | at `0x00CC` in practice — verify |
| `0x?` | 40×N | Chunk descriptors | immediately follows |

The chunk table begins at offset **204 (`0xCC`)** in a v1 mish block. Confirm this
against a fixture; different sources describe the reserved/checksum region
differently and this is the offset most likely to be wrong.

---

## 5. Chunk descriptors

40 bytes each, repeated until a terminator entry.

| Offset | Size | Field | Meaning |
| --- | --- | --- | --- |
| `0x00` | 4 | **EntryType** | compression method — table below |
| `0x04` | 4 | Comment | usually 0; `+beg` / `+end` on comment entries |
| `0x08` | 8 | **SectorNumber** | start sector, *relative to the mish block's FirstSectorNumber* |
| `0x10` | 8 | **SectorCount** | uncompressed length in 512-byte sectors |
| `0x18` | 8 | **CompressedOffset** | byte offset into the data fork, relative to `DataForkOffset` |
| `0x20` | 8 | **CompressedLength** | bytes to read and hand to the codec |

### EntryType values

| Value | Meaning | Codec | v1? |
| --- | --- | --- | --- |
| `0x00000000` | Zero fill | — emit zeros, no read | ✅ |
| `0x00000001` | Raw | direct copy | ✅ |
| `0x00000002` | Ignore / free | — emit zeros | ✅ |
| `0x80000004` | Apple ADC | ADC (written here) | ✅ |
| `0x80000005` | zlib | `ZLibStream` | ✅ |
| `0x80000006` | bzip2 | — | ❌ report |
| `0x80000007` | LZFSE | — | ❌ report |
| `0x80000008` | LZMA | — | ❌ report |
| `0x7FFFFFFE` | Comment | skip | ✅ |
| `0xFFFFFFFF` | Terminator | end of list | ✅ |

An unsupported type is not an error in the parse — it is an error at *decode* time,
and only if the mount actually touches that chunk. `dmg info` should report which
codecs an image uses without failing.

### Absolute sector of a chunk

```
absoluteStartSector = mish.FirstSectorNumber + chunk.SectorNumber
```

This is a common source of bugs: `SectorNumber` is relative to the mish block, not
to the disk.

---

## 6. Apple ADC

A simple LZ variant used in older images. Three token types, decided by the top
bits of the first byte:

| First byte | Type | Encoding |
| --- | --- | --- |
| `1xxxxxxx` | Literal run | run length = `(b & 0x7F) + 1` bytes follow verbatim |
| `01xxxxxx` | Long match | length = `((b & 0x3F) >> 2) + 3`, offset = `((b & 0x03) << 8) \| next` + 1 |
| `00xxxxxx` | Short match | length = `(b >> 2) + 3`, offset = `((b & 0x03) << 8) \| next` + 1 |

Matches copy from the already-decoded output, byte at a time (overlapping copies
are legal and intentional). Roughly 150 lines. Guard the output pointer against the
declared chunk size on every write.

---

## 7. Container variants

| Variant | Detection | v1 support |
| --- | --- | --- |
| UDZO | koly present, chunks mostly `0x80000005` | ✅ |
| UDRW / UDTO / raw | koly present, chunks all `0x00000001`; or no koly at all | ✅ |
| UDBZ | chunks `0x80000006` | ❌ report |
| ULFO | chunks `0x80000007` | ❌ report |
| ULMO | chunks `0x80000008` | ❌ report |
| Encrypted v2 | `encrcdsa` magic at offset 0 | ✅ see [04](04-encrypted-dmg-reference.md) |
| Encrypted v1 | `cdsaencr` near EOF | ❌ report |
| NDIF | classic `.img`, different structure entirely | ❌ report |
| sparseimage / sparsebundle | different container / a directory | ❌ report |

---

## 8. Hardening checklist

Every item here corresponds to a story in [BACKLOG.md](../BACKLOG.md).

- [ ] `CompressedOffset + CompressedLength` ≤ data fork length, checked per chunk
- [ ] `SectorCount × 512` in `checked` arithmetic; reject on overflow
- [ ] Decoder output hard-capped at `SectorCount × 512`; abort, don't truncate
- [ ] Chunk count bounded before allocating the descriptor array
- [ ] Extents may not overlap; the union must cover `[0, koly.SectorCount)` with no
      gaps, or the image is corrupt
- [ ] `XMLLength` bounded before reading the plist into memory
- [ ] Plist entity expansion disabled (`DtdProcessing.Prohibit`)
- [ ] Base64 payload size bounded before decode
