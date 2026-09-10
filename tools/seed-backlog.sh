#!/usr/bin/env bash
# Create the GitHub epics and stories from tools/backlog.tsv.
# NOT idempotent: running twice creates duplicates. Check the repo first.
#
#   ./tools/seed-backlog.sh
#
# Requires: gh, authenticated with `repo` scope.
set -uo pipefail

REPO="${REPO:-JimRoton/Windows_DMG}"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
TSV="$HERE/backlog.tsv"
MAP="$HERE/.issue-map"          # ID<TAB>issue-number
DOCS="https://github.com/$REPO/blob/main/docs"
BACKLOG="https://github.com/$REPO/blob/main/BACKLOG.md"

: > "$MAP"

num_of() { awk -F'\t' -v k="$1" '$1==k{print $2; exit}' "$MAP"; }

# ---------------------------------------------------------------- epics
echo "==> epics"
while IFS=$'\t' read -r id epic size plat deps title acc; do
  case "$id" in \#*|"") continue ;; E*) ;; *) continue ;; esac

  body="$acc

---

<!--STORIES-->

See [BACKLOG.md]($BACKLOG) for the full table with dependencies, sizes and the
parallelisation graph.

### Definition of Done for this epic

- [ ] Every story below is closed
- [ ] The epic's bar in [testing strategy section 6]($DOCS/05-testing-strategy.md#6-coverage-expectations) is met
- [ ] CI green on macOS and Windows, including the dependency guard"

  url=$(gh issue create -R "$REPO" -t "$id — $title" -b "$body" \
        -l epic -l "$id" -l "status:todo") || { echo "FAILED $id"; continue; }
  n="${url##*/}"
  printf '%s\t%s\n' "$id" "$n" >> "$MAP"
  echo "  #$n  $id — $title"
  sleep 0.6
done < "$TSV"

# ---------------------------------------------------------------- stories
echo "==> stories"
while IFS=$'\t' read -r id epic size plat deps title acc; do
  case "$id" in \#*|""|E*) continue ;; esac

  epic_no="$(num_of "$epic")"

  # acceptance criteria: pipe-separated -> checklist
  acc_md="$(printf '%s' "$acc" | tr '|' '\n' | sed '/^[[:space:]]*$/d; s/^/- [ ] /')"

  # dependencies -> linked list
  if [ "$deps" = "-" ] || [ -z "$deps" ]; then
    dep_md="_None — can start immediately._"
  else
    dep_md=""
    old_ifs="$IFS"; IFS=','
    for d in $deps; do
      dn="$(num_of "$d")"
      if [ -n "$dn" ]; then dep_md="${dep_md}- $d (#$dn)"$'\n'; else dep_md="${dep_md}- $d"$'\n'; fi
    done
    IFS="$old_ifs"
  fi

  case "$plat" in
    core) plat_md='`core` — runs on macOS **and** Windows' ;;
    win)  plat_md='`win` — needs a Windows machine; write against `FakeVirtualDiskService`, confirmed on hardware by S10.4' ;;
    ci)   plat_md='`ci` — pipeline work' ;;
    *)    plat_md='documentation only' ;;
  esac

  body="**Epic:** $epic (#$epic_no) · **Size:** $size · **Platform:** $plat_md

### Acceptance criteria

$acc_md

### Depends on

$dep_md
### Definition of Done

- [ ] Merged to \`main\` via branch \`story/$id-<slug>\`
- [ ] Tests exist for this behaviour and the epic's coverage bar is met
- [ ] CI green on macOS and Windows, including the dependency guard
- [ ] No \`PackageReference\` added under \`src/\` ([ADR-002]($DOCS/adr/ADR-002-zero-third-party-dependencies.md))
- [ ] Any format finding that contradicts [docs/03]($DOCS/03-udif-format-reference.md) or [docs/04]($DOCS/04-encrypted-dmg-reference.md) is corrected there in the same PR

### Reference

[CLI design]($DOCS/02-cli-design.md) · [BACKLOG.md]($BACKLOG)"

  set -- -l story -l "$epic" -l "status:todo" -l "size:$size"
  [ "$plat" != "-" ] && set -- "$@" -l "plat:$plat"

  url=$(gh issue create -R "$REPO" -t "$id — $title" -b "$body" "$@") \
    || { echo "FAILED $id"; continue; }
  n="${url##*/}"
  printf '%s\t%s\n' "$id" "$n" >> "$MAP"
  echo "  #$n  $id — $title"
  sleep 0.6
done < "$TSV"

# ------------------------------------------------- backfill epic bodies
echo "==> linking stories into epic bodies"
while IFS=$'\t' read -r id epic size plat deps title acc; do
  case "$id" in \#*|"") continue ;; E*) ;; *) continue ;; esac
  en="$(num_of "$id")"
  [ -z "$en" ] && continue

  list="$(awk -F'\t' -v e="$id" -v mapf="$MAP" '
    BEGIN { while ((getline l < mapf) > 0) { split(l, a, "\t"); m[a[1]] = a[2] } }
    $1 ~ /^S/ && $2 == e { printf "- [ ] #%s — %s %s\n", m[$1], $1, $6 }
  ' "$TSV")"

  cur=$(gh issue view "$en" -R "$REPO" --json body -q .body)
  new="${cur/<!--STORIES-->/**Stories**

$list}"
  gh issue edit "$en" -R "$REPO" -b "$new" >/dev/null && echo "  #$en  $id"
  sleep 0.4
done < "$TSV"

echo
echo "Issue map: $MAP"
echo "Total issues: $(wc -l < "$MAP" | tr -d ' ')"
