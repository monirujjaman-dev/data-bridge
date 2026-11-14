# Product

<!-- impeccable:product-schema 1 -->

## Platform

web

DataBridge is a desktop application built with Avalonia UI, not a website.
Avalonia draws its own controls with one design language on Windows, Linux
and macOS, which is closest to `web` among the recorded values. It does not
adopt per-OS native design languages, so it is not `adaptive`.

## Stack

Avalonia UI (.NET 10, C#), MVVM with CommunityToolkit.Mvvm. Visual design is
delivered as Avalonia theme resources (ResourceDictionary / Styles XAML) under
`src/DataBridge.Desktop/Themes/` and assets under
`src/DataBridge.Desktop/Assets/`. Chosen by the user over an HTML reference
screen.

## Users

-   **Developer:** compares development, test and production database
    structures and data.
-   **DBA:** investigates differences and controls approved deployments.
-   **Technical lead:** reviews proposed changes and synchronization reports.

All are technical users working against real SQL Server databases, where
pointing an operation at the wrong target is the most costly mistake.

## Product Purpose

Compare the schema and data of two Microsoft SQL Server databases, inspect
differences, build a synchronization plan, review the generated SQL, apply
approved changes and verify the target. Success is an accurate comparison and
a synchronization the user could predict before approving it.

## Positioning

Two equal claims, per the user:

1.  **Safety-first synchronization:** an explicit compare → plan → validate →
    review/approve → execute → verify workflow. Nothing destructive by default,
    approved plans are immutable, and the target is revalidated immediately
    before execution.
2.  **Cross-platform and local-first:** runs natively on Windows, Linux and
    macOS with no backend server.

## Operating Context

-   Local desktop app connecting directly to two SQL Server databases.
-   Screens (FR-UI-001): Dashboard, Connections, New Comparison, Schema
    Comparison, Data Comparison, Synchronization Preview, SQL Preview,
    Execution Monitor, History, Settings.
-   Long-running streaming comparisons and executions with progress and
    cancellation.
-   Results can be very large (millions of rows); grids are virtualized and
    load incrementally.
-   Reports export to HTML and CSV.

## Capabilities and Constraints

-   Version 1.0 supports SQL Server only; schema and data comparison are
    separate capabilities.
-   Source and target identities (server, database) must be shown
    prominently, especially before execution; selecting the same database for
    both sides triggers a warning.
-   Operations carry a risk classification; DROP and DELETE require explicit
    opt-in and per-plan approval.
-   Execution results can be success, partial success or failure; no false
    rollback guarantee may be shown.
-   Row values are not persisted or logged by default.
-   Full product rules: `CLAUDE.md`, `docs/requirements.md`.

## Brand Commitments

-   Name: DataBridge. Logo: one database cylinder (blue to teal bands)
    struck by an amber lightning bolt. Palette: primary navy #0B2A4A,
    accent blue #2563EB, sync teal #14B8A6, slate neutrals (see DESIGN.md).
-   Follow the operating system's light/dark setting by default, and use
    the brand accent blue (#2563EB) for focus, selection and primary actions.

## Evidence on Hand

No screenshots, customers, testimonials, benchmarks or pricing exist. Do not
invent them.

## Product Principles

1.  The target is always visible: never let the user lose track of which
    database is being changed.
2.  Show the plan before the act: every change is previewable as operations
    and SQL before approval.
3.  Danger is explicit, never default: destructive and risky operations are
    marked, opt-in and separately approved.
4.  State honestly: partial completion, unstable snapshots and unsupported
    objects are reported, not hidden.
5.  Stay responsive at scale: large results stream in without blocking work.

## Accessibility & Inclusion

-   WCAG 2.2 AA contrast, focus visibility and keyboard operation in both
    light and dark themes.
-   Risk and difference states must not rely on color alone.
