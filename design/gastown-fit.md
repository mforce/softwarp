# Gas Town + Beads: fit against softwarp requirements

Evaluated 2026-10-11 from source and docs only. Paths: `gt:` = gastownhall/gastown, `bd:` = gastownhall/beads. "Unknown" = not confirmed from sources.

## 1. Requirement table

| Req | Covered | How (source) | Gap closable with GT extension points? | Effort |
|---|---|---|---|---|
| R1 harness/model | partial | Presets for claude, codex, pi, opencode, gemini (`gt:internal/config/agents.go`). No Hermes preset. Sessions are interactive TUIs in tmux; headless `non_interactive` is used only by formulas/dogs (`gt:docs/agent-provider-integration.md`). vLLM: through Pi/OpenCode own config, or a `claude` preset with `ANTHROPIC_BASE_URL` (Groq preset precedent). GLM on vLLM: unknown. | Hermes and vLLM: yes, a JSON preset in `settings/agents.json` (Tier 1). Headless: no. | M |
| R2 GitHub Issues truth | partial | `bd github sync` pulls issues into beads, but its default is bidirectional and pushes local beads to GitHub as issues (`bd:docs/cli-reference/github.md`). Formulas tell agents to `bd create` found work (`gt:.../mol-polecat-work.formula.toml`). Label/comment write-back: unknown. | Partly. Use `--pull-only`, keep `GITHUB_TOKEN` from agents; a directive is prose only. Beads is a second store to reconcile. | M |
| R3 PR flow | partial | `gt sling --no-merge` with rig `merge_strategy=pr`: `gt done` pushes and runs `gh pr create`, no merge (`gt:internal/cmd/done.go` ~1470-1560). Against it: default Refinery merges to main (`gt:README.md`); `merge_strategy=pr` Refinery merges by API and `require_review` defaults false (`gt:internal/refinery/engineer.go` ~172); convoy `direct` pushes `branch:main` (`done.go` ~1209); shipped `pr-workflow` guard blocks `gh pr create`; dangerous-command guard blocks `--force`/`-f` but allows `--force-with-lease` (`gt:internal/cmd/tap_guard*.go`). | Partly. `gt hooks override` can swap guards (string-match PreToolUse, hook-capable harnesses only). The PR-flow harness admits agents "can still misbehave" (`gt:docs/contrib-harnesses/polecat-pr-flow/README.md`). Real block = GitHub rulesets. | M |
| R4 gates | partial | Refinery `gates` run `sh -c`, judged by exit code, with timeouts (`engineer.go` `runGate`). But `gt done --pre-verified` is an agent-set flag that makes the Refinery skip gates (`done.go` ~1744, `engineer.go` ~607). In the no-merge PR path no Refinery runs, so gates are formula prose. | Partly. Overlay `replace` of `submit-and-exit` plus a custom PreToolUse guard on `--pre-verified`. | M |
| R5 approval | no | `human` gate closes with `bd gate resolve` (`bd:docs/cli-reference/gate.md`). Dolt listens on 3307 with no password (`gt:docs/design/dolt-storage.md`). Agents run as the same UID with host credentials; sandbox is only a proposal (`gt:docs/design/sandboxed-polecat-execution.md`). No GitHub-identity check exists. | No. Directives/overlays cannot stop `gt sling` or a gate resolve. Needs a check outside GT, and a sandbox or second UID. | L |
| R6 agent identity | no | `GIT_AUTHOR_NAME` and `BD_ACTOR` are labels (`gt:docs/concepts/identity.md`). GitHub calls use the host `gh`/git credentials. A preset `env` can inject `$VAR` (Groq preset); App token refresh: unknown. | Partly. Preset env plus a credential helper that mints App tokens. | M |
| R7 other-vendor review | partial | `gt sling --agent codex --review-only`; `code-review` convoy formula (10 parallel legs); `mol-polecat-code-review`. Read-only is a flag plus prose; default Codex preset has no hooks (`agent-provider-integration.md` matrix). | Yes. Own formula, `--agent` per sling. Enforced read-only: sandbox only. | M |
| R8 holdouts | no | No concept. Implementer and verifier share the host filesystem. | Formula can run a verifier; hiding files needs an external sandbox. | L |
| R9 visibility | partial | tmux is required for `gt up` roles (`gt:README.md` prerequisites); readiness is regex/delay on the pane. Own `gt dashboard`, `gt feed`, `gt <role> attach`. Agents are not Paseo agents. A Paseo terminal tab could run `tmux attach` (my inference, unverified). "Minimal mode" lets you run `claude` yourself in a polecat worktree and drops the daemon and Witness. | No for Paseo-managed agents. | L |
| R10 per-repo commands | yes | Rig `settings/config.json`: setup/typecheck/lint/test/build commands and a named `gates` map (`gt:docs/reference.md`; `engineer.go`). No auto-detect; no targeted-test `{paths}` command. | n/a | S |
| R11 resume/stall | partial | Hook plus bead state, `gt handoff`, Witness respawn, `resume_flag` per preset. Default wisps have no step checkpoint; only `pour = true` does (`gt:docs/concepts/molecules.md`). Stall: "30 min with hook but no progress" or 30 min of tmux silence, judged by an LLM Witness (`gt:docs/design/polecat-lifecycle-patrol.md`). Side-effect-based: no. Idempotent steps: unknown. | Partly: tune thresholds. | M |
| R12 metrics | partial | `gt costs` reads Claude transcripts only (`gt:internal/cmd/costs.go`). OTel token usage is a pending PR; per-bead cost attribution is a listed gap (`gt:docs/design/otel/otel-architecture.md`). | Partly: a Stop hook that parses Codex/Pi logs. | M |
| R13 right-size | partial | Sold for 20-30 agents. Roles are optional ("Choosing Roles" in `gt:docs/INSTALLING.md`); `scheduler.max_polecats` caps concurrency. Patrol agents burn model budget (`gt:gt-model-eval/README.md`). Dolt and daemon stay. | Partly: run polecats plus Witness only. | M |
| R14 skills/slash | unknown | Formula text already says `/review --branch`. Context arrives as hook output from `gt prime`, not a user turn, so skills marked `disable-model-invocation` probably cannot start from it (inference). Nudges are tmux send-keys and might carry `/triage #42`: untested. | Maybe, via nudge or overlay text. | S |

Counts: yes 1, partial 9, no 3, unknown 1.

## 2. Operational footprint

- **Runtime:** Dolt SQL server >= 2.0.7 (one per town, port 3307, no embedded fallback), tmux >= 3.0, git >= 2.20, sqlite3, `bd` >= 0.57.0, `gh`, the agent CLIs. Go 1.26.2+ and ICU dev headers only for source builds. A Docker Compose path exists (`gt:docs/INSTALLING.md`, `gt:docs/docker.md`).
- **Install (Linux):** install Dolt; `go install` gt and bd; `gt install ~/gt --shell --git`; `gt up`; `gt rig add`; `gt doctor --fix`; configure agent aliases.
- **Concepts to learn (about 20):** town, rig, mayor, deacon, boot, dogs, witness, refinery, polecat, crew, hook, bead, formula/molecule/wisp, convoy, sling, nudge, mail, handoff/prime, seance, escalation, directives/overlays, GUPP.
- **OS:** macOS and Linux first-class. Windows is CLI-only or WSL.
- **License:** MIT for gastown, beads and gascity.
- **Cadence:** gastown released five times from 2026-04-03 to v1.2.1 on 2026-06-06, then nothing. Default-branch commits stop at 2026-07-23 (none after 2026-08-01). 507 open issues. `pushed_at` is 2026-10-08, probably other branches (unknown). `gt:docs/agent-provider-integration.md` says Gas Town "is being succeeded by Gas City". Gas City shipped v1.5.0 on 2026-10-05 with subprocess, exec, ACP and k8s providers (tmux still required) and an optional file-based beads store. I read only its README and migration page. Beads is active: v1.3.1 on 2026-09-30, rc on 2026-10-10, 1,423 open issues.

## 3. Copy these ideas into softwarp

1. **Prime on every session start and after compaction** (`gt prime` via SessionStart/PreCompact hooks). A fresh session reads its assignment first.
2. **Overlays with replace/append/skip plus a doctor check** for stale step IDs. WORKFLOW.md has no per-step override or drift check.
3. **Named gates with timeout, parallel run and phase**, plus retry for flaky tests. Record the base SHA at which gates passed, and skip a re-run only if the orchestrator measured it (not an agent flag).
4. **Escalation with severity, ack, stale threshold and bounded re-escalation** (`gt:docs/design/escalation.md`). softwarp has only `needs-human`.
5. **Zero-commit rejection** at completion and **bead id inside the branch name** so every PR maps to its item.
6. **Capacity governor with a circuit breaker** (`gt:docs/design/scheduler.md`).
7. **`agents.json` preset schema** (process names, resume flag, non-interactive flags, session-id env) as the shape of `bindings.yaml`.

## 4. Recommendation: build softwarp

Adopt-plus-policy-layer is not worth it. The layer would own gates, approval, identity, review routing and metrics. That is most of softwarp.

Decisive reasons:
1. **Execution model mismatch.** GT drives interactive TUIs through tmux send-keys and prompt regexes. Its own design doc calls this a hack and proposes an API that is not built (`gt:docs/design/factory-worker-api.md`). R1 (headless) and R9 (Paseo) both fail here.
2. **Policy is prose, and the defaults point the other way.** Refinery merges to main, a shipped guard forbids PRs, `--pre-verified` and `bd gate resolve` are agent-forgeable, Dolt has no password. R3 to R6 and R8 need enforcement outside GT.
3. **Footprint and trajectory.** Dolt, a daemon and LLM-run patrol roles for a 20-30 agent design, against 3 implementers. The default branch has been quiet since 2026-07-23 and the successor is Gas City. Beads adds a second store when GitHub Issues is the truth.

Watch item: Gas City. A separate 1-2 hour look at its exec/subprocess providers is the only adopt path I see.

## 5. If adoption is reconsidered: trial on one small issue

1. **R3/R4 path.** Rig with `merge_strategy=pr`, Refinery off. Run `gt sling <bead> <rig> --no-merge --agent codex`. Confirm it pushes only its own branch and opens a PR under an App token injected through preset env. Then try `gt done --pre-verified`, `git push origin HEAD:main` and `gh pr merge` as the agent, and record which are blocked.
2. **Headless and Paseo.** Run Pi on the vLLM GLM endpoint and Codex without tmux ("minimal mode"). Kill each mid-task. Check resume, and whether the stall signal sees commits or only pane silence. Check what Paseo can show.
3. **R2/R5 forgery.** Pull one issue with `bd github sync --pull-only`. As the agent, run `bd create` (confirm no GitHub issue appears), `bd gate resolve` on a human gate, and write to Dolt on 3307.
