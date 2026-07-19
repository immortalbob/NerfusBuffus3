# Decal 3 managed API — reference notes (platform only)

**Scope:** these are *platform* facts about the Decal 3 managed API, confirmed by inspecting
the metadata of a shipped managed plugin (BuffMe). **No behaviour, feature, code, or data from
that plugin is used in the NB3 revival** — it served only to verify which `Decal.Adapter`
members exist and their shapes, the same way one would read the SDK docs. NB3's behaviour comes
exclusively from the recovered NB3 binaries.

The value of this: it lets the NB3 shell's glue call the *real* API instead of names guessed
from documentation, and it resolves most of the `‹confirm›` markers in `DecalGameState.cs` /
`PluginCore.cs`.

## Confirmed members (present in the reference's metadata)

Lifecycle / plumbing:

- `Decal.Adapter.PluginBase` — base class; `get_Host` → `PluginHost`.
- `CoreManager.CharacterFilter`, `CoreManager.WorldFilter`, `CoreManager.HotkeySystem`.
- `PluginHost.Actions` → `HooksWrapper`; `PluginHost.Render` → render service.
- Command/chat interception: `Extension.add_CommandLineText`, `add_ChatBoxMessage`,
  `add_WindowMessage`; args `ChatParserInterceptEventArgs.Text` + `EatableEventArgs.Eat`
  (this confirms the `/nb*` command handler shape already in `PluginCore`).
- Events: `CharacterFilter.add_LoginComplete`, `add_Logoff`.

Casting & character actions (`HooksWrapper`, reached via `Host.Actions`):

- `CastSpell(spellId, targetGuid)` — the cast call NB3's cycle drives.
- `UseItem(guid, type)` — equip/use (focusing stone, elixirs, healing kits).
- `SetCombatMode(CombatState)` + `CombatMode` getter — must be `Magic` to cast.
- `CurrentSelection` — the selected target's id.
- `Vital[VitalType]` — current/max Health/Stamina/Mana.
- `AddChatText(...)`, `InvokeChatParser(...)`, `IsValidObject(...)`.

World / items:

- `WorldFilter.GetInventory()` → `WorldObjectCollection`; `GetByContainer(...)`; indexer `[id]`.
- `WorldObject.Id`, `.Name`, `.Container`, `.ObjectClass`, `.Values(key)`.

Enchantments & spell data (present, used by NB3 only where NB3 originally used the equivalent):

- `CharacterFilter.Enchantments` → `EnchantmentWrapper.SpellId` + `.TimeRemaining`.
  *NB3 does not re-buff by expiry, so the cycle runner does not use this;* it's noted only for
  completeness (and for the status view, if we later show active-buff time).
- `FileService.SpellTable` → `Spell.Id` / `.Name` / `.Type` / `.Description`. NB3's original
  "Get Spell list from Decal.dat" maps here. (The mana-cost field used by the "% of Spell Cost"
  gate is still `‹confirm›` — it isn't among those four accessors.)

UI (native DecalControls binding is available as well as VVS):

- `ViewAttribute`, `WireUpControlEventsAttribute`, `ControlEventAttribute`,
  `ControlReferenceAttribute`, and the control wrappers (`ListWrapper`, `ChoiceWrapper`,
  `CheckBoxWrapper`, `ProgressWrapper`, `StaticWrapper`, `PushButtonWrapper`, `TextBoxWrapper`,
  `SliderWrapper`). NB3's revival uses VVS/`MetaViewWrappers` for the recovered views; this
  confirms the native path exists as a fallback.

## Formerly `‹confirm›` — how each was resolved in the fixed tree

- **Busy/casting detection.** Two distinct gates now: `Actions.BusyState == 0` before every
  item use (doc 13 §10.4 — BusyState covers item manipulation), and the shell's own
  `IsCasting` flag for casts (set on `CastSpell`, cleared by the cast-result handler).
- **Spellbook membership** → `CharacterFilter.IsSpellKnown(id)` (in doc 01 §4.3's confirmed
  method list); fails open so a miss degrades to an attempted cast the client refuses.
- **Coverage value-key** → resolved by the research corpus itself: coverage is
  `CLOTHING_PRIORITY_INT` = **key 4** (docs 13 §6 / 16 §1); Decal's *named* `Coverage` key in
  the synthetic 218103808+ range is the documented shipped bug and is not used. Worn items
  filter on `CURRENT_WIELDED_LOCATION_INT` = key 10 and translate via `NB3Coverage`.
- **Spell mana cost field** → read reflectively over candidate names
  (`ManaCost`/`BaseMana`/`Mana`, doc 13 §3), with the shipped 2012 dump supplying the value
  when the SDK's record won't (doc 16 §7.5's fallback posture). Same treatment for the
  stacking-group/difficulty fields (`Family`/`Category`, `Difficulty`/`Power`/`Level`).

Only in-IDE spot-checks remain (CombatMode getter type, IsSpellKnown presence); each is
soft-fail in the glue, so a mismatch is a diagnostic, not a crash.
