# ADR-0014: Key ordering and row streaming for data comparison

**Status:** Accepted\
**Date:** 2026-11-15

## Context

Data comparison merges two row streams sorted by the matching key
(`architecture.md` §8). The merge is correct only if the in-memory key
order is exactly the order each server returned (FR-DAT-016). String
keys are the risk: SQL Server sorts them by collation, which may ignore
case, accent, width or kana and may differ between source and target.
The baseline in §8 proposed binary-collation ordering, to be confirmed in
Phase 4. FR-DAT-009 allows keyset pagination or ordered streaming.

## Decision

-   **One ordered streaming query per table and side.** `SELECT` the key
    and compared columns `ORDER BY` the key, read with
    `CommandBehavior.SequentialAccess` and handed to the merge in batches
    of the configured size. Memory is bounded by batch size and the
    number of tables compared at once, not by table size.
-   **Text keys are ordered with `Latin1_General_100_BIN2`.**
    `nchar`/`nvarchar` keys use `COLLATE Latin1_General_100_BIN2`;
    `char`/`varchar` keys are first cast to `nvarchar` so the order is
    by character, not by code-page byte. In memory, text keys compare
    ordinally as if the shorter one were padded with spaces, as SQL
    Server compares (`'a'` and `'a '` are the same key, and a tab
    sorts below the padding).
-   **Other keys use the server's natural order.** Numbers, dates and
    times, `bit` and binary compare naturally; `datetimeoffset` compares
    by UTC instant; `uniqueidentifier` uses SQL Server's byte-group order
    (`SqlGuid`). Floating point, XML, LOB and CLR types cannot be keys.
-   **The order is verified, not assumed.** Each stream must arrive in
    strictly increasing key order under the in-memory comparer. An equal
    neighbour stops matching as `DuplicateKeysFound`; a smaller one stops
    it as `KeyOrderMismatch`. Results so far are discarded and the
    reason is reported. Two sorted sequences that disagree must have an
    adjacent inversion, so any disagreement between server and comparer
    on the data actually read is caught.
-   **Matched keys are also compared exactly.** Keys equal under the
    ordering can differ as stored (trailing spaces, a `datetimeoffset`
    offset); such a row is reported as modified on the key column.
-   **Columns compare only when their stored values compare exactly.**
    The provider decides: for SQL Server, `char`/`nchar`/`binary` only
    with fixed-length columns of the same length, and date-times and
    floating point only within the same type. Other pairs are excluded
    with a reason, or rejected as keys.
-   **Bounded by bytes as well as rows.** A batch also ends at about
    16 MB of values. Stopping a table early cancels its command instead
    of letting SqlClient drain the remaining rows.
-   **Matching is exact.** Keys that a case- or accent-insensitive
    database treats as equal (`'abc'`, `'ABC'`) are matched as different
    rows and shown as source-only and target-only. Whether a target
    accepts such a row is a synchronization question, validated in
    Phase 5.
-   **Key columns must not allow NULL** on either side, for discovered and
    user-selected keys alike.
-   **Consistency:** each table's read runs in a `SNAPSHOT` transaction
    when the database already has `ALLOW_SNAPSHOT_ISOLATION ON`, else
    under `READ COMMITTED`; the mode achieved is recorded per table and
    side. Comparison never changes database settings and never uses
    `NOLOCK`. Each table has its own read session, so different tables
    are not read at the same point in time, and the two databases never
    are.

## Consequences

-   Correctness does not depend on reproducing SQL Server collations in
    .NET. A dataset the comparer cannot order like the server (for
    example supplementary characters next to U+E000–U+FFFF, if binary
    ordering compares them differently) fails safely with a clear reason
    instead of giving wrong results.
-   Ordering by a binary collation usually cannot use the key's index
    when the column's own collation differs, so the server sorts each
    table once. That costs tempdb space and time on very large tables but
    happens once per table, not per batch.
-   A long read under `READ COMMITTED` can see rows change while it runs;
    the UI says so whenever a table was not read from a snapshot.
-   The `.claude/rules/database-correctness.md` rule on keyset
    continuation predicates is replaced by this streaming design.

## Alternatives considered

-   **Keyset pagination** (`WHERE key > @last ORDER BY key` per batch).
    Each batch is a short query, and a failed read could resume. But with
    a binary-collation order on a non-binary column every batch re-sorts
    the remaining table, which is quadratic. Kept as a possible later
    optimization for keys that are not text or already binary-collated.
-   **Emulate each collation's order in .NET.** `CompareInfo` does not
    reproduce SQL Server's collation versions exactly, and getting it
    wrong silently corrupts the merge. Rejected.
-   **Match keys case-insensitively when the target ignores case.** It
    would hide real stored differences and needs a collation-exact
    comparer. Deferred to Phase 5 validation.
