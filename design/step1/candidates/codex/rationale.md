## Usage (caller's view)

The coordinator advances one durable action per call. Repeating a call reconciles its previous action. Illustrative output uses run id `cluckwork-1223`.

```sh
# Coordinator, after the pilot's prerequisite PRs merge
softwarp run 1223 --repo ~/dev/cluckwork       # intake, pins inputs, claims item
softwarp advance cluckwork-1223               # triage, reproduce on pinned trunk
softwarp advance cluckwork-1223               # spec, local tickets, private holdouts
softwarp status cluckwork-1223                # waiting for approval; spec digest

# Maintainer
softwarp spec cluckwork-1223                  # spec text, digest, approval instructions
# In the forge's web UI, using the maintainer's separate authenticated device:
# comment: approve-spec cluckwork-1223 sha256:<digest>
softwarp approval cluckwork-1223 --receipt <comment-url>

# Coordinator
softwarp advance cluckwork-1223               # implementation commits
softwarp advance cluckwork-1223               # build and targeted-test exit codes
softwarp advance cluckwork-1223               # code cleanup and PR prose
softwarp advance cluckwork-1223               # gates again if cleanup changed HEAD
softwarp advance cluckwork-1223               # push and open ready PR linked to spec
softwarp status cluckwork-1223                # PR URL and exact published HEAD
```

An unchanged cleanup head needs no extra gate call; that call publishes instead. Later `advance` calls return the existing PR. Exit codes are 0 for completed actions, 2 for invalid input, 3 for a gate failure, 4 for waiting or escalation, and 5 for a concurrent caller. `--json` supplies the same typed result to coordinators. There is no force or skip-gate option.

```csharp
using Softwarp;
RunId run = await factory.StartAsync(new IntakeRequest(repoPath, 1223), ct);
AdvanceResult result = await factory.AdvanceAsync(run, ct);
// A maintainer or coordinator can supply the URL; only its remote author matters.
ApprovalCheck check = await factory.RecordApprovalAsync(run, commentUrl, ct);
```

## Problem

Step 1 must carry Cluckwork #1223 through stations 0–4, recover from interrupted commands, and permit both human and agent callers. Agent output and local approval files are untrusted. Inputs must be pinned; only observed command exits authorize publication. This package sketches the CLI, not the pilot's implementation.

## Shape

A reconciliation state machine selects the first unmet obligation from immutable receipts and fresh observations. It stores no mutable station cursor. `Factory` owns intake, approval checks and advancement; callers never arrange gates themselves. `NextAction` exposes only the work selected by policy. This keeps the public interface small while hiding recovery and gate ordering, per interface-depth and boundary-discipline.

Tracker labels own coarse work disposition. An immutable run manifest owns input pins, branch identity and canonical artifact hashes. Attempt receipts own completed local actions; git owns commit and branch facts; the forge owns PR and approval facts. Labels never prove that a gate passed. `progress.md`, `children.tsv`, `decisions.tsv` and `metrics.jsonl` are rebuildable views. A lost manifest cannot be reconstructed from a label alone and blocks recovery rather than guessing. Receipt prerequisites make obsolete evidence ineligible, per single-source-of-truth.

Every worker writes to its attempt directory, then the orchestrator validates and imports its output. A short per-run OS lock covers each CLI action, including its synchronous worker call. It prevents two local callers from publishing or launching twice. Different runs do not share a lock. A durable intent precedes each external action; stable operation ids let bindings find an existing job, comment or PR after a crash. Unknown completion blocks retry until reconciled. This deliberate single-host serialization is needed because publication is a shared side effect, per separate-before-serializing-shared-state.

Source-generated records supply both artifact schemas and JSON parsing. Semantic validation rejects absent reproduction evidence, invalid ticket edges, escaping paths, empty criteria, or holdouts embedded in implementer inputs. Approval hashes bind the run, spec, tickets, holdouts and pinned inputs. Only a fresh forge lookup matching the configured immutable maintainer account id can produce `ApprovedSpec`; local `decisions.tsv` entries are mirrors. Changing the package invalidates approval and dependent implementation receipts. No maintainer token enters this machine.

`WORKFLOW.md` keeps YAML front matter and Markdown. A compiled static YAML deserializer handles a closed settings DTO and rejects duplicate or unknown settings. Missing commands stop intake with a configuration request; onboarding discovery remains step 2. Binding selection follows repo, machine, detection, fallback precedence and is frozen in the manifest. Explicit unavailable choices fail. Bindings and serializer metadata are compiled in, per foundational-thinking.

## Synthesis decision

n/a (single candidate)

## Tradeoffs accepted

- We accept synchronous CLI calls and a run lock in exchange for a small recovery model on one host. Remote multi-host claiming is unsupported in step 1.
- We accept a browser approval step in exchange for independently attributable consent under a shared OS account. `approval` verifies consent; it cannot create it.
- We accept an OS isolation prerequisite in exchange for enforced holdout and credential separation. A plain child-process binding must refuse execution if it cannot enforce its role's access. Prompts alone do not enforce access.

## Alternatives considered

A mutable station row plus `advance` is smaller initially but makes callers repair disagreements between labels, cursor, commits and PRs. Reconciliation hides that work inside the CLI. A general event-sourcing engine hides persistence but exposes event migration and effect-handler registration for five stations; fixed receipt kinds and a pure planner are enough here.

## Open questions and risks

Can the first machine provide a sandbox that excludes the signing key, other processes' credentials and holdouts while preserving the agent's required tools? This is the least certain binding decision. Does the maintainer accept that a missing run manifest requires explicit recovery rather than automatic reconstruction from prose?

## Next implementation step

Implement the pure planner with fake-capability crash cases for gate freshness, changed approvals, duplicate advancement and a push that succeeds before its receipt is saved.
