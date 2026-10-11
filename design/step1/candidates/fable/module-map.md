# Module map

One AOT executable, one test project. Tracing any `advance` touches three files: `Program.cs` -> `Driver.cs` -> `Observation.cs`; bindings sit behind interfaces and are not on the path a reader follows.

```
factory/
  src/Factory/
    Program.cs        CLI: run | advance | status | schema; argv -> Invocation; exit code from Outcome
    Driver.cs         Observe -> Planner.Next -> Execute loop; Status rendering; the only place with side effects on all five capabilities
    Observation.cs    Observation (facts), Move (closed set), Planner (pure decision table), Approval.Find
    Model.cs          RunId, RunRecord, Pins, Child, Decision, GateResult, MachineConfig, enums
    Artifacts.cs      TriageVerdict, SpecArtifact, ImplementReport, Validated<T>, FactoryJson (source-gen), schema export
    Workflow.cs       WORKFLOW.md parse (JSON front matter + markdown body), Commands.Targeted
    RunDir.cs         run dir file layout (write-once / append-only); Briefs (embedded templates, PR body, questionnaire)
    Capabilities.cs   ITracker, ISpawner, IGit, IGateRunner, SpawnRequest, Worktree
    Bindings/
      Bindings.cs     AppToken, GitHubTracker, PaseoSpawner, HeadlessSpawner(Claude|Codex), GitCli, ShellGateRunner
    Briefs/           triage.md, spec.md, implement.md  (EmbeddedResource; content hash = brief version)
  tests/Factory.Tests/
    PlannerTests.cs   Observation fixtures -> expected Move, one test per table row, no fakes
    DriverTests.cs    FakeTracker, FakeSpawner (scripted results), FakeGit, FakeGates; crash-and-resume: kill after each Execute, re-advance, assert no duplicate side effect
    ArtifactTests.cs  schema export is stable (golden JSON); Check() rules; Render() digest is stable
```

| Module | Owns the decision about | Depends on |
|---|---|---|
| Planner | which move is legal next; attempt, bounce and revision limits; stall rule | Observation, Move |
| Driver | how a move becomes side effects; idempotency guards; wait polling; progress.md | all capabilities, RunDir, Briefs, Planner |
| Artifacts | what each station must return; schema; semantic validity; spec rendering and digest | System.Text.Json (source-gen, schema exporter) |
| RunDir | file names and append-only discipline | Artifacts (for typed reads) |
| Approval | what counts as the maintainer's word | Comment, MachineConfig.MaintainerLogin |
| ITracker | labels as projection; no issue creation | — |
| ISpawner | harness argv, `[coordinator]` prefix, structured-output retrieval, watch command | — |
| IGit | worktree path, branch-only push | AppToken |
| Workflow | repo commands and trust rung; `{paths}` substitution | FactoryJson |

Out of scope for step 1 and not blocked: stations 5–8 add `Move` records and planner rows; `factory doctor` writes `bindings.json`; scheduling wraps `Driver.Advance` in a loop over `factory:ready`; metrics append to `metrics.jsonl` from `Execute`.
