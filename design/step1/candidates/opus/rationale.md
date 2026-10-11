# Step 1 candidate (opus): a level-triggered reconciler

## Problem

One issue must pass stations 0–4 under a deterministic CLI that two different drivers trigger: a coordinator agent and the maintainer. Both run as the same Unix user, so any local file, flag or config value can be written by an agent. The pilot failed exactly there: approvals, reviews and redesigns could not be traced to an author. Facts are already spread over three homes (tracker, run dir, git), stations must be idempotent and resumable, and the binary is Native AOT, so JSON needs source generation and YAML has no built-in parser.

## Usage (caller's view)

The coordinator and the maintainer learn three commands. `advance` is the only command that changes the path, and it takes no target: the CLI decides the step. An invalid transition cannot be requested, so it never has to be refused.

```text
# Coordinator (Paseo tab "★ Platform Hardening")
$ factory advance mforce/cluckwork#1223
station 0  claimed mforce/cluckwork#1223 (label factory:active)
           worktree ~/.factory-slices/cluckwork/1223  branch factory/1223  fork main@9441aef1
station 1  started triage-1  paseo a41f… · claude/sonnet · read-only
           output ~/.factory/runs/cluckwork/1223/agents/triage-1/
           watch  paseo logs a41f… -f        expect ≤ 10 min
waiting on agent triage-1                                              [exit 10]

$ factory advance mforce/cluckwork#1223 --wait
station 1  triage-1 valid: ready-for-agent · chore · S · two-way · premise holds at 9441aef1
station 2  started spec-1  paseo 9b20… · claude/opus · read-only      expect ≤ 25 min
station 2  spec-1 valid: 5 criteria, 1 ticket, 4 holdouts (kept in the run dir)
           posted spec 3f9a2c1d7b04 → github.com/mforce/cluckwork/issues/1223#issuecomment-…
waiting on maintainer: on #1223 comment `/factory approve 3f9a2c1d7b04`
                       or `/factory revise <what to change>`           [exit 11]

# Maintainer, in the browser on #1223 (gh is logged out; the browser is the only maintainer identity)
/factory approve 3f9a2c1d7b04

# Maintainer or coordinator: read-only, never takes the lock
$ factory status mforce/cluckwork#1223
mforce/cluckwork#1223  label factory:needs-human → factory:active on next advance
  1 triage    ✓ triage-1   ready-for-agent
  2 spec      ✓ spec-1     3f9a2c1d7b04 approved by @mforce (comment 2399…, unedited)
  3 implement · next step: start implement-1

$ factory advance mforce/cluckwork#1223 --wait
station 3  started implement-1  paseo · claude/opus · worktree-write   expect ≤ 60 min
station 3  implement-1 exited: 3 commits, head 5c0e77a2, tree clean
           gate build          ✓ 2m14s  dotnet build Cluckwork.slnx
           gate test_targeted  ✓ 41s    dotnet test tests/Cluckwork.Application.Tests --filter 'FullyQualifiedName~FileSizeLimitTests'
station 4  clean-1 (deslop) → head 81d2f0c4; gates ✓ at 81d2f0c4
           pushed factory/1223 @ 81d2f0c4 as mforce-factory[bot]
           prbody-1 (unslop) valid
           opened PR #1241 "test: cap legacy files at size+10%, test files at 1,500" (Refs #1223)
published https://github.com/mforce/cluckwork/pull/1241 (label factory:review)   [exit 0]
```

When a gate fails twice, the same command ends with `escalated: implement failed gates after 2 revisions; questionnaire on #1223 [exit 11]`. The maintainer answers with `/factory resume <answers>` on the issue, and the next `advance` starts a new budget epoch with those answers in the brief.

**Call site 1: the coordinator's loop** (its brief, not C#):

```text
r = factory advance <run> --wait --json
0  → tell the maintainer r.halt.url (PR open); stop
11 → tell the maintainer r.halt.ask and r.halt.url; stop until they say "continue"
3  → report r.halt.reason (claimed elsewhere, issue closed); stop
4  → another driver holds the run; run `factory status` and stop
```

**Call site 2: the advance loop** (`Reconciler.Advance`, the whole shell):

```csharp
for (var i = 0; i < MaxSteps; i++) {
    var facts = await Observe(ct);
    var step = Process.Next(facts);              // pure
    await forge.SetFactoryLabel(run, Process.Label(facts, step), ct);
    if (step is Step.Halt h) {
        if (wait && h.Reason is HaltReason.OnAgent a) { await agents.Wait(a.Handle, ct); continue; }
        return report.With(h);
    }
    await Execute(step, facts, ct);              // one side effect, then observe again
}
```

**Call site 3: an approval-forgery test** (pure, no fakes):

```csharp
var f = Scenario.SpecPosted("3f9a2c1d7b04…")
    .Comment(Bot("mforce-factory"), "/factory approve 3f9a2c1d7b04");
Assert.Equal(new HaltReason.OnMaintainer(Ask.ApproveSpec, f.SpecUrl), ((Step.Halt)Process.Next(f)).Reason);
```

## Shape

**No stored station.** Nothing records "the run is at station 3". Every `advance` observes the world into one immutable `Facts` snapshot, and `Process.Next(Facts) → Step` decides the next step. The function is `Intake(f) ?? Triage(f) ?? Spec(f) ?? Implement(f) ?? Publish(f) ?? Halt(Published)`, which reads like the station table. Each step does exactly one side effect, then the loop observes again. Crash-resume and double-run safety come from this shape rather than from extra code. A crash at any point leaves facts that make `Next` return the same step or the next one (per make-operations-idempotent). Labels and `progress.md` are written projections that the factory never reads back. Even the claim is a marker comment, not a label.

**One home per fact, chosen by who can author it** (per separate-before-serializing-shared-state):

| Fact | Home | Why there |
|---|---|---|
| maintainer decisions (approve, revise, resume) | forge comments by a non-bot `OWNER` | the only identity agents cannot write |
| claim, posted spec, escalation notices | bot comments with `<!-- factory:… -->` markers | ordered on the same timeline as the maintainer's answers, so no clock skew |
| agent attempts and their raw output | `runs/<repo>/<n>/agents/<job>-<k>/` | an attempt dir is created with mkdir before the spawn; counting dirs gives the retry budget |
| code, fork point, head | git (`factory/<n>` branch, `merge-base`) | already authoritative |
| gate evidence | `gates/<sha>/<gate>.json` | keyed by SHA, so stale proof is unrepresentable |
| PR | forge, found by head branch | |

**Approval cannot be forged by an agent.** The CLI has no `approve` command. `Timeline.From(comments)` is pure core policy, not binding code. It counts `/factory approve <digest>` only from a non-bot author whose forge role is `Admin`: `author_association == OWNER` on a user-owned repo. The comment must be unedited, or edited only by its author, and the digest must match the current `SpecBody` digest. Changing the spec voids the approval. DESIGN's "maintainer's own words in `decisions.tsv`" is deliberately not accepted, because every agent can write that file.

**Records are the schemas.** Each artifact is a C# record implementing `IArtifact<T>`. `JsonSchemaExporter` turns its source-generated `JsonTypeInfo` into the `--json-schema` / `--output-schema` the harness enforces. A probe on this sketch printed the `TriageVerdict` schema with every member `required`, nullable members as `["string","null"]` and `additionalProperties:false`, which is the form Codex strict mode needs. The same context rejected an unknown member. `Artifact.Read<T>` parses with those rules, then runs `Problems()` for cross-field rules. The result is `ArtifactState<T>`: `Missing`, `Invalid(problems)` or `Valid(value, digest)`. Validation happens once, at that boundary (per boundary-discipline). There is no `schemas/` directory to drift; a snapshot test pins the exported schemas.

**Gates come from places agents cannot edit.** The gate commands come from `WORKFLOW.md` at the fork-point trunk commit, never from the branch under test. The `{targets}` filter comes from the approved spec, not from the implementer's report. A gate result counts only if the tree was clean and HEAD did not move during the run.

**Least privilege is enforced by the forge, not by prose.** Agents get an installation token minted with `contents/issues/pull_requests: read`, so a push from an agent is refused by GitHub. The orchestrator token is used only by `Git.Push()`. That method takes no ref argument, checks `branch --show-current`, and never forces.

**Budgets per epoch.** One retry for invalid output and two revision cycles per job. Both reset when the maintainer resumes (epoch = the count of `/factory resume` comments). An attempt records its epoch at spawn time. A resume resets budgets only: it never redoes a job whose latest artifact is valid. A notice halts every station, so there is at most one notice per epoch, and its marker is keyed by the epoch. `Timeline` owns the marker format; it renders and parses it in one place.

Interface depth: the coordinator sees two verbs (`advance`, `status`) and five exit codes to branch on. Behind them sit label projection, idempotency markers, budgets, schema validation, approval policy, pins and token scoping. `IForge`, `IAgentRunner` and `ICommandRunner` are the only seams, and each has two or more implementations (real bindings plus test fakes).

### Module map

| File | Owns | Pure? |
|---|---|---|
| `Run.cs` | `RunRef`, `Sha`, `Digest`, `BranchName`, `ExitCode`; `RunDir` layout and per-run lock | layout yes, lock no |
| `Artifacts.cs` | artifact records, `IArtifact<T>`, `FactoryJson` context, `Artifact.Read/Schema` | yes |
| `Facts.cs` | `Facts` snapshot; `Timeline` (marker format, maintainer policy, epochs); attempts and pins; gate results | yes |
| `Process.cs` | `Step` union, `Jobs` table, `Process.Next` with the shared attempt policy `RunAgent`, `Process.Label` | yes |
| `Capabilities.cs` | `IForge`, `IAgentRunner`, `ICommandRunner`; `Git`; `AppTokens` | no (shell) |
| `Shell.cs` | `WorkflowConfig` with the subset-YAML reader; `Briefs`; `Reconciler` (observe, execute, advance); `Cli` | no (shell) |

Implementation layout: `src/Factory/` holds the above plus `Bindings/{GhForge,PaseoAgents,ClaudeCliAgents,CodexCliAgents,ProcessRunner}.cs`. `prompts/*.md` are embedded resources, so the template digest is pinned with the binary. `tests/Factory.Tests/` has table tests on `Process.Next` and `Timeline.From`, built from a `Scenario` builder with no fakes. The fakes (`FakeForge`, `FakeAgents`, `FakeCommands`) serve one property test on `Reconciler`: from a fresh issue with scripted agents, `advance --wait` reaches `Published`, and no non-halting step is chosen twice in a row.

## Synthesis decision

n/a (single candidate)

## Tradeoffs accepted

- We accept re-observing the forge and git on every loop iteration (about 4 `gh` calls per step) in exchange for having no stored state to drift.
- We accept that the maintainer must go to the browser to approve, in exchange for an approval that no local process can mint.
- We accept a strict YAML subset (block maps, block lists and scalars, with no anchors, flow collections or multi-line scalars) in exchange for no new dependency. `ponytail:` upgrade path: YamlDotNet with its static generator, which reads the same files.
- We accept one PR per run, even when the spec lists several tickets. Tickets go into one implementer brief, and fan-out comes later.
- We accept that a failed deslop pass is discarded (`DiscardClean` resets to the pre-clean SHA, which was never pushed) instead of escalating.
- We accept that same-Unix-user agents can still read `app.pem` or the run dir. The threat model is drift, not malice (see risks).

## Alternatives considered

- **Event-sourced journal** (`events.jsonl` is the truth; labels and files are projections). This was the cleanest replay story, but it is a second copy of facts that git and the forge already hold authoritatively. A hand-edited branch or a PR closed in the browser makes the journal lie. The interface depth is the same, and it adds one more sync problem.
- **Explicit transition commands** (`factory triage`, `factory approve`, `factory implement`). These are easy to read, but they expose the station graph to callers and need a refusal path for each wrong call. A local `approve` is forgeable by the coordinator. Five-plus verbs replace one.
- **Stored state field plus guards** (`run.json: {station: 3}`). This is the classic state machine, but crash windows between "did the effect" and "wrote the state" need a recovery path for each step, which is exactly what deriving from facts removes.

## Open questions and risks

- Where does `paseo run --output-schema` deliver the structured output (`inspect --json`, `wait`)? `PaseoAgents` depends on it, and the grounding does not say.
- Should agents run under a second Unix user (or a codex sandbox with a read deny on `~/.config/factory` and `~/.factory`)? Until they do, the forge-side checks are the only hard boundary.
- Should cluckwork get a committed `WORKFLOW.md` before the pilot? The fallback `~/.config/factory/workflows/mforce/cluckwork.md` can be written by agents, so gate commands would come from an agent-writable file.
- Is `author_association == OWNER` acceptable as the step 1 approver test? Org-owned repos need the collaborator-permission API (`admin`/`maintain`) instead.
- Does the `grill-with-docs` approval session survive as a forge loop (`/factory revise`), or does the maintainer want to grill inside the spec agent's Paseo tab before the artifact is written?
- Does `dotnet test --filter` with no matching test exit 0? If so, `test_targeted` can pass vacuously, and the gate needs a "tests ran > 0" check.

## Next implementation step

Build `Process.Next` and `Timeline.From` with their table tests against hand-built `Facts`, before any binding exists.
