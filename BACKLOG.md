# Backlog

**10 epics · 84 stories.** Generated from [docs/02-cli-design.md](docs/02-cli-design.md).
This file is the source of truth; GitHub issues are created from it.

Story IDs are stable. Branch names are `story/S<id>-<slug>`. Every story lists its
dependencies so the parallelisable work is visible at a glance.

---

## Legend

| Field | Meaning |
| --- | --- |
| **Dep** | Stories that must be Done first. `—` means it can start immediately. |
| **Size** | S ≈ half a day · M ≈ a day · L ≈ two days |
| **Plat** | `core` runs on macOS and Windows · `win` needs Windows · `ci` is pipeline work |

---

## E1 — Foundation and CI

*Goal: a repo where `dotnet test` passes on macOS and Windows, fixtures exist, and
the dependency rule is enforced by a red build rather than by good intentions.*

| ID | Story | Dep | Size | Plat |
| --- | --- | --- | --- | --- |
| S1.1 | Create the solution and five projects (`Dmg.Core`, `Dmg.Windows`, `Dmg.Cli`, and three test projects) with `Directory.Build.props` setting `nullable`, `TreatWarningsAsErrors`, `InvariantGlobalization` and AOT analyzers | — | M | core |
| S1.2 | Add `Result<T>` and `DmgError` with the exit-code taxonomy from the CLI design §2 | S1.1 | S | core |
| S1.3 | Add verbosity plumbing: a `Verbosity` enum and an `IOutput` writing to stdout/stderr, no third-party logger | S1.1 | S | core |
| S1.4 | Write `tools/make-fixtures.sh` producing the eleven fixtures in the testing strategy §2 via `hdiutil` | — | L | core |
| S1.5 | Generate `fixtures/manifest.json` with the SHA-256 of each fixture's decoded stream, taken from `hdiutil convert -format UDTO` | S1.4 | M | core |
| S1.6 | CI workflow: `core` job on macos-latest, `windows` job on windows-latest, `dependency-guard` job failing on any `PackageReference` under `src/` | S1.1 | M | ci |

---

## E2 — UDIF container reader

*Goal: given a `.dmg`, produce a validated extent index. No decoding yet.*

| ID | Story | Dep | Size | Plat |
| --- | --- | --- | --- | --- |
| S2.1 | Big-endian reading helpers over `ReadOnlySpan<byte>` with bounds checks and `checked` arithmetic | S1.2 | S | core |
| S2.2 | Parse and validate the koly trailer; reject on bad magic, bad header size, or offsets past EOF | S2.1 | M | core |
| S2.3 | Minimal XML property-list reader (`dict`/`array`/`key`/`string`/`data`/`integer`) with DTD processing prohibited | S2.1 | M | core |
| S2.4 | Extract `resource-fork` → `blkx` entries and base64-decode each `Data` payload, stripping whitespace | S2.3 | S | core |
| S2.5 | Parse the mish block header; verify the chunk-table offset against a real fixture and record the finding in `docs/03` | S2.1 | M | core |
| S2.6 | Parse the 40-byte chunk descriptor table, handling comment and terminator entries | S2.5 | M | core |
| S2.7 | Build `ExtentIndex`: flat sorted array with binary search; assert the extents cover `[0, koly.SectorCount)` with no gaps or overlaps | S2.6 | M | core |
| S2.8 | `IImageFormatProbe` chain — `encrcdsa` → UDIF → raw → unknown — with a good terminal error | S2.2 | M | core |
| S2.9 | Hostile-input hardening pass over E2 with the corpus from testing strategy §2: every malformed fixture exits cleanly with a specific code | S2.7, S2.8 | L | core |

---

## E3 — Chunk codecs

*Goal: decode every chunk type v1 supports, verified against Apple's own output.*

| ID | Story | Dep | Size | Plat |
| --- | --- | --- | --- | --- |
| S3.1 | `IChunkDecoder` interface and the `Dictionary<uint, IChunkDecoder>` registry | S1.2 | S | core |
| S3.2 | Zero-fill (`0x00000000`) and ignore (`0x00000002`) decoders | S3.1 | S | core |
| S3.3 | Raw (`0x00000001`) decoder | S3.1 | S | core |
| S3.4 | zlib (`0x80000005`) decoder using `ZLibStream` | S3.1 | M | core |
| S3.5 | Apple ADC (`0x80000004`) decoder, written from scratch per `docs/03` §6 | S3.1 | L | core |
| S3.6 | Decompression-bomb guard: hard-cap output at `SectorCount × 512` in the registry, enforced once for all decoders | S3.4 | M | core |
| S3.7 | Unsupported codecs (bzip2, LZFSE, LZMA) produce a `Result` failure naming the codec, exit 3 — not an exception | S3.1 | S | core |
| S3.8 | Codec conformance suite: each decoder's output hashed against `fixtures/manifest.json` | S3.4, S3.5, S1.5 | M | core |

---

## E4 — Encrypted DMG support

*Goal: `encrcdsa` v2 images open with a passphrase, and a wrong passphrase says so.*

| ID | Story | Dep | Size | Plat |
| --- | --- | --- | --- | --- |
| S4.1 | Parse the `encrcdsa` v2 header per `docs/04` §2, with every variable-length field bounded against its container | S2.1 | M | core |
| S4.2 | PBKDF2-HMAC-SHA1 derivation with the iteration-count cap (reject above 10 000 000) | S4.1 | S | core |
| S4.3 | 3DES-EDE-CBC keyblob unwrap yielding the AES key and HMAC-SHA1 key | S4.2 | M | core |
| S4.4 | Per-block IV derivation: `HMAC-SHA1(hmacKey, BE32(n))[0..16]` | S4.3 | S | core |
| S4.5 | `EncryptedBlockStream` decorator — seekable AES-CBC over 4096-byte blocks, `Length` reporting `DataSize`, with a one-block cache | S4.4 | L | core |
| S4.6 | Passphrase input: `--password-stdin`, `--password-env`, and a no-echo console prompt; `byte[]` zeroed on every exit path | S4.5 | M | core |
| S4.7 | Wrong-passphrase detection at construction time (keyblob padding, then plaintext sanity) → exit 4, never exit 9 | S4.5 | M | core |
| S4.8 | Detect legacy v1 (`cdsaencr`) and machine FIPS policy blocking 3DES; report each specifically | S4.3 | M | core |
| S4.9 | Round-trip test: decrypt an AES-128 and an AES-256 fixture, parse the UDIF inside, verify against the manifest hash | S4.7, S1.5 | M | core |

---

## E5 — Block stream and cache

*Goal: the seam. A `Stream` over the decoded image that is fast enough to be usable.*

| ID | Story | Dep | Size | Plat |
| --- | --- | --- | --- | --- |
| S5.1 | `DmgBlockStream : Stream` — seekable, read-only, `Length = SectorCount × 512` | S2.7, S3.1 | L | core |
| S5.2 | LRU chunk cache with a configurable byte budget, default 64 MiB | S5.1 | M | core |
| S5.3 | Reads spanning multiple chunks, partial chunk reads, and the boundary cases: first sector, last sector, exact chunk edges | S5.1 | M | core |
| S5.4 | Sequential-access detection and single-threaded prefetch of chunk *n*+1 | S5.2 | M | core |
| S5.5 | Throughput benchmark with a regression threshold, run in CI | S5.4 | M | ci |

---

## E6 — Partitions and the filesystem gate

*Goal: know what is inside, and refuse clearly what cannot be mounted.*

| ID | Story | Dep | Size | Plat |
| --- | --- | --- | --- | --- |
| S6.1 | Parse the protective MBR and GPT header and entries | S5.1 | M | core |
| S6.2 | Parse the Driver Descriptor Map and Apple Partition Map | S5.1 | M | core |
| S6.3 | Handle whole-disk images with no partition table | S6.1 | S | core |
| S6.4 | exFAT boot-sector probe, extracting the volume label and serial | S6.3 | M | core |
| S6.5 | FAT32 and NTFS probes | S6.4 | S | core |
| S6.6 | HFS+ and APFS probes that identify the filesystem and mark it unmountable, driving exit 5 with a message naming it | S6.4 | M | core |
| S6.7 | Volume selection: `--partition <n>`, and the default rule "the single mountable volume, or an error listing the candidates" | S6.5, S6.6 | M | core |

---

## E7 — VHD writer

*Goal: turn the decoded stream into a file Windows will mount.*

| ID | Story | Dep | Size | Plat |
| --- | --- | --- | --- | --- |
| S7.1 | Fixed VHD footer: struct, geometry calculation, and the one's-complement checksum | S1.2 | M | core |
| S7.2 | `VhdWriter` streaming `DmgBlockStream` to a `.vhd`, with progress callbacks | S7.1, S5.1 | M | core |
| S7.3 | Scratch directory management: mount IDs, path construction that never trusts the image, cleanup, `--keep-scratch` | S7.2 | M | core |
| S7.4 | Free-space precheck before writing; exit 8 with the required and available figures | S7.3 | S | core |
| S7.5 | Round-trip conformance: the written VHD's sectors are byte-identical to the source stream | S7.2 | M | core |
| S7.6 | Dynamic (sparse) VHD writer skipping zero-fill and ignore chunks, behind `--dynamic` | S7.5 | L | core |

---

## E8 — Windows mount

*Goal: a drive letter, and the ability to get rid of it again.*

| ID | Story | Dep | Size | Plat |
| --- | --- | --- | --- | --- |
| S8.1 | `IVirtualDiskService` port plus `FakeVirtualDiskService`, so mount logic is testable without Windows | S1.2 | M | core |
| S8.2 | `WindowsVirtualDiskService`: `LibraryImport` bindings for `OpenVirtualDisk`, `AttachVirtualDisk`, `DetachVirtualDisk`, `GetVirtualDiskPhysicalPath` | S8.1 | L | win |
| S8.3 | Read-only versus read-write attach flags; read-only is the default | S8.2 | S | win |
| S8.4 | Drive-letter discovery: physical path → `IOCTL_STORAGE_GET_DEVICE_NUMBER` → volume enumeration → mount point | S8.2 | L | win |
| S8.5 | `--letter X:` via `ATTACH_VIRTUAL_DISK_FLAG_NO_DRIVE_LETTER` and `SetVolumeMountPoint`, with a clear error if the letter is taken | S8.4 | M | win |
| S8.6 | Elevation detection before any expensive work; exit 7 with the exact remedy. No self-elevation | S8.2 | M | win |
| S8.7 | `MountRegistry` — JSON at `%LOCALAPPDATA%\dmg\mounts.json`, using `System.Text.Json` source generation for AOT | S8.1 | M | win |
| S8.8 | Registry reconciliation: prune entries whose disk is no longer attached, so a reboot does not leave ghosts | S8.7, S8.4 | M | win |
| S8.9 | Detach path: unmount by letter or ID, `--all`, VHD removal, registry update, and correct behaviour when a handle is still open | S8.8 | M | win |

---

## E9 — CLI surface

*Goal: the contract users and scripts actually depend on.*

| ID | Story | Dep | Size | Plat |
| --- | --- | --- | --- | --- |
| S9.1 | `ICliCommand` and the dispatcher; `Program.Main` becomes routing and a top-level exception handler only | S1.3 | M | core |
| S9.2 | Hand-rolled argument parser: `--flag`, `--opt value`, `--opt=value`, `-v`, `--`, and unknown-option errors that suggest the nearest match | S9.1 | L | core |
| S9.3 | `dmg info`, human and `--json`, working on images that cannot be mounted | S9.2, S6.7 | M | core |
| S9.4 | `dmg mount`, orchestrating the full sequence in CLI design §3.4 with the three fail-fast checks before the write | S9.3, S7.4, S8.6 | L | win |
| S9.5 | `dmg unmount` accepting a letter, an ID, or `--all` | S9.2, S8.9 | M | win |
| S9.6 | `dmg list` with registry reconciliation | S9.2, S8.8 | S | win |
| S9.7 | `dmg extract` writing raw or VHD, with `--format` | S9.2, S7.2 | M | core |
| S9.8 | `dmg verify`: full decode pass validating every checksum, reporting the first failure's chunk index | S9.2, S3.8 | M | core |
| S9.9 | Help text, per-verb help, and `version` reporting the build | S9.2 | M | core |
| S9.10 | Exit-code contract enforced by a test per code, in both output modes | S9.4, S9.5 | M | core |
| S9.11 | Progress rendering that detects a non-TTY or redirected stdout and degrades to line-per-update | S9.4 | S | core |

---

## E10 — Packaging, docs and QA

*Goal: something Jim can download and run, and evidence that it works.*

| ID | Story | Dep | Size | Plat |
| --- | --- | --- | --- | --- |
| S10.1 | NativeAOT publish for win-x64 and win-arm64; single self-contained `dmg.exe`; assert the binary has no runtime dependency | S9.10 | M | ci |
| S10.2 | Release workflow: tag → build both architectures → attach to a GitHub Release with checksums | S10.1 | M | ci |
| S10.3 | README rewrite against the shipped behaviour: real usage, the exit-code table, the elevation requirement, the stated limits | S9.10 | M | — |
| S10.4 | E2E suite on windows-latest: mount a real fixture, read a known file, verify its hash, unmount, assert the VHD is gone | S9.5, S1.6 | L | win |
| S10.5 | Hostile-corpus suite promoted to a CI gate covering E2, E3 and E4 | S2.9, S3.6, S4.8 | M | core |
| S10.6 | `docs/manual-test-plan.md` for the Windows 11 VM: Explorer behaviour, eject, UAC messaging, unmount with an open handle | S10.4 | S | — |
| S10.7 | Keep the dependency ledger current and wire it to the CI guard so a new package fails the build with a pointer to ADR-002 | S1.6 | S | ci |

---

## Parallelisation

Nine tracks can start the moment E1 lands, which is the main reason the epics are
cut this way:

```
        ┌── E2 container ──┬── E3 codecs ──┐
        │                  └── E4 crypto ──┼── E5 stream ── E6 partitions ─┐
E1 ─────┤                                  │                               ├── E9 CLI ── E10 ship
        ├── E7 VHD footer (S7.1 only) ─────┘                               │
        └── E8 port + fake (S8.1) ── E8 Windows mount ─────────────────────┘
```

- **S1.4 (fixtures) is on the critical path for almost everything** and should be
  done first, or in parallel with S1.1, because eight later stories assert against
  its output.
- **E3 and E4 are fully independent of each other** and both depend only on E2's
  reading helpers. Two agents, no contention.
- **S7.1 (VHD footer) and S8.1 (the port and fake) have no dependency on the DMG
  side at all.** Both can start at S1.2 and run alongside all of E2.
- **E8's Windows stories cannot be verified on the Mac.** They are written against
  the fake, and the real service is exercised only in E10's E2E suite. Expect those
  stories to land "green on the fake, unverified on hardware" and to be confirmed
  as a batch by S10.4.

## Definition of Done

A story is Done when all of the following hold:

1. Code merged to `main` via a branch named `story/S<id>-<slug>`
2. Tests exist for the story's behaviour, and the epic's bar in
   [testing strategy §6](docs/05-testing-strategy.md#6-coverage-expectations) is met
3. CI is green on macOS and Windows, including the dependency guard
4. No `PackageReference` added under `src/`
5. Any format finding that contradicts `docs/03` or `docs/04` is corrected in those
   documents in the same PR
