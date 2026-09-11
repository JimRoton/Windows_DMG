# Manual test plan — Windows 11

`dmg` cross-builds on macOS but its Win32 calls (`AttachVirtualDisk`, elevation
checks, drive-letter and volume enumeration) can only actually run on Windows.
CI's `e2e` job covers real mounts on `windows-latest`, which already runs
elevated — so it cannot exercise the **non-elevated** path, a UAC-relevant
message, or anything that depends on Explorer, a reboot, or a human eyeballing
a drive window. This is that residual checklist, to run by hand on a real
Windows 11 machine.

Each item lists what to run, what to expect, and the exit code (`echo
%ERRORLEVEL%` in cmd, `$LASTEXITCODE` in PowerShell) where one applies. Work
top to bottom; later items build on earlier ones (e.g. the encrypted-image
checks assume you already know plain `mount` works).

## Setup

- A Windows 11 machine or VM, x64 or arm64, build 1809-equivalent or later.
- `dmg.exe` — either downloaded from a [Release](../../releases) or built per
  the README's "Build from source" section.
- Two PowerShell windows: one **elevated** (Run as Administrator), one
  **not**. Most verbs need the elevated one; §3 specifically wants the other.
- The test images from "Making test images on a Mac" below, copied onto the
  Windows machine.

## Making test images on a Mac

The repo already has a fixture generator that produces exactly this corpus
with Apple's own `hdiutil`, so there is no need to hand-roll images:

```sh
tools/make-fixtures.sh
```

This writes `fixtures/generated/*.dmg` (gitignored, not committed — regenerate
whenever you need them; see `fixtures/README.md` for what each one is and the
full recipe list). Copy the ones this plan uses to the Windows machine:

| Fixture | Use below |
| --- | --- |
| `exfat-zlib.dmg` | exFAT mount, read/copy, `--rw`, `--letter` |
| `fat32.dmg` | FAT32 mount |
| `exfat-enc256.dmg` / `exfat-enc128.dmg` | Encrypted mount, wrong passphrase |
| `exfat-enc-udzo.dmg` | Encrypted **and** compressed — decrypt then inflate (§5) |
| `exfat-sparse.dmg` | Dynamic VHD size comparison (§10) |
| `hfsplus.dmg` | HFS+ refusal (exit 5) |

The passphrase for every encrypted fixture is `dmg-test-passphrase`.

Every populated volume carries three known files, useful for the read/copy
check below:

| File | Contents |
| --- | --- |
| `HELLO.TXT` | `Hello, DMG fixture!` (20 bytes, one line) |
| `README.TXT` | `Windows_DMG test corpus` / `fixture v1` (two lines, 35 bytes) |
| `DATA.BIN` | 65536 bytes: big-endian `uint32` counter 0…16383 |

If you'd rather build your own image instead of using the generator: `hdiutil
create -size 64m -fs exFAT -volname TEST test.dmg`, copy files onto it via
Finder or `hdiutil attach`, then `hdiutil convert test.dmg -format UDZO -o
test-zlib.dmg` (add `-encryption AES-256 -stdinpass` before `-o` for an
encrypted one — supply the passphrase on stdin, newline-terminated, never as
an argument).

## 1. Basic mount, read, unmount (exFAT)

Elevated shell:

```
dmg mount exfat-zlib.dmg
```

- [ ] Exits 0. Prints the assigned drive letter.
- [ ] The drive appears in Explorer and File Explorer's sidebar, labeled
      exFAT, read-only (no "eject/write" restrictions beyond that — just
      confirm Windows treats it as a normal removable-style volume).
- [ ] `HELLO.TXT`, `README.TXT`, `DATA.BIN` are present and their contents
      match the table above (open `HELLO.TXT`, or `fc /b` `DATA.BIN` against a
      copy you trust).
- [ ] Copy all three files to the desktop. Copies succeed and are
      byte-identical (`fc /b`) to the originals.
- [ ] Attempt to create a new file on the mounted drive. Fails — the volume is
      read-only.

```
dmg unmount <letter>
```

- [ ] Exits 0. Drive disappears from Explorer immediately.

## 2. FAT32

```
dmg mount fat32.dmg --letter T
dmg unmount T
```

- [ ] Mounts at drive `T:` specifically (not a Windows-chosen letter).
- [ ] Filesystem shows as FAT32 in Explorer's drive properties.
- [ ] Files readable, same as §1.
- [ ] Unmounts cleanly, exit 0.

## 3. `--rw`

```
dmg mount exfat-zlib.dmg --rw
```

- [ ] Mounts read-write. Creating, editing and deleting a file on the drive
      all succeed.
- [ ] `dmg unmount <letter>` still exits 0 and detaches cleanly afterward.

## 4. Not elevated → exit 7

In the **non-elevated** PowerShell window:

```
dmg mount exfat-zlib.dmg
```

- [ ] Exits 7.
- [ ] The message names the problem (needs an elevated shell) and says what to
      do about it — it must not just fail, and must not pop a UAC prompt
      itself.
- [ ] No drive appears, nothing is left mounted (`dmg list` from the elevated
      window shows nothing new).

## 5. Encrypted image — correct passphrase

Elevated shell, each of the three input methods:

```
"dmg-test-passphrase" | dmg mount exfat-enc256.dmg --password-stdin
```
```
$env:DMG_TEST_PASS = "dmg-test-passphrase"
dmg mount exfat-enc128.dmg --password-env DMG_TEST_PASS
```
```
dmg mount exfat-enc256.dmg
```
(no flag — should prompt on the console; type `dmg-test-passphrase` and press
Enter)

- [ ] All three mount successfully, exit 0.
- [ ] The interactive prompt does not echo the passphrase to the screen.
- [ ] Files inside match §1's table (both encrypted fixtures decode to the
      same content as the plain one).
- [ ] Unmount each afterward.

## 6. Wrong passphrase → exit 4

```
"not-the-passphrase" | dmg mount exfat-enc256.dmg --password-stdin
```

- [ ] Exits 4.
- [ ] Message says the passphrase was wrong (not a generic/garbled failure).
- [ ] Nothing is left mounted.

## 7. HFS+ → exit 5

```
dmg info hfsplus.dmg
dmg mount hfsplus.dmg
```

- [ ] `info` exits 0 and reports the volume with `canMount: false` (`--json`)
      or the equivalent human-readable note — describing the image is not a
      failure.
- [ ] `mount` exits 5, with a message naming HFS+ specifically and saying
      Windows has no driver for it, rather than a generic mount failure.
- [ ] `dmg extract hfsplus.dmg hfsplus.vhd` still succeeds (exit 0) — extract
      only decodes, it doesn't need Windows to mount anything. Confirm the VHD
      exists and is roughly the volume's decoded size.

## 8. Unmount with Explorer open on the drive

```
dmg mount exfat-zlib.dmg --letter U
```

Open `U:\` in Explorer and leave the window open (or `cd U:\` in a third
shell), then:

```
dmg unmount U
```

- [ ] If Windows refuses to detach (a handle is still open), `dmg` reports
      that plainly rather than forcing it, and `U:` is still mounted and
      usable afterward.
- [ ] Close the Explorer window (and any shell with `U:\` as its current
      directory), retry `dmg unmount U` — now it exits 0 and the drive is
      gone.

## 9. `dmg list` after a reboot

```
dmg mount exfat-zlib.dmg --letter V
dmg list
```

- [ ] `V:` appears in the listing.

Reboot the machine (a real reboot, not sleep — `AttachVirtualDisk` mounts do
not survive one). After it comes back up, elevated shell:

```
dmg list
```

- [ ] `V:` is **not** listed — a reboot silently drops the attach, and `list`
      reconciles against what Windows actually has mounted rather than
      trusting its own record.
- [ ] No error, no stale entry, no crash. (`dmg` keeps its mount record at
      `%LOCALAPPDATA%\dmg\mounts.json`, if you want to inspect it directly —
      it's fine for the entry to still be in that file as long as `list`
      doesn't report it as live.)

## 10. Dynamic VHD and `--cache`

S9.12 wired the dynamic VHD writer to the CLI. `mount`, `extract` and `verify`
all now take `--cache MB`; `mount` and `extract` also take `--dynamic`, which
writes the scratch VHD as an allocate-on-demand disk instead of a fixed one.
A mostly-empty volume is where that shows: `exfat-sparse.dmg` is a 48 MiB
volume holding one small file.

```
dmg help mount
dmg help extract
```

- [ ] Both list `--dynamic` and `--cache`. (If they don't, the build under
      test predates S9.12.)

```
dmg extract exfat-sparse.dmg sparse-fixed.vhd
dmg extract exfat-sparse.dmg sparse-dyn.vhd --dynamic
```

- [ ] Both exit 0.
- [ ] `sparse-dyn.vhd` is substantially smaller **on disk** than
      `sparse-fixed.vhd`. `dir` reports the logical size, which may look
      identical — check "Size on disk" in the file's Properties dialog.

**This next item is the highest-risk check in the whole plan: no dynamic VHD
this tool writes has ever been attached by Windows.** Elevated:

```
Mount-DiskImage -ImagePath <full path to sparse-dyn.vhd>
```

- [ ] Windows accepts the disk and assigns a volume. Its files match §1's
      table.
- [ ] `Dismount-DiskImage -ImagePath <same path>` afterwards, exit 0.
- [ ] If Windows rejects it, capture the exact error — that points at the
      dynamic VHD header/BAT geometry, not at the DMG decode.

```
dmg mount exfat-sparse.dmg --dynamic
dmg unmount <letter>
```

- [ ] Mounts, assigns a letter, files readable, unmounts cleanly.

```
dmg mount exfat-zlib.dmg --cache 8
dmg verify exfat-zlib.dmg --cache 1
dmg extract exfat-zlib.dmg out.raw --format raw --dynamic
```

- [ ] The first two exit 0 — `--cache` changes performance only, never output.
- [ ] The third exits **2**: `--dynamic` with `--format raw` is a usage error,
      because a raw image has no dynamic form. It must say so rather than
      silently ignoring the flag.

## Reporting a failure

For anything that doesn't match: note the exact command, the exit code you
got, the full stderr/stdout, and (`--json` where relevant) the JSON body.
"It didn't work" is not enough to act on — the specific message text and exit
code are usually the whole story `dmg` is designed to give you.
