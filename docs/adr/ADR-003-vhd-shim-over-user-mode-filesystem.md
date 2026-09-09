# ADR-003 — VHD shim, not a user-mode filesystem

**Status:** Proposed · 9 September 2026

## Context

Three routes give a Windows drive letter for a DMG's contents
([feasibility study §2](../01-feasibility-and-architecture.md#2-question-1--a-virtual-drive)):

- **A** — WinFsp user-mode filesystem: we implement the filesystem
- **B** — VHD shim: decode to a `.vhd`, let Windows mount it
- **C** — kernel SCSI miniport: we write a signed kernel driver

The stated requirement is exFAT images.

## Decision

**Route B.** Decode the DMG's sector stream, append a fixed VHD footer, attach with
`virtdisk.dll`, and let Windows' own exFAT driver do the filesystem work.

## Why

For an exFAT payload, Windows already has the driver. Routes A and C both amount to
building infrastructure to deliver sectors to a filesystem — and then, in A, writing
the filesystem too. Route B delivers the same sectors through a mechanism Windows
already ships.

Concretely, Route B removes from the project:

- an exFAT implementation (read *and* write, correctly, or the volume corrupts)
- a filesystem driver dependency with a GPLv3 licence to resolve (WinFsp)
- kernel-mode code, an EV certificate and attestation signing (Route C)
- the entire Mac-to-Windows semantic mapping layer — names, forks, timestamps,
  hard links — which is where most of the bugs in a tool like this live

And it gains native read *and* write at native speed, because it is Microsoft's
exFAT driver doing the work.

## What it costs

Both costs are real, and both are surfaced in the CLI rather than hidden.

1. **Scratch space equal to the uncompressed image.** The image is materialised
   before it is mounted. `dmg mount` checks free space before writing a byte and
   exits 8 if there isn't room. A dynamic (sparse) VHD is a planned mitigation for
   the common case of a mostly-empty volume.
2. **Only filesystems Windows understands.** HFS+ and APFS cannot be mounted this
   way, ever. They are detected, named and refused with exit code 5 — which is the
   honest outcome, and better than a route that half-works.

Note that (2) is not really a cost of Route B so much as a consequence of the
requirement. Route A is the only route that could lift it, and it lifts it by
making us write an HFS+ implementation.

## Keeping the door open

The layering does not foreclose Route A. Everything below the `Stream` seam —
container, crypto, codecs, extent index, cache — is route-agnostic. If HFS+ support
is ever wanted, a WinFsp head consumes the same `DmgBlockStream` and nothing in
`Dmg.Core` changes. That is the main reason the seam is drawn where it is.

## Rejected

- **Route A now:** solves a problem we do not have (HFS+) at the cost of a
  filesystem implementation and a licence negotiation.
- **Route C:** buys only raw `\\.\PhysicalDriveN` access, which nothing in the
  requirements needs, for the highest cost of the three.
- **Shelling out to PowerShell `Mount-DiskImage`:** works, but adds a process
  launch, a parsing dependency on PowerShell's output format, and no way to get a
  reliable handle back. `virtdisk.dll` is the API PowerShell itself calls.
