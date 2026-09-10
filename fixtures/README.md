# Test fixtures

The DMG test corpus. Every fixture is produced by Apple's own `hdiutil`, so the
images are genuine articles rather than something we hand-rolled — which is the
whole point: they are the ground truth our reader is measured against.

## Layout

| Path | Committed? | What |
| --- | --- | --- |
| `tools/make-fixtures.sh` | yes | Generates the images. |
| `tools/make-manifest.sh` | yes | Generates `manifest.json`. |
| `fixtures/manifest.json` | yes | Recipe + size + ground-truth SHA-256 per fixture. |
| `fixtures/generated/` | **no** (gitignored) | The `.dmg` files themselves. |

The images are not committed. Regenerate them with:

```sh
tools/make-fixtures.sh      # writes fixtures/generated/, ~37 MB total
tools/make-manifest.sh      # refreshes fixtures/manifest.json
```

Both scripts are idempotent and safe to re-run; they rebuild from scratch each
time. `make-fixtures.sh` prints a produced/skipped summary and exits non-zero
only if it cannot even stage its content files.

## Passphrase

Every encrypted fixture uses the passphrase:

```
dmg-test-passphrase
```

It is handed to `hdiutil` **only on stdin, newline-terminated, via
`-stdinpass`** — never as a command-line argument, so it never appears in the
process table. This is a throwaway passphrase for a throwaway test corpus; it is
deliberately in the clear here and in the script header, and it must never be
reused for anything real.

## The fixtures

| Name | Recipe | Filesystem | Purpose |
| --- | --- | --- | --- |
| `exfat-raw.dmg` | `create -size 12m -fs exFAT` → `convert -format UDRW` | exFAT | Raw (uncompressed) chunks; the happy path. |
| `exfat-zlib.dmg` | …→ `convert -format UDZO` | exFAT | zlib chunks — the main case. |
| `exfat-sparse.dmg` | `create -size 48m -fs exFAT`, one small file → `convert -format UDZO` | exFAT | Mostly empty, so most chunks are zero-fill / ignore. |
| `exfat-enc256.dmg` | …→ `convert -format UDRW -encryption AES-256 -stdinpass` | exFAT | `encrcdsa` v2 wrapper, AES-256. |
| `exfat-enc128.dmg` | …→ `convert -format UDRW -encryption AES-128 -stdinpass` | exFAT | AES-128 key length. |
| `fat32.dmg` | `create -size 48m -fs "MS-DOS FAT32"` → `convert -format UDZO` | FAT32 | FAT32 probe. |
| `hfsplus.dmg` | `create -size 12m -fs "HFS+"` → `convert -format UDZO` | HFS+ | Negative case — must be refused. |
| `apfs.dmg` | `create -size 32m -fs APFS` → `convert -format UDZO` | APFS | Negative case. |
| `bzip2.dmg` | …→ `convert -format UDBZ` | exFAT | Negative case — unsupported codec. |
| `adc.dmg` | …→ `convert -format UDCO` | exFAT | Apple ADC decoder. |
| `multipart.dmg` | blank `-layout NONE`, `diskutil partitionDisk … MBR ExFAT ExFAT` → `convert -format UDZO` | exFAT ×2 | Partition selection. |

"…" means the shared 12 MiB exFAT source image, so the eight fixtures built from
it decode to **the same raw sector stream**. That is deliberate: it lets a test
assert that raw, zlib, ADC, bzip2 and both encrypted variants all produce
byte-identical output, isolating the codec from everything else.

### Volume contents

Each populated volume holds three known files:

| File | Contents |
| --- | --- |
| `HELLO.TXT` | `Hello, DMG fixture!\n` (20 bytes) |
| `README.TXT` | `Windows_DMG test corpus\nfixture v1\n` (35 bytes) |
| `DATA.BIN` | 65536 bytes: the big-endian `uint32` counter `0…16383` |

`exfat-sparse.dmg` deliberately holds only `HELLO.TXT`. Both partitions of
`multipart.dmg` hold `HELLO.TXT` and `README.TXT` but not `DATA.BIN`.

## Notes and limitations

* **Sizes are floors, not preferences.** FAT32 needs at least 65525 clusters, so
  `fat32.dmg` is built at 48 MiB; APFS will not `newfs` into 12 MiB, so
  `apfs.dmg` is built at 32 MiB. Both are then converted to UDZO, which brings
  the committed-corpus footprint back down to tens of kilobytes.
* **`multipart.dmg` uses an MBR (`fdisk`) partition scheme, not GPT.** `diskutil`
  insists on a 200 MiB EFI partition when it lays down GPT, which would have
  made the image an order of magnitude larger for no extra coverage. MBR gives
  exactly the two data partitions the fixture is for. If a GPT multi-partition
  case is ever needed, it has to be a separate, larger fixture.
* **`exfat-raw.dmg`, `exfat-enc128.dmg` and `exfat-enc256.dmg` are ~12 MiB each**
  because UDRW is uncompressed. The other eight are all under 100 KiB.
* No fixture was skipped on the machine this corpus was last generated on
  (macOS 26.7, build 25G227) — all 11 were produced. If a recipe ever stops
  working, `make-fixtures.sh` records it as a skip with a reason rather than
  fabricating the image, and the fixture is simply absent from `manifest.json`.

## Safety rules for anyone editing these scripts

These are not style preferences. An earlier attempt at this corpus hung
`hdiutil` and put a GUI passphrase dialog on the developer's screen.

1. **Never call `hdiutil` or `diskutil` directly.** Go through the `hd()`,
   `hdp()` and `dk()` wrappers, which always supply stdin explicitly and impose
   a hard `SIGALRM` timeout. An `hdiutil` that runs out of stdin escalates to a
   GUI prompt.
2. **`-stdinpass` reads up to a newline.** Always `printf '%s\n'`, never a bare
   `printf` — a passphrase with no trailing newline leaves `hdiutil` blocked on
   stdin, which is exactly what escalated last time.
3. **`-stdinpass` on `hdiutil convert` sets the passphrase of the *output*
   image. It cannot decrypt a source.** To read an encrypted image you must
   `attach -stdinpass` it and read the raw device.
4. **Detach only devices this run attached.** Both scripts track them in
   `$ATTACHED` and clean up via a `trap`. The developer may well have unrelated
   images mounted; a broad or looping `hdiutil detach` is destructive.
5. **Parse `/dev/diskN` with a `/dev/disk` match, not "the first line".**
   `attach` interleaves `Checksumming …` progress lines with the device table.
