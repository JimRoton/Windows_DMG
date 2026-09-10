# Kanban Board

**The live board is [Windows_DMG — Delivery](https://github.com/users/JimRoton/projects/4).**
That is the source of truth for status. This file is a static index so the same
information is readable from a clone.

| | |
| --- | --- |
| Board | <https://github.com/users/JimRoton/projects/4> |
| Columns | Todo · In Progress · Done |
| Issues | 10 epics (#1–#10) · 77 stories (#11–#87) |
| Current state | **87 in Todo · 0 In Progress · 0 Done** |

Each story is a **sub-issue** of its epic, so every epic shows a live completion
rollup on GitHub without anything needing to be kept in sync by hand.

## How work moves

1. An agent picks a story whose dependencies are all closed.
2. Status → **In Progress**, `status:todo` label swapped for `status:in-progress`.
3. Branch `story/S<id>-<slug>` off `main`; implement; tests alongside the code.
4. PR referencing the issue; CI green on macOS and Windows including the
   dependency guard; merge to `main`.
5. Status → **Done**, issue closed, labels updated.
6. When every sub-issue of an epic is closed, run that epic's test bar from
   [testing strategy section 6](docs/05-testing-strategy.md#6-coverage-expectations)
   before closing the epic.

## Index


### E1 — Foundation and CI  ([#1](https://github.com/JimRoton/Windows_DMG/issues/1))

| Story | Issue | Dep | Size | Plat |
| --- | --- | --- | --- | --- |
| S1.1 Solution and project scaffolding | [#11](https://github.com/JimRoton/Windows_DMG/issues/11) | — | M | core |
| S1.2 Result<T> and the error taxonomy | [#12](https://github.com/JimRoton/Windows_DMG/issues/12) | S1.1 | S | core |
| S1.3 Verbosity and output plumbing | [#13](https://github.com/JimRoton/Windows_DMG/issues/13) | S1.1 | S | core |
| S1.4 Fixture generator script | [#14](https://github.com/JimRoton/Windows_DMG/issues/14) | — | L | core |
| S1.5 Fixture manifest with ground-truth hashes | [#15](https://github.com/JimRoton/Windows_DMG/issues/15) | S1.4 | M | core |
| S1.6 CI workflows | [#16](https://github.com/JimRoton/Windows_DMG/issues/16) | S1.1 | M | ci |

### E2 — UDIF container reader  ([#2](https://github.com/JimRoton/Windows_DMG/issues/2))

| Story | Issue | Dep | Size | Plat |
| --- | --- | --- | --- | --- |
| S2.1 Big-endian reading helpers | [#17](https://github.com/JimRoton/Windows_DMG/issues/17) | S1.2 | S | core |
| S2.2 koly trailer parse and validation | [#18](https://github.com/JimRoton/Windows_DMG/issues/18) | S2.1 | M | core |
| S2.3 Minimal XML property-list reader | [#19](https://github.com/JimRoton/Windows_DMG/issues/19) | S2.1 | M | core |
| S2.4 blkx extraction and base64 decode | [#20](https://github.com/JimRoton/Windows_DMG/issues/20) | S2.3 | S | core |
| S2.5 mish block header parse | [#21](https://github.com/JimRoton/Windows_DMG/issues/21) | S2.1 | M | core |
| S2.6 Chunk descriptor table parse | [#22](https://github.com/JimRoton/Windows_DMG/issues/22) | S2.5 | M | core |
| S2.7 ExtentIndex with binary search | [#23](https://github.com/JimRoton/Windows_DMG/issues/23) | S2.6 | M | core |
| S2.8 Image format probe chain | [#24](https://github.com/JimRoton/Windows_DMG/issues/24) | S2.2 | M | core |
| S2.9 Hostile-input hardening pass over E2 | [#25](https://github.com/JimRoton/Windows_DMG/issues/25) | S2.7,S2.8 | L | core |

### E3 — Chunk codecs  ([#3](https://github.com/JimRoton/Windows_DMG/issues/3))

| Story | Issue | Dep | Size | Plat |
| --- | --- | --- | --- | --- |
| S3.1 IChunkDecoder and the registry | [#26](https://github.com/JimRoton/Windows_DMG/issues/26) | S1.2 | S | core |
| S3.2 Zero-fill and ignore decoders | [#27](https://github.com/JimRoton/Windows_DMG/issues/27) | S3.1 | S | core |
| S3.3 Raw decoder | [#28](https://github.com/JimRoton/Windows_DMG/issues/28) | S3.1 | S | core |
| S3.4 zlib decoder | [#29](https://github.com/JimRoton/Windows_DMG/issues/29) | S3.1 | M | core |
| S3.5 Apple ADC decoder | [#30](https://github.com/JimRoton/Windows_DMG/issues/30) | S3.1 | L | core |
| S3.6 Decompression-bomb guard | [#31](https://github.com/JimRoton/Windows_DMG/issues/31) | S3.4 | M | core |
| S3.7 Unsupported codecs report cleanly | [#32](https://github.com/JimRoton/Windows_DMG/issues/32) | S3.1 | S | core |
| S3.8 Codec conformance suite | [#33](https://github.com/JimRoton/Windows_DMG/issues/33) | S3.4,S3.5,S1.5 | M | core |

### E4 — Encrypted DMG support  ([#4](https://github.com/JimRoton/Windows_DMG/issues/4))

| Story | Issue | Dep | Size | Plat |
| --- | --- | --- | --- | --- |
| S4.1 encrcdsa v2 header parse | [#34](https://github.com/JimRoton/Windows_DMG/issues/34) | S2.1 | M | core |
| S4.2 PBKDF2 key derivation | [#35](https://github.com/JimRoton/Windows_DMG/issues/35) | S4.1 | S | core |
| S4.3 3DES keyblob unwrap | [#36](https://github.com/JimRoton/Windows_DMG/issues/36) | S4.2 | M | core |
| S4.4 Per-block IV derivation | [#37](https://github.com/JimRoton/Windows_DMG/issues/37) | S4.3 | S | core |
| S4.5 EncryptedBlockStream decorator | [#38](https://github.com/JimRoton/Windows_DMG/issues/38) | S4.4 | L | core |
| S4.6 Passphrase input | [#39](https://github.com/JimRoton/Windows_DMG/issues/39) | S4.5 | M | core |
| S4.7 Wrong-passphrase detection | [#40](https://github.com/JimRoton/Windows_DMG/issues/40) | S4.5 | M | core |
| S4.8 Legacy and FIPS failure modes | [#41](https://github.com/JimRoton/Windows_DMG/issues/41) | S4.3 | M | core |
| S4.9 Encrypted round-trip tests | [#42](https://github.com/JimRoton/Windows_DMG/issues/42) | S4.7,S1.5 | M | core |

### E5 — Block stream and cache  ([#5](https://github.com/JimRoton/Windows_DMG/issues/5))

| Story | Issue | Dep | Size | Plat |
| --- | --- | --- | --- | --- |
| S5.1 DmgBlockStream | [#43](https://github.com/JimRoton/Windows_DMG/issues/43) | S2.7,S3.1 | L | core |
| S5.2 LRU chunk cache | [#44](https://github.com/JimRoton/Windows_DMG/issues/44) | S5.1 | M | core |
| S5.3 Chunk-boundary read correctness | [#45](https://github.com/JimRoton/Windows_DMG/issues/45) | S5.1 | M | core |
| S5.4 Sequential prefetch | [#46](https://github.com/JimRoton/Windows_DMG/issues/46) | S5.2 | M | core |
| S5.5 Throughput benchmark and regression gate | [#47](https://github.com/JimRoton/Windows_DMG/issues/47) | S5.4 | M | ci |

### E6 — Partitions and the filesystem gate  ([#6](https://github.com/JimRoton/Windows_DMG/issues/6))

| Story | Issue | Dep | Size | Plat |
| --- | --- | --- | --- | --- |
| S6.1 GPT and protective MBR | [#48](https://github.com/JimRoton/Windows_DMG/issues/48) | S5.1 | M | core |
| S6.2 Apple Partition Map and DDM | [#49](https://github.com/JimRoton/Windows_DMG/issues/49) | S5.1 | M | core |
| S6.3 Whole-disk images | [#50](https://github.com/JimRoton/Windows_DMG/issues/50) | S6.1 | S | core |
| S6.4 exFAT probe | [#51](https://github.com/JimRoton/Windows_DMG/issues/51) | S6.3 | M | core |
| S6.5 FAT32 and NTFS probes | [#52](https://github.com/JimRoton/Windows_DMG/issues/52) | S6.4 | S | core |
| S6.6 HFS+ and APFS probes | [#53](https://github.com/JimRoton/Windows_DMG/issues/53) | S6.4 | M | core |
| S6.7 Volume selection | [#54](https://github.com/JimRoton/Windows_DMG/issues/54) | S6.5,S6.6 | M | core |

### E7 — VHD writer  ([#7](https://github.com/JimRoton/Windows_DMG/issues/7))

| Story | Issue | Dep | Size | Plat |
| --- | --- | --- | --- | --- |
| S7.1 Fixed VHD footer | [#55](https://github.com/JimRoton/Windows_DMG/issues/55) | S1.2 | M | core |
| S7.2 VhdWriter | [#56](https://github.com/JimRoton/Windows_DMG/issues/56) | S7.1,S5.1 | M | core |
| S7.3 Scratch management | [#57](https://github.com/JimRoton/Windows_DMG/issues/57) | S7.2 | M | core |
| S7.4 Free-space precheck | [#58](https://github.com/JimRoton/Windows_DMG/issues/58) | S7.3 | S | core |
| S7.5 VHD round-trip conformance | [#59](https://github.com/JimRoton/Windows_DMG/issues/59) | S7.2 | M | core |
| S7.6 Dynamic (sparse) VHD writer | [#60](https://github.com/JimRoton/Windows_DMG/issues/60) | S7.5 | L | core |

### E8 — Windows mount  ([#8](https://github.com/JimRoton/Windows_DMG/issues/8))

| Story | Issue | Dep | Size | Plat |
| --- | --- | --- | --- | --- |
| S8.1 IVirtualDiskService port and fake | [#61](https://github.com/JimRoton/Windows_DMG/issues/61) | S1.2 | M | core |
| S8.2 virtdisk.dll bindings | [#62](https://github.com/JimRoton/Windows_DMG/issues/62) | S8.1 | L | win |
| S8.3 Read-only and read-write attach | [#63](https://github.com/JimRoton/Windows_DMG/issues/63) | S8.2 | S | win |
| S8.4 Drive letter discovery | [#64](https://github.com/JimRoton/Windows_DMG/issues/64) | S8.2 | L | win |
| S8.5 Explicit drive letter | [#65](https://github.com/JimRoton/Windows_DMG/issues/65) | S8.4 | M | win |
| S8.6 Elevation detection | [#66](https://github.com/JimRoton/Windows_DMG/issues/66) | S8.2 | M | win |
| S8.7 Mount registry | [#67](https://github.com/JimRoton/Windows_DMG/issues/67) | S8.1 | M | win |
| S8.8 Registry reconciliation | [#68](https://github.com/JimRoton/Windows_DMG/issues/68) | S8.7,S8.4 | M | win |
| S8.9 Detach and cleanup | [#69](https://github.com/JimRoton/Windows_DMG/issues/69) | S8.8 | M | win |

### E9 — CLI surface  ([#9](https://github.com/JimRoton/Windows_DMG/issues/9))

| Story | Issue | Dep | Size | Plat |
| --- | --- | --- | --- | --- |
| S9.1 ICliCommand and dispatcher | [#70](https://github.com/JimRoton/Windows_DMG/issues/70) | S1.3 | M | core |
| S9.2 Argument parser | [#71](https://github.com/JimRoton/Windows_DMG/issues/71) | S9.1 | L | core |
| S9.3 dmg info | [#72](https://github.com/JimRoton/Windows_DMG/issues/72) | S9.2,S6.7 | M | core |
| S9.4 dmg mount | [#73](https://github.com/JimRoton/Windows_DMG/issues/73) | S9.3,S7.4,S8.6 | L | win |
| S9.5 dmg unmount | [#74](https://github.com/JimRoton/Windows_DMG/issues/74) | S9.2,S8.9 | M | win |
| S9.6 dmg list | [#75](https://github.com/JimRoton/Windows_DMG/issues/75) | S9.2,S8.8 | S | win |
| S9.7 dmg extract | [#76](https://github.com/JimRoton/Windows_DMG/issues/76) | S9.2,S7.2 | M | core |
| S9.8 dmg verify | [#77](https://github.com/JimRoton/Windows_DMG/issues/77) | S9.2,S3.8 | M | core |
| S9.9 Help and version | [#78](https://github.com/JimRoton/Windows_DMG/issues/78) | S9.2 | M | core |
| S9.10 Exit-code contract tests | [#79](https://github.com/JimRoton/Windows_DMG/issues/79) | S9.4,S9.5 | M | core |
| S9.11 Progress rendering | [#80](https://github.com/JimRoton/Windows_DMG/issues/80) | S9.4 | S | core |

### E10 — Packaging, docs and QA  ([#10](https://github.com/JimRoton/Windows_DMG/issues/10))

| Story | Issue | Dep | Size | Plat |
| --- | --- | --- | --- | --- |
| S10.1 NativeAOT publish | [#81](https://github.com/JimRoton/Windows_DMG/issues/81) | S9.10 | M | ci |
| S10.2 Release workflow | [#82](https://github.com/JimRoton/Windows_DMG/issues/82) | S10.1 | M | ci |
| S10.3 README rewrite against shipped behaviour | [#83](https://github.com/JimRoton/Windows_DMG/issues/83) | S9.10 | M | - |
| S10.4 End-to-end suite on Windows CI | [#84](https://github.com/JimRoton/Windows_DMG/issues/84) | S9.5,S1.6 | L | win |
| S10.5 Hostile corpus promoted to a CI gate | [#85](https://github.com/JimRoton/Windows_DMG/issues/85) | S2.9,S3.6,S4.8 | M | core |
| S10.6 Manual test plan for the Windows VM | [#86](https://github.com/JimRoton/Windows_DMG/issues/86) | S10.4 | S | - |
| S10.7 Dependency ledger kept current | [#87](https://github.com/JimRoton/Windows_DMG/issues/87) | S1.6 | S | ci |

## Ready to start now

These have no unmet dependencies and can run in parallel from a cold start:

| Story | Issue | Why it unblocks others |
| --- | --- | --- |
| S1.1 | [#11](https://github.com/JimRoton/Windows_DMG/issues/11) | Everything depends on the solution existing |
| S1.4 | [#14](https://github.com/JimRoton/Windows_DMG/issues/14) | Eight later stories assert against its fixtures — start it first |

Once S1.1 and S1.2 land, five tracks open simultaneously: **E2** (container),
**E3** (codecs), **E4** (crypto), **S7.1** (VHD footer) and **S8.1** (the
virtual-disk port and fake). E3 and E4 have no dependency on each other, and
S7.1/S8.1 have no dependency on the DMG side at all — see the graph at the end
of [BACKLOG.md](BACKLOG.md#parallelisation).
