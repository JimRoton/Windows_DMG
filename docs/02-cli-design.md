# CLI Design — `dmg`

> Status: **proposed**, awaiting review · 9 September 2026
> Supersedes nothing. Depends on [01-feasibility-and-architecture.md](01-feasibility-and-architecture.md).

A single native Windows executable that mounts an exFAT-formatted Apple disk image
as a real drive letter, decrypting it first if necessary, with no third-party
runtime dependencies.

---

## 1. The shape of the thing

Windows will not mount a `.dmg`. It will mount a `.vhd`, natively, through
`virtdisk.dll`, and it will then use its own exFAT driver to read the volume.

A fixed-format VHD is **raw sector data followed by a 512-byte footer**. So the
entire tool is one sentence: *decode the DMG's sector stream, append a VHD footer,
and hand the file to Windows.*

```
image.dmg ──▶ [decrypt] ──▶ [decode chunks] ──▶ sector stream ──▶ scratch.vhd ──▶ AttachVirtualDisk ──▶ E:\
             encrcdsa       zlib/ADC/raw                          + footer         virtdisk.dll        Windows'
             AES-CBC                                                                                   exFAT driver
```

Everything to the left of `scratch.vhd` is ours and is portable C#. Everything to
the right is Windows doing what it already knows how to do.

### What this design refuses to do

It does not implement a filesystem. It does not install a driver. It does not sign
anything. Those are the costs of the WinFsp and virtual-SCSI routes described in
[the feasibility study](01-feasibility-and-architecture.md), and the exFAT
requirement makes them unnecessary — Windows already has the driver we need.

The price is paid in two coins, both stated plainly in the CLI's own output:

1. **Scratch space.** The image is materialised before it is mounted. A 4 GiB image
   needs 4 GiB free. `dmg mount` checks before it starts and fails immediately if
   there isn't room, rather than at 94%.
2. **Filesystem coverage.** HFS+ and APFS payloads cannot be mounted this way. They
   are detected, named, and refused with exit code 5 — never with a corrupt mount
   or a stack trace.

---

## 2. Command surface

```
dmg <verb> [arguments] [options]

  mount    <image.dmg>              Decode, convert and attach as a drive letter
  unmount  <letter | id | --all>    Detach and clean up
  list                              Show what this tool currently has mounted
  info     <image.dmg>              Describe the container without mounting it
  verify   <image.dmg>              Full decode pass; validate every checksum
  extract  <image.dmg> <output>     Write the decoded image to raw or VHD
  help     [verb]                   Per-verb help
  version                           Version and build information
```

### `dmg mount`

```
dmg mount <image.dmg> [options]

  --letter <X:>            Request a specific drive letter
  --rw                     Attach read-write (default is read-only)
  --partition <n>          Mount partition n (default: the only mountable one)
  --password-stdin         Read the passphrase from stdin
  --password-env <VAR>     Read the passphrase from an environment variable
  --scratch <dir>          Where to write the VHD  (default: %LOCALAPPDATA%\dmg\scratch)
  --keep-scratch           Do not delete the VHD on unmount
  --cache <MB>             Chunk cache budget  (default: 64)
  --dynamic                Write a dynamic (sparse) VHD instead of fixed
  --json                   Machine-readable output
  -v, --verbose            Progress and diagnostics on stderr
  -q, --quiet              Errors only
```

There is deliberately **no `--password <value>`**. A passphrase on the command line
lands in shell history, in the process list and in any command-line auditing the
machine has. If no `--password-*` option is given and the image is encrypted, the
tool prompts on the console with echo disabled. Non-interactive callers use
`--password-stdin`.

Human output:

```
C:\> dmg mount .\installer.dmg
  container   UDIF · zlib (UDZO) · 2.41 GiB decoded
  partition   1 of 1 · GPT · exFAT "Installer" · 2.40 GiB
  scratch     C:\Users\jim\AppData\Local\dmg\scratch\7f3a91c2.vhd
  writing     ############################  100%   2.41 GiB in 18.4s
  attached    \\.\PhysicalDrive4

Mounted  installer.dmg  ->  E:\   (exFAT, 2.40 GiB, read-only)
```

Machine output (`--json`) is a single object on stdout; progress and diagnostics
always go to stderr so `--json` output is never polluted:

```json
{
  "ok": true,
  "id": "7f3a91c2",
  "source": "C:\\Users\\jim\\Downloads\\installer.dmg",
  "vhd": "C:\\Users\\jim\\AppData\\Local\\dmg\\scratch\\7f3a91c2.vhd",
  "letter": "E:",
  "readOnly": true,
  "filesystem": "exFAT",
  "volumeLabel": "Installer",
  "sizeBytes": 2576980377,
  "encrypted": false,
  "compression": "zlib"
}
```

### `dmg info`

Reads only the trailer, the property list and the block map. It never decodes a
chunk, so it is instant on any image and safe on an image it cannot handle.

```
C:\> dmg info .\installer.dmg
  file           installer.dmg  (912.4 MiB on disk)
  container      UDIF v4
  encryption     none
  decoded size   2.41 GiB  (5,033,164 sectors)
  chunks         2,462   zlib 2,301 · raw 44 · zero-fill 117
  partitioning   GPT (protective MBR)
  partitions     1  ·  exFAT "Installer"  2.40 GiB  →  mountable
```

For an image it cannot mount, `info` says exactly why, and exits 0 because
answering the question *is* the job:

```
  partitions     1  ·  HFS+ "Install macOS"  12.1 GiB  →  NOT MOUNTABLE
                       Windows has no HFS+ driver. Use `dmg extract` to write
                       the raw image, or read it on a Mac.
```

### `dmg unmount`, `dmg list`

```
C:\> dmg list
  ID        LETTER  FILESYSTEM  SIZE      MODE  SOURCE
  7f3a91c2  E:      exFAT       2.40 GiB  ro    C:\Users\jim\Downloads\installer.dmg
  a1b4ff03  F:      exFAT       512 MiB   rw    D:\images\scratch.dmg

C:\> dmg unmount E:
Unmounted E:  ·  removed 7f3a91c2.vhd (2.41 GiB reclaimed)
```

`unmount` accepts a drive letter, a mount ID, or `--all`. `list` reconciles its
registry against the disks Windows actually has attached, so entries that did not
survive a reboot are pruned rather than reported as live.

### Exit codes

The contract matters more than the messages — scripts depend on it.

| Code | Meaning |
| --- | --- |
| 0 | Success |
| 1 | Unexpected internal error (a bug; prints a report path) |
| 2 | Usage error — bad arguments |
| 3 | Unsupported container or codec (bzip2, LZFSE, LZMA, NDIF, sparsebundle) |
| 4 | Decryption failed — wrong passphrase, or an unsupported encryption version |
| 5 | Payload filesystem not mountable on Windows (HFS+, APFS) |
| 6 | Windows refused the mount (`AttachVirtualDisk` failed) |
| 7 | Requires elevation |
| 8 | Insufficient scratch space |
| 9 | Image is corrupt or fails validation |

---

## 3. Architecture

### 3.1 Layers

```
┌──────────────────────────────────────────────────────────┐
│  Dmg.Cli            verbs, argument parsing, output      │  net10.0-windows
├──────────────────────────────────────────────────────────┤
│  Dmg.Windows        virtdisk P/Invoke, volume discovery, │  net10.0-windows
│                     mount registry, elevation check      │
├──────────────────────────────────────────────────────────┤
│  Dmg.Core           container · codecs · crypto ·        │  net10.0
│                     partitions · filesystem probe ·      │  NO Windows
│                     block stream · VHD writer            │  dependency
└──────────────────────────────────────────────────────────┘
```

**`Dmg.Core` has no Windows dependency at all.** That is not tidiness — it is the
single decision that makes this project practical to build on a Mac. Roughly 80% of
the code and 90% of the difficulty lives in `Dmg.Core`, and all of it compiles,
runs and unit-tests on macOS against fixtures generated by `hdiutil`. Only the
attach/detach path needs a Windows machine.

### 3.2 The seam

```csharp
// Dmg.Core
public sealed class DmgBlockStream : Stream
{
    public override bool CanRead  => true;
    public override bool CanSeek  => true;
    public override bool CanWrite => false;
    public override long Length   => _sectorCount * 512L;
    public override int  Read(Span<byte> buffer) { /* index → cache → codec → file */ }
}
```

The abstraction between "DMG problem" and "everything else" is
`System.IO.Stream` — not a bespoke interface. Every consumer we will ever write
(the VHD writer, `extract`, `verify`'s hasher, a future test harness) already
speaks `Stream`, so none of them needs to know that DMG exists.

This is worth stating explicitly because the tempting alternative — an
`IBlockDevice` of our own — buys nothing and costs adapters at every boundary.

### 3.3 Read path

```
DmgBlockStream.Read(offset, length)
  │
  ├─ ExtentIndex.Find(sector)          binary search over a flat sorted array
  │                                    → chunk descriptor #217
  ├─ ChunkCache.TryGet(217)            LRU, 64 MiB default
  │      hit  ──────────────────────▶  copy out, return          ← the common case
  │      miss
  ├─ ChunkDecoderRegistry[EntryType]   Strategy: zlib | raw | zero | ADC
  ├─ source.Read(CompressedOffset, CompressedLength)
  │      └─ EncryptedBlockStream       Decorator, present only if encrypted
  │             └─ FileStream
  └─ ChunkCache.Add(217, decoded) ─▶  copy out, return
```

The cache is architecture, not optimisation. UDZO chunks are typically 1 MiB, so a
4 KiB read decompresses 256× the data it returns. Without a cache, walking a
directory tree re-inflates the same chunk dozens of times and the mount feels
broken. With it, the second read of any chunk costs a `memcpy`.

### 3.4 Mount sequence

```
 1. Probe format          IImageFormatProbe chain: encrcdsa → UDIF → raw
 2. Decrypt if needed     EncryptedBlockStream decorates the FileStream
 3. Parse container       koly trailer → XML plist → blkx → mish → chunk table
 4. Build extent index    flat sorted array; binary search on read
 5. Open DmgBlockStream   the seam
 6. Read partition table  GPT / MBR / APM / whole-disk
 7. Probe filesystem      exFAT? FAT32? NTFS?  → else exit 5, naming what it found
 8. Check scratch space   → else exit 8
 9. Check elevation       → else exit 7
10. Write VHD             stream + 512-byte fixed footer, with progress
11. AttachVirtualDisk     virtdisk.dll, read-only by default
12. Discover drive letter physical path → device number → volume → mount point
13. Record in registry    %LOCALAPPDATA%\dmg\mounts.json
14. Print result
```

Steps 7, 8 and 9 are all cheap and all happen **before** step 10, which is the
expensive one. Failing fast on the three things most likely to be wrong is worth
more to the user than any amount of speed in the write.

---

## 4. Design patterns — the ones that earn their place

A CLI with roughly twenty types does not need a pattern catalogue. These six are
here because each removes a specific, identified cost.

| Pattern | Where | What it buys |
| --- | --- | --- |
| **Stream as seam** | `DmgBlockStream : Stream` | Every consumer already speaks it. No adapters, no bespoke interface. |
| **Decorator** | `EncryptedBlockStream` wraps `FileStream`; `CachingChunkReader` wraps the decoder | The UDIF parser never learns that encryption exists. Composition instead of a flag threaded through nine call sites. |
| **Strategy + registry** | `IChunkDecoder` keyed by `EntryType` in a `Dictionary<uint, IChunkDecoder>` | Adding LZFSE later is one new class and one registration. No `switch` to find and edit. |
| **Chain of responsibility** | `IImageFormatProbe`: encrcdsa → UDIF → raw → unknown | Format sniffing stays ordered and open. The "unknown" terminal produces a good error instead of an exception. |
| **Command** | `ICliCommand { Verb; Execute(ctx) }` + registry | `Main` is a dispatcher and nothing else. Each verb is independently testable with no process launch. |
| **Ports & adapters** | `IVirtualDiskService` → `WindowsVirtualDiskService` \| `FakeVirtualDiskService` | The mount logic is unit-testable on macOS. This is the difference between testing the tool and testing it only on CI. |

### Result over exceptions for expected failure

```csharp
public readonly struct Result<T>
{
    public bool     Ok      { get; }
    public T?       Value   { get; }
    public DmgError Error   { get; }   // code + message + optional detail
}
```

A truncated file, a bad magic number, an unsupported codec and a wrong passphrase
are all *expected outcomes of correct code operating on hostile input*. They are
values, not exceptions. Exceptions are reserved for bugs, and the top-level handler
treats one as exit code 1 with a report path.

This matters more than usual here because the input is attacker-controlled: every
parse failure is a path that will be exercised deliberately, and control flow via
exceptions makes it far too easy to catch the wrong thing and continue.

### What this design deliberately does not use

Stated so the absence reads as a decision rather than an oversight.

- **No DI container.** Hand-wire the object graph in `Program.Main`. Twenty types
  do not justify a container, and every container is a third-party package.
- **No repository or unit-of-work over the mount registry.** It is a JSON file with
  two operations. `MountRegistry.Load()` / `.Save()` is the whole abstraction.
- **No abstract factory hierarchy for codecs.** The dictionary *is* the factory.
- **No async by default.** This work is CPU-bound decompression plus large
  sequential writes. `async` adds machinery and subtracts throughput. The one
  exception is prefetch, which is a single background thread, not a task graph.
- **No plugin loading, no configuration framework, no logging abstraction.**
  A verbosity enum and `Console.Error` cover every real requirement.

---

## 5. Dependency ledger

The constraint is zero third-party libraries in the shipped binary. It is met.

### Shipped runtime dependencies: **none**

| Need | Provided by | Source | License |
| --- | --- | --- | --- |
| deflate / zlib decode | `System.IO.Compression.ZLibStream` | .NET BCL | MIT |
| PBKDF2-HMAC-SHA1 | `Rfc2898DeriveBytes` | .NET BCL | MIT |
| 3DES-EDE-CBC | `System.Security.Cryptography.TripleDES` | .NET BCL | MIT |
| AES-128/256-CBC | `System.Security.Cryptography.Aes` | .NET BCL | MIT |
| HMAC-SHA1 | `System.Security.Cryptography.HMACSHA1` | .NET BCL | MIT |
| XML property list | `System.Xml` (`XmlReader`) | .NET BCL | MIT |
| Base64 | `Convert.FromBase64String` | .NET BCL | MIT |
| VHD attach / detach | `virtdisk.dll` P/Invoke | Windows OS | OS API |
| On-demand file projection | `ProjectedFSLib.dll` P/Invoke ([ADR-008](adr/ADR-008-projfs-projection-head.md)) | Windows OS | OS API |
| Volume + device enumeration | `kernel32.dll`, `DeviceIoControl` | Windows OS | OS API |
| Elevation check | `advapi32.dll` token query | Windows OS | OS API |
| Apple ADC decode | **written here**, ~200 lines | this repo | MIT |
| VHD image writer (footer, dynamic header, block allocation table, sparse map) | **written here**, ~2,900 lines | this repo | MIT |
| Argument parsing | **written here**, ~900 lines | this repo | MIT |

The .NET Base Class Library is part of the runtime, and NativeAOT statically links
what is used into `dmg.exe`. The shipped artifact is a single native executable
with no runtime install and no package references.

### Build-and-test-only dependencies

| Need | Choice | License | Shipped? |
| --- | --- | --- | --- |
| Unit test framework | xUnit + `xunit.runner.visualstudio` | Apache-2.0 | **No** — `tests/` only |
| Runtime test skip | Xunit.SkippableFact ([ADR-007](adr/ADR-007-xunit-skippablefact.md)) | MS-PL | **No** — `tests/` only |
| Test host SDK | `Microsoft.NET.Test.Sdk` | MIT | **No** — `tests/` only |

CI enforces this: a build step (`dependency-guard` in `ci.yml`) fails if any
`PackageReference` appears in a `.csproj`/`.props`/`.targets` file under `src/`;
test projects under `tests/` are exempt. See
[ADR-002](adr/ADR-002-zero-third-party-dependencies.md).

### If a later version needs the deferred codecs

Both have licenses that permit use, and both would be vendored as source rather
than referenced as packages:

| Codec | Source | License |
| --- | --- | --- |
| LZFSE | Apple's reference implementation | Apache-2.0 ✅ |
| LZMA | 7-Zip LZMA SDK (Igor Pavlov) | Public domain ✅ |
| bzip2 | Written from scratch (~900 lines) or the bzip2 reference | BSD-style ✅ |

---

## 6. Repository layout

```
src/
  Dmg.Core/                    net10.0            portable, no Windows API
    Containers/                UdifReader, KolyTrailer, MishBlock, PropertyList
    Codecs/                    IChunkDecoder, Zlib, Raw, ZeroFill, Adc, Registry
    Crypto/                    EncrCdsaHeader, KeyBlob, EncryptedBlockStream
    Imaging/                   DmgBlockStream, ExtentIndex, ChunkCache
    Partitions/                Gpt, Mbr, ApplePartitionMap, DriverDescriptorMap
    FileSystems/               ExFatProbe, Fat32Probe, NtfsProbe, HfsPlusProbe, ApfsProbe
    Vhd/                       FixedVhdFooter, VhdWriter, DynamicVhdWriter
    Result.cs, DmgError.cs
  Dmg.Windows/                 net10.0-windows
    VirtualDisk/               IVirtualDiskService, WindowsVirtualDiskService, NativeMethods
    Volumes/                   DriveLetterResolver, VolumeEnumerator
    MountRegistry.cs, Elevation.cs
  Dmg.Cli/                     net10.0-windows    NativeAOT publish target
    Commands/                  MountCommand, UnmountCommand, ListCommand, InfoCommand, ...
    ArgumentParser.cs, Output.cs, Program.cs
tests/
  Dmg.Core.Tests/              runs on macOS AND Windows
  Dmg.Windows.Tests/           Windows only, uses FakeVirtualDiskService
  Dmg.E2E.Tests/               Windows CI only, elevated, real mounts
tools/
  make-fixtures.sh             hdiutil; run on macOS
fixtures/
  manifest.json                expected SHA-256 of each fixture's decoded stream
docs/
```

---

## 7. Encryption

Handled as a **decorator on the byte source**, not as a mode inside the parser.

An `encrcdsa` v2 image has its header at the *front* of the file, and the UDIF
container — koly trailer and all — sits inside the encrypted payload. So:

```
FileStream ──▶ EncryptedBlockStream ──▶ UdifReader ──▶ DmgBlockStream
               (AES-CBC, per-block IV)   (koly at EOF   (chunk decode)
                                          of the *decrypted* view)
```

`UdifReader` sees a plain seekable `Stream` and has no idea. That is the whole
argument for the Decorator here.

Unwrapping the key:

```
passphrase ─PBKDF2-HMAC-SHA1(salt, iterations)─▶ derived key
derived key ─3DES-EDE-CBC decrypt─▶ keyblob ─▶ { AES key, HMAC-SHA1 key }

for each 4096-byte block N:
    IV = HMAC-SHA1(hmacKey, BE32(N))[0..16]
    plaintext = AES-CBC-decrypt(aesKey, IV, ciphertext)
```

Every primitive is in the .NET BCL. Details and field offsets are in
[04-encrypted-dmg-reference.md](04-encrypted-dmg-reference.md).

Two operational notes carried into the stories:

- **Wrong passphrase must be detectable and cheap.** Decrypt block 0 and check that
  the resulting bytes make sense as a container (or validate the keyblob's own
  padding). Never report "corrupt image" when the real answer is "wrong password".
- **3DES may be unavailable under FIPS policy.** Detect that specific failure and
  say so, rather than reporting a generic crypto error.

---

## 8. Security posture

Every byte of a `.dmg` is attacker-supplied — these files arrive from the internet.
The parser is the attack surface and is treated as such.

- **Bound every declared length against the real file size** before allocating or
  seeking. `CompressedOffset` and `CompressedLength` are the obvious vectors.
- **Cap decompression output** at `SectorCount × 512` and abort on overrun. A chunk
  claiming 4 GiB of output is a decompression bomb, not a large file.
- **Checked arithmetic on all sector maths.** 64-bit sector counts multiplied by
  512 overflow, and the result indexes a buffer. `checked` blocks, not hope.
- **Never write outside the scratch directory.** Paths are constructed, not taken
  from the image.
- **Passphrases never reach a log, a crash report or the process title.** Held in
  a `byte[]`, cleared after key derivation.
- **Read-only by default.** `--rw` is an explicit act.

---

## 9. Open questions for review

1. **`--rw` semantics.** Writes land in the scratch VHD, not the `.dmg`. Should the
   tool offer to re-encode back into a new `.dmg` on unmount, or is
   "changes live in the VHD, use `--keep-scratch` if you want them" the right
   answer for v1? *Recommendation: the latter — re-encode is a v2 epic.*
2. **Elevation.** `mount` needs an elevated shell. Should the tool self-elevate with
   a UAC prompt, or refuse with instructions? *Recommendation: refuse. A CLI that
   spawns UAC prompts is hostile in scripts.*
3. **Dynamic VHD by default?** Sparse images are common and a dynamic VHD would cut
   scratch usage dramatically. It is more code and more to get wrong.
   *Recommendation: fixed VHD in v1, `--dynamic` opt-in, flip the default once it
   has proven itself.*
4. **xUnit for tests.** It is a third-party package, Apache-2.0, never shipped in
   the binary. *Recommendation: accept. The alternative is writing a test runner,
   which is real work that tests nothing.*
5. **Should `dmg info` on an HFS+ image exit 0 or 5?** *Recommendation: 0. It
   answered the question correctly; the image simply isn't mountable.*
