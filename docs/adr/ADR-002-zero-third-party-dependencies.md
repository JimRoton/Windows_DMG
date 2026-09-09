# ADR-002 — Zero third-party runtime dependencies, enforced in CI

**Status:** Proposed · 9 September 2026

## Context

The requirement is: no third-party libraries if possible, native code as much as
possible, and anything third-party must carry a permissive license. Requirements
of this kind decay silently — someone adds a package to solve a small problem and
nobody notices until the licence audit.

## Decision

**No `PackageReference` in any project under `src/`.** A CI job fails the build if
one appears. Test projects under `tests/` may reference xUnit and nothing else
(see [ADR-006](ADR-006-test-framework-exception.md)).

Everything else comes from three places, in this order of preference:

1. The .NET Base Class Library — part of the runtime, statically linked by NativeAOT
2. A Windows OS API via P/Invoke
3. Code written in this repository

## Why enforcement rather than a policy statement

A policy in a README is not a constraint; a red build is. The check is four lines
of shell and it makes the rule self-maintaining.

## The ledger

Kept current in [the CLI design, §5](../02-cli-design.md#5-dependency-ledger). Every
capability the tool needs is mapped to one of the three sources above, with a
license column. As of this ADR, the shipped binary has **zero** third-party code.

## Consequences

- Apple ADC, the VHD footer, the property-list reader and the argument parser are
  written here. That is roughly 600 lines total and all of it is well-specified.
- bzip2, LZFSE and LZMA are **out of scope for v1** rather than pulled in as
  packages. They are detected and reported by name. This is a real functional
  limit and it is stated in the README rather than hidden.
- If those codecs are wanted later, both viable sources are license-compatible and
  would be **vendored as source**, not referenced as packages: Apple's LZFSE
  (Apache-2.0) and the 7-Zip LZMA SDK (public domain). The rule stays intact.
- Some things get harder. Structured logging, config binding and rich console
  rendering are all reimplemented at a small scale or skipped. Given the size of
  this tool, that is a good trade; it would not be on a larger product.
