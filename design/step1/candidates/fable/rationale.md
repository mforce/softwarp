# Build step 1: `factory` CLI, stations 0–4

Candidate: fable. Type sketch in `src/`, module map in `module-map.md`.

## Problem

One issue must go from intake to an open PR along a path that is fixed by code while the agents' output is not. Two actors drive the same run through the same CLI, an LLM coordinator and the maintainer, so the CLI cannot trust its caller to pick the next transition. State is split by the spec across tracker labels, a run directory and git, and the obvious design (a `state.json` with a `current_station` field) adds a fourth copy that drifts from the other three the first time a process dies between two writes. Native AOT forbids reflection JSON and the BCL has no YAML. The pilot is Cluckwork #1223, whose acceptance criteria are checkable mutations.

## Usage (caller's view)

The coordinator runs inside `~/dev/cluckwork`; the repo slug comes from `origin`. Exit codes are the coordinator's branch point: 0 done, 2 waiting on an agent, 3 waiting on the maintainer, 1 error.

```
$ factory run 1223
claimed mforce/cluckwork#1223 -> ~/.factory/runs/mforce/cluckwork/1223   branch factory/1223-legacy-file-growth-caps @ main 9441aef1
pinned  WORKFLOW.md@9441aef1  briefs triage:3f1a spec:b07c implement:9e22  models cheap=haiku standard=sonnet strong=opus  factory 0.1.0
label   factory:active   comment #c1
triage  spawned claude (standard, read-only) in ~/.factory-slices/mforce/cluckwork/1223
        log   ~/.factory/runs/mforce/cluckwork/1223/children/triage-1.log      watch  tail -f <log>      expect ~8m
WAITING triage-1 (0m of 8m)                                                                                        exit 2

$ factory advance 1223 --wait 20m
triage-1  valid: ready-for-agent, bug, S, two-way, reproduced on main (FileSizeLimitTests.cs:41 keeps names only)
spec      spawned claude (strong) /to-spec ... expect ~15m
spec-1    valid: 4 criteria, 3 tickets, 2 holdouts -> spec.md (digest 7f3a9c0d41e2), holdout/
posted    spec to #1223 as comment #c2
BLOCKED   awaiting `approve 7f3a9c0d41e2` or `revise 7f3a9c0d41e2: <feedback>` from mforce on #1223               exit 3
```

The maintainer reads comment #c2 in the browser (their browser session, not `gh`, which is logged out) and replies `approve 7f3a9c0d41e2`.

```
$ factory advance 1223 --wait 90m
approval   comment #c3 by mforce (User) matches 7f3a9c0d41e2 -> decisions.tsv
implement  spawned paseo (standard) tab "worker · #1223" id 3f9e...   watch  paseo logs 3f9e   expect ~45m
implement-1 finished; report valid (4/4 criteria met)
gates @ 1c2d3e  build ok 2m10s   test_targeted ok 41s
pushed     factory/1223-legacy-file-growth-caps   PR #1240 opened   label factory:review
DONE       https://github.com/mforce/cluckwork/pull/1240                                                           exit 0
```

Had `test_targeted` been red, the same call would have printed `BOUNCE implement-1: test_targeted exit 1 (log ...)` and come back with exit 2; a second red at the fix commit escalates with a questionnaire comment and `factory:needs-human`.

Call sites:

```bash
# coordinator loop
until factory advance 1223 --wait 30m; do rc=$?; [ $rc -eq 3 ] && { factory status 1223; break; }; done

# maintainer, anywhere: what is it waiting on, and what did it decide
factory status 1223
factory status 1223 --json | jq .next

# a planner test: no I/O, one Observation in, one Move out
var o = Fixtures.AfterSpecPosted() with { Approval = new(ApprovalKind.Revise, "7f3a9c0d41e2", 99, "mforce", "cap at +5%") };
Assert.Equal(new Spawn(Station.Spec, "cap at +5%"), Planner.Next(o));
```

## Shape

**The run has no stored state; the station is derived from facts.** `Observation` (`Observation.cs`) is a snapshot of every fact a run can leave behind: `run.json`, the three artifact files, the maintainer's approval comment, the newest child's live status, the branch head and ahead-count, recorded gate results, the PR. `Planner.Next(Observation) -> Move` is a pure decision table: the first row whose condition holds names the one legal move. `Driver` loops Observe -> Next -> Execute until the move is `Wait`, `Blocked`, `Stopped`, `Escalate` or `Done`. Idempotency falls out: after a crash, re-observing finds whatever did land and the table picks up from there; nothing has to be rolled back because nothing is cached. This is "derive instead of sync" applied to the whole run.

**Refusal is structural, not a check.** The CLI has `run`, `advance`, `status`, `schema`. There is no verb that names a transition, so a coordinator cannot ask to skip approval; `advance` will post the spec and return exit 3 until a matching approval exists. Rung 0 (`--step`) executes one move per call; rung 1 loops.

One source of truth per fact (labels are a projection, written by `Execute`, never read to decide):

| Fact | Source | Projection |
|---|---|---|
| claimed, pins, branch, base SHA | `run.json` (written once) | `factory:active` + run comment |
| triage verdict | `triage.json`, validated on every read | `factory:needs-human` / `needs-info` / `wontfix` |
| spec text, holdouts | `spec.json` -> rendered `spec.md`, `holdout/` | issue comment carrying the digest |
| spec approved or revise | tracker comment by maintainer login, non-bot, quoting the digest | `decisions.tsv` row |
| attempts, bounces, revisions | row counts in `children.tsv` and `decisions.tsv` | — |
| implementation exists | git: head, commits ahead of `BaseSha` | — |
| gates passed | `gates.jsonl` rows keyed by SHA | — |
| PR open | forge lookup by branch | `factory:review` |

**Artifacts are records, never files the agent writes.** Each agent station returns one JSON object as the harness's structured output; `FactoryJson` (source-generated, the only JSON path under AOT) parses it, and `JsonSchemaExporter` turns the same record into the `--json-schema` / `--output-schema` the brief hands the agent. One type is both contract and parser; markdown (`spec.md`, PR body) is rendered deterministically from the record, so the spec digest the maintainer approves is a hash of bytes the factory produced. Semantic rules a schema cannot say (`Ready` requires `ReproducedOnMain`) live in `IArtifact.Check`; shape and meaning failures both yield `Invalid<T>` with reasons, which the retry brief quotes. Raw output is kept even when invalid, as evidence (per boundary-discipline: validate once at the edge, trust `Valid<T>` inside).

**Approval cannot be forged from inside the factory.** The orchestrator and every agent hold only the GitHub App installation token, which authors comments as `<slug>[bot]`. `Approval.Find` accepts a comment only from the configured maintainer login, with author type User, quoting the current digest. `decisions.tsv` records the approval; it is not a source, because agents run as the maintainer's Unix user and could write it.

**Capabilities are four interfaces with one job each**: `ITracker` (no `create` member, so the never-file-issues policy is an absent method), `ISpawner` (Paseo tab, or one `HeadlessSpawner` whose two argv shapes are Claude and Codex; `Send` prefixes `[coordinator] ` itself), `IGit` (`Push(Worktree)` has no refspec slot and checks the current branch), `IGateRunner` (command in, exit code and log out). Tests pass fakes to `Driver`; `Planner` tests need no fakes at all. `AppToken` is a class, not an interface: nothing but `GitHubTracker` and `GitCli` consume it.

**WORKFLOW.md front matter is JSON between the `---` fences.** JSON is YAML 1.2, so a Symphony-format reader still parses the file, and the factory needs no YAML dependency or static generator. The `Workflow` record is the only thing that changes if the maintainer later wants YAML syntax.

Interface depth: the public surface is four verbs and an exit code. Behind it sit pinning, worktree creation, brief rendering, schema export, structured-output collection, two-layer validation, approval matching, stall detection, gate runs keyed by SHA, bounces with attempt counting, push and PR creation. The coordinator learns none of it. What stays exposed on purpose: the digest (the maintainer has to type it) and the watch command for every child (dev-rules background visibility).

## Synthesis decision

n/a (single candidate)

## Tradeoffs accepted

- We accept re-reading the tracker, run dir and git on every `advance` and `status` in exchange for never having a stored station to keep consistent. Cost: a few API calls per call; fine at one run.
- We accept that `decisions.tsv` is a record, not an approval source, in exchange for an approval the App token cannot produce. The maintainer approves in the browser, not in the shell.
- We accept JSON front matter (valid YAML, odd to the eye) in exchange for zero YAML code under AOT.
- We accept that attempt and bounce counts are row counts rather than fields, in exchange for one append-only log that is also the audit trail. A deleted row changes a count; the run dir is not adversarial.
- We accept a step-1 `Observation` with 13 fields in exchange for a planner that is a pure function and fully unit-testable without fakes.
- We accept that the implementer, not a separate publish agent, runs deslop and writes the PR body (`ImplementReport`), in exchange for one agent, one worktree, one artifact per station.

## Alternatives considered

- **Stored FSM: `state.json` with `current_station`, each command a transition.** Exposes transition names to the caller (so refusal becomes a check against a table the coordinator can see), and adds a fourth copy of state that drifts after a crash. Hides less: the caller has to know which transition is next.
- **Long-running daemon polling the tracker (Symphony shape).** Right for step 5 scheduling; in step 1 it adds a process to supervise and hides the coordinator's turn-taking, which is the thing the pilot is meant to exercise. The `Driver` loop is the daemon's body later; nothing here blocks it.
- **Markdown artifacts with required headings, written by the agent into the run dir.** Two validators (JSON and heading scan), trust in the agent's file writes, and no schema to hand the harness. Lost on depth: the record-as-schema gives one validator and a deterministic digest.
- **YamlDotNet with the static generator for WORKFLOW.md.** A dependency and a generator for six keys. Lost on size alone; `Workflow.Parse` stays if the maintainer overrules.

## Open questions and risks

1. Is browser-only approval acceptable, given `gh` is logged out? If the maintainer wants `factory approve` in the shell, what distinguishes them from an agent on the same Unix user: a TTY check, a passphrase, or nothing (accepting forgeability)?
2. Does `paseo run --output-schema` deliver the structured output through `paseo inspect --json`? The sketch assumes yes; if not, the Paseo implementer returns its `ImplementReport` by a final `claude -p` turn in the worktree.
3. Should `MaintainerLogin` come from `bindings.json` (as sketched) or from the forge (`collaborators/{login}/permission == admin`)? The forge is stronger but the App may lack the permission to ask.
4. Triage is marked read-only via `--permission-mode plan` / `-s read-only`; Bash under plan mode is still a judgment call by the harness. Is a dirty worktree after triage (`Branch.Clean == false` at Implement spawn) a reason to reset, or to escalate?
5. Does the maintainer accept JSON front matter in `WORKFLOW.md`, or is YAML syntax a requirement that justifies the dependency?
6. Risk: `Revisions` is derived as spec spawns minus one; a Replace of a stalled spec child inflates it. If this bites, add a `Kind` column to `children.tsv` rather than a counter.

## Next implementation step

Write `Planner.Next` as real code with the fixture builder from the test call site above, and make the 15-row table pass as 15 tests before any binding exists.
