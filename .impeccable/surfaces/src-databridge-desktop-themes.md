---
version: 1
slug: "src-databridge-desktop-themes"
primary_target: "src/DataBridge.Desktop/Themes"
related_targets: []
---

# Surface brief: DataBridge desktop theme

Scope: the app-wide Avalonia theme (tokens, control styles, glyphs, app icon)
for all ten FR-UI-001 screens; proving screens are Synchronization Preview and
Schema Comparison. Mode: Operate.

Audience/job: developers, DBAs and tech leads comparing two SQL Server
databases and approving a sync plan without ever losing track of the target.

Constraints: follows OS light/dark setting; OS accent colour drives focus,
selection and primary buttons; WCAG 2.2 AA in both themes; state never by
colour alone; virtualized grids; keyboard navigation.

History: the user first picked Interlocking Route, then switched to the
assigned Wayfinding Signage direction. The track-diagram world is retired.

## Direction contract

THESIS: Every screen tells you, like an airport sign, where data comes from
and where it goes. It refuses the neutral grey tree-grid-SQL tool whose
source/target identity is a small pair of combo boxes.

OWN-WORLD: Sign yellow #FFCC00 with anthracite #1E2124 lettering, reserved for
identity and direction (route sign, selected nav item, app mark); anthracite
rail; light drafting-grey or anthracite ground with flat hairline panels.
Signage state colours: green add/cleared, blue change, orange caution, red
danger, grey excluded, each with its own glyph. Atkinson Hyperlegible Next
with tabular figures; JetBrains Mono only for SQL and identifiers.

STORY: The user always reads the route sign first (source → target), scans
what differs, reviews the plan and SQL, and approves knowing exactly which
database will change.

FIRST VIEWPORT: Anthracite navigation rail left; full-width yellow route sign
on top, SOURCE · READ ONLY left, signage arrow, heavy rule, TARGET · WILL BE
CHANGED right at 28px display; toolbar with plan or comparison status; main
area grid plus SQL and validation panel; primary action bottom right of the
panel; status bar with progress and cancel.

FORM: Wayfinding Signage, my rank 4 of 7 (THE ROLL, assigned).
seed:370686d9daef9458. Raises kept: one type scale (12/13/15/20/28); one
magnitude ramp for counts; one 8px armature; single-rank elevation with
instant state changes.

FINISH: unreviewed and undocumented is unfinished; this build ends with the
finish review, the verdict, DESIGN.md, and every shipping raster carrying its
provenance

## Open decisions

-   Execution-time sign state (e.g. `route-sign.alert` while executing) needs
    confirming when the Execution Monitor is built (Phase 6).
