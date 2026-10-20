# Build step 1: merged design sketch

Types: `src/*.cs` (402 lines, compiles under `PublishAot` with 0 warnings). Candidates: `../candidates/`. Judge: `../judge.md` (it read anonymized copies: candidate-1 = fable, 2 = sonnet, 3 = codex, 4 = opus).

## Usage (caller's view)

```sh
# Coordinator (Platform Hardening), inside ~/dev/cluckwork
softwarp run 1223                      # intake: pin issue, trunk, WORKFLOW.md@trunk, briefs, models; claim
softwarp advance cluckwork-1223        # triage (reproduce on trunk)          -> 4 waiting on agent
softwarp advance cluckwork-1223 --wait 30m   # triage done; spec written and posted with its digest
                                       #                                       -> 5 waiting on maintainer
# Maintainer, in the browser (gh is logged out on this machine):
#   /softwarp approve 7f3a9c0d41e2      or   /softwarp revise 7f3a9c0d41e2: cap at +5%
softwarp advance cluckwork-1223 --wait 90m   # implement -> gates -> cleanup -> gates -> push -> PR
softwarp status cluckwork-1223         # PR URL and exact published head      -> 0
```

Exit codes: `0` progressed or done, `2` invalid input, `3` a gate failed (repair queued), `4` waiting on an agent, `5` waiting on the maintainer, `6` another caller holds the run. There is no verb that names a transition, and no skip or force option.

```bash
# coordinator loop
until softwarp advance "$run" --wait 30m; do
  case $? in 4|3) continue ;; 5) softwarp status "$run"; break ;; *) break ;; esac
done
```

```csharp
// planner table test: no I/O, no fakes
var e = Fixtures.SpecPosted() with { Maintainer = [Comments.Approve(fromBot: true, digest)] };
Assert.IsType<NextAction.AwaitMaintainer>(Policy.Next(e, Fixtures.Workflow));   // a bot cannot approve
```

## Problem

One issue goes from intake to an open PR on a path fixed by code, while agent output is not fixed. Two callers drive it, a coordinator agent and the maintainer, and they share one Unix user. So any local file or CLI path is forgeable, and the CLI must not trust its caller to choose the next step. Native AOT rules out reflection-based JSON and plugin loading.

## Shape

**The next step is derived, never stored.** `AdvanceAsync` gathers fresh evidence (immutable receipts, git, forge), and the pure `Policy.Next` returns one `NextAction` from a 14-row table. A crash leaves facts that make `Next` return the same step or the next one. Before each external effect, a durable intent is written with a stable operation id, so a retry reconciles instead of acting twice. All four candidates reached this shape on their own.

**One home per fact.**

| Fact | Home |
|---|---|
| Pins (issue revision, trunk, WORKFLOW.md at trunk, brief and skill hashes, models, bindings) | immutable run manifest |
| Agent results, retries, bounces | write-once attempt receipts, keyed by input digest and epoch |
| Gate results | receipts keyed by head and command digest; valid only if the head did not move and the tree stayed clean |
| Code, branch, head | git |
| Maintainer decisions (approve, revise, resume) | forge comments, with author facts from the forge |
| PR | the forge, found by branch |

Labels, `progress.md` and `decisions.tsv` are views. They are written but never read to decide anything.

**Approval cannot be forged locally.** An approval counts only if all of these hold:
- the forge reports it as a comment by a non-bot account with an admin or owner role that matches the pinned maintainer id;
- nobody else edited it;
- it was posted after the spec;
- it quotes the current package digest.

The digest covers the spec, the tickets, the holdouts and the run pins. Any change voids the approval and every receipt downstream of it.

**Gates are judged by things agents cannot edit.** The commands come from `WORKFLOW.md` at the trunk commit. The targeted-test filter comes from the approved spec. A targeted test that ran zero tests fails.

**Holdouts are absent by type.** The implementer receives `ImplementationInput`, which has no holdout field.

**Records are schemas.** Source-generated records give both the JSON schema (passed to the harness's structured-output flag) and the strict parser. Semantic rules sit in `Policy.Validate`.

**Bindings sit outside the core.** `Model`, `Policy`, `Capabilities` and `Cli` name no tool. The harness (argv, schema flag, output envelope) is split from the agent host (Paseo tab or child process).

## Synthesis decision

**Base: the Codex candidate.** The judge scored it 25/30 and I scored it 27/30. The judge is also Codex, but my independent scoring agreed. It had the strongest receipts (each one bound to its inputs and to the gate command), crash reconciliation, and portability: it is the only candidate whose core names no tool.

**Grafts:**
- From fable: the usage walkthrough, the exit-code loop for the coordinator, `--wait`, and a numbered planner table that doubles as the test list.
- From sonnet: strict JSON (required members and nullable annotations enforced at runtime), test targets in the approved spec, and an implementer input that has no holdout field.
- From opus:
  - Approval decided by the forge role and edit history, not by local config.
  - Budget epochs reset by `/softwarp resume`.
  - Gate receipts that require an unmoved head and a clean tree.
  - A targeted test that ran zero tests counts as a failure.
  - Gate commands read from the trunk commit.

**Bug fixed:** the base re-ran a failed gate on the same head. Row 10 now routes a red gate to repair before row 11 can re-run the gates. `WaitAsync` now has a deadline and `StopAsync` exists.

**Dropped:**
- Codex's `approval --receipt <url>` verb: the factory now scans the item's comments, so you only comment.
- Codex's YamlDotNet dependency: replaced by fable's JSON front matter, pending open question 2.
- Sonnet's and opus's hand-written YAML subsets.

## Tradeoffs accepted

- We accept browser-only approval in exchange for consent that no local process can mint.
- We accept a few forge reads on every `advance` in exchange for having no stored state that can drift.
- We accept one host and one run lock in exchange for a small recovery model. A second host is unsupported.
- We accept that a missing run manifest stops the run instead of being rebuilt from labels.

## Alternatives considered

- **A stored state field** (`run.json: station: 3`). Every crash window between an effect and the state write needs its own repair path. All four candidates rejected it.
- **One verb per transition** (`softwarp triage`, `softwarp approve`). This exposes the station graph to callers, and a local `approve` is forgeable.
- **An event-sourced journal.** It is a second copy of facts that git and the forge already own authoritatively.

## Deviations from DESIGN.md

Applied 2026-10-20: §8 labels are views, §11 JSON front matter, §12 approval by forge comment only.

## Decisions (2026-10-20, DESIGN.md §18 rows 11–16)

1. **Approval:** a browser comment `/softwarp approve <digest>` for every spec.
2. **`WORKFLOW.md` syntax:** JSON front matter.
3. **Isolation:** harness sandboxes from step 1; agents never run Docker; a separate OS user only if needed.
4. **Branch names:** the repo's convention, from `WORKFLOW.md`, with the issue number.
5. **One PR per run.**
6. **Build, not adopt Gas City.** Borrowed: exit 75, fail-closed gates, the runtime protocol for `spawn`, adopting live agents.

## Next implementation step

Implement `Policy.Next` as real code, with one table test per row and fault-injection tests: a crash after the claim, after a worker launch, after a push but before the PR, and on a red gate on an unchanged head.
