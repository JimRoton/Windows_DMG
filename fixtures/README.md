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
tools/make-fixtures.sh      # writes fixtures/generated/, ~38 MB total
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
| `exfat-raw.dmg` | `create -size 12m -fs exFAT` → `convert -format UDRW` | exFAT | **Not** a UDIF container. See below. |
| `exfat-zlib.dmg` | …→ `convert -format UDZO` | exFAT | zlib chunks — the main case. |
| `exfat-udro.dmg` | …→ `convert -format UDRO` | exFAT | Genuine **raw** (`0x00000001`) chunks. |
| `exfat-sparse.dmg` | `create -size 48m -fs exFAT`, one small file → `convert -format UDZO` | exFAT | Mostly empty, so most chunks are zero-fill / ignore. |
| `exfat-enc256.dmg` | …→ `convert -format UDRW -encryption AES-256 -stdinpass` | exFAT | `encrcdsa` v2 wrapper, AES-256. |
| `exfat-enc128.dmg` | …→ `convert -format UDRW -encryption AES-128 -stdinpass` | exFAT | AES-128 key length. |
| `fat32.dmg` | `create -size 48m -fs "MS-DOS FAT32"` → `convert -format UDZO` | FAT32 | FAT32 probe. |
| `hfsplus.dmg` | `create -size 12m -fs "HFS+"` → `convert -format UDZO` | HFS+ | Negative case — must be refused. |
| `apfs.dmg` | `create -size 32m -fs APFS` → `convert -format UDZO` | APFS | Negative case. |
| `bzip2.dmg` | …→ `convert -format UDBZ` | exFAT | Negative case — unsupported codec. |
| `adc.dmg` | …→ `convert -format UDCO` | exFAT | Apple ADC decoder. |
| `multipart.dmg` | blank `-layout NONE`, `diskutil partitionDisk … MBR ExFAT ExFAT` → `convert -format UDZO` | exFAT ×2 | Partition selection. |
| `zerofill.dmg` | `create -srcfolder … -fs exFAT -format UDZO` | exFAT | **Zero-fill** (`0x00000000`) chunks. |

"…" means the shared 12 MiB exFAT source image, so the **seven** fixtures built
from it — `exfat-raw`, `exfat-zlib`, `exfat-udro`, `exfat-enc256`,
`exfat-enc128`, `bzip2` and `adc` — decode to **the same raw sector stream**.
That is deliberate: it lets a test assert that raw, zlib, ADC, bzip2 and both
encrypted variants all produce byte-identical output, isolating the codec from
everything else. The manifest bears this out: all seven carry an identical
`decoded_sha256`.

### `exfat-raw.dmg` is not what its name suggests

It is a flat UDRW image: the sector stream on its own, with **no koly trailer,
no property list and no chunk table**, so it exercises no chunk codec at all.
It is kept because a flat image is its own ground truth and the reader has to
correctly refuse to find a container in it — but the fixture that actually
carries raw chunks is `exfat-udro.dmg`.

### Volume contents

Each populated volume holds three known files:

| File | Contents |
| --- | --- |
| `HELLO.TXT` | `Hello, DMG fixture!\n` (20 bytes) |
| `README.TXT` | `Windows_DMG test corpus\nfixture v1\n` (35 bytes) |
| `DATA.BIN` | 65536 bytes: the big-endian `uint32` counter `0…16383` |

`exfat-sparse.dmg` deliberately holds only `HELLO.TXT`. Both partitions of
`multipart.dmg` hold `HELLO.TXT` and `README.TXT` but not `DATA.BIN`.
`zerofill.dmg` holds all three plus `ZEROS.BIN`, 8 MiB of zeros — that file is
what puts a long run of zeros inside *allocated* space, which is what makes
`hdiutil` emit zero-fill chunks rather than `ignore`.

## The manifest

`fixtures/manifest.json` is the ground truth the reader is measured against.
Per fixture it records the name, the `hdiutil` recipe, the container size and
its SHA-256, whether it is encrypted (and with what passphrase), the expected
filesystem, and — the point of the whole exercise — the **SHA-256 of the fully
decoded raw sector stream**: the whole-disk image with every codec and the
encryption wrapper stripped off.

**Every decoded hash comes from Apple's decoder, never from ours.** A hash
produced by the code under test would make the test circular and worthless.
There are two ways to get one, because `hdiutil convert` cannot decrypt a
source:

| Case | How the hash is obtained |
| --- | --- |
| Unencrypted | `hdiutil convert <img> -format UDTO -o <tmp>` → `shasum -a 256 <tmp>.cdr` |
| Encrypted | `hdiutil attach -stdinpass -nomount -readonly <img>` → `dd if=/dev/rdiskN bs=1m \| shasum -a 256` → detach that exact device |

### The two methods agree

`exfat-zlib.dmg` is deliberately hashed **both** ways on every run, as a
control, and the result is recorded in the manifest under `hash.cross_check`.
On the corpus as generated they **agree** — `convert`-to-`.cdr` and
`attach`-plus-`dd` produce the same byte stream — so the split above is a
matter of what is possible, not of two different notions of "decoded". If they
ever disagree, `make-manifest.sh` says so loudly, sets `cross_check.result` to
`DISAGREE`, and records both values; the unencrypted hashes would then be the
`convert` ones (encrypted fixtures have no alternative to attach + `dd`), and
that discrepancy would need chasing before the corpus could be trusted.

### Seven fixtures share one hash

`exfat-raw`, `exfat-zlib`, `exfat-udro`, `exfat-enc256`, `exfat-enc128`,
`bzip2` and `adc` are all converted from the same 12 MiB exFAT source, so all
seven carry an **identical** `decoded_sha256`. That equality is itself a test:
raw, zlib, ADC, bzip2 and both encrypted variants must all decode to the same
sectors, which isolates the codec and the encryption wrapper from everything
else.

### The manifest is a snapshot, not a golden value

`hdiutil` bakes volume UUIDs and creation timestamps into the images it makes,
so **re-running `make-fixtures.sh` produces a corpus with different hashes.**
The manifest describes the corpus currently sitting in `fixtures/generated/` on
this machine; it is not a universal constant, and a hash from another machine
will not match. Regenerate the two together:

```sh
tools/make-fixtures.sh && tools/make-manifest.sh
```

Each record also carries `image_sha256`, the hash of the `.dmg` container
itself, so a test can tell "the manifest is stale relative to these fixtures"
apart from "the reader decoded the image wrongly" — which are otherwise the
same symptom.

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
  because UDRW is uncompressed. `exfat-udro.dmg` and `zerofill.dmg` are ~250 KB
  each, because UDRO stores its chunks uncompressed too; the rest are under
  100 KiB.
* **Zero-fill chunks needed a different verb, not a different format.**
  `hdiutil convert` never emits type `0x00000000`, whatever you give it: it
  hands every chunk holding data to the codec — even a chunk that is entirely
  zeros — and marks only filesystem free space as `ignore` (`0x00000002`).
  Measured on macOS 26.7 (25G227), all of these produced **no** zero-fill:

  | Tried | Got |
  | --- | --- |
  | `convert <exFAT image> -format UDZO / UDRO / UDCO` | zlib / raw / ADC, plus `ignore` |
  | `convert <64 MiB of zeros> -format UDZO` | 64 zlib chunks |
  | `convert <64 MiB of zeros> -format UDRO` | 1 raw chunk |
  | `convert <half-zero raw disk> -format UDZO` | 48 zlib chunks |
  | `convert <.sparseimage> -format UDZO` | zlib + `ignore` |
  | `convert <.sparsebundle> -format UDZO` | 64 zlib chunks |
  | `convert … -format UDZO -imagekey zlib-level=0` | 64 **raw** chunks — real, but a side effect of asking zlib for no compression, so too fragile to build a fixture on |
  | `create -size 32m -type UDIF -fs exFAT` | a flat image with no koly at all |

  `hdiutil create -srcfolder` takes a different path — it lays a fresh volume
  down and knows which of the sectors it wrote are zeros — and that one does
  emit zero-fill. Hence `zerofill.dmg`.
* **`create -srcfolder -format UDRO` would have been better and is unusable.**
  It yields zero-fill *and* raw *and* `ignore` in one image, but on macOS 26.7
  (25G227) the image it writes is one `hdiutil` itself will not read back:
  `hdiutil verify`, `hdiutil convert` and even `hdiutil attach -noverify` all
  answer `corrupt image`. With no way to ask Apple what the image decodes to
  there is no ground truth, which is the entire point of a fixture — so
  `zerofill.dmg` is UDZO and `exfat-udro.dmg` supplies the raw chunks
  separately. (Our own reader parses the UDRO one without complaint, which is
  not a comforting fact about either implementation.)
* No fixture was skipped on the machine this corpus was last generated on
  (macOS 26.7, build 25G227) — all 13 were produced. If a recipe ever stops
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
5. **`hdiutil create -srcfolder` attaches a volume internally, and that one
   cannot be tracked.** It is the recipe behind `zerofill.dmg` and there is no
   substitute for it, so it is allowed — but only as a single `hd()` call, with
   explicit stdin and a hard timeout, and it does clean up after itself
   (verified by comparing `hdiutil info` before and after). Do not "improve" it
   by attaching the volume yourself: `-srcfolder` is the whole reason the
   zero-fill chunks exist.
6. **Parse `/dev/diskN` with a `/dev/disk` match, not "the first line".**
   `attach` interleaves `Checksumming …` progress lines with the device table.
