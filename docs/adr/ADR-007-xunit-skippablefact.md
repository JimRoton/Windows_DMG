# ADR-007 — Xunit.SkippableFact permitted for tests only

**Status:** Accepted · 11 September 2026

## Context

S2.10 (#92) required fixture-dependent tests to report **skipped**, not passed, when
the hdiutil corpus is absent — otherwise a clean checkout goes green having verified
none of the format work. xUnit 2.9.3's own `SkipException` / dynamic skip token is
**not honoured** by `xunit.runner.visualstudio` 3.1.5 (verified: it reports a
failure, not a skip). No BCL or in-box mechanism produces a runtime skip.

[ADR-006](ADR-006-test-framework-exception.md) permits xUnit and states that any
further test-only dependency needs its own ADR. This is that ADR.

## Decision

Permit **Xunit.SkippableFact 1.4.13**, referenced only by projects under `tests/`.

## License

**MS-PL** (Microsoft Public License), OSI-approved. It permits use and
redistribution; its conditions attach only to distributing the package itself.
It is never linked into `dmg.exe`, so the shipped artifact carries no obligation
from it. Compatible with the project's MIT license for this use.

## Consequences

- The CI dependency guard still checks `src/` only; this package cannot reach the
  shipped binary.
- Tests that degrade on missing fixtures use `[SkippableFact]` / `[SkippableTheory]`
  with `Skip.If(...)`, never `Assert.True(true); return;`.
- Any further test-only package still needs its own ADR.
