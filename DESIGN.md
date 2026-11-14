---
name: DataBridge
description: Wayfinding signage for comparing and synchronizing two SQL Server databases; every screen says where data comes from and where it goes.
colors:
  sign-navy: "#0B2A4A"
  sign-lettering: "#FFFFFF"
  sign-lettering-muted: "#B4C5D9"
  sync-teal: "#14B8A6"
  logo-bolt-amber: "#F59E0B"
  rail-lettering-selected: "#2DD4BF"
  brand-accent: "#2563EB"
  sign-alert-red: "#B3261E"
  rail-slate: "#0F172A"
  rail-slate-dark: "#060B16"
  rail-selected: "#1E293B"
  rail-selected-dark: "#172036"
  rail-lettering: "#FFFFFF"
  rail-lettering-muted: "#94A3B8"
  ground: "#F1F5F9"
  ground-dark: "#0B1220"
  panel: "#FFFFFF"
  panel-dark: "#141D2F"
  sunken: "#EAEFF5"
  sunken-dark: "#080D18"
  hairline: "#D5DDE7"
  hairline-dark: "#273449"
  rule: "#0F172A"
  rule-dark: "#F1F5F9"
  text: "#0F172A"
  text-dark: "#F1F5F9"
  text-muted: "#475569"
  text-muted-dark: "#94A3B8"
  code: "#0F172A"
  code-dark: "#CBD5E1"
  state-add: "#0F766E"
  state-add-dark: "#2DD4BF"
  state-info: "#1D4ED8"
  state-info-dark: "#7EA6F2"
  state-caution: "#A34A00"
  state-caution-dark: "#F59E4A"
  state-danger: "#B3261E"
  state-danger-dark: "#F2766B"
  state-excluded: "#556274"
  state-excluded-dark: "#8D99AB"
  surface-add: "#E1F3F0"
  surface-add-dark: "#0E2E2D"
  surface-caution: "#FCEEDF"
  surface-caution-dark: "#36291B"
  surface-danger: "#FBEAE8"
  surface-danger-dark: "#3A1E24"
typography:
  display:
    fontFamily: "Atkinson Hyperlegible Next"
    fontSize: "28px"
    fontWeight: 700
    lineHeight: "32px"
  title:
    fontFamily: "Atkinson Hyperlegible Next"
    fontSize: "20px"
    fontWeight: 700
    lineHeight: "26px"
  subtitle:
    fontFamily: "Atkinson Hyperlegible Next"
    fontSize: "15px"
    fontWeight: 600
  body:
    fontFamily: "Atkinson Hyperlegible Next"
    fontSize: "13px"
    fontWeight: 400
  caption:
    fontFamily: "Atkinson Hyperlegible Next"
    fontSize: "12px"
    fontWeight: 400
  label:
    fontFamily: "Atkinson Hyperlegible Next"
    fontSize: "12px"
    fontWeight: 700
    letterSpacing: "1.2px"
  numeric:
    fontFamily: "Atkinson Hyperlegible Next"
    fontSize: "13px"
    fontWeight: 400
    fontFeature: "tnum"
  code:
    fontFamily: "JetBrains Mono"
    fontSize: "12px"
    fontWeight: 400
    lineHeight: "19px"
rounded:
  none: "0px"
  control: "2px"
  app-tile: "14px"
spacing:
  half: "4px"
  base: "8px"
  panel: "16px"
  page: "24px"
  wide: "32px"
components:
  route-sign:
    backgroundColor: "{colors.sign-navy}"
    textColor: "{colors.sign-lettering}"
    typography: "{typography.display}"
    padding: "16px 32px"
    height: "112px"
  route-sign-alert:
    backgroundColor: "{colors.sign-alert-red}"
    textColor: "{colors.rail-lettering}"
    padding: "16px 32px"
    height: "112px"
  nav-rail:
    backgroundColor: "{colors.rail-slate}"
    textColor: "{colors.rail-lettering-muted}"
    width: "200px"
  nav-item:
    textColor: "{colors.rail-lettering-muted}"
    typography: "{typography.body}"
    padding: "8px 16px"
    height: "32px"
  nav-item-hover:
    backgroundColor: "{colors.rail-selected}"
    textColor: "{colors.rail-lettering}"
  nav-item-selected:
    backgroundColor: "{colors.rail-selected}"
    textColor: "{colors.rail-lettering-selected}"
  panel:
    backgroundColor: "{colors.panel}"
    rounded: "{rounded.none}"
    padding: "{spacing.panel}"
  panel-sunken:
    backgroundColor: "{colors.sunken}"
    padding: "12px"
  toolbar:
    backgroundColor: "{colors.panel}"
    padding: "0px 24px"
    height: "40px"
  statusbar:
    backgroundColor: "{colors.panel}"
    padding: "4px 16px"
  button:
    rounded: "{rounded.control}"
    padding: "7px 16px"
    height: "32px"
  button-danger:
    textColor: "{colors.state-danger}"
    rounded: "{rounded.control}"
    padding: "7px 16px"
    height: "32px"
  button-danger-hover:
    backgroundColor: "{colors.surface-danger}"
    textColor: "{colors.state-danger}"
  button-danger-disabled:
    textColor: "{colors.state-excluded}"
  callout-caution:
    backgroundColor: "{colors.surface-caution}"
    textColor: "{colors.text}"
    padding: "12px"
  callout-danger:
    backgroundColor: "{colors.surface-danger}"
    textColor: "{colors.text}"
    padding: "12px"
  data-grid-row:
    backgroundColor: "{colors.panel}"
    typography: "{typography.numeric}"
    height: "32px"
  data-grid-row-two-line:
    height: "48px"
  data-grid-header:
    textColor: "{colors.text-muted}"
    typography: "{typography.label}"
    padding: "0px 4px 0px 12px"
    height: "32px"
  code-block:
    backgroundColor: "{colors.sunken}"
    textColor: "{colors.code}"
    typography: "{typography.code}"
    padding: "12px"
  progress-bar:
    rounded: "{rounded.none}"
    height: "6px"
  tab-item:
    typography: "{typography.subtitle}"
    padding: "0px 12px"
    height: "40px"
---

# Design System: DataBridge

## Overview

**Creative North Star: "Wayfinding Signage"**

DataBridge reads like airport signage. The first thing on every screen is a full-width primary-navy route sign: where the data comes from on the left, a heavy sync-teal signage arrow, a heavy teal rule, and where it goes on the right, set at display size in white lettering. The user reads the sign first, then scans what differs, then reviews plan and SQL, and approves knowing exactly which database will change. The world rejects the neutral grey tree-grid-SQL tool in which source and target identity shrink to a pair of combo boxes.

Everything below the sign is quiet: a light slate (or deep navy-slate, at night) ground, flat white panels drawn with hairlines, dense virtualized grids, and stock Fluent controls re-tinted from the tokens so the controls stay the ones users already know. State speaks in signage colours, teal, blue, orange, red, grey, and every state also carries its own glyph shape. Focus, selection and primary buttons use the brand accent blue; primary navy and sync teal never do that work.

The logo is one database cylinder, its bands running from accent blue to sync teal under a light-blue cap, struck by an amber lightning bolt: data, moved fast. The app icon sets it on a rounded primary-navy tile (`Assets/Brand/databridge-icon.svg`); the tile-less mark (`databridge-mark.svg`) draws the bolt's keyline in the surrounding text colour. Amber (`#F59E0B`) is the logo's alone and never appears in the UI. The brand palette is primary navy `#0B2A4A`, accent blue `#2563EB`, sync teal `#14B8A6`, background `#F1F5F9` and text `#0F172A`.

The system is an Avalonia 12 theme (`src/DataBridge.Desktop/Themes/`): Tokens, Icons, Controls and the DataBridgeTheme entry point. Light and dark follow the OS. The sign itself does not invert.

**Key Characteristics:**
- One signature element, the route sign, in primary navy with sync-teal direction (or alert red), identical in both themes.
- Primary navy and sync teal are reserved for identity and direction; accent blue carries interaction.
- Flat hairline panels; only overlays lift.
- Dense: 32px rows, 13px body, an 8px armature.
- State is always colour plus a distinct glyph shape.
- Atkinson Hyperlegible Next for everything; JetBrains Mono only for SQL, identifiers and measured values.

## Colors

The brand palette: a reserved primary navy, sync teal for direction, accent blue for interaction, slate neutrals, and five state colours tuned per theme to clear WCAG 2.2 AA.

### Primary
- **Sign Navy** (`sign-navy`, brand primary `#0B2A4A`): the route sign ground and the app icon tile. Static across themes.
- **Sign Lettering** (`sign-lettering`, white): every label and name on the navy sign. Detail lines on the sign (host, server version) use **Sign Lettering Muted** (`sign-lettering-muted`).
- **Sync Teal** (`sync-teal`, `#14B8A6`): the sign's arrow, its rule and its 2px bottom edge; the bottom band of the logo cylinder. **Rail Lettering Selected** (`rail-lettering-selected`, a lighter teal) letters the selected navigation item.
- **Alert Sign Red** (`sign-alert-red`): replaces sign navy when the route is dangerous (same database on both sides). All lettering, the arrow, the sign rule and the bottom edge turn white. Static across themes.

### Secondary
- **Brand Accent Blue** (`brand-accent`, `#2563EB`): the Fluent accent, so focus, selection tints and primary buttons; the top band of the logo cylinder.
- **Rail Slate** (`rail-slate`, `rail-slate-dark`): the 200px navigation rail. Rail items letter in **Rail Lettering Muted** (`rail-lettering-muted`), go white on hover over **Rail Selected** (`rail-selected`), and turn teal bold when selected.

### Tertiary (state)
Colour follows what a sync would do to the target.
- **Add Teal** (`state-add` / `-dark`): created on target, cleared, verified, completed. Shares the sync-teal family because a completed sync is the success state.
- **Change Blue** (`state-info` / `-dark`): changed, informational.
- **Caution Orange** (`state-caution` / `-dark`): awaiting approval, needs attention, unsupported object, unstable snapshot.
- **Danger Red** (`state-danger` / `-dark`): destructive, only in target (would be dropped), blocked, failed.
- **Excluded Grey** (`state-excluded` / `-dark`): not part of the plan; also disabled destructive buttons.
- **State surfaces** (`surface-add`, `surface-caution`, `surface-danger`, with `-dark`): the pale grounds inside callouts and the danger button's hover.

### Neutral
- **Slate Ground** (`ground` / `ground-dark`): window background.
- **Panel White** (`panel` / `panel-dark`): panels, grids, toolbar and status bar.
- **Sunken** (`sunken` / `sunken-dark`): code wells and inset areas.
- **Hairline** (`hairline` / `hairline-dark`): 1px panel frames and horizontal grid lines.
- **Rule** (`rule` / `rule-dark`): heavy rules in the page body.
- **Text** (`text` / `text-dark`), **Text Muted** (`text-muted` / `-dark`), **Code** (`code` / `code-dark`).

### Named Rules
**The Reserved Navy Rule.** Sign navy and sync teal mean identity and direction only: route sign, selected nav item, app icon. Never state emphasis, never a highlight, never a button.

**The Signage Does Not Invert Rule.** The route sign, alert sign and app icon are the same colours in light and dark. Only the ground around them changes.

**The Brand Accent Rule.** Focus, selection and primary buttons use accent blue `#2563EB`, set as the Fluent accent in both palettes (`DataBridgeTheme.axaml`); Fluent derives its lighter and darker steps. White on accent blue is 5.2:1.

**The AA Floor Rule.** Every text and state token clears 4.5:1 on ground, panel and sunken in its theme (lowest measured: light add on sunken 4.73:1; light add on add surface 4.76:1). White sign lettering is 14.5:1 on navy, sign lettering muted 8.3:1, the teal arrow 5.8:1; white on alert red is 6.5:1; selected rail teal is 7.9:1 or better. New tokens must hold the same floor in both themes.

## Typography

**Display / Body Font:** Atkinson Hyperlegible Next (Regular, SemiBold, Bold; bundled, OFL)
**Mono Font:** JetBrains Mono (Regular, SemiBold; bundled, OFL)

**Character:** A hyperlegible humanist sans built to separate look-alike glyphs, which matters when database names differ by one character; a mono that is strictly for machine text.

### Hierarchy
- **Display** (Bold, 28px, 32px line): database names on the route sign. Nowhere else by default.
- **Title** (Bold, 20px, 26px line): screen or dialog titles.
- **Subtitle** (SemiBold, 15px): tab items, sign detail lines (host and server version), section headings inside panels.
- **Body** (Regular, 13px): default for all controls and grid cells.
- **Caption** (Regular, 12px, muted): metadata, second lines in two-line grid rows, status text.
- **Label** (Bold, 12px, 1.2px tracking, authored in capitals, muted): sign field labels (SOURCE · READ ONLY), panel names and grid column headers. A label names the thing it sits on; it never sits as an eyebrow above a page heading.
- **Code** (JetBrains Mono, 12px, 19px line): SQL, identifiers in code contexts, measured values.

### Named Rules
**The Five Steps Rule.** The scale is 12 / 13 / 15 / 20 / 28. Nothing is set between steps.

**The Tabular Figures Rule.** Grids and numeric text use tabular figures (`tnum`) so counts align in columns.

**The Machine Text Rule.** Monospace only for SQL, identifiers and measured values. SQL never soft-wraps: a wrapped line at column 0 reads as a new statement. Host code in a horizontal scroll viewer.

## Layout

The window is a fixed anatomy: a 200px slate navigation rail on the left; a full-width route sign across the top of the content (112px minimum, 16px by 32px padding); a 40px toolbar holding plan or comparison status; the main area of grid plus side panels (SQL, validation); a status bar with progress and cancel along the bottom.

Spacing is one 8px armature with a 4px half-step: 4, 8, 16, 24, 32. Panels pad 16px, pages and toolbars pad 24px horizontally, sunken wells and callouts pad 12px. Rows are 32px; two-line comparison rows are 48px.

Within a panel, the primary action sits bottom-right, rightmost in a right-aligned button row; secondary actions sit to its left. Destructive opt-in buttons live inside the danger callout they relate to, never in the primary slot.

The verified sample screens (Synchronization Preview, Schema Comparison) show this anatomy as usage guidance; they were preview scaffolding and are not shipped views.

## Elevation & Depth

Flat. Panels are separated by tone (ground vs panel vs sunken) and 1px hairlines, not shadows. Only overlays (menus, flyouts, tooltips, dialogs) lift, using Fluent's stock overlay treatment. State changes are instant: no transitions are authored.

### Named Rules
**The Single Rank Rule.** There is one elevation rank above the page: overlays. Panels never cast shadows.

**The Instant State Rule.** Hover, selection and state changes apply immediately, without easing.

## Shapes

Square signage panels. Panels, grids, callouts, the route sign and progress bars have no radius; controls carry a barely-there 2px corner (also the overlay radius). The one rounded form is the app icon tile (14 on a 64 grid), following platform icon convention; it does not license rounded UI.

Glyphs are authored on a 16 by 16 grid as solid geometric shapes: plus, diamond, minus bar, solid circle, ring, solid square, circle with bar, hollow square, triangle. The signage arrow has a heavy shaft and a 45-degree head. The logo (64 by 64) is one three-band cylinder with an elliptical cap and a lightning bolt with a 3px navy keyline, drawn flat with no gradients so it holds at 16px. The sign rule is an 8px solid bar between source and target.

Borders: 1px hairline frames on panels, 1px bottom edge on the toolbar, 1px top edge on the status bar, 2px full frame on callouts (never a side stripe), 2px sync-teal bottom edge on the route sign.

## Components

### Route Sign (signature)
The source-to-target sign at the top of every screen that involves two databases.
- **Structure:** left block: label (SOURCE · READ ONLY), display-size database name, subtitle detail (host · server version); centre: 56px signage arrow; 8px sign rule; right block: TARGET · WILL BE CHANGED, name, detail.
- **Colour:** sign navy ground, white lettering, sync-teal arrow, rule and bottom edge; detail lines in sign lettering muted. Identical in both themes.
- **Alert state** (`route-sign.alert`): alert red ground; all lettering, arrow, rule and bottom edge white. Used when both sides resolve to the same database. Whether execution in progress also uses it is an open decision for the Execution Monitor.

### Navigation Rail
- **Style:** a keyboard-navigable list on rail slate, items 32px high with 16px by 8px padding, body size.
- **States:** rest in rail lettering muted; hover white on rail selected; selected teal (rail lettering selected) bold on rail selected. The app icon and name head the rail.

### Buttons
- **Shape:** stock Fluent button, 2px corner, 32px minimum height, 16px by 7px padding, SemiBold.
- **Primary:** Fluent's accent button: filled in accent blue. One per panel, bottom-right.
- **Secondary:** stock Fluent button face, to the left of the primary.
- **Destructive opt-in** (`Button.danger`): outlined in danger red with danger lettering on the stock face; danger surface on hover; keeps danger lettering and frame when pressed; excluded grey lettering and hairline frame when disabled. Never filled red, never in the primary slot, always inside its danger callout.
- **Focus:** a 2px accent-blue focus adorner, 3px outside the control, on every focusable element.

### Signals (state badges)
- **Style:** 12px glyph plus body-text label, 8px gap. The glyph takes the state colour; the label stays in text colour.
- **Variants:** add (plus, teal), change (diamond, blue), remove (minus bar, red), cleared (solid circle, teal), pending (ring, muted), awaiting (solid square, orange), blocked (circle with bar, red), excluded (hollow square, grey), warning (triangle, orange).

### Callouts
- **Style:** 2px full frame with 12px padding on a state surface. Caution by default; `callout.danger` for destructive or blocking conditions. Hosts its own glyph, a bold one-line statement, a sentence of detail, and the related destructive opt-in button.

### Data Grids
- **Density:** 32px rows, horizontal hairlines only, tabular figures, always virtualized.
- **Two-line** (`DataGrid.two-line`, 48px): object name on the first line, muted caption detail (`TextBlock.secondary`) on the second; used for comparison results.
- **Columns:** the scannable content takes the star column; truncated cells get an ellipsis and a tooltip with the full value; every column is at least header width plus sort-icon space, so headers never clip.
- **Headers:** label style (12px Bold, muted), 12px left padding.
- **Selection:** a pale accent-blue tint (16% at rest, 24% hovered), text stays in text colour, no edge stripe, so state glyphs keep their contrast. Lists outside the navigation rail (for example the Connections profile list) select the same way.

### Panels, Toolbar, Status Bar
- **Panel:** panel white, 1px hairline frame, 16px padding, square. **Sunken:** sunken tone, 12px padding, no frame (code wells).
- **Toolbar:** 40px minimum, 24px horizontal padding, hairline bottom edge; holds the plan or comparison name, metadata in caption, and a status signal on the right.
- **Status bar:** 16px by 4px padding, hairline top edge; progress text, a 6px square progress bar, count, Cancel, and snapshot warnings.

### Code Panels
- **Style:** JetBrains Mono 12/19 in code colour on a sunken well, inside a horizontal scroll viewer. No soft wrapping. Selection uses accent blue with white text.

### Magnitude Ramp
- **Style:** 8px-high bars in text colour, left-aligned, length on one fixed scale shared across the screen; `magnitude.danger` fills red for counts that are destructive.

### Tabs
- **Style:** 15px SemiBold, 40px high, 12px horizontal padding; Fluent's accent underline marks the active tab.

## Do's and Don'ts

### Do:
- **Do** put the route sign at the top of every screen that names a source and a target, source left, target right, at 28px Bold.
- **Do** switch to `route-sign.alert` when both sides resolve to the same database.
- **Do** pair every state colour with its glyph shape; pick the colour by what a sync would do to the target.
- **Do** place the primary action bottom-right of its panel, rightmost in a right-aligned row.
- **Do** keep destructive opt-in buttons outlined, inside the danger callout they relate to.
- **Do** use 32px grid rows, and 48px two-line rows with a muted detail line for comparison results.
- **Do** give truncated grid cells an ellipsis and a tooltip, and size every column to its header plus sort-icon space.
- **Do** host SQL in a horizontal scroll viewer with wrapping off.
- **Do** keep every new text or state token at 4.5:1 or better on ground, panel and sunken in both themes.
- **Do** include the theme from App.axaml via `avares://DataBridge.Desktop/Themes/DataBridgeTheme.axaml` with `RequestedThemeVariant="Default"`; reference Avalonia.Themes.Fluent and Avalonia.Controls.DataGrid; ship Themes and Assets/Fonts as AvaloniaResource in DataBridge.Desktop.

### Don't:
- **Don't** use sign navy or sync teal for state, emphasis, highlights, buttons or focus.
- **Don't** invert the sign, the alert sign or the app icon for dark mode.
- **Don't** convey state by colour alone.
- **Don't** fill a destructive button red or put it in the primary slot.
- **Don't** add shadows to panels, or transitions to state changes.
- **Don't** set type between the five steps, or use monospace for prose and labels.
- **Don't** soft-wrap SQL.
- **Don't** mark grid selection with an edge stripe or a saturated fill, or a callout with a side stripe.
- **Don't** introduce a second accent colour; accent blue drives interaction.
- **Don't** place a tracked-caps label above a page heading as an eyebrow.

### Future directions (open ceiling, not built)
- A workflow "you are here" sign strip (compare, plan, validate, approve, execute, verify).
- Rail pictograms for the ten screens.
- A heavy rule under grid headers.
