#!/usr/bin/env bash
# Move issues on the Windows_DMG Projects v2 board and keep labels in sync.
#
#   ./tools/board.sh todo    11 12 13
#   ./tools/board.sh wip     11 12 13
#   ./tools/board.sh done    11
#   ./tools/board.sh show
#
# Requires: gh with `project` scope.
set -uo pipefail

REPO="JimRoton/Windows_DMG"
PROJ="PVT_kwHOAwPTUM4BjAmC"
FIELD="PVTSSF_lAHOAwPTUM4BjAmCzhh2jYY"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CACHE="$HERE/.item-map"          # issue-number<TAB>project-item-id

case "${1:-}" in
  todo) OPT=f75ad846; LBL=status:todo ;;
  wip)  OPT=47fc9ee4; LBL=status:in-progress ;;
  done) OPT=98236657; LBL=status:done ;;
  show)
    gh project item-list 4 --owner JimRoton --limit 200 --format json \
      | python3 -c "
import json,sys,collections
items=json.load(sys.stdin)['items']
c=collections.Counter(i.get('status','(none)') for i in items)
for k in ('Todo','In Progress','Done','(none)'):
    if c[k]: print(f'{k:12} {c[k]}')
wip=[i for i in items if i.get('status')=='In Progress']
if wip:
    print('\nIn Progress:')
    for i in wip: print('  #%s %s' % (i['content']['number'], i['content']['title']))
"
    exit 0 ;;
  *) echo "usage: board.sh {todo|wip|done|show} [issue...]" >&2; exit 2 ;;
esac
shift

# Build / refresh the issue-number -> project-item-id cache.
if [ ! -s "$CACHE" ] || [ -n "${REFRESH:-}" ]; then
  gh project item-list 4 --owner JimRoton --limit 200 --format json \
    | python3 -c "
import json,sys
for i in json.load(sys.stdin)['items']:
    c=i.get('content') or {}
    if c.get('number'): print('%s\t%s' % (c['number'], i['id']))
" > "$CACHE"
fi

for n in "$@"; do
  item=$(awk -F'\t' -v k="$n" '$1==k{print $2; exit}' "$CACHE")
  if [ -z "$item" ]; then echo "  ?? #$n not on board" >&2; continue; fi
  gh api graphql -f query="mutation{updateProjectV2ItemFieldValue(input:{
      projectId:\"$PROJ\" itemId:\"$item\" fieldId:\"$FIELD\"
      value:{singleSelectOptionId:\"$OPT\"}}){projectV2Item{id}}}" >/dev/null 2>&1 \
    && echo "  #$n -> $LBL" || echo "  #$n FAILED" >&2
  gh issue edit "$n" -R "$REPO" \
     --remove-label status:todo --remove-label status:in-progress --remove-label status:done \
     --add-label "$LBL" >/dev/null 2>&1
  # keep issue state in lockstep with the board so the two cannot drift
  case "$LBL" in
    status:done) gh issue close "$n" -R "$REPO" >/dev/null 2>&1 ;;
    *)           gh issue reopen "$n" -R "$REPO" >/dev/null 2>&1 ;;
  esac
done
