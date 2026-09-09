# ADR-006 — xUnit permitted for tests only

**Status:** Proposed · 9 September 2026

## Context

[ADR-002](ADR-002-zero-third-party-dependencies.md) bans third-party packages. A
test framework is a third-party package. The options were: hand-roll a runner,
or carve an explicit exception.

## Decision

**xUnit, Apache-2.0, referenced only by projects under `tests/`.** It is never
linked into `dmg.exe`. The CI dependency guard enforces the boundary by checking
`src/` specifically, not the repository as a whole.

## Why an exception rather than a hand-rolled runner

Writing a test runner is real work that tests nothing about the product. It would
also cost every future contributor the ability to use `dotnet test`, IDE test
explorers, CI test reporting, and every convention they already know.

The constraint's actual purpose is that **the shipped artifact** carries no
third-party code and no licence obligations. A test-only reference satisfies that
purpose exactly. Reading the constraint more literally than its purpose would trade
something valuable for nothing.

## License check

xUnit is Apache-2.0: permissive, permits commercial and closed-source use, requires
only attribution and a notice of changes. It imposes no obligation on `dmg.exe`,
which does not contain it.

## Consequences

- The dependency guard must check `src/**` and not `**`, or it will fail on the
  test projects it is meant to permit.
- If a second test-only dependency is ever proposed, it needs its own ADR. This
  exception covers xUnit and nothing else.
- Test projects still may not reference assertion libraries, mocking frameworks or
  fixture builders. The `FakeVirtualDiskService` in
  [ADR-003](ADR-003-vhd-shim-over-user-mode-filesystem.md)'s port is a hand-written
  fake precisely so no mocking framework is needed — which is better design anyway.
