# Agent brief

Read this once. Your task prompt adds only the stories and their acceptance criteria.

## Rules

- **Do not explore the repo.** Read only the files your prompt names. Agents have
  been killed by usage limits during orientation, before writing any code.
- **Zero third-party packages under `src/`.** BCL and Windows P/Invoke only.
  Tests may use xUnit.
- `src/Dmg.Core` is `net10.0` and must have **no Windows dependency** — it builds
  and tests on macOS. `src/Dmg.Windows` and `src/Dmg.Cli` are `net10.0-windows`.
- Build must be **0 warnings** (`TreatWarningsAsErrors`).
- Failures are `Result<T>` values, not exceptions. Exceptions are for bugs only.
  `Result<T>.Value` THROWS on failure — use `TryGetValue`.
- `DmgExitCode`: 0 Success, 1 Internal, 2 Usage, 3 UnsupportedFormat,
  4 DecryptionFailed, 5 FilesystemNotMountable, 6 MountFailed, 7 ElevationRequired,
  8 InsufficientSpace, 9 CorruptImage.
- Input is attacker-supplied: bound every declared length against real file size,
  `checked` sector arithmetic, no unhandled exception on malformed input.
- Format docs in `docs/` are **unverified**. Four have already been proven wrong
  against real images. Trust the fixture, and fix the doc in the same commit.

## Fixtures

Gitignored. Generate with `tools/make-fixtures.sh` then `tools/make-manifest.sh`
from your worktree root. `manifest.json` is a **per-generation snapshot** — hdiutil
bakes UUIDs into every image, so hashes change on regeneration. **Never pin a
committed hash in a test.** Degrade gracefully when fixtures are absent.

Passphrase: `dmg-test-passphrase` (note: hdiutil keys off the trailing newline too).

## hdiutil safety — non-negotiable

An agent once hung hdiutil and popped a GUI password dialog on the user's screen.

- Every call needs explicit stdin (`< /dev/null`, or `printf '%s\n' "$PASS" |` for
  encrypted — **newline-terminated** or hdiutil waits forever) and a hard timeout:
  `perl -e 'alarm shift; exec @ARGV' 120 hdiutil ...`
- On timeout or failure: kill, record as skipped, **never retry interactively**.
- **Only detach devices you attached**, tracked by device path, with a `trap`.
  The user has an iOS Simulator runtime and a personal volume at `/Volumes/Private`
  mounted. Broad or looping `hdiutil detach` is destructive and forbidden.
- Snapshot `hdiutil info` before; confirm device count restored after.
- `-stdinpass` on `convert` sets the OUTPUT encryption. To read an encrypted
  source: `attach -stdinpass -nomount -readonly` then `dd` the raw device.

## Git

One branch per story, chained: `story/S<id>-<slug>`.

Commit with `-c user.name="Jim Roton" -c user.email="jim.roton@live.com"`.
First line `S<id> — <title>`, body says what and why, then `Closes #<issue>`,
blank line, `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`.

**Push each branch the instant its story is committed — never batch pushes.**
Pushed work has survived every usage-limit kill; unpushed work was lost every time.

**Do NOT checkout main, merge, or open PRs.** The orchestrator merges.
Build clean + relevant tests green before each push.

## Report format — return ONLY this

No code, no diffs, no file contents, no hashes, no key material.

```
S<id> DONE|BLOCKED <branch> <sha> <one line>
BUILD: clean | N warnings
TESTS: N passed, M failed
FINDINGS: real bugs or spec discrepancies found, or "none"
DECISIONS: max 3 lines later stories must know
BLOCKERS: none | what
```
