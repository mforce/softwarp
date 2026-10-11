# Build step 1: grounding (2026-10-11)

**Task.** Sketch the types, signatures and module map for build step 1 of the software factory (softwarp): one issue goes through stations 0–4 (intake, triage, spec, implement, publish), driven by a deterministic CLI, written in C# on .NET 10 and published as a Native AOT binary. (The CLI was renamed from `factory` to `softwarp` after the design runners started; read `factory` below as `softwarp`.) Read `~/dev/software-factory/DESIGN.md` §4, §7, §8, §9, §11, §12, §14, §17, §18, §19 and §20 first. That document is the spec.

This file has two parts:
- **Part 1, portable assumptions:** what the design must hold on ANY machine, harness, model and stack.
- **Part 2, this machine:** facts about the machine where the first bindings and the pilot run.

**Rule: core types MUST NOT name any tool from Part 2.** Core types are stations, state, artifacts, approvals and CLI verbs. Only binding implementations may name a tool, for example a `ClaudeCliHarness` or a `PaseoSpawner`. A new machine adds or picks bindings; it never edits the core.

## Part 1: portable assumptions (any machine)

- **Harness.** Any agent CLI that can run one prompt headless in a given directory, with a model and environment variables, and return its final text. Some harnesses also enforce a JSON schema on the output. Either way, the factory validates the output itself.
- **Skills.** A harness may or may not support skills. Where it does, a slash command in the prompt starts a skill, even one that only a user prompt can invoke (Part 2 shows this on two harnesses). Every brief MUST also work without skills, through a prompt-file fallback.
- **Agent host.** A tab or process manager (Paseo, Orca) may or may not exist. The fallback is a plain child process with a log file.
- **Forge.** git, plus a forge API: GitHub in v1, a pluggable `tracker` later. Agents and the orchestrator act under their own scoped, short-lived identity, never the maintainer's.
- **OS user.** Agents may run as the same OS user as the maintainer. Any local file the maintainer can write, an agent can write. Approvals therefore MUST NOT rest on local files alone.
- **Runtime.** The CLI is one self-contained binary that needs no runtime installed. Bindings are compiled in and chosen by detection or configuration; no plugins are loaded by reflection.
- **Repo facts** come from the repo (`WORKFLOW.md`, then AGENTS.md, then CI files, then manifests), never from the factory's code.

### Constraints from DESIGN (non-negotiable)

- **Deterministic path.**
  - The state machine is code.
  - Gates are commands with exit codes.
  - Every agent artifact is validated against its schema.
  - Every input is pinned and recorded per run: the brief template version, skill versions, model ids, and the `WORKFLOW.md` commit.
- **State** lives in tracker labels (`factory:*`) and in the run directory `~/.factory/runs/<repo>/<issue>/` (`progress.md`, `decisions.tsv`, `children.tsv`, `metrics.jsonl`, `holdout/`). Stations are idempotent. A run resumes from the label, the branch, the PR and the run directory.
- **Stall detection** counts only side effects.
- **Least privilege.**
  - Only the orchestrator pushes, labels and comments.
  - The `tracker` capability has no `create`.
  - An approval counts only under the maintainer's identity: a tracker comment by the maintainer's login, or the maintainer's own words in `decisions.tsv`.
  - Holdouts are stored outside the implementer's worktree.
- **`WORKFLOW.md`** in each repo: YAML front matter plus a markdown body (Symphony format). Runners may propose another format if they give a reason.
- **Scope of step 1:** stations 0–4 only. Verifiers, the review loop, `factory doctor`, scheduling and metrics dashboards are out of scope. The design must not block them.

## Part 2: this machine (checked 2026-10-11)

**Harness CLIs**
- Claude Code 2.1.296:
  - `claude -p "<prompt>"` runs one prompt headless.
  - `--output-format json` returns an object with `subtype`, `is_error`, `structured_output` and `result`.
  - `--json-schema '<schema>'` forces structured output, which arrives in `structured_output`.
  - Other flags: `--model`, `--permission-mode`, `--append-system-prompt`, `--allowedTools` / `--disallowedTools`, `--add-dir`, `--settings`, `--session-id` and `--resume`.
- Codex 0.159.2: `codex exec -C <dir> -s <sandbox> -m <model> --output-schema <file> -o <last-message-file> --json "<prompt>"`.
- **Skills that only a user prompt can invoke CAN be started headless.** This covers the skills marked `disable-model-invocation: true`, such as Matt Pocock's `/triage`, `/to-spec` and `/to-tickets`. Put the slash command in the prompt. Checked: `claude -p "/probe"`, `codex exec "$probe"` and `codex exec "/probe"` all ran a probe skill marked user-only. With `--json-schema`, Claude returned the skill's answer as validated JSON.

**Paseo CLI** (`~/.npm-global/bin/paseo`)
- `paseo run [--cwd] [--env K=V] [--title] [--provider p/model] [--model] [--output-schema] [-d] [--wait-timeout] [--label k=v] [--json] <prompt>`
- `paseo wait <id>`, `paseo send <id> <prompt>`, `paseo inspect <id> --json`, `paseo logs <id>`, `paseo stop <id>`, `paseo ls --json`
- Agents MUST be tabs: never pass `--new-workspace`.

**GitHub identity**
- `gh` is installed. It will be logged out (DESIGN §18 row 9).
- Agents and the orchestrator authenticate with GitHub App installation tokens. Each token expires after 1 hour. It is made from `~/.config/factory/app.pem`: sign a JWT with RS256, then `POST /app/installations/{id}/access_tokens`. The app ID, installation ID and slug are not known yet; they come from configuration.
- git uses HTTPS through a credential helper. There are no SSH keys.

**.NET**
- .NET 10.0.401 is installed.
- Native AOT does not allow reflection-based JSON, so System.Text.Json source generation (`JsonSerializerContext`) is required.
- `System.Text.Json.Schema.JsonSchemaExporter` (.NET 9+) can export a JSON schema from source-generated type info. One C# record can therefore be both the schema an agent must follow and the type the factory parses.
- The base library has no YAML parser. YamlDotNet needs its static generator for AOT.
- Plugins cannot be loaded by reflection, so bindings are compiled in.
- AOT binaries are built separately for each OS.

**Pilot: Cluckwork #1223** (`mforce/cluckwork`, local clone `~/dev/cluckwork`)
- Build: `dotnet build Cluckwork.slnx` (warnings are errors).
- Targeted tests: `dotnet test tests/Cluckwork.Application.Tests --filter <expr>`. CI runs the full suite.
- Integration tests need Docker; on this machine, run them through `sg docker -c '…'`.
- Git identity is set in each repo's own config.
- The existing worktree convention is `~/.cluckwork-slices/<id>`. DESIGN says `~/.factory-slices/<repo>/<id>`.
- Issue body:

```
`FileSizeLimitTests` (#1153) keeps its legacy list as names only, so a listed file may grow without limit. It also excludes all test files: 27 test files under `web/src` are over 500 lines. See the amendment on #1154.

Maintainer decision, 2026-10-11:

## Source files: current size + 10%
- Record each legacy file once with a cap of its current size plus 10%, at least 50 lines. Example: `FeedPage.tsx` 522 → 574; `styles.css` 3,255 → 3,581.
- A listed file over its cap fails. Raising a cap needs a maintainer decision.
- Ordinary edits need no re-pinning. A file back at 500 lines or fewer still fails until its entry is deleted.

## Test files: their own cap of 1,500
- `*.test.ts(x)` and `*.spec.ts(x)` under the scanned roots are capped at 1,500 lines.
- Test files over 1,500 today (about 11) go on their own legacy list, capped the same way: current size + 10%, at least 50 lines.

## Acceptance
- Mutation shown red for each case: a listed source file grown past its cap, a listed test file grown past its cap, a new test file over 1,500, and an entry whose file dropped to the limit.
- The header comment and AGENTS.md (if it states the rule) describe the new behavior. #1215's file list stays in sync.

Follows #1153, #1154, #1215.
```

**Who drives the factory**
- The Platform Hardening coordinator, a Claude agent in Paseo, triggers transitions by calling `factory` CLI commands (DESIGN §4, last bullet). The CLI refuses a transition whose gate has not passed.
- The maintainer approves every spec (§18 row 2).
- Agents run as the maintainer's Unix user.

## What the sketch must settle

1. The CLI command surface: what the coordinator and the maintainer type.
2. The state model: where each fact lives, with one source of truth across labels, the run directory and git.
3. Artifact types and schemas for each station, and how agent output is validated.
4. The capability interfaces (`tracker`, `identity`/token, `isolate`, `spawn`, gate runner) and their bindings: Paseo, `claude -p`, `codex exec`.
5. How `factory advance` decides the next step and refuses an invalid one.
6. How an approval is detected, and how it is kept from being forged.
7. How `WORKFLOW.md` is parsed under AOT.
8. The project layout, and how tests replace capabilities with fakes.
9. How a machine's bindings are chosen (detection, config) without editing core types.
