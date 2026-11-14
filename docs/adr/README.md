# Architecture decision records

Each record captures one architecture decision: the context that forced
it, what was decided, what follows from it and what was rejected.
Records 0001--0008 record the baseline decisions in
`docs/architecture.md` §15.

| ADR | Decision | Status |
|-----|----------|--------|
| [0001](0001-desktop-application.md) | Local desktop application, no mandatory server | Accepted |
| [0002](0002-avalonia-ui.md) | Avalonia UI for the cross-platform desktop | Accepted |
| [0003](0003-modular-monolith.md) | Modular monolith with Clean Architecture layers | Accepted |
| [0004](0004-ado-net-comparison-engine.md) | ADO.NET, not EF Core, in the comparison engine | Accepted |
| [0005](0005-normalized-schema-model.md) | Custom provider-independent schema model | Accepted |
| [0006](0006-optional-dacfx-adapter.md) | DacFx is optional and isolated behind an adapter | Accepted |
| [0007](0007-sqlite-local-persistence.md) | SQLite for local application metadata | Accepted |
| [0008](0008-no-bidirectional-sync-v1.md) | No automatic bidirectional synchronization in v1 | Accepted |

## Writing a new record

-   Copy the structure of an existing record: Status, Date, Context,
    Decision, Consequences, Alternatives considered.
-   Number records sequentially and never reuse a number.
-   Write an ADR when a change alters approved architecture (`CLAUDE.md`
    workflow step 8), and when a design document asks for one (for
    example the key-ordering strategy in `architecture.md` §8, due in
    Phase 4).
-   Records are not rewritten after acceptance. To change a decision,
    add a new record and set the old one's status to
    `Superseded by ADR-NNNN`.
-   Update `docs/architecture.md` when a record changes what it
    describes, and add the record to the table above.
