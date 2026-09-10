# Windows_DMG

A native Windows command-line tool that mounts an Apple `.dmg` disk image as a real
drive letter — including encrypted images — with **zero third-party runtime
dependencies**.

```
C:\> dmg mount .\installer.dmg
Mounted  installer.dmg  ->  E:\   (exFAT, 2.4 GiB, read-only)

C:\> dmg unmount E:
Unmounted E:
```

## Status

**Design phase.** No code yet. The architecture, the CLI contract and the full
backlog are complete and awaiting review.

| Document | What it covers |
| --- | --- |
| [docs/01-feasibility-and-architecture.md](docs/01-feasibility-and-architecture.md) | Can this be done at all, and the three possible routes |
| [docs/02-cli-design.md](docs/02-cli-design.md) | **The CLI design** — commands, architecture, patterns, dependency ledger |
| [docs/03-udif-format-reference.md](docs/03-udif-format-reference.md) | The `.dmg` container format, field by field |
| [docs/04-encrypted-dmg-reference.md](docs/04-encrypted-dmg-reference.md) | `encrcdsa` v2 encryption and how to unwrap it |
| [docs/05-testing-strategy.md](docs/05-testing-strategy.md) | Fixtures, CI, and how to test Windows code from a Mac |
| [docs/adr/](docs/adr/) | The six decisions that shape everything else |
| [BACKLOG.md](BACKLOG.md) | 10 epics, 77 stories |
| [KANBAN.md](KANBAN.md) | Board state |

## The short version

A `.dmg` is not a filesystem — it is a **container** holding a compressed, chunked
copy of a disk's sectors. Reconstruct that sector stream and you have an ordinary
disk image. Windows already knows how to mount a disk image: a fixed-format VHD is
just raw sectors plus a 512-byte footer, and `virtdisk.dll` will attach one and
assign it a drive letter.

So the tool decodes the DMG, writes a VHD, and asks Windows to mount it. Windows'
own exFAT driver does the filesystem work. No kernel driver, no code signing, no
filesystem implementation of our own.

The trade is that the image must be materialised to scratch space first, and the
payload filesystem has to be one Windows already understands — which is exactly
the exFAT case this tool targets.

## Scope

**In scope for v1**

- UDIF containers: uncompressed (UDRW/UDTO), zlib (UDZO), Apple ADC
- `encrcdsa` v2 encrypted images (AES-128/256), password from stdin, env or prompt
- GPT, MBR, Apple Partition Map, and whole-disk images
- exFAT, FAT32 and NTFS payloads — mounted read-only by default
- `mount`, `unmount`, `list`, `info`, `verify`, `extract`

**Explicitly out of scope for v1**

- HFS+ and APFS payloads — detected and reported clearly, but not mounted.
  Windows has no driver for either, and the VHD route cannot supply one.
- bzip2, LZFSE and LZMA chunks — detected and named in the error, not decoded.
- Writing changes back into the `.dmg`.
- NDIF, sparseimage and sparsebundle containers.

Everything out of scope fails with a specific message and a specific exit code.
Nothing fails with a stack trace.

## Requirements

- Windows 10 1809 or later, x64 or arm64
- An elevated shell for `mount` and `unmount` (`AttachVirtualDisk` needs the
  Manage Volume privilege)
- Scratch space equal to the uncompressed size of the image

No runtime install. `dmg.exe` is a single NativeAOT binary.

## License

MIT — see [LICENSE](LICENSE).
