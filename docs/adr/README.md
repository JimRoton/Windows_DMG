# Architecture Decision Records

Eight decisions that shape everything else. Each records the context, the decision,
and what it costs — so a later reader can tell whether the reasoning still holds.

| ADR | Decision | Status |
| --- | --- | --- |
| [001](ADR-001-language-and-runtime.md) | C# on .NET 10, published with NativeAOT | Proposed |
| [002](ADR-002-zero-third-party-dependencies.md) | Zero third-party runtime dependencies, enforced in CI | Proposed |
| [003](ADR-003-vhd-shim-over-user-mode-filesystem.md) | VHD shim, not a user-mode filesystem | Proposed |
| [004](ADR-004-stream-seam-and-codec-strategy.md) | `System.IO.Stream` as the seam; Strategy for codecs | Proposed |
| [005](ADR-005-encryption-as-a-stream-decorator.md) | Encryption as a Stream decorator, not a parser mode | Proposed |
| [006](ADR-006-test-framework-exception.md) | xUnit permitted for tests only | Proposed |
| [007](ADR-007-xunit-skippablefact.md) | Xunit.SkippableFact permitted for tests only (MS-PL) | Accepted |
| [008](ADR-008-projfs-projection-head.md) | A ProjFS projection head for images too large to materialise | Accepted |
