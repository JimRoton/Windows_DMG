# Feasibility and Architecture

> 9 September 2026 · the study that preceded [the CLI design](02-cli-design.md).
> A formatted version of this document is at [design-study.html](design-study.html).

Two questions were asked:

1. Can a virtual drive be created on Windows that loads and reads a DMG?
2. If not, can an application be built on Windows that reads a DMG?

**Both are yes.** The rest of this document is why, and which route to take.

---

## 1. One correction to the premise

The hard part of a DMG is not the filesystem. It is the **container**. A `.dmg` is
a wrapper — Apple's Universal Disk Image Format — holding a compressed, chunked
copy of a disk's sectors. Before any filesystem question arises you have to
reconstruct that sector stream, and that is where the real work lives.

"Apple proprietary filesystem" is also not the wall it sounds like. HFS+ has been
publicly specified by Apple since 2004 (Technical Note TN1150) and implemented
openly many times. APFS has a published Apple reference. Both are *readable*. What
is genuinely hard is **writing** to them, and that difficulty has nothing to do
with Windows.

One scoping point that matters: the exFAT assumption is a fair simplification but
is not what you meet in the wild. Images downloaded from the internet are
overwhelmingly **HFS+**, because that is what application installers ship as. A
tool that handles only exFAT will open almost nothing found casually online — which
is fine when exFAT is the stated target, as it is here, but it should be a
deliberate choice rather than a surprise.

| | Difficulty |
| --- | --- |
| Reading the container | Solved, well understood, a few thousand lines |
| Reading HFS+ / APFS | Solved, but detail-heavy |
| Writing a compressed container | Mechanically awkward, bounded |
| Writing HFS+ / APFS | The only genuinely dangerous part |

---

## 2. Question 1 — a virtual drive

Three routes work. They differ in one respect that governs everything else:
**who implements the filesystem.**

### Route A — WinFsp user-mode filesystem

FUSE for Windows. A signed kernel filesystem driver ships with the project; you
write the filesystem itself as an ordinary user-mode service and get a real drive
letter. Dokany is the equivalent alternative.

- You write no kernel code and sign nothing. A parser bug crashes a service, not
  the machine.
- The only route that can support **HFS+ and APFS**, since Windows has no driver
  for either.
- Nothing is extracted to disk; reads are served on demand from the compressed image.
- **License check required.** WinFsp is GPLv3 with a free-software exception and a
  separate commercial license. Dokany (LGPL/MIT) is the fallback.

### Route B — the VHD shim ← **chosen**

A fixed-format VHD is raw sector data plus a 512-byte footer. Decode the UDIF into
a raw sector stream, append the footer, attach it with `virtdisk.dll`. Windows'
own storage stack and its own exFAT driver do everything after that.

- **Zero driver work of any kind.** Native read and write, at native speed, with no
  filesystem code of our own.
- Bounded to what Windows already mounts: exFAT, FAT32, NTFS, ISO 9660, UDF.
- Costs the full uncompressed size in scratch space and needs administrator rights.

Under the exFAT requirement this is the correct answer, and it is a fraction of the
work of the alternatives.

### Route C — virtual SCSI / StorPort miniport

A kernel storage driver presenting the decoded image as a physical disk, the way
Arsenal Image Mounter does.

- Requires an EV code-signing certificate and attestation or WHQL signing. You own
  kernel-mode bugcheck risk.
- Still does not solve HFS+ or APFS — there is no Windows driver to hand them to.
- Worth it only for `\\.\PhysicalDriveN` semantics in forensics tooling.

### Route D — Windows Projected File System

ProjFS projects a virtual namespace into a *directory*, not a volume. It ships in
Windows and needs no signing, but its hydration model is built for source trees,
write-back is awkward, and it cannot present as removable media. WinFsp does this
job better.

### Decision

**Route B.** The requirement is exFAT, and for exFAT the VHD shim gives full native
read/write for a small fraction of the effort, with no driver, no signing and no
filesystem implementation. Route A remains the upgrade path if HFS+ support is ever
wanted; the layering in the CLI design keeps that door open by isolating everything
above the sector stream behind a `Stream`.

See [ADR-003](adr/ADR-003-vhd-shim-over-user-mode-filesystem.md).

---

## 3. Question 2 — an application

Settled by existence proof. The UDIF container is read today by 7-Zip, `dmg2img`,
the `dmgwiz` Rust crate, QEMU's `block/dmg.c` and `darling-dmg`. Several of those
also walk HFS+. None of it is speculative.

In this design the application and the drive are the same engine with different
heads: `dmg info`, `dmg extract` and `dmg verify` are the application, and
`dmg mount` is the drive.

---

## 4. Layering

```
L0  Byte source            memory-mapped file or stream
L1  Container parser       koly → plist → blkx → mish → chunk descriptors
L2  Extent index + cache   flat sorted array, binary search, LRU chunk cache
L3  Codecs                 zero · raw · zlib · ADC  (bzip2/LZFSE/LZMA deferred)
────────────────────────── System.IO.Stream ─────────────────────────────────
L4  Partition mapper       DDM · APM · GPT+protective MBR · whole-disk
L5  Filesystem probe       exFAT · FAT32 · NTFS  → mount;  HFS+ · APFS → refuse
L6  Heads                  mount · unmount · list · info · verify · extract
```

L0–L3 turn a `.dmg` into something that answers `Read(offset, length)` like a plain
disk. Everything above the seam believes it is talking to ordinary hardware.

That seam is what makes the hard half testable in isolation, on a Mac, with no
driver installed and no Windows involved — and it is what would let Route A be
added later without touching any of L0–L3.

---

## 5. Performance constraints that shape the code

- **Cache sizing is the most consequential number in the system.** UDZO chunks are
  typically 1 MiB, so a 4 KiB random read decompresses 256× the data it returns.
  Default to 64 MiB of decompressed chunks.
- **Prefetch on sequential detection.** When reads advance monotonically,
  decompress chunk *n*+1 on a background thread. This is most of the difference in
  the VHD write, which is one long sequential pass.
- **Memory-map the data fork.** The OS page cache then holds hot compressed regions
  for free.
- **Fail fast before the expensive step.** Filesystem, scratch space and elevation
  are all checked before a single byte of VHD is written.

---

## 6. Security posture

Every byte is attacker-supplied. Bound every declared length against the real file
size; cap decompression output at the descriptor's declared sector count; use
checked arithmetic on all sector maths; bound any B-tree traversal. Full list in
[the CLI design, §8](02-cli-design.md#8-security-posture).

---

## 7. Legal

Nothing here is circumvention. UDIF is undocumented rather than protected; HFS+ and
APFS both have published Apple references; LZFSE is Apple's own Apache-2.0 release;
the 7-Zip LZMA SDK is public domain. The only license question in the alternatives
was WinFsp's GPLv3, and Route B avoids it entirely.
