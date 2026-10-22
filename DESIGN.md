# softwarp — Software Factory Design

Status: draft · 2026-10-09

## 1. Goal

Turn an issue into a reviewed PR that is ready to merge, on any repo, harness, model and tech stack. Agents do the work. The maintainer decides scope, approves specs, and merges.

The factory is a **process**, not a product. It is written as a harness-agnostic spec plus a small deterministic orchestrator. On each machine it binds to whatever tools that machine has.

**Non-goals (v1):** auto-merging, deploying, replacing CI, building a coding agent.

## 2. Principles

1. **Code does the mechanics; agents do the judgment.** Every step that can be a script is a script (§4).
2. **Verification is the lever.** An agent that can run the app and see the result can iterate without the maintainer. Each repo gets a verification skill before the factory runs on it (§6).
3. **Fix the environment, not the agent.** When agents repeat a mistake, change the repo (lint rule, type, convention, skill) so the mistake becomes impossible. Do not add more prose.
4. **Context, not control.** Agents fetch their own context from real sources. Each time the maintainer has to pass context by hand, record it as a bottleneck to remove.
5. **Gates check evidence, not claims.** "Tests pass" means the orchestrator ran the tests.
6. **The maintainer owns merge and issue filing.** Trust grows one rung at a time (§5).
7. **Push early, prove while CI runs.**
8. **Use the cheapest model tier that passes the gate.**

## 3. Layers

```
┌───────────────────────────────────────────────┐
│ Policy        portable rules, enforced as gates │
├───────────────────────────────────────────────┤
│ Orchestrator  deterministic: state, gates, I/O  │  ← code
├───────────────────────────────────────────────┤
│ Stations      judgment steps, schema'd output   │  ← agents
├───────────────────────────────────────────────┤
│ Capabilities  abstract verbs (spawn, verify, …) │
├───────────────────────┬───────────────────────┤
│ Bindings (machine)    │ WORKFLOW.md (repo)    │
└───────────────────────┴───────────────────────┘
```

Only the bottom row changes between machines and repos.

## 4. Determinism

**The process is deterministic. The agents' output is not.** Harness CLIs expose no seed, and the same prompt can produce different code. So the factory makes the *path* fixed and lets the gates control quality.

| Fixed by code (orchestrator) | Left to agents (judgment) |
|---|---|
| state machine and label transitions | triage: class, size, door, grouping |
| worktree create and remove | spec and holdout scenarios |
| spawning agents from versioned brief templates | implementation |
| running gates (build, test, lint) and reading exit codes | live verification on the real app |
| push, PR create, CI wait | review-finding triage and fixes |
| collecting review findings from both surfaces (dev-rules, rules 0–1) | retro and environment proposals |
| stall detection, retries, escalation | |
| metrics and cost accounting | |

**Rules**

- An agent call is a function: brief in, artifact out. Each artifact has a schema (JSON, or markdown with required headings). The orchestrator validates it. An invalid artifact gets one retry, then the item escalates.
- A gate is a command with an exit code. An agent's statement is never a gate.
- Every input is pinned and recorded per run: the brief template version, skill versions, model ids, and the `WORKFLOW.md` commit. A run can be replayed with the same inputs.
- Mechanical parts of skills move into scripts. poteto's verification CLI exists for this reason: without it, every agent rebuilt its own tooling, each in a different way.
- Where orchestration needs judgment (for example, grouping 30 related issues), the orchestrator calls an agent for that one decision and stores the result as a file.
- **Gates fail closed.** Exit 0 passes. Exit 75 means "could not check": the gate re-runs on a small budget of its own, without spending a repair attempt. Any other code fails. A gate that cannot start, times out or errors never lets a later station start. A red gate blocks every later station; there is no opt-in for that.
- **No escape hatch.** No CLI flag skips or forces a gate or a transition. Harness deny rules block `--force` on the orchestrator's state commands too. In the Gas City trial, an agent-reachable `--force` passed any gate.
- **Agents do no side effects.** Push, PR, comments and labels belong to the orchestrator, keyed by an operation id. In the Gas City trial, a crashed agent's replacement posted the same comment again.
- **The target repo stays clean.** The factory writes nothing into the primary checkout: no files, no commits. Each run works in its own worktree.
- **The state machine is code; the trigger can be anything.** A person, a schedule, or an LLM coordinator may start each transition (`softwarp advance <run>`). The CLI refuses any transition whose gate has not passed, and any artifact that fails its schema. This is how an existing coordinator agent can drive the factory without making the path non-deterministic. It follows poteto's pattern of a deterministic CLI inside an agent's workflow.

## 5. Trust ladder

Set per repo in `WORKFLOW.md` (`trust:`). The mode dial comes from noodle; the rungs come from poteto.

| Rung | Name | Who merges | Notes |
|---|---|---|---|
| 0 | manual | maintainer | the maintainer starts each station |
| 1 | **supervised (default)** | maintainer | the maintainer approves every spec and merges every PR |
| 2 | sampled | maintainer | the maintainer reviews a sample in depth and merges the rest on a clean verifier verdict |
| 3 | auto-land | agents, two-way doors only | the maintainer reviews history after merge. **Needs the maintainer's written grant per repo**, because it overrides the never-merge rule. One-way doors never auto-land. |

**Doors.** Triage marks each item one-way (migration, data deletion, auth, money, public API, external side effects) or two-way (easy to revert). One-way doors always stay at rung 1.

The repo readiness score (§6) sets the highest rung a repo is allowed to reach.

## 6. Repo onboarding (station R)

Run once per repo, then on a schedule.

1. **Stack detect**, then propose a `WORKFLOW.md` (§11) for the maintainer to approve.
2. **Readiness check (script):** AGENTS.md, CI, tests, a linter **and** a type check for each language, verification skill, and the factory labels must all exist. Trunk MUST require passing CI, with no bypass for the token that agents use, so a red PR cannot merge. Trunk MUST also require one approving review, including approval of the most recent push. An author cannot approve its own PR, so an agent's identity can never merge its own work. Harness hooks SHOULD block merges, pushes with an explicit refspec, and force-pushes, and `gh api` writes to rulesets, refs and contents. At high PR volume, trunk SHOULD use a merge queue: with only "require up to date" (strict), every merge makes every open PR update and re-run CI. On GitHub, a merge queue needs an organization-owned repo. Cluckwork, on 2026-10-10, shows the failure: strict was switched off to clear a batch, and a stale PR made `main` red. The score sets the highest allowed rung.
3. **Verification skill.** If the repo has none, create one (`pstack:create-verification-skill`, or the fallback prompt). It lets agents run the app and use it as a user would.
4. **Maintenance.** Keep the verification skill current on a schedule (`pstack:maintain-verification-skill`).

## 7. Stations

| # | Station | Does | Output | Exit gate |
|---|---------|------|--------|-----------|
| 0 | Intake | runs source adapters (§9) | work items | item not already claimed |
| 1 | Triage | checks for redundancy (already built?) and prior rejection (`.out-of-scope/`), **reproduces on main**, classifies (class, size, door, risk flags), groups related items | agent brief, or a stop state | `ready-for-agent`, `ready-for-human`, `needs-info` or `wontfix`. A false premise stops here. |
| 2 | Spec | writes the spec (`to-spec`), runs the approval session (`grill-with-docs`), splits into tickets with blocking edges (`to-tickets`), writes holdouts | `spec.md`, tickets, holdouts | **the maintainer approves every spec** |
| 3 | Implement | skill chosen by class; writes only in its worktree | commits | build clean, targeted tests pass |
| 4 | Publish | cleans the code, pushes, opens the PR, writes the PR body | PR linked to spec | PR exists |
| 5 | Prove | verifier swarm at the head SHA (lanes below) | verdict pinned to the SHA | clean verdict, CI green |
| 6 | Review | **local agent review, required on every PR:** read-only reviewer agents from another vendor than the implementer (on this machine: `pstack:thermo-nuclear-code-quality-review` and `ponytail-review`), findings fixed and answered. **Optional, per repo** (`review_bot:` in `WORKFLOW.md`): the GitHub bot loop (§12) on top | local findings triaged and fixed | local review clean; with a bot on, also reviewer quiet or stop rule hit |
| 7 | Handoff | rebases onto trunk; the verdict must cover the trunk-current SHA; notifies the maintainer | PR ready to merge | the maintainer merges (rungs 0–2) |
| 8 | Retro | after merge or abandon | proposals for environment changes | written to the buffer (§13) |

Stations 5 and 6 start as soon as the PR opens. They run in parallel with CI. The local agent review always runs; a GitHub review bot is optional per repo.

**Station 5 lanes** (from pstack `autopilot-full`):
- re-run the gates at the head SHA;
- live proof on the real surface through the verification skill, using the app as a user would and looking for regressions;
- holdout scenarios, run by an agent the implementer could not see;
- **regression lane:** run the same scenario on trunk;
- receipts and diff audit, without trusting the PR body;
- **scope judge:** does the diff match the spec? (the Spec axis of Matt's `code-review`). It grades each acceptance criterion as met or not met, with evidence. A redesign, extra scope, or an unmet criterion escalates to the maintainer before the PR can be ready. An issue closes only when every criterion is met, or the maintainer has amended it.

**Proof freshness:** every proof (captures, verifier verdicts, measurements) records the SHA it ran on. The orchestrator rejects proof whose SHA differs from the head, unless the patch-id is unchanged. Agents do not make this check.

A `perf` item adds a measurement lane (`pstack:benchmark-checklist`): what limits the number, and did the measured work actually happen?

The number of verifiers is tunable: 1 for a two-way door, more for a one-way door or a risk flag.

**Bounded rounds** (pstack 0.9.81):
- Only the first round runs the full swarm. A later round checks only the fix and re-runs the probe that found each blocking finding.
- A finding blocks only if the diff causes it (the PR makes behavior worse than trunk, or the claimed fix does not work). A defect that trunk already has goes to the buffer (§13) or is listed in the PR body as a known gap. It does not block, and it is not filed as an issue.
- Each blocking finding needs a failing test that covers every site with the same defect, or a repro receipt if no test can show it.
- After 2 fixes, if a blocking finding still remains, the PR is reported as blocked, with the evidence. The maintainer chooses: another round, a narrower PR, or close.

**Failure paths.** A station returns `bounce(station, reason)` or `escalate(reason)`. After 2 revision cycles at one station, the item escalates with `softwarp:needs-human`. An escalation is written as a short questionnaire (`to-questionnaire`), not a long thread.

**Holdout scenarios** (from StrongDM) are written by the maintainer or by a spec agent. They are stored outside the implementer's worktree, in `~/.softwarp/runs/<repo>/<issue>/holdout/`.

## 8. Work item state

Labels are **views**: written for people, never read to decide anything. The next step is derived from the run's receipts, git and the forge (`Policy.Next`, sketch row table). Labels map to Matt's `triage` states:

| Label | Meaning |
|-------|---------|
| `softwarp:needs-triage` | not evaluated yet |
| `softwarp:needs-info` | waiting for the reporter |
| `softwarp:ready` | ready for an agent (`ready-for-agent`) |
| `softwarp:needs-human` | needs the maintainer; the comment says why |
| `softwarp:active` | claimed; a comment names the run id and the agent |
| `softwarp:review` | in stations 5–6 |
| `softwarp:done` | handed off |
| `softwarp:wontfix` | closed with a reason |

Per run, in `~/.softwarp/runs/<repo>/<issue>/`:
- `progress.md`: a handoff file, so a new session or context window can continue (`handoff`);
- `decisions.tsv`: one line per decision (`show-me-your-work`); a mirror, never an approval;
- `children.tsv`: each sub-agent's id, expected runtime and state, for stall detection;
- `metrics.jsonl`, agent logs, holdouts.

Every station is idempotent. A run resumes from its receipts, the branch and the PR. A missing run manifest stops the run; it is never rebuilt from labels.

**Stall detection** (from Symphony and `autopilot-full`): only side effects count as progress (commits, pushes, PR or check changes, artifacts). An agent with no side effect past its expected runtime is stopped and replaced.

## 9. Capabilities

| Capability | Contract | This machine | Fallback (bare machine) |
|------------|----------|--------------|-------------------------|
| `source` | `sync` → NDJSON items; `done <id>` (noodle's adapter contract) | `gh` issues (v1) | plugins later: Slack, email, Linear, local `issues/*.md` |
| `tracker` | read, comment, label, link PR; **no create** | `gh` | same plugins |
| `identity` | agents act under their own forge identity, never the maintainer's token | a GitHub App or a machine user | a separate token per role |
| `isolate` | one workspace per item | `git worktree` in `~/.softwarp-slices/<repo>/<id>` | same |
| `spawn(role, tier, brief, ws)` | start an agent with least privilege for its role. Operations follow Gas City's runtime protocol: start, is-running, peek, nudge, stop (idempotent); one conformance check (`softwarp doctor spawn`) per binding. After an orchestrator crash it adopts the live agent and never starts a second. It sets cwd, model, effort and permission mode explicitly, inherits nothing from the caller, and never puts environment values on a command line | a transient systemd user service per operation, `softwarp-agent-<run>-<operation>`, that runs softwarp's sandbox (§12) around `claude -p` or `codex exec` with the harness's own sandbox off. The service outlives the CLI, and systemd refuses a second start under the same name. The CLI prints how to watch it: `systemctl --user list-units 'softwarp-agent-*'` and `tail -f <run>/logs/<operation>.log \| cat -v` (row 19; the log lies outside the agent's sandbox, and `cat -v` shows its control bytes as text). The machine config's `agents` section names each role's harness, model and effort, which intake pins into the run's manifest, and its `harnesses` section names each harness's install and credential file | `claude -p` / `codex exec` with a log file |
| `status(handle)` | checked live | `get_agent_status` | `pgrep` + log tail |
| `send(handle, msg)` | starts with `[coordinator]` | `send_agent_prompt` | resume the session |
| `verify` | run the app as a user would | the repo's verification skill | a `run` command from `WORKFLOW.md` |
| `review(diff)` | findings list from read-only reviewer agents of another vendor; required on every PR. The orchestrator counts rounds from its own run log, and reviewers that post use their own identity | local Paseo reviewer tabs (Codex `gpt-6-astra`, Claude, the free GLM lane). The GitHub bots (CodeRabbit, the Codex connector) are optional and off today because of token cost and rate limits | a second agent from another vendor |
| `clean_code` / `clean_prose` | remove slop | `pstack:deslop` / `pstack:unslop` | prompt files |
| `notify` | tell the maintainer | PushNotification / Paseo | issue comment + stdout |
| `schedule` | run on an interval | Paseo schedule / CronCreate | cron or a systemd timer |
| `attach` | images on a PR | `gh --attach` (gh ≥ 2.99) | link to the CI artifact |
| `shell_shim` | wrapper for privileged tools | `sg docker -c` | none |

**Least privilege per role** (from Vercel Foreman): triage and verifier agents are read-only. The implementer writes only in its worktree. Only the orchestrator pushes, labels and comments.

**Model tiers:** `free`, `cheap`, `standard`, `strong`, mapped per binding. On this machine:
- `free`: GLM 5.3 Flash on the maintainer's homelab vLLM, through Pi (`vllm/GLM-5.3-Flash-EXL3`) or Hermes. It costs time, not money. Use it for mechanical roles: first-pass triage classification, schema-retry repairs, gardening scans, metrics summaries. A Pi + GLM binding is also the cheapest proof of a third harness and a third model family.
- `cheap` / `standard` / `strong`: haiku / sonnet / opus or fable (per `pstack-models.md`). Codex (`gpt-6-astra`) is the other-vendor model for reviews and cross-judging.

### Skill routing on this machine

| Station | pstack | Matt Pocock | Fallback |
|---|---|---|---|
| R | `create-` / `maintain-verification-skill` | `improve-codebase-architecture` | prompt |
| 1 | — | `triage` (followed as steps; it is human-invoked only) | prompt |
| 2 | `architect` | `to-spec`, `grill-with-docs`, `to-tickets`, `wayfinder` (large work) | prompt |
| 3 | `tdd`, feature / bug-fix / perf playbooks | `implement`, `tdd`, `diagnosing-bugs` | prompt |
| 4 | `deslop`, `make-pr-easy-to-review`, `unslop` | `pr` | prompt |
| 5 | `swarm`, `prove-it-works` | `code-review` (Spec axis) | prompt |
| 6 | `babysit`, dev-rules review loop | — | prompt |
| 8 | `correct`, `reflect`, `recall` | `retro` | prompt |

`pstack:correct` (new in 0.9.81) is the core of station 8. It finds mistakes that agents repeat and makes each one impossible, in this order: architecture, then types, then a lint whose error names the fix, then a test, and docs last. Each new check must fail on a real past mistake.

**Matt Pocock's new skills can only be started by a user prompt** (`disable-model-invocation: true`). An agent cannot call them itself. The orchestrator's brief is the user turn, so the orchestrator starts them by writing the slash command into the brief (for example `/triage #42`).

**Note:** pstack's `autopilot-full` playbook already covers stations 3–7 on this machine. It runs one agent per PR, swarm-verifies each head, and the maintainer clicks every merge. It can be the station 3–7 binding here. The orchestrator still owns state, gates and metrics.

## 10. Binding discovery

`softwarp doctor` probes the machine and writes `~/.config/softwarp/bindings.yaml`:

- harness CLIs on PATH (`claude`, `codex`, `pi`, `hermes`, …);
- orchestrators (Paseo, Orca, noodle);
- installed skills and their versions **in each harness's skill directory**. A drift between directories is a warning. Example found on 2026-10-09: Claude Code loads pstack 0.9.27, the marketplace has 0.9.81, and `~/.agents/skills/pstack:*` (read by other harnesses) points to a local clone at 0.9.30;
- tracker host (from the git remote);
- reviewer bot and its trigger (from the authors of recent PR comments);
- tool versions and quirks (`gh` version, docker group via `getent`).

**Precedence:** repo `WORKFLOW.md` > machine `bindings.yaml` > detected > fallback.

**noodle** can be a `spawn` and `schedule` binding, but only in `manual` mode, with the implement skill opening a PR. Its own merge step MUST NOT run, and it supports only Claude and Codex.

## 11. WORKFLOW.md (per repo)

One file in the repo root, in the Symphony format: a settings block plus a markdown prompt body. The settings block is JSON, which is also valid YAML, so the Native AOT binary needs no YAML library. softwarp reads it at the pinned trunk commit, never from a worktree.

```markdown
---
{
  "trust": 1,
  "tracker": "github",
  "branch": "feat/{issue}-{slug}",
  "test_report": "trx",
  "targets_join": "|",
  "commands": {
    "build": ["…"],
    "test": ["…"],
    "test_targeted": ["…", "{targets}"],
    "lint": ["…"],
    "run": ["…"],
    "services_up": ["…"]
  },
  "verify_skill": ".agents/skills/verify",
  "review_bot": "none",
  "wip_limit": 3,
  "routines": [{ "name": "banned-patterns", "every": "1d", "skill": "gardening-scan" }]
}
---
Repo-specific guidance for every agent …
```

- `branch` follows the repo's own convention and MUST contain `{issue}`, so softwarp can find its branch and PR again after a crash.
- `commands` are argv arrays, never shell strings. `{targets}` is filled from the approved spec's test targets.
- `test_report` is required: `trx` or `junit`. `test_targeted` writes that report into `/softwarp/out`, and the gate runner counts the tests that ran from it. A missing report or zero tests fails the gate.
- `targets_join` is optional. When it is set, all approved targets are joined with it into the one argv element that `{targets}` stands for (a dotnet filter joins with `|`). When it is absent, each target is its own argv element. Each target is still validated on its own.
- `review_bot`: `coderabbit`, `codex` or `none`. The local agent review always runs.

**Where the commands come from**, in order: AGENTS.md → CI workflow files (they list the real commands) → manifests (`package.json`, `*.sln`, `Cargo.toml`, `go.mod`, `pyproject.toml`, `Makefile`, `justfile`). If no source gives a command, ask once and write the answer into the file.

## 12. Policy

Each rule is **enforced** (code blocks the action) or **instructed** (prompt only). Over time, instructed rules move to enforced.

| Rule | Mode |
|------|------|
| Never merge (rungs 0–2). Never push with an explicit refspec. The orchestrator's own push is the one exception: it pushes from its trusted bare clone with `refs/heads/<branch>:refs/heads/<branch>`, no `+` and no force, so no repository config can change what it pushes. | enforced (only the orchestrator pushes; pre-push guard) |
| Never file issues without approval; offer a batch at the end. | enforced (`tracker` has no `create`) |
| Every coordinator prompt starts with `[coordinator]`. | enforced (`send` adds it) |
| Push when the build is clean and targeted tests pass, then prove. Fixes are new commits, never force-pushes. | enforced (station order) |
| Every PR gets the local agent review; it is never skipped. When a repo has `review_bot` on: find the reviewer, read both surfaces, reply on the thread, trigger the next round. Stop after 2 rounds in a row with no product defect. | enforced (station 6 gate; orchestrator counts rounds) + instructed (triage of findings) |
| Every background job states what it is, its output path, a watch command and an expected duration. | enforced by `spawn` |
| Images go up as attachments, never on branches. | enforced by `attach` |
| Call the user "the maintainer". | instructed |
| Agents never use the maintainer's token. | enforced (`identity` binding) |
| An approval counts only as a forge comment `/softwarp approve <digest>` that quotes the current spec digest, posted after the spec by the maintainer's account (a non-bot admin, matched by account id) and edited by nobody else. Nothing local counts, `decisions.tsv` included. This covers specs, scope changes, extra issues and ceiling raises. softwarp checks it again just before every push. | enforced (`Policy.Explain` on forge facts) |
| Agents run in softwarp's sandbox, the gate profile with the network shared, and the harness's own sandbox is off inside it. The environment is cleared, and the user manager's environment too (`env -i` before bwrap). The home is a tmpfs. The implement and cleanup agents can write their worktree, with the real `.git`; triage and spec agents get a throwaway overlay of it, so nothing they write (a file, a commit, a note in `.git`) reaches the repository, and holdouts never reach the implementer. The overlay needs bwrap 0.10 or later and overlayfs in user namespaces. softwarp tries every agent's sandbox around `true` before it starts the agent, so where the sandbox cannot start (no bwrap, a worktree that cannot be bound, no overlay support) the agent is not started and no attempt is spent. Every agent can write its own io directory. It can read, read-only, `/usr`, `/etc`, the configured toolchains, its harness and that harness's credential, and the host's DNS stub resolver. It cannot see the run store, other io directories, holdouts (except the spec agent's own io directory), `~/.softwarp`, the trusted clone, `~/.ssh`, `~/.config` or the session bus. It cannot reach its log's path (the log lies beside its io directory, under `holdout/` for the spec agent), so it cannot swap the log for a link, but it can read and rewrite the log's content through its own stdout (`/proc/self/fd/1`). The credential reaches the harness only through the variable the harness reads it from (`CLAUDE_CODE_OAUTH_TOKEN`, `CODEX_API_KEY`). It is never on a command line, in softwarp's environment, or in a file under the io directory or the worktree. A hijacked agent can still read its own harness's credential (the prototype proved it), so each credential is dedicated and revocable (row 19). No harness loads user settings, hooks, MCP servers, repository skills (Codex `-c skills.include_instructions=false`) or the repository's CLAUDE.md or AGENTS.md. softwarp reads an agent's output only from its harness's channel, once the agent's unit is inactive: the file Codex's `-o` writes, or the structured output of the last result event in Claude's log, read on the host. A file the agent wrote never stands in for Claude's result. An agent that runs code can still choose what that channel holds (write the file, or print its own result line), so the role's contract check is the boundary. Agents commit in their own repository, which has no remote, and never push. Until part B2, agents share the host network, so they reach the host's loopback services and abstract sockets (a known HIGH finding, accepted for part B1 only); B2 moves them to a private network namespace before the pilot and before any agent works on a real repository. Agent disk use (worktree, io directory, log) has no cap yet; part B2 takes it up. Agents never run Docker (Docker access defeats any sandbox). Docker-backed tests never run as gates; CI proves them. | enforced (`spawn` binding) |
| Gates run in a sandbox: bubblewrap inside a systemd user scope with memory, task and runtime caps; network off; environment cleared, systemd-run's own included (every process in the sandbox can read the environment bwrap started with, so systemd-run gets only `PATH`, `XDG_RUNTIME_DIR` and `DBUS_SESSION_BUS_ADDRESS`); the home hidden except softwarp's own package cache (`~/.softwarp/packages`, bound at `~/.nuget/packages`, where dotnet looks) and the configured toolchain directories inside the home, all bound read-only; the worktree writable and its `.git` read-only. softwarp reads the implementer's repository only in the same sandbox with the worktree read-only: the clean check, and the import into the trusted clone, whose `git-upload-pack --strict` runs there, so no path the repository names resolves on the host. Every git command has a timeout. Without bwrap or a user systemd manager the gate exits 75. Before gates, a trusted fetch fills the package cache from the lock files and project SDK references, read as data, through a synthetic project outside the repository, with its own NuGet.Config and no repository code. Packages that the pinned trunk's lock files do not name are capped per run and recorded for the maintainer. NuGet audit is off in gates and stays in CI. | enforced (gate runner, git isolation) |
| Agents never change trunk protection or rulesets. The maintainer changes them, and each change is recorded in the run log. | enforced (identity has no admin rights) + instructed |

## 13. Loops

From poteto's inner and outer loops.

- **Inner loop:** stations 1–7.
- **Outer loop:** source adapters bring context in, so the maintainer does not carry it by hand. v1 reads `gh` issues. Later: Slack, email, X. Triage reproduces each report on main before any work starts.
- **Grouping:** related items (for example, 30 performance reports) go to one coordinator as a batch, not to 30 agents. This avoids duplicate work and finds the shared root cause.
- **Gardening loop:** scheduled scanners (banned patterns, code quality) **append to a buffer**. They do not fix and do not file issues. Every few days an agent clusters the buffer, and the maintainer picks which clusters become work. This also fits the rule that issues need approval.
- **Feedback loop:** a retro after each run, plus periodic transcript mining (`pstack:reflect`, `recall`). Both look for repeated failures and maintainer interventions, and propose lint rules, types, scripts or skill changes. The proposals go to the buffer.

The buffer lives in `~/.softwarp/buffer/<repo>.md`.

## 14. Layout

```
factory/
  bin/softwarp             orchestrator: run, doctor, onboard, status
  PROCESS.md              stations and gates (the spec)
  POLICY.md
  schemas/                artifact schemas per station
  prompts/                versioned briefs: triage, spec, implement-*, verifier, reviewer, retro, clean-*
  bindings/
    claude-paseo-pstack.yaml
    codex.yaml
    bare.yaml
  templates/WORKFLOW.md
  skills/softwarp/SKILL.md entry point (Agent Skills format)
AGENTS.md                 entry point for harnesses without skills
```

## 15. Triggers

- **Manual:** `softwarp run 12 15 18` (or `/softwarp` in a harness that has skills).
- **Scheduled:** every N minutes, claim `softwarp:ready` items up to the WIP limit. Verifier and reviewer agents do not count against the limit.
- **Routines:** the gardening scanners from `WORKFLOW.md`.
- **Host:** this machine for v1. All state is in the tracker and the run dir, so moving to an always-on host later only needs a new binding.

## 16. Metrics

Per run, in `metrics.jsonl`:
- time from issue to handoff;
- review rounds, and confirmed product defects per round;
- verifier findings per head;
- **maintainer interventions** (each one is a bottleneck to remove);
- tokens and time per station;
- outcome (merged, abandoned, escalated), and reverts after merge.

These numbers decide when a repo moves up a trust rung.

## 17. Build order

1. `PROCESS.md`, schemas, briefs, and an orchestrator skeleton. Run one issue through stations 0–4 by hand on this machine. First check that a headless call (`claude -p "/to-spec …"`) can start a skill that only a user prompt can invoke.
2. Onboarding: stack detect, `WORKFLOW.md`, readiness check, verification skill.
3. Stations 5–7: verifier swarm, reviewer loop, handoff.
4. `softwarp doctor` and the bare binding. Prove it on a second harness (Codex).
5. Schedule, labels, WIP limit, stall detection.
6. Gardening and feedback loops, metrics.

## 18. Decisions

| # | Question | Decision |
|---|----------|----------|
| 1 | Who writes holdout scenarios? | The maintainer or a spec agent, either. |
| 2 | Spec approval threshold? | The maintainer approves every spec. |
| 3 | Tracker scope? | `gh` in v1, behind the pluggable `source` / `tracker` capabilities. |
| 4 | Coordinator host? | This machine in v1; an always-on host later. |
| 5 | Is the workflow deterministic? | The process is (§4). The agents' output is not. |
| 6 | Default trust rung? | 1, supervised. |
| 7 | Matt Pocock's new skills? | Installed (2026-10-09) in `~/.agents/skills`. |
| 8 | Pilot (build step 1)? | Cluckwork #1223: a growth cap for legacy files and a 1,500-line cap for test files. The maintainer's decision is recorded in the issue, and its acceptance criteria are checkable mutations. It re-runs the case that failed the pilot's acceptance check (§20). The Platform Hardening coordinator (Paseo agent `0e442060…`) drives it through the `softwarp` CLI, after its in-flight PRs merge. |
| 9 | Agent identity? | A GitHub App. The maintainer logs out of `gh` on this machine. Agents get short-lived installation tokens. Git pushes over HTTPS through the `gh` credential helper and there are no SSH keys, so the logout also removes the maintainer's identity from `git push`. |
| 10 | Orchestrator language? | C# on .NET 10, released as a Native AOT binary (one per OS, built in CI). The maintainer owns and reviews it in their main language, and it can use the same build guards as Cluckwork. |
| 11 | Build or adopt (Gas Town, Gas City)? | Build (2026-10-20). The Gas City trial (`design/gascity-trial.md`) found its defaults open exactly where softwarp must hold: plain gate edges and errored gates let the next step start, `--force` passes any gate, and a crashed agent's replacement repeats side effects. It also has a heavy stack and packs with no license. Borrowed: exit 75, fail-closed gates, the runtime protocol for `spawn`, adopting live agents after a crash, per-role routing. Revisit when many agents in parallel matter. |
| 12 | How does the maintainer approve a spec? | A browser comment `/softwarp approve <digest>` (or `/softwarp revise <digest>: …`) on the issue, for every spec. No local file or CLI verb grants approval (2026-10-20). |
| 13 | `WORKFLOW.md` settings syntax? | JSON front matter: no dependency, valid YAML, AOT-safe (2026-10-20). |
| 14 | Isolation in step 1? | Harness sandboxes (Claude Code, Codex) with write and read limits, from step 1. Agents never run Docker. A separate OS user only if the sandbox proves too weak (2026-10-20). Replaced by row 19 (2026-10-22): on this machine the harness sandboxes limit writes and the network, not reads, so softwarp's own sandbox wraps every agent. |
| 15 | Branch names? | The repo's own convention, set in `WORKFLOW.md` (`branch`), containing the issue number (2026-10-20). |
| 16 | PRs per run? | One. A spec with several tickets goes to one implementer, ordered by their blocking edges (2026-10-20). |
| 17 | Gate sandbox, network and packages? | The maintainer, 2026-10-22: "all recommended". (1) Gates run with the network off. Before gates, a trusted fetch downloads packages from the repository's lock files and project SDK references through a synthetic project outside the repository, and runs no repository code. softwarp commits its own lock files. (2) The gate runner sets `NuGetAudit=false`; audit stays in CI. (3) Docker-backed tests never run as gates; CI proves them. (4) The systemd user scope (memory, task and runtime caps) is required. Without it or bwrap, the gate fails closed with exit 75. (5) Gates bind the maintainer's `~/.nuget/packages` read-only (replaced by row 18). (6) `WORKFLOW.md` gains `targets_join`: all approved targets are joined into the one argv element `{targets}` stands for (dotnet uses `\|`), and each target is still validated on its own. (7) A timed-out gate records the reserved exit code `TimedOut = -2`, next to `RefusedLog = -1`. Timeouts are 30 minutes for build and 30 minutes for targeted tests, configurable. |
| 18 | Package cache, new packages? | The maintainer, 2026-10-22: "ok go with your suggestions", answering the suggestion "give softwarp its own package cache". (1) softwarp owns its package cache at `~/.softwarp/packages`. The trusted fetch fills it, and gates bind only that cache, read-only, at `~/.nuget/packages` inside the sandbox, where dotnet looks. Gates never see the maintainer's own `~/.nuget/packages`. (2) Packages from outside the pinned trunk's lock files stay capped at 20 per run and are recorded for the maintainer, as built. This row replaces item (5) of row 17. |
| 19 | Agent spawn: sandbox, lifetime, credentials, network, skills, instruction files, commit identity? | The maintainer, 2026-10-22: "All recommended", answering the eight recommendations of the agent spawn prototype. (1) softwarp's own bwrap profile wraps every agent, and the harness sandboxes are off inside it (Claude `--permission-mode bypassPermissions`, Codex `--dangerously-bypass-approvals-and-sandbox`). (2) Each agent runs as a transient `systemd-run --user` service named from its operation, not as a Paseo tab. The maintainer watches with `tail -f <io>/agent.log` and `systemctl --user list-units 'softwarp-agent-*'`, and the CLI prints both when it starts an agent. (3) Claude agents use a dedicated long-lived token from `claude setup-token`, which the maintainer keeps in a file of mode 0600 that the machine config names. Never the maintainer's own login. (4) Codex agents use `CODEX_API_KEY` from a file of mode 0600 that the machine config names; the maintainer sets a spend limit on the key. (5) Network: part B2 installs passt and moves agents to a private network namespace before they work on real repositories. Until then (part B1) agents share the host network, and B2 must replace it before the pilot. (6) Skills: a pinned read-only copy, with its version in the run manifest (part B2). (7) No harness loads CLAUDE.md or AGENTS.md by itself (Claude `--setting-sources ""`; Codex `-c project_doc_max_bytes=0`, both checked against a fake local endpoint in part B1). The factory's prompt tells the agent to read them as documents about the repository. (8) Agents commit as `softwarp implementer <softwarp-implementer@invalid>` through `GIT_AUTHOR_*` and `GIT_COMMITTER_*`, with `GIT_CONFIG_NOSYSTEM=1` and `GIT_TERMINAL_PROMPT=0`, until the GitHub App exists. A hijacked agent can read its own harness's credential, from the bound file or the harness's `/proc` entry; no sandbox can hide it, which is why each credential is dedicated and revocable. This row replaces row 14. The local review of part B1 moved the log out of the io directory to `logs/<operation>.log` beside it, so the agent cannot swap it for a link the watch command would follow; the CLI prints the actual path. |

## 19. Open questions

1. **Cost.** Proposal: record tokens, time and maintainer interventions from the first run. Set no ceiling until there are about 10 runs. A run over budget escalates; it is never stopped silently.
2. **Orchestrator language:** decided (§18, row 10).
3. **Draft or ready PR?** `autopilot-full` opens PRs as ready, and some review bots skip drafts. Recommendation: ready.
4. **Rebase or merge trunk into the branch?** `autopilot-full` rebases onto trunk before handoff and publishes with `git push --force-with-lease` on the PR's own branch. dev-rules says a fix is a new commit and never a force-push. Option A: allow `--force-with-lease` only for a rebase of the PR's own branch, and never for a fix. Option B: merge trunk into the branch, with no force-push at all.
5. **Second-vendor reviewer** on a machine with only one vendor: fall back to a different model from the same vendor?
6. **App permissions:** grant Contents, Pull requests, Issues, Checks (read) and Actions (read). Do not grant Administration, so the app cannot change rulesets or protection. Workflows write is open: agents edited `ci.yml` in the pilot. Without it, those PRs go to the maintainer.

## 20. Pilot record: Cluckwork guardrails (2026-10-10 to 2026-10-11)

A manual run of stations 3–7: one coordinator prompt, one worker per task, and about 20 merged PRs. A read-only audit afterwards found five lessons, now built into the design:

| Lesson | Evidence | Where it lives now |
|---|---|---|
| Agents shared the maintainer's GitHub account, so no action could be traced to its author. | Ruleset toggles, approvals and Codex review rounds were all authored by `mforce`. | §9 `identity`, §12 |
| Acceptance drift went unchecked. | The file-size guard was redesigned to names only without sign-off. #1154 was closed on false acceptance text. Task D shipped more than its top 3. | §7 scope judge, §12 approvals |
| Proof was stale. | The SalesPage captures were taken at `1789288b`, but the merged head was `9441aef1`. | §7 proof freshness |
| Review rounds could not be verified. | Local Codex reviews were posted as the maintainer, with no bot record. | §9 `review` |
| Strict mode does not scale with PR volume. | Strict was switched off to clear a batch, and stale #1195 turned `main` red for 18 minutes. | §6 merge queue |

## References

- poteto with Matt Pocock, "shipping 1,000's of PRs a month at SpaceX" (2026-10-03): https://www.youtube.com/watch?v=MN9dGgmLyso. Transcript: `~/videotranscripts/2026-10-03-poteto-matt-pocock-software-factory-MN9dGgmLyso.txt`
- pstack `poteto-mode` → `playbooks/autopilot-full.md`
- StrongDM Software Factory / Attractor: https://factory.strongdm.ai/products/attractor
- OpenAI Symphony: https://openai.com/index/open-source-codex-orchestration-symphony
- Vercel Foreman / course: https://vercel.com/academy/creating-a-software-factory
- Matt Pocock skills: https://github.com/mattpocock/skills
- noodle: https://github.com/poteto/noodle
- Gas Town: https://github.com/gastownhall/gastown; Gas City: https://github.com/gastownhall/gascity (fit check `design/gascity-fit.md`, trial `design/gascity-trial.md`)
- AGENTS.md, Agent Skills, MCP, GitHub Spec Kit, 12-Factor Agents
