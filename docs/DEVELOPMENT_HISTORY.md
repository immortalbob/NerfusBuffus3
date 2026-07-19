# Nerfus Buffus III — development history

A consolidated, historical record of the revival's *findings → fixes* passes, kept for design
rationale and provenance. It folds together the two former logs: the source/verification audit
(`VERIFICATION_FIXES.md`) and the UI-wiring revival (`UI_WIRING.md`).

> **These are point-in-time notes.** Test counts, view pixel dimensions, and "next steps" reflect
> the moment each pass was written and are *not* the current state. For current behavior and
> numbers see the top-level `README.md`; for the mana/recovery system as it ships today see
> [`MANA_RECOVERY.md`](MANA_RECOVERY.md). (For example: the Options view later grew from 271 px to
> 290 px for the recovery controls, and the "five mana-regen modes" became six with the default
> spell-recovery mode — the references below to five modes describe the *original 2003 plugin*.)

---

## Part 1 — Verification findings → fixes (source audit against the Decal-dev corpus)


Every finding from the src5 cross-verification, and exactly what changed. Doc numbers refer
to the decaldev-research set. Re-run all three offline gates after any edit:
`dotnet run --project tests/NB3.Core.Tests -c Release -p:CoreTfm=net8.0` (97/97),
`bash tools/shimcheck/run-shimcheck.sh`, and — after any view-XML geometry edit —
`python3 tools/viewlint/viewlint.py` (the doc-14 sect. 10 lints; see the src11 section below).

## Build blockers (plugin would not compile)

| Finding | Fix |
|---|---|
| A1 — vendored MetaViewWrappers source missing (doc 10 §1) | Added the five canonical MIT files (© 2010 VirindiPlugins) under `src/NB3.Plugin/VirindiViews/`, taken verbatim from the community SamplePlugin-VVS template lineage. |
| A2 — `VVS_REFERENCED` not defined (doc 10 §2 / 12 §3): builds, loads, **no window, no error** | `<DefineConstants>$(DefineConstants);VVS_REFERENCED</DefineConstants>` in `NerfusBuffus3.csproj`. |
| A3 — `System.Windows.Forms.Timer` used, assembly not referenced | `<Reference Include="System.Windows.Forms" />` (+ `System.Drawing` for the wrapper's Point/Rectangle/Color). |
| A4 — `Decal.Filters` used, `Decal.FileService.dll` not referenced (doc 12 §2) | Added the reference with its own HintPath off `$(DecalSdk)`; `Private=false` like the others. |
| A5 — `_state.WieldedWeapon()`/`WieldedShield()` called but defined nowhere (found while fixing) | Implemented in `DecalGameState` over `WorldFilter.GetByContainer` + slot masks (keys 10 → 9 fallback, doc 16 §3.1 / 13 §9). **Diagnostic/direct-cast support only** (the recovered `/nbid` command): on modern servers weapon buffs are self-cast auras and shields inherit from the target's banes, so nothing in the planner targets weapons/shields by default — the GUIDs serve the optional "cast directly on a shield/armor piece" case. |

## Runtime findings

| Finding | Fix |
|---|---|
| B1 — `get_FileService<FileService>()` contradicts doc 13 §3 | `core.FileService as Decal.Filters.FileService` — the cast, never a generic/`Filter<T>()` call (`DecalSpellTable`). `DecalGameState` no longer touches FileService at all; mana resolves through the same table the planner uses (one source of truth). |
| B2 — `LongValueKey.Coverage` is the doc-13 §6 shipped-bug key | Coverage is read as `(LongValueKey)4` (`CLOTHING_PRIORITY_INT`), and translated into NB3's own scheme by the new `NB3.Core/NB3Coverage.cs` (docs 16 §3.1–3.2), closing COVER_MASK_RECOVERY.md's open item. Unit-tested (6 new cases). |
| B3 — `WornItems` matched pack items (any nonzero coverage) | Filter on `CURRENT_WIELDED_LOCATION_INT` = key 10 ≠ 0 first; only genuinely worn/wielded items reach the spellgroup match. |
| B4 — no portal-space discipline (doc 13 §10 crash class) | UtilityBelt-confirmed pattern in `PluginCore`: `_inPortalSpace = true` at startup, `ChangePortalMode` drives it, `LoginComplete` clears it, ~15 s watchdog releases a missed `ExitPortal`, and `CycleTick` performs **no** Actions calls while gated. `Actions.BusyState == 0` (`DecalGameState.ClientIdle`, fails closed) now gates every UseItem/equip (doc 13 §10.4). |
| B5 — `[BaseEvent("CommandLineText", "CommandLineText")]` dubious source arg | Dropped `[WireUpBaseEvents]`/`[BaseEvent]` entirely; all core events wired manually in `Startup` and unwired in `Shutdown` (doc 11 §1's recommended model): `CommandLineText`, `ChangePortalMode`, `LoginComplete`. |
| B6 — `Spell.Family/.Difficulty/.School/.ManaCost` bound directly (names vary per SDK — doc 13 §3) | `DecalSpellTable` reads all numeric fields **reflectively** over candidate names, per-field falling back to the embedded 2012 dump (`spellcat-2012.tsv`, now an embedded resource) — doc 16 §7.5's live-first/dump-fallback posture. Also: the table is built lazily at first `/nbuff` (post-login), not in `Startup`. |
| B7 — mechanics correction (owner): weapon buffs are now **self-cast auras** ("Aura of Infected Caress"), banes are self-cast whole-suit with **shields inheriting** (ACE-source-verified: every equipped enchantable Clothing item + shield), and direct item casts remain optional | Refined after cross-checking ACE's `SpellId` enum against the 2012 dump (doc 18 §6): the aura lines ARE in the dump under classic ids (BD Self I..VIII = 35/1612–1616/2096/4395, group 154) — the dump's `target=item` column is what's stale. `DecalSpellTable` now enumerates the live table exactly (IdNameTable `Length`+indexer, `GetById` sweep fallback) and classifies target from the live record's **`IsUntargetted`** flag first, dump second, name heuristics last. `MODERN_SPELL_MODEL.md` §3 and the planner's Item-path comment match the verified mechanics. |

## Registration / views / infrastructure

| Finding | Fix |
|---|---|
| C1 — invalid GUID in `register-NB3.reg` (`…-000000000NB2}` isn't hex); no `[Guid]` on the class | Minted `{915ED3D0-26CD-493D-80E8-34A3099FF511}`; applied as `[Guid]` + `[ComVisible(true)]` on `PluginCore` and in the .reg. .reg header now includes the 32-bit-RegAsm and hive-check instructions (docs 12 §4 / 09 §4.2). |
| C2 — 76 custom `NerfusBuffus3.*` progids across editor/charconfig/casting views | Converted: `NerfusBuffus3.List/TextColumn/IconColumn` → `DecalControls` equivalents; `NerfusBuffus3.Frame` → the thin-PushButton separator convention the control view already used. Zero custom progids remain. |
| C3 — width-less FIRST TextColumn renders zero px under VVS (doc 14 §9) | Columns reordered per list: icon columns first with `fixedwidth="18"`, one trailing width-less TextColumn absorbs the remainder. (Row-population code, when the views get wired, indexes icons 0..n-1 and text last.) |
| C4 — mixed `CheckBox`/`Checkbox` progid casing (doc 14 §7) | Normalised to `Checkbox` everywhere. |
| C5 — no offline gate for the glue (doc 15) | `tools/shimcheck/` — faithful shims + `run-shimcheck.sh`, compiling core + the three glue files with `csc` at LangVersion 7.3 (pinned in the csproj so the gate matches the real build). Passing. |
| sln omitted the plugin project | `NerfusBuffus3.csproj` added to the solution (builds on Windows; core + tests still build anywhere). |
| README drift (14 vs 61 cases; "worker thread" wording; RegAsm/offline caveats) | README rewritten: 67 cases, RegAsm 32-bit warning, ns2.0-needs-a-feed note, and step 4b now explicitly keeps casting on the Decal callback timer (doc 13 §10 discipline) instead of a worker thread. |

## Doc-14 audit status of the views

All four views: explicit `<view>` width+height, flat FixedLayout (no BorderLayout), every
List/Notebook carries both dimensions (mechanical grep audit in place), no custom progids,
single Checkbox spelling. Font-width clipping (doc 14 §10) remains a visual pass to do
in-game — recovered 2004 pixel widths may truncate under VVS's wider font.

---

# src10 → src11: VVS-spec compliance pass (against the overhauled corpus, updated4)

The VVS docs gained the live-build field-note sections (03 §10.11–§10.16, 14 §6–§10) after the
src5 audit above. This pass brings the plugin up to that spec. Gates after the change:
`bash tools/shimcheck/run-shimcheck.sh` (PASS), `python3 tools/viewlint/viewlint.py` (PASS, new),
core tests 97/97.

## Wireup + lifecycle

| Finding | Fix |
|---|---|
| D1 — stock `MVWireupHelper` aborts ALL binding on the first bad name; abort point varies by machine (03 §10.11) | `Wrapper_WireupHelper.cs` modified (MIT header kept, change noted): every `[MVControlReference]`/`[MVControlEvent]` binds independently; failures are recorded, never thrown; unresolved names sit in a retry queue (`RetryPendingWireups`, serviced ~1 Hz from the render-frame poll — lazy tabs wire when first shown); `GetWireupReport`/`GetPendingWireupCount` surface health. `Shims.cs` mirrors the new surface. |
| D2 — `WireupStart` ran BEFORE the manual core-event wiring inside one try/catch: a single wireup failure silently killed chat commands, the portal gate, and the cast monitor | `Startup()` reordered: core events (incl. the new `RenderFrame` poll) wire first; view creation is isolated in its own try/catch; wireup health is reported at `LoginComplete` (chat exists) and in `/nbdiag`, per 03 §10.11. |

## Backend visibility + window-state recovery

| Finding | Fix |
|---|---|
| D3 — no backend report; the §10.13 silent-DecalInject fallback was undiagnosable | Login banner now prints the backend (classified from `IView.ViewType` — what the window IS), plus VVS version and `Service.Running` at Startup vs now (reflection probe, works with VVS absent). `/nbdiag` repeats it. |
| D4 — no re-probe at first login | `TryRebuildOnVvs()` at `LoginComplete` (before the banner): if Startup fell back to DecalInject and VVS is running now, cached wrappers are nulled, per-name populate flags reset, and the view is re-created on VVS via `WireupStart` re-entry (which disposes the prior views). One shot, wrapped. |
| D5 — no `/reset` for state VVS persists in `vvs.s3db` (03 §10.14/§10.16) | `/nbreset`: restores the XML-default position AND size via `IView.Position` (UserW/UserH persist — a title-bar-only window stays 26 px forever), sets `Visible`, and clears `Ghosted`/`ClickThrough`/`Alpha` by reflection off the wrapper's `Underlying` with `Convert.ChangeType` (types vary by VVS build; "n/a" on the Decal backend). |
| D6 — no §10.5 control-resolution diagnostic | `/nbdiag` resolves all 16 main-view control names in one shot and prints the missing ones; also prints the exact ViewKey (`NerfusBuffus3:NB3`) for surgical `vvs.s3db` support SQL. |

## Views + UI wiring (doc 14)

| Finding | Fix |
|---|---|
| D7 — doc-14 §10 lint failures: right-edge overflows (control view `staticStatus` 210>200; five in the 270-wide Options view), text needing more px than declared (Extras-page instructions ~360px in 260; `checkboxFallbackTo6` ~314 in 271; "Profile Editor" ~101 in 95), Editor page lists 12 px past the ~293 px page interior, overlapping label/value boxes | All four views repaired: labels resized/shortened ("Fizzles:"→"Fizz:", "Casting:"→"Cast:", "Profile Editor"→"Editor", fallback caption shortened, Extras rewrapped to five ≤38-char lines), values pulled inside containers, Editor lists/buttons pulled above the page floor. New `tools/viewlint/viewlint.py` encodes both §10 lints (1 px tolerance) and exits nonzero on any issue — run it after every geometry edit. |
| D8 — buttons silent (14 §6.3) | Every `[MVControlEvent]` handler echoes success or a state-gated no-op ("no cycle running.", "select a configuration in the dropdown first…"); Editor/Options stubs say so and point at `/nbnew`/`/nbset`. |
| D9 — main view write-only: `UpdateStatusView` stub, `choiceLoadConfig` never populated | ~4 Hz `RenderFrame` poll (cheap reads only): `EnsureProfileCombo` populates the dropdown on first successful resolve keyed by control NAME (14 §6.1), invalidated on `/nbnew`; `UpdateStatusView` pushes Status/Spells/Left/Time/Fizz/Busy/Mana/Cast into the labels, diffed per label so quiet frames cost string compares; `PbStart` starts the dropdown's selection. |
| D10 — duplicate control names across views (`pbPause`/`pbResume`/`pbDismiss`) would bind first-match-wins once a second `[MVView]` is added | Renamed unique per view: `pbCastPause`/`pbCastResume`/`pbCastDismiss` (casting), `pbEdDismiss` (editor), `pbOptDismiss` (options). No code referenced the old names. |

Still open (unchanged from src10): the Editor/Options/Casting views exist as fixed, lint-clean XML
but are not yet created/wired (`/nboptions` and the Editor button say so in chat); wiring them must
follow the 14 §6 patterns (per-name flags, seed-once-then-poll-diff, chat acks) and 03 §10.12's
"every writer of a polled setting also writes the control" rule, since `/nbset` co-owns the options.

---

# Field fixes (post-release, from live play)

Gates after each: `bash tools/shimcheck/run-shimcheck.sh`, `python3 tools/viewlint/viewlint.py`,
and the core suite (`dotnet tests/NB3.Core.Tests/bin/Debug/net8.0/NB3.Core.Tests.dll`, currently
130/130). UI-side field fixes (secondary windows out of the VVS bar; the retired Editor Color
Scheme dropdown) are logged in **Part 2 — UI wiring** below (U13–U14).

| Finding | Fix |
|---|---|
| E2 — no way to generate a full profile for a specific character | New `/nbgen [name]` (Core `ProfileGenerator`, pure + tested) builds a self-buff profile from the LIVE **trained/specialized** skills. `IGameState.SkillTrainingLevel(charFilterSkillType)` returns the TrainingType rank (0-3) — DecalGameState reads it off the same `Skills[type]` SkillInfoWrapper (`.Training`) as the effective-skill path; `>=2` (Trained) gates each skill-mastery buff. Every family resolves to its era-stable stacking category through the shared editor catalog (`EnsureFamilyCatalog`, now used by both the Editor and `/nbgen`). **Cast order** is fixed at the front per the owner: Focus → Willpower → Creature Enchantment → Mana Conversion → Life Magic (bootstrap the casting stats so the rest land), then attributes / defences / weapon+utility masteries / Life vitals+protections / 7 banes+Impen. **Weapon auras are archetype-chosen** (not include-all): melee → Blood Drinker + Heart Seeker + Defender + Swift Killer; missile → the same minus Heart Seeker (accuracy is melee-only); war/void caster → Spirit Drinker + Hermetic Link only. Blood Drinker and Spirit Drinker share stacking group 154 (they converge to "Infected Caress" at L7), so ONE category-154 entry casts whichever the character knows — the caster case needs no new spell data. Each block is gated on its casting school being trained (Creature/Item/Life). `/nbskills` prints the detected Specialized/Trained skills to sanity-check the filter against the in-game panel. 6 new generator tests (order prefix, trained-in/untrained-out, all three archetypes, school gating). |
| E1 — "kits + H2M" regen drained health to ~60/320 and never healed | `ManaRegenController` compared **absolute** `CurrentHealth`/`CurrentStamina` against a floor of `50`, so a 50 read as "50 HP", not "50 %". On any real character 50 HP is never reached before H2M (which drains health for mana) does real damage, and the S2M modes had the same bug. Now the floor is a **percent of max** (`Pct(cur,max)`, div-by-zero-guarded to 100 % on a no-data read so a bad vital never spam-heals or drains). Default 50 = "under half", per the owner's expectation. New `NB3Settings.HealthFloorPercent`/`StaminaFloorPercent` (persisted, default 50) make it tunable from chat: `/nbset healthpct <1-99>` / `stampct <1-99>`. Regression tests: 60/200 (=30 %) heals under the 50 % default and casts H2M under a 25 % floor; a 30 %-stamina RestS2M rests. Confirmed clean: the heal path (`SelectItem(self)` + `UseItem(kit,1)`) issues no combat-mode switch, so it works from Magic mode. |


---

## Part 2 — UI wiring: reviving the dead views (2026)

The first Windows build loaded and the main window drew, but "nothing works in the UI":
the two secondary-window buttons on the main view (**Editor**, **Options**) were literal
stubs that only printed "isn't wired in this build yet", and the three recovered secondary
views (`nb3-charconfig.xml`, `nb3-editor.xml`, `nb3-casting.xml`) were embedded as
resources but never created, populated, or handled. This pass implements all of it.

## What the teardown of the original established

Ripping the original 2003 `NerfusBuffus2.dll` (400 KB native Win32 COM, © 2001–2003) apart:

- The **NSIS installer** was unpacked by walking the format directly (modern 7-Zip refuses
  it): firstheader `0xDEADBEEF`+`"NullsoftInst"`, then `[u32 size | high-bit=compressed]`
  DEFLATE sub-blocks. Out came the DLL, `BuffComplete.wav`, and the demo profile.
- The DLL's **PE resources** carried the six view XML schemas **CAB-compressed** under
  custom `VIEWS`/`XML_LISTS` resource types (hence the `Cabinet.dll` import). Decompressed
  and diffed against the new build's copies — the layouts already matched (the new build
  had recovered them), confirming the control **names and IDs** the code must bind to.
- The DLL's **string/type tables** gave the complete behavioural spec: the `/nb*` command
  set (`/nbuff … [,…]`, `/nbed`, `/nbinclude`, `/nbrefresh`, `/nboptions`, …), the editor's
  control names verbatim (`pbNewGroup`, `pbClearGroup`, `pbSaveGroup`, `choiceGroup`,
  `listSpellsInGroup`, `listCreatureSelf`, the `cbICover*` cover checkboxes, the `?` wizard
  buttons `pb*WizNamed/GUID`, …), the five mana-regen modes, the editor color-scheme names
  ("No Theme"/"Default Theme"/"Doc's Fusion"/"Forest Gump"), the include recursion guard
  ("Inclusion of this profile will result in infinite recursion!"), the duplicate guard,
  the profile-name character rules, and the completion line + `BuffComplete.wav`.

## Findings → fixes

| # | Finding (what didn't work) | Fix |
|---|---|---|
| U1 | `pbEditor`/`pbOptions` on the main view were stubs; the Editor/Options/Casting views were never created | `PluginCore` is now `partial`; the three views carry their own `[MVView]` on partial parts and are created by the one `WireupStart`. `PluginCore.Views.cs` maps all four views **by XML title** (attribute order is unspecified) and hides the secondary ones until opened. |
| U2 | No secondary-view controls were resolved/populated/handled | `PluginCore.OptionsView.cs`, `.EditorView.cs`, `.CastingView.cs`: every recovered control resolved defensively per-view (`CtlIn<T>`), populated on first successful resolve, polled+diffed, and given a `[MVControlEvent]` handler that echoes success or a state-gated no-op (doc 14 §6.3). |
| U3 | Options view had no model binding | Bound one-for-one to `NB3Settings` (the same option set the original kept in the registry): seeded on open, read back and saved per character on **Save**. (The recovered `choiceEditorScheme` combo was wired to nothing and was later retired — see the color-scheme follow-up below.) |
| U4 | Editor view had no model; the original stored per-level spell ids, which don't survive the era break | New `EditorCatalog` builds the pick lists by resolving the recovered **275-family** classic table against the **live** spell table → modern stacking **category** + **school** + target; the original's seven per-level icon columns collapse to one "add" column per family. New `ModernProfileStore` does New/Copy/Revert/Delete/Save/list (excludes `config_*` settings files). `ModernBuffEntry` gained the recovered per-entry target detail (`targetname`/`targetguid`/`itemname`/`itemguid`/`targetcover`); `ModernBuffPlanner` honours all of them. |
| U5 | `<Include>` / `/nbinclude` / multi-profile `/nbuff a,b` were absent | `ModernProfile.Includes` + `ResolveIncludes` (flatten, dedup, cycle-guarded — the original's exact semantics); `/nbuff` splits on commas; `/nbinclude` and the Extras tab both add includes; `/nbrefresh` rescans the folder into every dropdown. |
| U6 | Casting view (the live "NB3 Spells" list) was never shown | `PluginCore.CastingView.cs`: opened at cycle start, the remaining actions listed with the in-flight one marked, refreshed only when the cycle's cursor/count changes, closed at completion. `BuffCycle` exposes `Actions`/`Cursor` for it. |
| U7 | List columns: a width-less first/middle `TextColumn` renders zero-px under VVS (doc 14 §9) | Every list reworked to **fixed-width glyph/icon columns first, one trailing flexible `TextColumn`**: content list = `[X][^][v][text]`, pick lists = `[+][text]`, casting list = `[»][text]`. Row-population code indexes them accordingly. |
| U8 | Item-target GUIDs above `0x7FFFFFFF` (all real AC item guids) would overflow `int.Parse`/`Convert.ToInt32` | `ParseGuid` parses hex through `uint` and reinterprets to `int` (`unchecked`). |
| U9 | Completion was silent | Plays the recovered `BuffComplete.wav` (embedded `nb3-notify.wav`) via `System.Media.SoundPlayer.Play()` — async, never `PlaySync` from a Decal callback (doc 06). |
| U10 | `/nbreset` only recovered the main window | Resets all four NB3 windows to their XML-default geometry (secondary ones back to hidden), clearing pin/click-through/alpha on each. |

## Follow-up: the editor tabs came up empty (the era break, again)

First in-game test: all four windows opened and laid out correctly, but the editor's
five spell-pick tabs were **empty** (nothing to add) and the Options header read
`(0x00000000)`. Root cause and fixes:

- **U11 — empty spell tabs.** `EditorCatalog` was resolving the recovered 275-family
  classic table against the **live** client spell table. NB3's family ids are 2003
  Dark-Majesty ids; on a modern/ACE server the live table renumbers them, so almost none
  resolve and every tab is blank — the exact era break the case study warns about (only
  ~1 old id survives against a renumbered export). Fix: build the editor catalog from the
  **embedded 2012 retail dump**, whose ids match NB3's classic table exactly (id 2 =
  Strength Self I, 1161 = Heal Self VI). The dump gives each family its era-stable stacking
  **category**, which is what the profile stores; at cast time the planner still asks the
  **live** table for the best spell in that category, so a renumbered live server is
  handled — categories are era-stable even when ids aren't. A regression test simulates a
  fully-renumbered live table (resolves 0) vs the dump (resolves 200+).
- **U11b — latched-empty bugs.** The catalog was built once (an empty build stuck forever)
  and each tab list was marked "populated" even when it got zero rows. Now the catalog
  rebuilds while empty and a tab is only marked populated once rows actually land, so a
  slow/late resolve self-heals on the next poll.
- **U11c — Item-tab targeting.** With no explicit direct-target mode ticked, an Item-tab
  family now stores as a **Self** buff (modern banes/weapon-auras are self-cast whole-suit),
  so it matches what the live table reports and actually casts. Ticking By Name / By GUID /
  Cover / Weapon / Shield still stores the legacy per-item direct cast.
- **U12 — Options `(0x00000000)`.** A settings load before `LoginComplete` cached
  `CharacterId = 0`. Opening Options now re-reads the live character id and reloads the
  per-character settings if it differs, so the header and the saved file key are correct.

## Follow-up: secondary windows out of the Virindi bar until opened

The original created the Options/Editor/Casting views on demand and tore them down on
Dismiss, so each had **no Virindi HUD-bar icon** until it was opened and none again once
closed. The wrapper build keeps all four views created+wired for the plugin's lifetime (so
`[MVControlEvent]` bindings never churn), which had a side effect: all three secondary
views showed their bar icon at all times, even while hidden.

- **U13 — hide the bar entry, not just the window.** `HudView.ShowInBar` (VVS; present in
  the vendored `vvs_dump.txt` surface) is the per-view bar toggle. A new
  `ShowManagedView(title, show)` in `PluginCore.Views.cs` moves a secondary view's `Visible`
  and its `ShowInBar` **in lockstep** — so "in the bar" always means "open on screen". All
  three views are set hidden+unbarred in `InitViews` (immediately after every `WireupStart`,
  including the §10.13 VVS rebuild), flipped on by the open paths (`/nboptions`, `/nbed`,
  cycle-start `ShowCastingView`), and flipped off again by every Dismiss handler and
  `HideCastingView`. The main `NB3` view is never touched — its bar icon is the plugin's
  permanent handle. `ShowInBar` is written through the same reflection helper as
  `Ghosted`/`ClickThrough`/`Alpha` (`SetProp` over the `IView.Underlying` HudView), so it is
  a safe no-op on the Decal backend, where the window still shows/hides via `Visible`.
  `/nbreset` now writes `ShowInBar` to match each window's reset visibility, keeping the bar
  consistent after a recovery.

## Follow-up: the "Editor Color Scheme" dropdown retired (didn't map to VVS)

The Options view carried the original's recovered **Editor Color Scheme** dropdown
(`choiceEditorScheme`) and an `EditorColorScheme` setting. The teardown recovered its four
labels from the v1.52 string table — "No Theme" / "Default Theme" / "Doc's Fusion" /
"Forest Gump" — but the control was only ever populated and persisted; **nothing applied
it**, so picking a scheme did nothing.

- **U14 — removed rather than reinterpreted.** Those four were *native-Decal* per-control
  palettes (the original was a Win32 COM plugin drawing its own chrome). VVS has no
  equivalent: window appearance comes from the global **theme** registry
  (`HudView.Theme` / `HudViewDrawStyle`), whose themes are a different, fixed set
  (Float/Decal/Castle/Minimalist/…) — two of the four originals ("Doc's Fusion", "Forest
  Gump") have no VVS analogue at all. Wiring the dropdown to VVS themes would have replaced
  the feature with a different one, and it would have duplicated a capability VVS **already**
  exposes natively (the per-window title-bar theme dropdown). So the control, its four
  labels, and the `EditorColorScheme` setting were removed outright. The Options view
  re-flowed up to close the gap (height 307 → 271; the Mana/scheme divider now separates
  Mana Regeneration Mode from Quiet Mode) and `OptionsHome` (`/nbreset` geometry) tracks the
  new height. Old `config_*.xml` files keep working — the dropped `editorColorScheme`
  attribute is simply ignored on load. Users who want NB3 windows themed use VVS's built-in
  title-bar theme picker.

## Discipline followed (docs 03 §10, 10, 14)

- **Lazy realization**: controls on unopened notebook tabs don't exist until first shown —
  every resolve is defensive and retried from the ~4 Hz render-frame poll; the wireup
  helper's pending-binding retry (already per-binding-tolerant) services their events.
- **Unreliable `Change` events**: checkbox/combo state is **polled and diffed**, never
  driven by `Change`. The editor's radio-like target-mode checkboxes are made mutually
  exclusive by poll-diff (the box that became checked since last poll wins).
- **Seed-then-diff**: the Options view is seeded from the model once per open (a re-seed
  every frame would fight the user's edits); values are read back only on **Save**.
- **Every button acknowledges** in chat — success or a state-gated no-op — so a silent
  no-op is never mistaken for a dead button or a rendering bug.
- **Nothing new touches Actions off the Decal callback thread**; all view work is cheap
  reads/writes on the render-frame poll.

## Verification

- `dotnet run --project tests/NB3.Core.Tests -c Release -p:CoreTfm=net8.0` → **110 passed**
  (13 new cases: include resolution + dedup + cycle guard, per-entry Other/Item targeting
  through the planner, cover-mask filtering, `ModernProfileStore` CRUD, `EditorCatalog`
  family/tab/school resolution against the real 2012 catalog, and the era-break regression
  — a renumbered live table resolves 0 families while the 2012 dump resolves 200+).
- `bash tools/shimcheck/run-shimcheck.sh` → **PASS** (all `src/NB3.Plugin/*.cs` now type-
  checked against the doc-15 shims; shims extended with `IList`/`ICheckBox`/`ITextBox`/
  `INotebook`/list cell model, `MVWireupHelper.GetViews`, `WorldFilter.GetByName`,
  `System.Media.SoundPlayer`).
- `python3 tools/viewlint/viewlint.py` → **PASS** (geometry unchanged; only `<column>`
  elements were reworked).

After U13–U14 the full suite runs **128 passed / 0 failed**, and shimcheck + viewlint stay
green (U13 is plugin-side reflection glue type-checked by shimcheck; U14 re-flowed
`nb3-charconfig.xml`, which viewlint re-checks for overlaps/edges).

The only things the offline gates can't prove are live-client behaviours — VVS backend
realization order, lazy-tab wireup timing, font-width clipping, and whether `ShowInBar`
takes effect the instant it's toggled (vs. only at wireup) on the installed VVS build — so
the one remaining step is an in-game smoke test of each view (README "Next steps").
