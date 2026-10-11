# Module map

| Sketch file | Knowledge owned | Production location |
|---|---|---|
| `Model.cs` | Domain evidence, station artifacts, pins and actions | `Softwarp.Core` |
| `Policy.cs` | Receipt eligibility, next action, approval policy and durable orchestration | `Softwarp.Core` |
| `Capabilities.cs` | Effects policy requires, with no transport details | `Softwarp.Core` |
| `Cli.cs` | Arguments, presentation, exit codes and cancellation | `Softwarp.Cli` |
| `Bindings.cs` | Composition, wire formats, AOT serialization and tool invocation | `Softwarp.Bindings` |

The sketch compiles as one executable. These are ownership boundaries, not a requirement for separate assemblies. A normal call traverses CLI, Factory/Policy, then a capability binding. There are no load/validate/save wrapper modules.

The run directory is `~/.softwarp/runs/<repo>/<issue>/`. Its immutable manifest records original issue bytes/revision, workflow bytes/commit, trunk SHA, brief and skill content hashes, model ids, binding versions, branch and maintainer account id. Store pinned content alongside its digest so replay does not depend on mutable installations. `attempts/<operation>/` contains the intent, worker output, logs and imported receipts. `holdout/` is outside the implementation worktree and excluded from that role's filesystem view. The required Markdown, TSV and metrics files are projections. Readers merge per-attempt receipts; agents cannot write the manifest or accepted receipts.

Intake uses the tracker's read capability for this one-item slice. Local ticket artifacts express blocking edges without creating issues or parallel workspaces. A spec that requires independently published tickets stops for scope clarification. The four #1223 mutation cases become separate acceptance criteria; cap increases require a new approved package. Full acceptance proof remains station 5, after publication, and is not claimed by the build gate.

The filesystem store makes one run per item with atomic create and an OS lock. It flushes intents before effects and atomically renames receipts after validation. Claim labels are checked under that lock on this host. The forge binding discovers PRs by repository and unique branch, and comments by operation marker. If an API timeout leaves an ambiguous result, it reads until the outcome is known or escalates; it does not blindly repeat the write. A second host is unsupported and MUST NOT run concurrently.

The first concrete set is `GitHubBinding` for scoped App identity and tracker operations, `AgentBinding.GitWorktree` for isolation, and a host/harness pair selected through `AgentBinding`. The host owns launch deduplication, live inspection, log visibility and coordinator message prefixes. The harness owns command arguments, schema support and output-envelope parsing. No core switch names these tools. No station uses a skill's built-in push or PR action.

`WORKFLOW.md` parsing requires YamlDotNet's static generator when implemented. It is deliberately absent from this package because the parser body is a stub. JSON contracts already use System.Text.Json source generation; schemas are exported from those type descriptions during implementation. Static YAML DTOs retain the documented settings, while `Workflow` exposes only what stations 0–4 consume. No runtime plugin discovery or reflection serializer is needed.

Production tests replace `IRunStore`, `ITracker`, `IIsolation`, `ISpawn` and `IGates` with fakes. A pure planner table covers each obligation and failure outcome. Fault injection interrupts after claim, worker launch, commit, push and PR creation; a repeated call must adopt the existing effect. Other cases cover simultaneous CLI callers, forged local approval, edited remote approval, cyclic tickets, an old-head gate receipt and cleanup that changes HEAD. Binding integration tests then check real token refresh, permission isolation and AOT publishing. This design package contains no executable policy to test yet.
