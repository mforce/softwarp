# Step 1 verdict

Recommend **candidate-3, 25/30**, as the base. Its durable intents, prerequisite-bound receipts, enforced execution scopes and separate host/harness bindings address failures the other candidates leave to convention. Fix its gate ordering and define bounded worker execution before implementation. These are design scores, not claims of working code. No build was run.

Each citation below is relative to the named candidate's directory under `design/step1/blind/`. Scores assess the supplied rationale and pseudocode together.

| Candidate | Determinism | State | Interface depth | Portability | Approval / privilege | Pilot / tests | Total |
|---|---:|---:|---:|---:|---:|---:|---:|
| candidate-1 | 2 | 1 | 3 | 2 | 1 | 3 | **12** |
| candidate-2 | 3 | 2 | 4 | 3 | 2 | 3 | **17** |
| candidate-3 | 4 | 3 | 5 | 5 | 4 | 4 | **25** |
| candidate-4 | 3 | 2 | 3 | 2 | 2 | 3 | **15** |

## Score evidence

### Candidate-1

- Determinism: the planner controls transitions, but accepts a non-null invalid implementation report; execution selects live models instead of recorded pins (`src/Observation.cs:103`, `src/Driver.cs:56`). Deserialization alone is presented as schema validation (`src/Artifacts.cs:96`).
- State: claim and spawn effects precede completion records, without reconciliation keys or locking (`src/Driver.cs:54`). Retry arithmetic and gate ordering can loop (`src/Observation.cs:95`).
- Interface depth: four CLI verbs are compact, but public Observe/Execute expose coordination, and the driver assembles low-level storage operations (`src/Program.cs:4`, `src/Driver.cs:48`, `src/RunDir.cs:21`).
- Portability: JSON front matter provides a justified AOT alternative; core `MachineConfig` directly embeds `GitHubApp`, violating grounding (`src/Workflow.cs:10`, `src/Model.cs:64`).
- Approval/privilege: remote authorship is checked, but the maintainer login is locally writable. No same-user credential isolation is established, and the documented push supplies `HEAD` explicitly (`src/Observation.cs:11`, `src/Model.cs:64`, `src/Capabilities.cs:58`).
- Pilot/tests: criteria and fake-capability tests fit #1223; cleanup is folded into implementation. Recovery defects prevent the illustrated flow from being dependable (`src/Artifacts.cs:39`, `module-map.md:21`, `rationale.md:99`).

### Candidate-2

- Determinism: strict JSON options and approved test targets are strong; subsequent implementation evidence is not tied to the approved spec (`src/Artifacts.cs:57`, `src/Artifacts.cs:117`, `src/Machine.cs:61`).
- State: immutable ledger entries and spawn lookup address retries, but admission is not locked and running agents have no enforced deadline (`src/Engine.cs:12`, `src/Capabilities.cs:90`, `src/Machine.cs:86`).
- Interface depth: private orchestration and a shared retry policy yield a three-file flow. The ledger still exposes several coordinated operations (`src/Engine.cs:54`, `src/Machine.cs:81`, `module-map.md:28`).
- Portability: domain-facing capabilities are replaceable; custom YAML adds parser work, while shared configuration assumes App credentials (`src/Capabilities.cs:23`, `src/Workflow.cs:23`, `src/Workflow.cs:37`).
- Approval/privilege: tracker has no create, agents receive no token, and push is constrained. However, a locally editable maintainer set can authorize another human account; environment scrubbing does not isolate the signing key (`src/Capabilities.cs:21`, `src/Capabilities.cs:55`, `src/Workflow.cs:35`).
- Pilot/tests: typed implementer input excludes holdouts and approved targets suit #1223. Scripted fakes are clear; ignored manual trust and missing stop semantics remain gaps (`src/Workflow.cs:50`, `rationale.md:36`, `rationale.md:106`).

### Candidate-3

- Determinism: prerequisite hashes, command-bound gate receipts, schema validation and retained pinned content provide the strongest contract (`src/Model.cs:48`, `src/Policy.cs:59`, `module-map.md:13`). Gate-failure ordering remains ambiguous.
- State: intent-before-effect, an exclusive lease and ambiguous-outcome reconciliation cover crash windows. Worker termination and the gate-order defect below prevent a higher score (`src/Policy.cs:61`, `src/Capabilities.cs:54`, `module-map.md:17`).
- Interface depth: Factory owns complete operations; storage, isolation and spawn capabilities hide substantial policy. The normal flow spans CLI, policy and binding (`src/Policy.cs:44`, `module-map.md:11`).
- Portability: core types name no machine tool. Separate host/harness factories and static YAML generation preserve replacement and AOT compatibility (`src/Bindings.cs:16`, `src/Bindings.cs:69`).
- Approval/privilege: freshly fetched immutable account identity, package-bound approval, scoped credentials and enforced execution scopes cover the stated threat. Actual sandbox enforcement is unresolved (`src/Policy.cs:25`, `src/Capabilities.cs:20`, `rationale.md:69`). Tracker has no create; push has no refspec/force (`src/Capabilities.cs:5`, `src/Capabilities.cs:40`).
- Pilot/tests: local ticket scope and four mutation criteria fit #1223; all effect capabilities have fake seams. The isolation prerequisite adds setup work (`module-map.md:15`, `module-map.md:23`, `rationale.md:61`).

### Candidate-4

- Determinism: strict artifacts and approved targets help, but refreshing the base changes supposedly pinned workflow inputs (`src/Artifacts.cs:104`, `src/Process.cs:93`, `src/Shell.cs:17`).
- State: per-run locking and attempt adoption help; the directory omits repository owner, and waiting is unbounded despite MaxSteps (`src/Run.cs:48`, `src/Capabilities.cs:29`, `src/Shell.cs:87`).
- Interface depth: few verbs hide useful behavior, but Git also runs gates and the shell depends directly on App token machinery (`src/Capabilities.cs:61`, `src/Shell.cs:71`).
- Portability: core Jobs hardcodes installed skills; shared configuration embeds `GitHubApp`. YAML requires another handwritten parser (`src/Process.cs:16`, `src/Shell.cs:29`, `src/Shell.cs:40`).
- Approval/privilege: forge role/editor checks beat writable approver lists. Read-only tokens are undermined by accessible signing keys and a CLI that prints orchestrator tokens (`src/Facts.cs:51`, `src/Capabilities.cs:42`, `src/Shell.cs:101`).
- Pilot/tests: approved filters and fake scenarios fit the pilot; acceptance proof remains later. Concrete Git and token dependencies make isolation tests less direct (`rationale.md:43`, `rationale.md:124`, `src/Shell.cs:71`).

## Bugs and red flags

**Candidate-1:** spec attempts minus revisions stays one during repeated invalid specs; replacements bypass limits. Missing targeted-test evidence precedes handling a red build, so build repeats forever. Terminal outcomes break before Execute writes escalation labels. Crashing after WriteRun skips unfinished claim work; crashing after spawn loses its handle (`src/Observation.cs:55,89,96,106`; `src/Driver.cs:35,55,57`). A worker that commits once can hang indefinitely because stall detection compares only against its initial head (`src/Observation.cs:117`). RunDir is shallow; JSON metadata leaks through capabilities (`src/RunDir.cs:21`; `src/Capabilities.cs:26`).

**Candidate-2:** approving a revised spec reuses the old implementation and publication artifacts. Stop's marker is absent from Plan, and Stop removes worktrees without stopping agents. A crash after claim but before pinning encounters an AlreadyMine no-op (`src/Machine.cs:61–72`; `src/Engine.cs:39,51`). A repair that leaves HEAD unchanged hits the same gate rejection key; Append does nothing, so Plan repeats Bounce forever (`src/Machine.cs:75`; `src/Engine.cs:22`). MiniYaml and the public Caps/Store/Config properties add avoidable complexity and information leakage (`src/Workflow.cs:26`; `src/Engine.cs:35`).

**Candidate-3:** read as ordered pseudocode, “missing successful receipts” reruns a failed gate before reaching repair. Check failure first (`src/Policy.cs:14–15`). WaitAsync has no explicit deadline or stop contract (`src/Capabilities.cs:59`). No clear shallow, temporal or pass-through decomposition; credential-lease application remains underspecified (`src/Capabilities.cs:26`).

**Candidate-4:** Implement sees cleanup's failing gates before Publish can discard cleanup; revised specs retain valid implementation attempts. Discarding invalid cleanup leaves the invalid latest artifact eligible again (`src/Process.cs:70,85,94,103`). Station-order decomposition repeats gate ownership. Public marker strings leak tracker representation (`src/Facts.cs:59`; `src/Capabilities.cs:13`).

## Grafts into candidate-3

- Candidate-1: retain its explicit coordinator exit-code walkthrough (`rationale.md:9`).
- Candidate-2: adopt approved test targets and separate public Spec from private holdouts (`src/Artifacts.cs:57–67`).
- Candidate-4: adopt explicit attempt reasons and maintainer-resume budget epochs; retain its remote last-editor approval check (`src/Facts.cs:51,64,104`).
