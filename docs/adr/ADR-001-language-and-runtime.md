# ADR-001 — C# on .NET 10, published with NativeAOT

**Status:** Proposed · 9 September 2026

## Context

The tool must be a native Windows executable with no runtime install and no
third-party dependencies. It must parse a hostile binary format, do bulk
decompression and AES work, and P/Invoke into `virtdisk.dll`. It should be
developable on macOS, which is the only machine available day to day.

Candidates were C++ with Win32 only, Rust, and C# with NativeAOT.

## Decision

**C# targeting .NET 10, with `Dmg.Cli` published via NativeAOT** to a single
self-contained `dmg.exe`.

## Why

- **The BCL already contains almost every primitive we need**, and the BCL is part
  of the runtime rather than a third-party package: `ZLibStream` for deflate,
  `Rfc2898DeriveBytes` for PBKDF2, `TripleDES`/`Aes`/`HMACSHA1` for the key unwrap,
  `System.Xml.Linq` for the property list. In C++ every one of those is either a
  vendored library or code we write. That is the decisive point — the
  no-dependencies constraint is *easier* to meet in C#, not harder.
- **NativeAOT produces a genuine native binary.** No .NET install on the target
  machine, no JIT, a single file. The "native code" requirement is satisfied in the
  form that matters to a user: one exe that runs.
- **Memory safety on the attack surface.** The parser handles attacker-supplied
  input and its output is handed to the OS storage stack. `Span<T>` with bounds
  checking removes the entire class of bug that has historically made image parsers
  dangerous, without the ceremony C++ would need to get to the same place.
- **P/Invoke is first-class.** `virtdisk.dll` and `kernel32.dll` are a
  `[LibraryImport]` declaration away, and source-generated marshalling is
  AOT-compatible.
- **It builds and tests on macOS.** `Dmg.Core` targets plain `net10.0` and runs
  natively on the Mac. Cross-compiling the Windows head from macOS works for
  building; only the E2E suite needs a Windows machine.

Rust was the close second and would have been the choice if a WinFsp filesystem
were in scope (ADR-003 removed that). It loses here on the BCL point: with no
third-party crates allowed, deflate and the crypto primitives would all have to be
written by hand.

## Consequences

- NativeAOT constrains reflection. This is fine — the design uses no serialization
  frameworks and no DI container — but `System.Text.Json` needs source generation
  if it is used for the mount registry, and any future reflection-based code will
  fail at publish rather than at runtime.
- `TripleDES` under NativeAOT maps to Windows CNG, which respects machine FIPS
  policy. That produces a real failure mode, documented and handled in
  [ADR-005](ADR-005-encryption-as-a-stream-decorator.md).
- Trimming warnings must be treated as errors from the first commit, not retrofitted.
- Binary size will be roughly 8–15 MB. Acceptable for a tool; noted so it is not a
  surprise.
