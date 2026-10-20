# Gas City trial

Started 2026-10-20. Plan: `gascity-fit.md` §6. Gas City v1.5.0 (checksum verified), bd 1.3.1 (checksum verified), Dolt 2.5.0 (no published checksum). Everything lives under `~/gc-trial` with `GC_HOME=~/gc-trial/home`. Scratch repo: [mforce/softwarp-trial](https://github.com/mforce/softwarp-trial) (private), issue [#1](https://github.com/mforce/softwarp-trial/issues/1).

## Verdict

| Check | Result |
|---|---|
| 1. Engine | **Pass with conditions.** Exit-code gates hold only inside an `abort_scope` scope. A plain `needs` edge and a controller error both fail open. Control-plane crashes repeat nothing. An agent crash repeats the agent step's side effect. |
| 2. Approval and identity | **Part 1 fail:** `gc bd update <gate> --status closed --force` passes any gate without running it. **Part 2 not run:** it needs the GitHub App. |
| 3. Harnesses and visibility | **Pass with fixes.** Claude, Codex and GLM on Pi all worked; Codex first needed its folder trusted. A 60-line shim runs workers as Paseo tabs (it loses Gas City's harness hooks). `gc costs` misses Codex and Pi and has no prices. |

By the §6 rule in `gascity-fit.md` ("build if closing a bead counts as an approval and no documented extension point can fix it"), check 2 points to **build**. A harness deny rule on `--force` would close it only as softly as the other deny rules.

Decision (maintainer, 2026-10-20): **build**. Recommendation was: **build the C# sketch, and borrow Gas City's proven ideas:**
- exit 75 for "could not check", which costs no attempt;
- scope-abort semantics;
- per-step `run_target` routing;
- the runtime protocol as the shape of softwarp's `spawn` binding (tmux or Paseo).

softwarp exists for gates that hold, effects that happen once, and approval no local process can mint. Those are exactly where Gas City's defaults are open, so adopting it would mean writing guards around most of it, on a heavy stack whose packs have no license. Revisit Gas City at build step 3 or later, if many agents in parallel matter.

## Setup findings

- **The file store cannot run agent work.** Agents close beads with `bd`, and `gc bd` refuses to run on `GC_BEADS=file`. The trial uses the default store: 2 Dolt servers plus 2 `bd` proxies (one pair for the city, one for each rig).
- **`gc start` installs a systemd user unit** (`Restart=always`), even when a supervisor already runs in the foreground. The unit then crash-looped against the running supervisor. Disabled for the trial.
- **It writes into the target repo:** untracked `.gc/`, `.beads/` and `.gitignore`.
- **It writes outside `GC_HOME`:** `bd` and `dolt` create `~/.beads` and `~/.dolt`. Anonymous usage metrics: `bd` on by default (turned off); `gc` is opt-in (off).
- **Claude sessions default to `--effort max`**, Codex to `xhigh` (`internal/worker/builtin/profiles.go`). Set `effort` per agent.
- **The `bd` pack's Dolt-maintenance agent ("dog") is a Claude session** on the default model. Turned off with `[[patches.agent]] suspended = true`.
- **Sessions load the maintainer's whole Claude setup:** plugins, MCP servers, hooks. Claude runs with `--dangerously-skip-permissions` by default.
- Gas City's control dispatcher is Go code, not an LLM. Only `check` scripts and orders run commands without an agent; every formula step is agent work.

## Check 1: engine

### Gate fails open on a controller error

Run 1 (`trial-gate`, workflow `st-3xt`): the check script path was outside Gas City's trusted roots (the city, the rig store, formula paths). The controller quarantined the control bead and **closed it** with `gc.outcome=fail`. That close satisfied the next step's `needs`, and the engine dispatched the next step to the worker. The script never ran. The worker declined only because the step was titled "Must never run".

### Plain `needs` ignores outcome

Spec §3.5: a failed step stops later steps only inside a scope whose members carry `gc.on_fail = "abort_scope"`. A plain `needs` edge is satisfied by any close. So `gascity-fit.md` R4 ("fail blocks downstream") holds only inside a scope.

| Run | Formula | Gate | Next step | Root outcome |
|---|---|---|---|---|
| 1 `st-3xt` | plain `needs`, script outside trusted roots | never ran (quarantined, closed `fail`) | **dispatched** | fail |
| 2 `st-ikt` | plain `needs` | failed 2 of 2 attempts | **dispatched and ran** (marker written) | fail |
| 3 `st-xfk` | scope plus `abort_scope` | failed 2 of 2 attempts (16 s apart) | skipped | fail |

So softwarp MUST put every gated step in an `abort_scope` scope. A lint could enforce it.

- Check scripts run with `HOME` set to the city folder, so they do not see `~/.gitconfig` or the `gh` login. A check that reads the forge needs its credentials passed in explicitly.
- Check scripts must sit under the city, a rig store or a formula path. A same-user agent can edit all of these.

### Crash and resume

Formula `trial-fix`, workflow `st-hfl`: implement (gate: tests pass on the committed head) → publish (push, one comment; gate: remote head equals local head), both in an `abort_scope` scope.

| Test | What was killed | Result |
|---|---|---|
| A | supervisor (SIGKILL) during implement, after the commit; restarted 26 s later | Live sessions adopted. Tests gate ran once and passed. No repeated work. |
| B | supervisor (SIGKILL) during publish, after push and comment; restarted 20 s later | 1 push, 1 comment, push gate ran once. Workflow `pass`. |
| C (`trial-crash`, `st-v9p`) | the Claude worker (SIGKILL), right after its comment | Reconciler closed the dead session, started a **fresh** session (no resume), re-dispatched the same bead. **Comment posted twice** (04:14:29, 04:15:01). |

- After restart A, the reconciler started a second worker session (pool max 1) to own the workflow root. It found nothing to claim and sent an escalation mail. Cost only, no wrong effect.
- The engine does not repeat side effects when the control plane crashes, but the data plane is not idempotent: any effect inside an agent step repeats after an agent crash. softwarp would move push, comment and PR creation out of agent steps into idempotent scripts keyed by an operation id (`gascity-fit.md` R11).
## Check 2 (part 1, no App needed): forging a gate

Workflow `st-823` (`trial-scope`, gate always fails), worker suspended. Acting as a same-user agent:

1. `gc bd update <control> --set-metadata gc.outcome=pass --status closed` was refused: "cannot close blocked issue … (use --force to override)".
2. The same command with `--force` succeeded. The scope check accepted `pass`, the next step became ready, and after the worker resumed it ran (marker at 04:18:29). **The gate script never ran**, and the gated work bead was still open.

So any process running as the maintainer's user can pass any gate with one documented flag, and the refusal message suggests that flag. The softwarp sketch has the same root weakness (a same-user agent can tamper with local files or binaries), but there it takes deliberate tampering, not one CLI flag an agent may try on its own. Under either option, the approval re-check on the forge immediately before push is the only hard local guard. Real enforcement needs OS separation (`rationale.md` question 3).

Again two worker sessions ran at once (`ci6e`, `gb1g`) with pool max 1.

## Check 3: harnesses, costs, Paseo

### Paseo runtime (R9: was "no")

Prototype shim: [`gascity-trial/gc-runtime-paseo`](gascity-trial/gc-runtime-paseo), about 60 lines of bash.
- A harness command (`claude`, `codex`, `pi`) becomes a Paseo agent tab through `paseo run`: model parsed from Gas City's argv, `GC_*` env and `PATH` passed with `--env`, title `gc:<session>`.
- Any other command (Gas City's control dispatcher) runs as a plain background process.

Results:
- `gc runtime check`: 12 passed, 0 failed. `is-attached` and `get-last-activity` are not implemented (optional).
- Workflow `st-7av` (`trial-paseo`) passed. The worker appeared in Paseo as `gc:worker-tr-wisp-9y96` (Sonnet, repo folder), then claimed, did and closed its bead.

Gaps:
- Gas City's harness hooks are lost, because Paseo builds the harness argv itself. The initial prompt `Run gc prime` replaces the SessionStart hook. Stop-hook drain and mail injection: untested.
- The runtime is per city, not per agent.
- Paseo modes differ per provider (`bypassPermissions`, `full-access`, none for Pi).
- With no `work_dir`, the Paseo CLI falls back to the **calling agent's workspace**. One check agent ran in this repo and changed nothing. The shim now unsets `PASEO_*` and always passes `--cwd`.
- `paseo terminal create` creates a Paseo workspace for its directory. The shim uses agent tabs instead.

So R9 moves from "no" to "partial: the prototype works, hooks are missing".

### Three vendors (R1, R7)

Workflow `st-6js` (`trial-3h`) passed, each step routed with `gc.run_target`:
- Claude Sonnet wrote a haiku.
- Codex `gpt-6-astra` reviewed it and caught the 8-syllable middle line.
- GLM 5.3 Flash on Pi via the homelab vLLM summarized both. It used the stock `builtin:pi` provider with `model = "vllm/<id>"`; no `[upstreams]` needed.

Problems:
- **Codex crash-looped 7 times** on its first-run "Trust this folder?" prompt. Gas City's Enter nudge did not pass it, so the reconciler killed and restarted the session each time. A `-c projects."<dir>".trust_level` override in argv did not help. A `~/.codex/config.toml` entry for the exact rig root did. A trust entry for `/home/mforce` does not cover a nested git repo.
- All agents share the rig's checkout (now on `softwarp/1`, with 2 modified and 6 untracked files). There is no worktree per agent unless one is configured.

### Costs (R12)

`gc costs` listed 4 of about 13 sessions: the Paseo one, but no Codex or Pi session and not the Claude worker of `st-6js`. It had no prices for current models (`EST_USD` 0.0000, 28 unpriced invocations). The stray worker `tr-wisp-21j` used 3.2k output and 481k cache-read tokens; the dog used 5.5k output.

## Notes

- **`gc rig add` committed to the target repo.** `bd init` committed `.beads/*` and `.gitignore` to local `main` as the maintainer (`3ab6798`, not pushed). The agent cut `softwarp/1` from it, so the pushed branch carries that commit. It also adds `.claude/skills` and `.agents/skills` symlinks (untracked). A rig MUST be a dedicated clone, never the primary checkout.
- Each `paseo run --cwd <dir>` from the shim created a Paseo directory workspace (3 for the rig in 64 s). Archived after the trial.
- My own `pkill -f control-dispatcher` matched my shell's command line and killed it. Use PIDs.

## Cleanup (2026-10-20)

Removed: `~/gc-trial`, `~/.dolt`, `~/.beads`, the systemd unit, the Codex trust entry (config matches the pre-trial state), and the trial's Claude and Pi session transcripts. Paseo `gc:` agents and the trial workspaces are archived. The private repo `mforce/softwarp-trial` is left for the maintainer to delete: the `gh` token has no `delete_repo` scope.
