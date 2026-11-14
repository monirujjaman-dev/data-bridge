# ADR-0002: Avalonia UI for the cross-platform desktop

**Status:** Accepted\
**Date:** 2026-11-14

## Context

DataBridge must run natively on Windows, Linux and macOS
(`requirements.md` §2) and is built on .NET 10 and C#. The UI shows
large, virtualized result grids that load incrementally, progress and
cancellation for long-running work, and light and dark themes that
meet WCAG 2.2 AA (`PRODUCT.md`).

## Decision

Use Avalonia UI with the MVVM pattern and CommunityToolkit.Mvvm. Views
are presentation only; view models call Application use cases
(`architecture.md` §12). The visual design is delivered as Avalonia
theme resources under `src/DataBridge.Desktop/Themes/`, layered on the
Fluent theme and Avalonia's DataGrid.

## Consequences

-   One UI code base and one design language on all three operating
    systems. DataBridge does not adopt per-OS native look and feel
    (`PRODUCT.md`, Platform).
-   XAML, data binding and MVVM carry over from the WPF ecosystem, and
    compiled bindings catch binding errors at build time.
-   Avalonia's native dependencies (rendering, windowing, fonts) must be
    tested on every target OS (`architecture.md` §14).
-   Avalonia-specific types must stay in the Desktop project. Core must
    not reference Avalonia (`CLAUDE.md` rule 14), and the architecture
    tests enforce that.

## Alternatives considered

-   **WPF or WinUI.** Mature, but Windows-only. Rejected because Linux
    and macOS are required.
-   **.NET MAUI.** Its desktop support targets Windows and macOS; Linux
    is not supported. Rejected.
-   **Electron or another web UI shell.** Would add a second language
    and runtime to a .NET code base and increase memory use. Rejected.
