---
paths:
  - "src/DataBridge.Core/**"
  - "src/DataBridge.Application/SchemaComparison/**"
  - "src/DataBridge.Application/DataComparison/**"
  - "src/DataBridge.Infrastructure/Providers/**"
  - "tests/DataBridge.IntegrationTests/**"
---

# Database correctness requirements

-   Schema identity includes schema name, object name and object type.
-   Compare ordered key columns, index key order, included columns,
    uniqueness, filters and relevant index options.
-   Preserve identity seed/increment, computed expressions, defaults,
    collation, precision, scale, nullability and relevant SQL
    Server-specific metadata.
-   Handle composite keys with lexicographic keyset continuation
    predicates.
-   Fingerprint serialization must be unambiguous, typed,
    length-prefixed and NULL-aware. Hashes are an optimization, not
    proof of equality.
-   Treat `rowversion`/`timestamp` as provider-specific binary values;
    do not assume they can be copied as ordinary values.
-   Detect unsupported/incompatible objects and report them; do not
    silently skip them.
-   Define consistency/isolation behavior for concurrent source/target
    writes and report when a stable snapshot is not guaranteed.

See `docs/domain-model.md` (object identity, typed values, invariants)
and `docs/architecture.md` §8–9 (comparison design, consistency).
