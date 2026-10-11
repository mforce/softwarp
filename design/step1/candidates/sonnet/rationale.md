# Step 1 design, candidate "sonnet": derive the phase, never store it

## Problem

One issue goes through stations 0–4 under a deterministic C# Native AOT CLI. Two callers drive it: the Platform Hardening coordinator (an agent) and the maintainer. They run as one Unix user, so any file or CLI path the coordinator can reach is forgeable. The constraints that shape the design: the path is fixed and agent output is not; every artifact is schema-checked; only the orchestrator pushes, labels and comments; an approval must be unforgeable by the coordinator; stations are idempotent and resume from the label, branch, PR and run dir; stations 5–7, `doctor`, scheduling and stall detection must fit in later.

## Usage (caller's view)

The maintainer's and coordinator's session for Cluckwork #1223. Full-form names shown; `1223` alone works inside the clone.

```
$ factory run mforce/cluckwork#1223
claimed mforce/cluckwork#1223 · pinned WORKFLOW.md@7c1e9a0, factory 0.1.0 · label factory:active
$ factory advance mforce/cluckwork#1223 --follow 900
launched triage#1 (paseo 3f2a, standard, ~5m)   watch: paseo logs 3f2a
ingested triage#1: feature · M · two-way · ready-for-agent
launched spec#1 (paseo 81c4, strong, ~10m)      watch: paseo logs 81c4
ingested spec#1: 1 ticket · 6 criteria · 4 holdouts
posted spec to #1223 (digest 3fa9c1d2e4b5)
waiting on maintainer: comment "/approve 3fa9c1d2e4b5" on mforce/cluckwork#1223        # exit 11
```
The maintainer reads the spec comment on GitHub and posts `/approve 3fa9c1d2e4b5`. A different edit request is `/revise keep one legacy list`.
```
$ factory advance mforce/cluckwork#1223 --expect approval --follow 3600
approval seen: comment 4412 by mforce, digest matches
launched implement#1 …  ingested implement#1 (head 9b2e1f0)
gate build exit 0 · gate test_targeted exit 0 @ 9b2e1f0
launched publish#1 …    ingested publish#1 (head c40a7de)
gate build exit 0 · gate test_targeted exit 0 @ c40a7de
pushed factory/1223 · opened PR #1231 · label factory:review
finished: stations 5–7 are not built, the run stops here                              # exit 0
```

Call site 1, the coordinator: loop on `factory advance <ref> --json --follow 900` and branch on the exit code only (0 progressed or finished, 10 agent running, 11 waiting on the maintainer, 12 halted, 13 `--expect` did not match). It never names a target state, so it cannot request an illegal one.

Call site 2, a scenario test with fakes:
```csharp
var h = Harness.With(new FakeTracker(Issue1223), ScriptedSpawner.Returns(
    (Station.Triage, Fixtures.ReadyTriage), (Station.Spec, Fixtures.Spec1223),
    (Station.Implement, Fixtures.Impl), (Station.Publish, Fixtures.Draft)));
await h.AdvanceUntil(Outcome.WaitingOnMaintainer);
h.Tracker.Comment("cluckwork-bot[app]", $"/approve {h.SpecDigest.Short}");    // a bot: ignored
await h.Advance(); Assert.Equal(Outcome.WaitingOnMaintainer, h.Last.Outcome);
h.Tracker.Comment("mforce", $"/approve {h.SpecDigest.Short}");
await h.AdvanceUntil(Outcome.Finished);  Assert.Equal(FactoryLabel.Review, h.Tracker.Label);
```

Call site 3, a pure table test: `Machine.Plan(observed)` with an `Observed` built by hand and no I/O. Checks: a stale gate receipt gives `RunGate`; a gate that exited 1 gives `Bounce(Gate)`; two schema rejections give `Halt(Escalated)`; a ledger edited after approval gives `PostSpec` again.

## Shape

**One function is the state machine.** `Machine.Plan(Observed) → Decision(Phase, Step)` is pure. There is no cursor, no transition table and no `state.json`. `Engine.Advance` is Observe → Plan → Execute → set label. `advance` takes no target, so "refuse an invalid transition" is by construction: Plan returns a wait or halt (per *encode-lessons-in-structure*, *make-operations-idempotent*).

**Each fact has one home** (per *single source of truth*; the rest is derived):

| Fact | Home | Derived from it |
|---|---|---|
| Claim | marker comment by the App identity | `IssueSnapshot.ClaimedByRun` |
| Agent results, rejections, bounces | `ledger/NNN-station-attempt.json`, write-once | `Ledger.Current<T>()` |
| Run pins, per-call pins | `pins.json`, `LedgerEntry.Pins` | replay |
| Gate results | `gates/*.json` keyed by head SHA | fresh only while head is unchanged |
| Approval | maintainer comment on the tracker | never copied; `decisions.tsv` only logs |
| Branch, head, pushed or not, PR | git and forge, re-read each time | never stored |
| Phase, `factory:*` label | nowhere | label = `Projection.Label(Plan(...))`, rewritten each advance |

**Artifacts.** `IArtifact<T>` binds each record to its station (`static abstract Home`) and to its source-generated `JsonTypeInfo`. The same record gives the schema (`JsonSchemaExporter`) and the parse. Probed on .NET 10: the exporter works over source-gen types; the default parser accepts `{"class":"feature"}` with null members, so `FactoryJson.Strict` turns on `RespectRequiredConstructorParameters` and `RespectNullableAnnotations` (runtime options, not attribute options). Invalid → `Problems` quoted in a retry brief → a second invalid artifact → `Halt(Escalated)`. Semantic checks the schema cannot state live in `Problems()`; checks that need git or a shell live in `Execute`.

**Agents.** `ISpawner` is start / find / poll, never blocking, so a crashed CLI loses nothing: `Find` re-attaches by label or output file. `AgentCall` has no token field: the least-privilege rule is the absence of a field. Briefs are typed per station; `ImplementInput` takes `Spec`, not `SpecBundle`, so holdouts cannot reach the implementer. Holdouts are stored outside the worktree and appear in the issue only as id + hash.

**Approval.** The spec is rendered once (`SpecReview.Render`), posted by the App, and its SHA-256 is the digest. An approval is a comment that: (1) GitHub says was written by a `User` in the machine's maintainer set (`bindings.yaml`, not the repo); (2) reads `/approve <digest>` matching the current ledger; (3) comes after the spec comment. The coordinator's App token cannot write as the maintainer, and `gh` is logged out. `decisions.tsv` is excluded: a process running as this Unix user can write it (DESIGN §12 allowed it; I deviate). Plan recomputes the digest on every advance, so tampering with the ledger after approval voids the approval.

**Interface depth.** `Engine` has four public methods. Behind them: observation, a retry and escalation policy shared by four stations (`Slot`), proof freshness, idempotent push and PR open, label repair, pin recording. Callers learn five verbs and five exit codes. The capability interfaces carry domain types only; GitHub JSON, Paseo JSON and `claude -p` envelopes stay inside bindings.

**WORKFLOW.md.** Symphony format, read from the pinned commit on `origin/main`, never from a worktree, so an implementer's PR cannot rewrite the commands that judge it. The front matter is a restricted YAML subset (maps, lists, scalars, `|` blocks) read by a ~120-line `MiniYaml`; unknown top-level keys are kept, not rejected. The same parser reads `bindings.yaml`.

**Tests.** Fakes for the five interfaces (`Caps`), a real temp dir for `RunStore`, scripted agent outputs. See `module-map.md`.

## Synthesis decision

n/a (single candidate)

## Tradeoffs accepted

- We accept 3–4 forge reads and a git inspect on every advance, with no cache, in exchange for zero drift between cursor and world.
- We accept that the maintainer approves in the GitHub UI (not from a terminal) in exchange for an approval the coordinator cannot forge.
- We accept a restricted YAML subset and a hand-written parser in exchange for no dependency in the trust base. Anything outside the subset fails with a line number.
- Labels are written but never read for decisions (only the claim marker is). That looks like an oversight; it is the point. `factory:ready` is unused until the unattended claimer exists.
- One implementer takes all tickets. `BlockedBy` only orders the brief. Per-ticket agents later add a ticket id to `AgentKey`.
- No override flags. A stuck run ends through `/revise` or `factory stop`, never through a skipped gate.
- A failed gate spends one of the two machine attempts; a maintainer `/revise` spends none.

## Alternatives considered

- **Stored cursor plus transition table.** Reads simply, but cursor, ledger, label and branch can disagree after a crash, and every disagreement needs repair code. Derived phase has no such state to repair.
- **One class per station owning launch, ingest and gate.** Small interfaces per station, but retry, escalation, freshness and bounce rules would be copied four times, and `advance` would become a loop over stages (temporal decomposition). `Slot` keeps them in one place.
- **Approval by `factory approve` writing `decisions.tsv`.** Best terminal UX; forgeable by the coordinator. Rejected on the threat model.
- **YamlDotNet with the static generator.** Handles all of YAML; adds a dependency and generator step for about 15 lines of config. Revisit if the subset hurts.

## Open questions and risks

1. Is approving with `/approve <digest>` in the web UI or mobile acceptable, given `gh` is logged out? A terminal path needs a credential the coordinator cannot reach.
2. For a feature such as #1223, is `Repro: NotApplicable` plus gap evidence enough, or must triage always produce a re-runnable command?
3. Branch `factory/<n>` and slice root `~/.factory-slices` follow DESIGN, not Cluckwork's `feat/<n>-slug`. Should the branch prefix come from `WORKFLOW.md`?
4. `CallPins.SkillVersions` needs a read of each harness skill dir, which is `doctor` work. Is a hand-written map in `bindings.yaml` acceptable for step 1?
5. The interactive `grill-with-docs` approval session becomes `/revise` comments. Does that lose something the maintainer wants?
6. `Trust: 0` is treated as 1 in step 1. Is that right?

## Next implementation step

Write `Machine.Plan`, `Slot` and `Signals.Latest` with table tests over hand-built `Observed` values (no I/O), then `MiniYaml`.
