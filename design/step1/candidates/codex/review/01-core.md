# Core review

The review covers only this candidate directory and the supplied design constraints. Expected load is one issue on one host with occasional concurrent CLI calls from the coordinator and maintainer.

`Policy.cs` is the only station-order policy. `NextAction` closes the set of decisions, and no CLI argument permits a caller to choose a station or override a gate. Evidence is nullable where an obligation is genuinely unmet. The planner must validate prerequisite hashes before accepting an artifact, and must reject successful gate receipts if either the head or command digest differs.

`Model.cs` distinguishes imported attempt receipts from live authoritative observations. Bounce targets are now durable. Retry counts come from the attempts rather than a separate counter. The implementation must distinguish malformed-output retries from station revision cycles, as the planner contract requires.

`Capabilities.cs` makes external side effects explicit. It contains no issue-create or merge method. Store locking is intentionally coarse for one run, and separate workers produce separate outputs. Launch deduplication remains binding work: a crash after launching but before recording the handle cannot authorize another worker until the binding resolves the operation id.

Approval authority comes from a forge fetch tied to a configured account id and content digest. Local files, CLI arguments and an agent's self-report cannot establish identity. A compromised same-user process could still tamper with binaries or keys absent effective sandboxing. The candidate therefore requires an enforced execution scope and a signing broker outside child visibility. That is a deployment prerequisite, not protection supplied by these C# types.

The review does not claim runtime correctness from placeholder bodies. The requested build checks signature consistency and source-generated serialization. The fault-injection and isolation tests in the module map are implementation work. YAML parsing also remains a stub; a successful sketch build is not evidence that a YAML library was validated under AOT.
