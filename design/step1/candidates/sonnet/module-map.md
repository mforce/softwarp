# Module map

One AOT binary (`factory`), one project, folders as modules. A second project, `tests/Factory.Tests`, references it. The sketch holds the same code flat in `src/`.

| Module (sketch file) | Owns | Depends on | I/O |
|---|---|---|---|
| Domain (`Domain.cs`) | ids, enums, `LedgerEntry`, `Ledger`, `GateReceipt`, pins | nothing | none |
| Artifacts (`Artifacts.cs`) | the four station records, `IArtifact<T>`, `SpecReview`, `FactoryJson` (the only JSON context) | Domain | none |
| Machine (`Machine.cs`) | `Observed`, `Step`, `Plan`, `Signals`, `Projection` | Domain, Artifacts, Workflow settings | none (pure) |
| Workflow (`Workflow.cs`) | `WORKFLOW.md` and `bindings.yaml` parsing (`MiniYaml`), `Briefs` | Domain, Artifacts | none (string in, record out) |
| Capabilities (`Capabilities.cs`) | the five interfaces and their domain types | Domain | none (interfaces) |
| Engine (`Engine.cs`) | `Engine` (4 methods), `RunStore`, Observe, Execute | all of the above | files, calls through capabilities |
| Cli (`Cli.cs`) | `CommandLine.Parse`, `Program.Main` composition root | Engine | stdout, exit code |
| Bindings (not in the sketch) | `GitHubTracker`, `GitHubIdentity`, `GitWorkspaces`, `PaseoSpawner`, `ClaudeSpawner`, `CodexSpawner`, `ShellGates` | Capabilities | network, processes |

Rules for the dependency arrows: Domain, Artifacts, Machine and Workflow import no `System.IO`, no `HttpClient` and no `Process`. Bindings never reference Machine or Engine. Only `Program` names a binding.

## Layout in the repo

```
factory/
  src/Factory/                 Domain/ Artifacts/ Machine/ Workflow/ Capabilities/ Engine/ Cli/ Bindings/
  src/Factory/prompts/         triage.md spec.md implement.md publish.md   (embedded resources)
  tests/Factory.Tests/         Machine/ (table tests on Observed)  Scenarios/ (Engine + fakes)  Bindings/ (recorded I/O)
  tests/Factory.Tests/Fakes/   FakeTracker FakeWorkspaces ScriptedSpawner FakeGates
```

## Call chain for one `factory advance` (3 files)

`Cli.Program.Main` → `Engine.Advance` → `Engine.Observe` (capabilities) → `Machine.Plan` (pure) → `Engine.Execute` (capabilities, `RunStore`) → `Projection.Label` → `ITracker.SetLabel`.

## Where each of the grounding file's 8 questions is answered

| Question | File |
|---|---|
| 1 command surface | `Cli.cs` |
| 2 state model | `Domain.cs` (Ledger), `Machine.cs` (`Observed`), rationale table |
| 3 artifacts and validation | `Artifacts.cs` |
| 4 capabilities and bindings | `Capabilities.cs` |
| 5 how advance decides | `Machine.cs` (`Plan`, `Slot`) |
| 6 approval | `Machine.cs` (`Signals`), `Artifacts.cs` (`SpecReview`) |
| 7 WORKFLOW.md under AOT | `Workflow.cs` (`MiniYaml`) |
| 8 layout and fakes | this file, `Capabilities.cs` (`Caps`) |
