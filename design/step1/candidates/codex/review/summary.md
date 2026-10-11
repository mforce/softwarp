# Candidate review

What this change does: It defines a receipts-based CLI design for stations 0–4. It gives human and coordinator callers the same command interface and requires external approval evidence.

The maintainability and ponytail reviews found two gaps in the first sketch. A bounce target existed only in the worker result and would disappear on resume. `Attempt.BounceTo` now persists it. The planner also needed to send failed gates to bounded implementation repair instead of repeating the same command indefinitely. Its transition contract now states that rule.

No remaining structural blocker was found for a type sketch. The design has five source files, one orchestration class, explicit capabilities and a pure planner. There is no general event bus, dynamic plugin mechanism or redundant stage cursor. See [detailed review](01-core.md).

Verdict: Ship as a candidate design. Runtime behavior, security isolation and Native AOT publication remain implementation checks.

Verification: `dotnet build` restored and compiled successfully. A final `dotnet build --no-restore` after review edits also passed with zero warnings and zero errors. The five source files total 375 lines. Generated `bin/` and `obj/` directories were removed.
