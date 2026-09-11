#!/usr/bin/env python3
"""Reconcile the board with reality: closed stories -> Done; epics whose
sub-issues are all closed -> Done and closed. Safe to run any time."""
import json, subprocess
R, P = "JimRoton/Windows_DMG", "PVT_kwHOAwPTUM4BjAmC"
F, DONE = "PVTSSF_lAHOAwPTUM4BjAmCzhh2jYY", "98236657"
def sh(*a): return subprocess.run(a, capture_output=True, text=True).stdout
def gql(q): return json.loads(sh("gh","api","graphql","-f","query="+q))
items = json.loads(sh("gh","project","item-list","4","--owner","JimRoton","--limit","300","--format","json"))["items"]
board = {i["content"]["number"]:(i["id"], i.get("status")) for i in items if i.get("content",{}).get("number")}
state = {i["number"]:i["state"] for i in json.loads(sh("gh","issue","list","-R",R,"--state","all","--limit","300","--json","number,state"))}
def done(n):
    gql('mutation{updateProjectV2ItemFieldValue(input:{projectId:"%s",itemId:"%s",fieldId:"%s",value:{singleSelectOptionId:"%s"}}){projectV2Item{id}}}'%(P,board[n][0],F,DONE))
    sh("gh","issue","edit",str(n),"-R",R,"--remove-label","status:todo","--remove-label","status:in-progress","--add-label","status:done")
    sh("gh","issue","close",str(n),"-R",R)
moved=[]
for n,(_,st) in board.items():                                   # stories
    if n>10 and state.get(n)=="CLOSED" and st!="Done": done(n); moved.append(n)
for n in range(1,11):                                             # epics
    subs=[x["number"] for x in gql('{repository(owner:"JimRoton",name:"Windows_DMG"){issue(number:%d){subIssues(first:50){nodes{number}}}}}'%n)["data"]["repository"]["issue"]["subIssues"]["nodes"]]
    if subs and all(state.get(s)=="CLOSED" or s in moved for s in subs) and board[n][1]!="Done":
        done(n); moved.append(n)
print("moved to Done:", " ".join("#%d"%m for m in moved) or "nothing — already in sync")
