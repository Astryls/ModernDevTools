# Settings parity rule (internal)

Modern Dev Tools exposes its settings on **two distinct surfaces** that intentionally share one visual
language (`Palette`) but keep their own layouts:

- **Mod-settings page** — `Source/UI/SettingsPage.cs`, shown at Options → Mod settings → Modern Dev Tools.
  This is the **canonical, complete** home for every user-facing setting: a single scrolling column of
  suite cards (General, Analysis modules, Ignored warnings, Community data, Experimental hardening).
- **Modules & filters window** — `Source/UI/Dialog_Modules.cs`, a quick-access pop-out opened from the
  debug log's *Modules* button. A convenient subset for in-the-moment use.

## The rule

> Anything the Modules window exposes must also be reachable from the mod-settings page.

The settings page is the superset; the Modules window is a shortcut. Never add a control that lives *only*
on the Modules window.

## Checklist — adding a setting

1. Add the field + `Scribe_*` line to `ModernDevToolsSettings` (`Source/ModernDevToolsMod.cs`).
2. Add a control to `SettingsPage`: a row in an existing card (General / Community / Experimental
   hardening) or a new `DrawXCard` method wired into `DrawContent`.
3. If it belongs on the quick-access surface, add a matching control to `Dialog_Modules`.
4. Add the Keyed strings (label + description) to `Languages/English/Keyed/ModernDevTools.xml`.

## Shared styling, no drift

- Both surfaces draw with `Palette` (cards, `SectionHeader`, `ToggleRow`/`DrawToggle`, `GrayButton`,
  `StateStrip`, `LabelFit`, `BeginScroll`). Use those helpers — never vanilla `Widgets.CheckboxLabeled`
  or `ButtonText` inside a suite panel.
- Row-content logic that both surfaces need lives in one place: `Dialog_Modules.SourceTag` and
  `Dialog_Modules.CommunityStatus` are `internal static` and reused by `SettingsPage` so the two can't
  disagree on a module's provenance tag or the community-data status line.
- Size every text rect by measurement (`Text.CalcHeight`, `Palette.ToggleRowHeight`) — never a hardcoded
  pixel height — and reserve the scrollbar gutter unconditionally so descriptions never clip and the
  layout doesn't reflow-flicker.
