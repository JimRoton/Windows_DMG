# E2E fixtures

Two small images, checked in (unlike everything under `fixtures/` at the repo
root, which is gitignored). Windows CI runners have no `hdiutil` and cannot
generate the corpus themselves, so `dmg.exe`'s own end-to-end suite needs its
fixtures committed instead.

Both were produced on macOS with `tools/make-fixtures.sh` and copied here
verbatim from `fixtures/generated/`.

| File | Size | Recipe | Filesystem | Purpose |
| --- | --- | --- | --- | --- |
| `exfat-zlib.dmg` | ~33 KB | `create -size 12m -fs exFAT` → `convert -format UDZO` | exFAT | The positive case: `dmg mount` attaches it for real. |
| `hfsplus.dmg` | ~42 KB | `create -size 12m -fs "HFS+"` → `convert -format UDZO` | HFS+ | The negative case: Windows has no HFS+ driver, so `dmg mount` must refuse with exit 5. |

## Known file

Every populated volume `tools/make-fixtures.sh` builds - `exfat-zlib.dmg`
included - carries the same known file, written by the script's
`build_stage()`:

| File | Contents |
| --- | --- |
| `HELLO.TXT` | `Hello, DMG fixture!\n` (20 bytes) |

`Dmg.E2E.Tests.E2eFixtures.KnownFileContent` hardcodes this string. That is
safe: the file's *content* does not change when the corpus is regenerated,
only the `.dmg` container's own bytes do (`hdiutil` bakes a fresh volume UUID
into every image it writes) - see `fixtures/README.md` at the repo root,
"The manifest is a snapshot, not a golden value". The end-to-end suite mounts
`exfat-zlib.dmg`, reads `HELLO.TXT` off the real, Windows-assigned drive
letter, and checks both its exact content and its SHA-256 against this
constant - never against a hash pinned to one specific generation of the
image.

`hfsplus.dmg` is never mounted (that is the point), so its contents are never
read.

## Regenerating these two files

```sh
tools/make-fixtures.sh
cp fixtures/generated/exfat-zlib.dmg tests/Dmg.E2E.Tests/Fixtures/exfat-zlib.dmg
cp fixtures/generated/hfsplus.dmg    tests/Dmg.E2E.Tests/Fixtures/hfsplus.dmg
```

Only worth doing if the recipe itself changes - the known file's content is
stable across regenerations, so there is no routine need to refresh these.
