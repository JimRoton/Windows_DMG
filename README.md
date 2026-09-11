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

## Install

**Download.** Grab `dmg-win-x64.exe` or `dmg-win-arm64.exe` from the
[Releases](../../releases) page, rename it to `dmg.exe` if you like, and run it. It
is a single self-contained NativeAOT binary — no .NET runtime install, no other
files needed.

**Build from source.** Two prerequisites, not one:

- the .NET 10 SDK, and
- **the MSVC toolchain** — Visual Studio 2022 (or the standalone Build Tools)
  with the **Desktop development with C++** workload, which supplies `link.exe`
  and the Windows SDK.

The second one is easy to miss. `PublishAot` is set in the project file, and
NativeAOT compiles through the platform linker, so without it the publish fails
with `error : Platform linker not found. Ensure you have all the required
prerequisites` — an error about C++ tooling in the middle of a C# build. GitHub's
`windows-latest` runners have the workload preinstalled, which is why CI never
sees this.

```
dotnet publish src/Dmg.Cli/Dmg.Cli.csproj -c Release -r win-x64 --self-contained true -o out
```

Swap `win-x64` for `win-arm64` on Arm64 Windows. The binary lands at
`out/dmg.exe`.

**Without the C++ workload**, add `-p:PublishAot=false` to get an ordinary
self-contained build instead:

```
dotnet publish src/Dmg.Cli/Dmg.Cli.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=false -p:PublishSingleFile=true -o out
```

That still needs no .NET runtime on the target machine, and it cross-builds from
macOS or Linux, which the AOT path cannot. It is bigger (~36 MB against ~5 MB)
and starts marginally slower, and it is **not** what ships — use it for
functional testing, not for validating the release binary.

## Usage

Run `dmg help` for the command list, or `dmg help <command>` for one command's full
help (options, exit codes, behavioural notes). The summary below is the shape of
each verb; `--json` is available on every command for scripting.

### `dmg info IMAGE`

Describes an image without mounting or decoding it — trailer, property list, block
map, and (for a UDIF container) whether each partition can be decoded and mounted
by this build. Always exits 0 for an image it can parse at all; check `canDecode`
and `canMount` in `--json` output for the verdict.

```
C:\> dmg info .\installer.dmg
```

### `dmg mount IMAGE`

Decodes a volume to a scratch VHD and attaches it as a Windows drive.

```
C:\> dmg mount .\installer.dmg --letter X
C:\> dmg mount .\installer.dmg --rw --scratch D:\scratch
```

- `-p, --partition N` — which partition to mount (see `dmg info`); defaults to the
  image's single mountable volume, and is required if there is more than one.
- `--rw` — attach read-write instead of the default read-only.
- `--letter LETTER` — request a specific drive letter instead of letting Windows
  choose one.
- `--scratch DIR` — write the scratch VHD under `DIR` instead of the per-user
  default.
- `--keep-scratch` — leave the scratch VHD behind if the mount does not complete.

**Requires an elevated shell.** Attaching a virtual disk needs the Manage Volume
privilege; `dmg` checks for it up front and exits 7 with the remedy rather than
raising a UAC prompt itself. Mounting also refuses up front (exit 8) if the
scratch volume has no room for the decoded volume — only the volume being
mounted is materialised, not the whole image.

### `dmg unmount LETTER|ID`

Detaches a mount `dmg` made and deletes its scratch VHD.

```
C:\> dmg unmount E:
C:\> dmg unmount --all
```

Accepts the drive letter or the id `dmg mount` printed. A target that names no
current mount exits 0 (nothing left to do). If Windows can't detach it — most
often a handle still open on the volume, e.g. Explorer sitting on it — `dmg` says
so, leaves the mount as it was, and does not force it; close whatever holds it
open and try again. `--keep-scratch` leaves the scratch VHD in place instead of
deleting it. `--all` detaches every mount on record and is an alternative to a
target, not a modifier of one.

### `dmg list`

Lists images `dmg` currently has mounted, reconciled against Windows first (a
mount a reboot silently dropped is never listed). Size is the scratch VHD's size
on disk, not the source `.dmg`'s size.

### `dmg extract IMAGE OUTPUT`

Decodes the image straight to a file — no elevation, no mount — for images that
can't be mounted at all (an unmountable filesystem, an unsupported codec, or a
machine that can't elevate right now).

```
C:\> dmg extract .\installer.dmg .\installer.vhd
C:\> dmg extract .\installer.dmg .\installer.raw --format raw
```

`--format` is `vhd` (default, a fixed VHD — the same bytes `dmg mount` would
attach) or `raw` (decoded sectors, no container). `--force` overwrites an
existing `OUTPUT`; without it, extract refuses. A failed extract deletes any
partial `OUTPUT` it wrote.

### `dmg verify IMAGE`

Decodes every chunk of the image, in order, to confirm it is intact — not just
the handful `info` reads. Exits 0 if every chunk decoded cleanly, 9 if a chunk's
compressed bytes don't decode to its declared length (corrupt/truncated image).

```
C:\> dmg verify .\installer.dmg
```

### Encrypted images

An `encrcdsa` v2 encrypted image needs a passphrase. There is no `--password
<value>` option by design — a passphrase on the command line lands in shell
history and the process list. Instead, on `info`, `mount`, `extract` and
`verify`:

- `--password-stdin` — read the passphrase from standard input.
- `--password-env VAR` — read it from the named environment variable.
- Neither given — `dmg` prompts on the console with input hidden, if a console
  is attached. Redirected input with neither flag set is a usage error.

```
C:\> "dmg-test-passphrase" | dmg mount .\encrypted.dmg --password-stdin
C:\> $env:DMG_PASS = "dmg-test-passphrase"; dmg mount .\encrypted.dmg --password-env DMG_PASS
```

A wrong passphrase exits 4.

## Exit codes

| Code | Name | Meaning |
| --- | --- | --- |
| 0 | Success | The operation completed. |
| 1 | InternalError | A bug in this tool — an unexpected exception or broken invariant. Please report it. |
| 2 | UsageError | The command line was wrong — unknown verb, missing or conflicting argument. |
| 3 | UnsupportedFormat | The image is well-formed but this build can't handle it — an unsupported container, a chunk codec outside v1's scope, or a payload filesystem Windows has no driver for. |
| 4 | DecryptionFailed | The passphrase was wrong, or the key material failed to unwrap. |
| 5 | FilesystemNotMountable | Decoded fine, but Windows won't mount the filesystem inside it (HFS+ and APFS are the expected cases). |
| 6 | MountFailed | Attaching the VHD or assigning a drive letter failed. |
| 7 | ElevationRequired | The operation needs an elevated shell and didn't get one. |
| 8 | InsufficientSpace | Not enough scratch space to materialise the decoded volume. |
| 9 | CorruptImage | The image is malformed — bad magic, impossible offsets, a failed checksum, or a chunk that didn't decode to its declared length. |

## Limits

- **HFS+ and APFS payloads are detected and reported, but never mounted or
  extracted to a working filesystem.** Windows has no driver for either, and the
  VHD route this tool uses can't supply one. `info` reports `canMount: false`;
  `mount` exits 5.
- **bzip2, LZFSE and LZMA chunk codecs are detected and named in the error, not
  decoded.** Every codec `hdiutil` might have used for the payload data is
  identified; only raw, zlib and Apple ADC chunks actually decode in this build.
  Hitting one of the others exits 3.
- Writing changes back into a `.dmg` is not supported.
- NDIF, sparseimage and sparsebundle containers are not supported — UDIF only.

Everything out of scope fails with a specific message and a specific exit code.
Nothing fails with a stack trace.

## Requirements

- Windows 10 1809 or later, x64 or arm64
- An elevated shell for `mount` and `unmount` (`AttachVirtualDisk` needs the
  Manage Volume privilege)
- Scratch space equal to the decoded size of the volume being mounted or
  extracted

## Documentation

| Document | What it covers |
| --- | --- |
| [docs/02-cli-design.md](docs/02-cli-design.md) | The CLI design — commands, architecture, patterns, dependency ledger |
| [docs/03-udif-format-reference.md](docs/03-udif-format-reference.md) | The `.dmg` container format, field by field |
| [docs/04-encrypted-dmg-reference.md](docs/04-encrypted-dmg-reference.md) | `encrcdsa` v2 encryption and how it's unwrapped |
| [docs/05-testing-strategy.md](docs/05-testing-strategy.md) | Fixtures, CI, and how Windows code is tested from a Mac |
| [docs/manual-test-plan.md](docs/manual-test-plan.md) | Manual checklist for a real Windows 11 machine |
| [docs/adr/](docs/adr/) | The decisions that shape everything else |

Format reference docs are written ahead of and alongside implementation and are
not gospel — where a doc and a real `.dmg` disagree, the code follows the real
image and the doc gets corrected.

## License

MIT — see [LICENSE](LICENSE).
