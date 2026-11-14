---
paths:
  - "src/DataBridge.Desktop/**"
  - "tests/DataBridge.Desktop.Tests/**"
---

# UI requirements

-   Use MVVM (CommunityToolkit.Mvvm); Views contain presentation only.
    No SQL or comparison algorithms in Desktop; view models call
    Application use cases.
-   Use virtualized grids and incremental result loading.
-   Do not bind millions of result rows to an in-memory observable
    collection, and never marshal huge result sets to the UI thread.
-   Show source and target identities prominently, especially before
    execution.
-   Display progress, cancellation, errors and operation status.
-   Support keyboard navigation and light/dark themes.

See `docs/architecture.md` §3 (Desktop) and §12.
