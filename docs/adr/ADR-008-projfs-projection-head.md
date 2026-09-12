# ADR-008 — A ProjFS projection head for images too large to materialise

**Status:** Accepted · 12 September 2026
**Amends:** [ADR-003](ADR-003-vhd-shim-over-user-mode-filesystem.md) — which stands as
the default, and is not withdrawn.

## Context

ADR-003 chose the VHD shim and named its first cost honestly: *"scratch space equal
to the uncompressed image"*, with a dynamic VHD as "a planned mitigation for the
common case of a mostly-empty volume". That mitigation shipped in S9.12.

A real image has now shown where the design runs out.

- `Private.dmg`: **931.3 GiB**, `encrcdsa` AES-256 wrapping a **raw sector image**
  with no UDIF container. GPT, two partitions; the payload is a 931.1 GiB exFAT
  volume.
- Mounting it means ~931 GiB read, ~931 GiB decrypted, and up to ~931 GiB written —
  roughly **1.9 TB of I/O**, with source and scratch contending for the same drive.
  At a realistic sustained 200 MB/s that is over two hours. The user aborted it.
- The same image mounts on macOS in **seconds**, because `hdiutil attach` copies
  nothing and decrypts blocks on demand.

Two measurements ruled out the obvious suspects before this decision was taken:

| Path | Throughput |
| --- | --- |
| Flat encrypted (AES only) | 532–621 MiB/s |
| Encrypted + UDZO (AES + zlib) | 558–801 MiB/s |
| Sequential write | 1.52 GB/s |

The decode pipeline is not the bottleneck. **The copy is the bottleneck**, and no
optimisation inside "copy the whole volume first" removes it. Two candidate
optimisations were investigated and both rejected on the evidence: a crypto
hot-path rewrite (would speed up a path already running at 600+ MiB/s) and an exFAT
allocation-bitmap sparse map (saves only in proportion to free space, and this image
is a capture of a real disk whose free clusters hold non-zero remnants, so
zero-detection finds little).

## Decision

Add a **second head**: a read-only projection of the image's contents through the
**Windows Projected File System** (ProjFS), alongside — not instead of — the VHD
shim.

`dmg mount` keeps its current behaviour and remains the default. The projection is
a separate verb for the case the VHD route cannot serve: an image too large to
materialise, or one where the user wants a few files rather than the whole volume.

## Why ProjFS and not the Route A that ADR-003 rejected

ADR-003 rejected Route A as **WinFsp**, for reasons that were specific to WinFsp:

- *"a filesystem driver dependency with a GPLv3 licence to resolve"* — ProjFS is
  in-box, shipped by Microsoft, no licence to resolve and nothing to install.
- Kernel-mode code, EV certificates, attestation signing — ProjFS is **user-mode**.

ProjFS requires Windows 10 1809 or later, which is already this tool's stated
minimum ([README](../../README.md#requirements)), so it adds no floor.
[ADR-002](ADR-002-zero-third-party-dependencies.md) ranks "a Windows OS API via
P/Invoke" as an approved source, the same standing `virtdisk.dll` already has. The
dependency rule is untouched: no `PackageReference` is added.

ADR-003 also anticipated this explicitly:

> The layering does not foreclose Route A. Everything below the `Stream` seam —
> container, crypto, codecs, extent index, cache — is route-agnostic. […] That is
> the main reason the seam is drawn where it is.

This is that second head, on that seam. Nothing in `Dmg.Core`'s existing stack
changes.

## What it costs, stated plainly

One of ADR-003's objections to Route A survives intact and is the bulk of this work:

> an exFAT implementation (read *and* write, correctly, or the volume corrupts)

Read-only projection halves it and removes the corruption risk — we never write to
the image — but a **read-only exFAT reader still has to be written**: directory
traversal, the `0x85`/`0xC0`/`0xC1` entry triplet, cluster chains, the `NoFatChain`
contiguous case, and file extents. The existing `ExFatProbe` already parses the
geometry, follows the FAT and walks the root directory for the label, so this
extends proven code rather than starting fresh — but it is still the largest single
piece of the change.

ADR-003's second objection also partly survives: the Mac-to-Windows semantic
mapping. Read-only and exFAT-only shrinks it to name legality, case and timestamps,
because exFAT has no forks, no hard links and no POSIX modes. That is a much smaller
surface than the general case ADR-003 was weighing.

## Limits, accepted deliberately

- **A folder, not a drive letter.** ProjFS projects a directory. Tools that require
  `X:\` are not served; `dmg mount` remains for those.
- **Read-only.** Writes are refused. This tool has never written back into a `.dmg`.
- **exFAT only** to begin with. FAT32 is a plausible follow-on; HFS+ and APFS remain
  out of scope for the reasons in ADR-003, and are still refused by name with exit 5.
- **ProjFS may be disabled.** It is an optional Windows feature. Absence must be
  reported as a named, actionable failure — telling the user the feature to enable —
  never as a crash or a generic HRESULT.

## Consequences

- A new port, `IProjectionService`, with a hand-written fake, following
  `IVirtualDiskService` exactly. The ProjFS P/Invoke lives in `Dmg.Windows`; the
  exFAT reader is platform-independent and lives in `Dmg.Core`.
- The exFAT reader is testable on macOS against the existing fixture corpus, whose
  images already carry known files (`HELLO.TXT`, `README.TXT`, `DATA.BIN`). Only the
  projection head itself needs Windows, and it is exercised through the fake
  elsewhere.
- Mount time for the 931 GiB case becomes **independent of image size**: opening the
  projection reads the boot sector, the FAT regions it needs and the root directory.
  Bytes are decrypted when a file is actually read.
- The VHD route keeps its advantage where it applies: Microsoft's own exFAT driver,
  at native speed, with a real drive letter. Neither head makes the other redundant.
