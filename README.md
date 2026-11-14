# DataBridge

DataBridge is a cross-platform desktop application for comparing and
safely synchronizing two Microsoft SQL Server databases. It compares
schema and data, shows the differences, builds a synchronization plan
you can review as operations and generated SQL, applies only what you
approve, and verifies the result.

It runs locally on Windows, Linux and macOS and connects directly to
your databases. There is no backend server.

> **Status:** early development (Phase 0, repository bootstrap). The
> solution builds and the app opens an empty window. No comparison or
> synchronization features exist yet. See
> [the development plan](docs/development-plan.md) for what comes next.

## Design principles

-   **Comparison never modifies either database.** Schema and data
    comparison are separate capabilities.
-   **Synchronization is explicit:** compare → plan → validate →
    review and approve → execute → verify.
-   **Nothing destructive by default.** DROP and DELETE require explicit
    opt-in and approval for each plan.
-   **Approved plans are immutable.** Any change needs a new plan and a
    new approval.
-   **The target is revalidated** immediately before execution.
-   **Large tables stream** in bounded batches. Rows are matched only by
    a primary key, unique constraint or a key you choose, never by
    position.
-   **Honest results:** partial completion is reported as such, and
    DataBridge does not claim rollback that SQL Server cannot guarantee.
-   **Secrets and row values are not logged.** Passwords are kept in the
    operating system's credential store.

Version 1.0 supports SQL Server only. Other database engines, automatic
bidirectional synchronization, scheduling and collaboration are out of
scope for 1.0 ([requirements](docs/requirements.md) §3 and §9).

## Building from source

Prerequisites:

-   The .NET 10 SDK, version 10.0.400 or a later 10.0.4xx patch (pinned
    in `global.json`).
-   Windows 10 or later, a recent Linux desktop distribution, or macOS.

``` text
git clone git@github.com:monirujjaman-dev/data-bridge.git
cd data-bridge
dotnet build DataBridge.sln
dotnet test --solution DataBridge.sln
dotnet run --project src/DataBridge.Desktop
```

Benchmarks run on demand in Release:

``` text
dotnet run -c Release --project benchmarks/DataBridge.Benchmarks
```

Integration tests will need a disposable SQL Server instance once they
exist (Phase 2). Never point them at a database you care about.

## Repository layout

``` text
src/
  DataBridge.Desktop/         Avalonia UI and composition root
  DataBridge.Application/     use cases and ports
  DataBridge.Core/            provider-independent domain
  DataBridge.Infrastructure/  SQL Server provider, SQLite, credential stores
  DataBridge.Shared/          generic result, error and diagnostic primitives
tests/                        unit, integration, performance and architecture tests
benchmarks/                   BenchmarkDotNet measurements
docs/                         requirements, architecture, domain model, plan, ADRs
```

Dependencies point inward: Desktop → Application and Infrastructure →
Core → Shared. `DataBridge.ArchitectureTests` enforces this on every
build.

## Documentation

-   [Requirements](docs/requirements.md): scope, user journeys and
    acceptance criteria.
-   [Architecture](docs/architecture.md): projects, provider
    abstractions, runtime flows and consistency model.
-   [Domain model](docs/domain-model.md): schema identity, typed values,
    comparison and plan models.
-   [Development plan](docs/development-plan.md): phased delivery and
    current status.
-   [Architecture decision records](docs/adr/README.md).
-   [Product context](PRODUCT.md) and [visual design system](DESIGN.md).

## Contributing

[CLAUDE.md](CLAUDE.md) holds the contributor and agent instructions:
product rules, conventions, dependency rules, the required workflow and
the definition of done. CI builds and tests every pull request on
Windows, Linux and macOS.

## License

[MIT](LICENSE)
