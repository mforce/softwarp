# Gas City fit for softwarp

Read-only review, 2026-10-11. Nothing was installed or run. Paths: `GC:` = github.com/gastownhall/gascity/blob/main/, `GP:` = .../gascity-packs/blob/main/, `BD:` = .../beads/blob/main/.

## 1. Requirements

| Req | Covered | How (source) | Gap, existing pack, closable? | Effort |
|---|---|---|---|---|
| R1 harnesses, GLM | partial | Built-in `claude`, `codex`, `pi`; custom `[providers.x]`; `[upstreams]` renders base URL and key per harness (`GC:internal/worker/builtin/profiles.go`, `docs/guides/harness-recipes.md`). Headless: subprocess or ACP (`docs/reference/config.md` SessionConfig). | No Hermes built-in (unknown if it speaks ACP). Pi is login-based, no upstream binding. vLLM works only if the harness accepts the endpoint: unknown. No pack. Config. | S-M |
| R2 issues are truth | partial | GitHub adapters take an issue URL and only comment or open PRs (`GP:gascity/assets/scripts/github_api.py`). `bd github sync --pull-only` mirrors (`BD:docs/cli-reference/github.md`). | `bd github push`, and bidirectional `sync`, create issues. A token with comment rights can also create. Pack `gascity` fits the flow. Close by keeping tokens and `gh` out of agent env. | S |
| R3 PR flow | partial | `publish` formula: `push`/`open_pr` default false, lease-checked, no merge (`GP:gascity/formulas/publish.formula.toml`). `github` pack `push-branch` pushes without force (`GP:github/scripts/github_intake_push_branch.py`). | Rules live in prompts. Gas City is "not a sandbox" (`GC:docs/reference/trust-boundaries.md`). Add rulesets and harness deny hooks. Do not import `gastown` or `pr-review`: they merge. | M |
| R4 gates | yes | `[steps.check]`, `mode="exec"`: exit 0 closes; other codes retry, then fail and block downstream (`GC:docs/reference/specs/formula-spec-v2.md` 3.1). | Write scripts. Steps must be idempotent (`GC:docs/guides/understanding-formulas.md`, #3005). | S |
| R5 forge-proof approval | partial | Waits watch beads only (`gc session wait --on-beads`, `GC:docs/reference/cli.md`). `[steps.gate]` types `gh:pr`/`human` are inert (spec 4). Order `condition` runs any `sh -c` (`GC:docs/tutorials/07-orders.md`). `pr-review` gate is `gc bd close`. | No pack checks identity. Close: `check` script that reads the maintainer's comment and the spec digest. Residual: same-OS-user agent may edit the script or close beads (no ACL seen: unknown). | M |
| R6 agent identity | partial | `github` pack: App import, identity resolver, short-lived installation tokens, `gc github comment-issue\|push-branch\|create-pr` (`GP:github/README.md`, `docs/github-app-identity.md`). `gascity` adapters use ambient `gh`. | Unknown: whether the commands run without the webhook service. Plain `gh` needs a token refresher. | M |
| R7 cross-vendor review | yes | Provider is set per agent; review lanes in `gascity`, reviewer fan-out in `compound-engineering` (`GP:README.md`). `github-pr-review`: one sticky comment keyed by head SHA, no formal review. | Patch reviewers to Codex, read-only mode. | S |
| R8 holdouts | partial | Verifier agent and `work_dir` are config (`config.md` Agent). | Hiding is convention: same OS user. Need another user, `overlay_dir` deny rules, or an exec/k8s runtime. No pack. | M |
| R9 Paseo | no | gc is the ACP client and owns the pipes (`GC:internal/runtime/acp/protocol.go`); Paseo cannot attach. gc shows tmux, herdr (`docs/reference/herdr-provider.md`), a dashboard. | Close: `exec:` session script mapping start, stop, nudge, peek, is-running to Paseo (`docs/reference/exec-session-provider.md`). Effect on readiness and sleep: unknown. | M-L |
| R10 per-repo commands | yes | Rig `formula_vars`, `default_branch` (`config.md` Rig). | None. | S |
| R11 resume, stall | partial | State is beads; the controller adopts live sessions (`GC:docs/getting-started/how-gas-city-works.md`). Stall timers use provider-reported activity (`config.md` SessionConfig). | Not side effects. Add an exec order that polls git and PR state. Data plane is not idempotent. | M |
| R12 metrics | partial | `gc costs` over `.gc/usage.jsonl` (`cli.md`); pack `profiler` (`GP:profiler/README.md`); `gascity-otel`. | Token facts come from Claude and Codex transcripts (`GC:engdocs/design/usage-facts-v0.md`, a proposal); Pi, Hermes: unknown. otel: see section 5. | S-M |
| R13 size | partial | `max_active_sessions` caps at 3 (`config.md`). | Heavy stack, section 3. | n/a |
| R14 skills | partial | Pack `skills/` is symlinked into `.claude/skills` and `.agents/skills` (`GC:docs/guides/capabilities-for-coding-agent-users.md`). | Whether a bead or `nudge` can start a human-only slash command: unknown. | S |

## 2. Mapping softwarp to Gas City

**Config:**
- Station 1 triage: `github-issue-triage` (verdicts reproduced, duplicate, needs_info, security_sensitive; rubric via `triage_rubric_path`).
- Station 2 spec: `github-issue-fix-base` steps requirements, plan, `design-review`.
- Station 3 implement: `do-work` plus pool, `max_active_sessions=3`.
- Station 4 publish: `publish`, or `gc github push-branch|create-pr`.
- Gates: `check`. Review: reviewer agents on Codex. Bindings: `[providers]`, `[upstreams]`, pack imports and patches. WORKFLOW.md commands: rig `formula_vars`; prompt body: agent templates. Trust rungs 0-1: `pr_mode`, `push`, `open_pr`. Run state: root-bead metadata and `artifact_root`. Metrics: `gc costs`, `profiler`.

**Custom code:**
- Station 0 intake: cooldown order running `gh` or `bd github sync --pull-only`.
- Spec-approval check (R5), holdout isolation, Paseo runtime, side-effect stall order, `softwarp:*` label sync, proof-freshness checks, retro buffer.

**No home:**
- Repo-owned `WORKFLOW.md` front matter (Gas City config lives in city and rig).
- Rungs 2-3 and the readiness score.
- The maintainer-intervention metric.
- `softwarp advance`: the Go control dispatcher replaces it.

## 3. Footprint and maturity

- Always: tmux, git, jq, pgrep, lsof (`GC:README.md`). Default store adds Dolt 2.1.0+, `bd` 1.0.4+, flock (`docs/getting-started/installation.md`).
- `GC_BEADS=file` skips Dolt, `bd` and flock (README, FAQ), but docs call it "good for tutorials and small setups" (`docs/tutorials/06-beads.md`). Whether pack scripts that call `gc bd` work on it: unknown.
- Install: `brew install gascity`, or a tarball. A supervisor runs as a service.
- Learn: six primitives, formula v1/v2, sling, hook, orders, packs, sessions, mail.
- Releases: v1.5.0 (2026-10-05), v1.4.2, v1.4.1, v1.4.0 at 2-5 week gaps. Repo created 2026-02-22; 1,359 stars; 1,501 open issues. The 1.6 roadmap rewrites the reconciler and moves infra beads to SQLite (`GC:ROADMAP.md`). Shipped formulas use a deprecated key (`understanding-formulas.md`).
- Extensions in any language: pack commands, doctor checks, exec orders, `exec:` providers (session, beads, mail, events, usage), `[runtimes]`, OpenAPI REST and SSE (`GC:docs/reference/api.md`). Core is Go 1.26; do not fork it.

## 4. Recommendation

**(a): build softwarp on Gas City as a pack plus small extensions.** Run the trial first; fall back to (c) if it fails.

Reasons:
1. The engine pieces softwarp would otherwise build (graph, exit-code gates, reconcile, per-agent provider, per-rig vars) exist: R4, R7, R10 are yes.
2. `gascity` and `github` packs already give GitHub-native stations 1-4, App identity and no-merge publish. The `gastown` pack has merge paths (`refinery`, `pr-merge-reconcile`), so (b) conflicts with R3.
3. Every gap (R5, R8, R9, stall) is a script at a documented seam. R5 and R8 need OS-level separation under (c) as well.

Main risk: weight and churn. Dolt/bd, or an unproven file store, plus the reconciler rewrite. R9, the only "no", also depends on an exec runtime that gc may treat as second-class.

## 5. Ecosystem

- `gascity-packs` (95 stars, no license id returned): `gascity`, `github`, `profiler`, `pr-pipeline` (author side; never pushes), `bmad`, `superpowers`, `gstack`, `compound-engineering`. No pack covers R5, R8 or R9. `superpowers` approvals are agent or in-session passes, not identity checks.
- `gascity-otel` (Apache-2.0, last push 2026-03-13): VictoriaMetrics, Logs, Grafana via docker. Covers gc, bd and agent-lifecycle metrics. Tokens are `bd_ai_*` plus Claude Code telemetry only. Partial for R12; `profiler` plus `gc costs` is closer.
- `marketplace`: Claude Code plugin index with one plugin (Wasteland). Not relevant.
- `bdp`: draft HTTP "Bead Protocol" spec with a TypeScript reference client and server over `bd`. Not needed.

## 6. First trial (1-2 hours, one small issue)

1. Run `github-issue-fix` (`pr_mode=draft`) on `GC_BEADS=file`. Add a failing `[steps.check]` test script and confirm the agent's "done" does not pass it. Kill the supervisor mid-step and confirm the resume does not repeat a push.
2. Write the approval `check` (maintainer comment plus spec digest). Confirm an agent as the same OS user cannot pass it or close the control bead. Push only through `gc github push-branch` with an App token, with a ruleset blocking `main`. Test without the webhook service.
3. Run Claude as implementer, Codex as reviewer, and Pi on vLLM GLM through custom `[providers]` and `[upstreams]`. Check that `gc costs` shows tokens for all three. Then prototype the Paseo `exec:` shim and run `gc runtime check`.
