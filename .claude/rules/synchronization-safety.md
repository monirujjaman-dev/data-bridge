---
paths:
  - "src/DataBridge.Core/Synchronization/**"
  - "src/DataBridge.Application/Synchronization/**"
  - "src/DataBridge.Application/ScriptGeneration/**"
  - "src/DataBridge.Infrastructure/Providers/SqlServer/**"
  - "tests/**/Synchronization/**"
---

# Synchronization safety

-   Use a dependency graph and topological ordering; detect cycles.
-   Validate permissions, object compatibility, foreign keys, unique
    constraints, identity behavior, target fingerprint and plan
    staleness.
-   Use parameterized DML.
-   Prefer `SqlBulkCopy` or staging-table strategies only when
    correctness and constraints are preserved.
-   Make transaction boundaries explicit and configurable. Record
    partial completion.
-   Do not disable constraints or triggers automatically.
-   Provide a generated-script preview and execution manifest.
-   Verify affected rows and post-execution state.

See `docs/architecture.md` §10 and `docs/domain-model.md` §9–11 (plan,
operation states, execution manifest, invariants).
