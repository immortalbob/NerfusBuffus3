# Nerfus Buffus III

A profile-driven self-buff bot for **Asheron's Call**, running as a **Decal 3** plugin.
Build a profile (equip a focus, cast a list of buffs, imbue your gear), run `/nbuff <profile>`,
and it casts the whole list in a cycle — tracking spells cast / left / fizzles / busy / mana on
a live status window, with Pause/Resume/Abort and — by default — a spell-based recovery that keeps
mana up by converting stamina and health as you buff (one of six selectable mana-regen modes).

Nerfus Buffus III is the Decal 3 rebuild of the original **Nerfus Buffus II** (Pascal Jolin /
Nerf Soft, 2001–2003). The buff logic, cycle runner, per-character options and profile editor are
faithful to the original (plus a new default spell-recovery mode for mana); the spell layer was
rebuilt for the modern (End-of-Retail / ACE) game, which renumbered spell ids and changed how buffs
stack and cast.

Unlike the original, **NB3 needs no companion filter** — the old *Nerfus Filter* (which hand-parsed
the spellbook, enchantments, vitals and inventory off the wire) is fully replaced by Decal 3's
managed `CharacterFilter` / `WorldFilter` API.

## Requirements

- **Asheron's Call** with **Decal 3.0** installed.
- **VirindiViewService (VVS)** for the windowed UI. NB3 falls back to Decal's built-in view
  renderer if VVS isn't running, but VVS is recommended.
- To build from source: the **.NET Framework 4.8 developer pack** and a current .NET SDK.

## Install

The plugin builds on Windows (net48 / x86 / ComVisible). From this source tree:

```powershell
# 1. build (points the two SDK properties at your installs; VVS is NOT in the Decal folder)
.\build-windows.ps1 `
  -DecalSdk "C:\Program Files (x86)\Decal 3.0" `
  -VvsSdk   "C:\Games\VirindiPlugins\VirindiViewService"

# 2. register the built DLL from an ADMINISTRATOR prompt — 32-bit RegAsm, never Framework64
.\register-windows.ps1
```

Then launch AC through Decal, open **Manage Plugins**, enable **Nerfus Buffus III**, and log in.
At login it prints a load banner and the UI backend it chose; the main window appears.

Full step-by-step (build flags, the RegAsm trap, the `register-NB3.reg` alternative) is in
[`docs/BUILD_ON_WINDOWS.md`](docs/BUILD_ON_WINDOWS.md).

> Upgrading from an NB2-era build: the COM identity is unchanged, so re-registering
> `NerfusBuffus3.dll` upgrades in place. Your profiles and settings move automatically — on first
> run NB3 copies anything under `%AppData%\NerfusBuffus2` into `%AppData%\NerfusBuffus3`.

## Quick start

**The zero-effort path (new characters).** Just log in. NB3 reads your trained/specialized
skills and, if you don't already have a profile named after your character, generates one for you
and **selects it in the main window** — so a brand-new user has nothing to create and can press
**START** immediately. This is the login-time equivalent of `/nbgen <yourname>`; it never
overwrites a profile you've already edited, and you can turn it off with `/nbset autogen 0`. See
[`docs/AUTO_ONBOARD.md`](docs/AUTO_ONBOARD.md).

Prefer to drive it yourself? Everything is still available from chat:

```
/nbgen                 generate a character-specific profile from your trained/spec skills
/nbnew                 create the classic 17-line starter profile named 'default'
/nbuff <profile>       plan and run the cycle; watch the counters in the status window
```

Mana recovery is on by default (mode 6 — cast Stamina→Mana / Cannibalize / Revitalize), so a cycle
finishes instead of running you dry. Pick a different strategy with `/nbset regen <0-6>` if you
prefer potions or kits. No Editor needed to get going — `/nbgen` builds a full set from your skills,
and `/nbnew` writes a ready profile of the classic self buffs (attributes, life staples,
protections), both resolved to spells your character can actually cast. Open the Editor (`/nbed`)
when you want to customize it.

## The windows

- **Main** (`NB3`) — current profile, status, live counters (Spells / Left / Time / Fizzles /
  Busy / Mana / Casting), and buttons: START, Pause, Resume, Abort, Editor, Options.
- **Editor** (`NB3 Editor`) — pick a profile or create one, then add buffs from the five tabs
  (Creature/Life × Self/Other, Item), Equip items, and Include other profiles. Each row in the
  profile has delete / move-up / move-down. New / Clear / Revert / Copy / Delete / Save.
- **Options** (`NB3 Options`) — every per-character setting (below), saved on **Save**. Its
  **Rescan Character** button rebuilds your profile from your *current* trained/spec skills (the
  `/nbgen` action), so you can refresh it after training new skills without deleting it and relogging.
- **Spells** (`NB3 Spells`) — the live list of what the running cycle still has to do; opens at
  cycle start, closes when it finishes.

Everything the windows do is also available from chat, so the bot is fully usable even if the UI
can't come up on your client.

## Commands

```
/nbgen [name]                     generate a full profile for THIS character from your
                                  trained/spec skills, and select it (default name: 'generated').
                                  Runs automatically at login into a profile named after your
                                  character — see docs/AUTO_ONBOARD.md / 'autogen' below.
/nbnew [name]                     create a starter self-buff profile (default: 'default')
/nbskills                         print the Specialized/Trained skills NB3 reads (sanity-check /nbgen)
/nbuff <profile>[,<profile>...] [force]   load the profile(s) and buff
                                  'force' recasts even buffs that are already active
/nbpause  /nbresume  /nbabort     cycle control
/nbed                             open/close the Profile Editor
/nboptions                        open the Options window
/nbinclude <profile>              add an Include to the profile being edited
/nbrefresh                        rescan the profile folder into every dropdown
/nbset [key value]                show or set options from chat (see below)
/nbstatus                         print current/max Health/Stamina/Mana
/nbid                             print your target's GUID + its weapon/shield GUID
/nbcovers                         print the GUID and cover mask of everything you're wearing
/nbdiag                           UI backend, wireup health, window state, control resolution
/nbreset                          restore every NB3 window's position/size, unpin, un-hide
/nbdebug                          toggle instrumentation logging (cast timings, status ids)
/nbhelp                           list the commands
```

## Options — the Options window and `/nbset`

Every setting is per character and persists across sessions. Open the **Options window**
(`/nboptions`, or its Virindi-bar icon) to set them with checkboxes and boxes, or use `/nbset`
from chat — no argument shows the current values, `/nbset <key> <value>` sets one. Both drive the
same per-character config; the window groups the everyday knobs, and a handful of advanced timing
settings are chat-only.

```
regen 0-6      mana-regen mode:
               0 none · 1 Trade Mana Elixirs · 2 Stamina Elixirs + S2M · 3 Rest + S2M ·
               4 Healing Kits + H2M · 5 Revitalize + S2M ·
               6 Spells: S2M + Cannibalize + Revitalize   (the default; no consumables required)
kits 0/1        allow healing kits as a fallback; the best kit you carry is auto-selected
potions 0/1     (mode 6) allow a mana potion as a last-resort fallback (default 0 = spells only)
cannibalize 0/1 (mode 6) allow Health->Mana (Cannibalize) + Heal Self (default 1); 0 = stamina-only
                recovery that never touches health
manafloor 0-99     interrupt buffing to regen when mana drops below this % of max (default 25; 0 = off)
manatarget 1-100   once regen starts, top mana back up to this % of max (default 90; above manafloor)
stampct 1-99    (mode 6) restore stamina when it drops below this % of max, before S2M (default 50)
healthpct 1-99  (mode 6) heal / H2M safety floor: keep health above this % of max (default 50)
maxrec 1-7     max level for the recovery spells (H2M / S2M / Revitalize / Heal Self); it casts the
               highest level you know at or below this, so 7 uses level-7 H2M ('Cannibalize') when known
aggr <pct>     Expected % of Spell Cost — buff only while mana >= this % of the next spell's cost
skillcap 0/1   cast the highest level your skill can LAND reliably, not just the highest you know
mincast <pct>  minimum cast-success chance for the skill cap (default 90)
bootstrap 0/1  (needs skillcap) after your casting stats land, re-check and recast any buff your
               raised skill can now cast higher — a level-6 Focus refreshed to 7 — until nothing improves (default 1)
recast 0/1     /nbuff recasts buffs already active (default 1); 0 = skip still-active buffs (mana-saving)
rebuffmins <n> when recast=0, recast buffs with fewer than n minutes left (0 = skip all active buffs)
autogen 0/1    at login, auto-generate a profile named after your character (if none exists yet)
               and select it in the main window (default 1 = on). See docs/AUTO_ONBOARD.md.
autowield 0/1  at Start, wield a wand/staff/orb from your pack if your casting hand is empty
               (default 1 = on) - you can't cast, or even enter Magic stance, without one

advanced (chat only): maxattempts <n>  fizzles/timeouts before an action is skipped (default 8)
                      casttimeout <ms>  per-cast watchdog before a retry (default 10000)
                      castsettle <ms>   pause after a cast resolves, before the next (default 500)
                      regenbackoff <ms> throttle after repeated recovery-cast failures (default 3000)
                      maxregenfails <n> consecutive recovery-cast failures before that backoff (default 5)
```

The recovery-mode knobs (`regen`, `potions`, `kits`, `cannibalize`, the mana floor/target and the
stamina/health floors) and how the default spell mode decides are documented in
[`docs/MANA_RECOVERY.md`](docs/MANA_RECOVERY.md).

## How buffing works

- **Era-proof spell selection.** Profiles store a buff's *stacking category*, not a spell id or
  name (ids were renumbered and level-7/8 names are irregular). At cast time NB3 asks the live
  client spell table for the strongest spell you know in that category, so a profile keeps working
  across servers and patches. See [`docs/MODERN_SPELL_MODEL.md`](docs/MODERN_SPELL_MODEL.md).

- **Skill-aware levels.** NB3 casts the highest level you can land *reliably* at your current magic
  skill, not the highest you happen to know — so it won't grind fizzles throwing level 8s your
  skill can't support. It promotes automatically as your skill grows. Tune with `/nbset skillcap`
  and `/nbset mincast` (also the *Min cast chance %* box in Options). See
  [`docs/SKILL_BASED_LEVEL.md`](docs/SKILL_BASED_LEVEL.md).

- **Level bootstrap.** Your first buffs raise the very skill that decides how high you can cast:
  Focus, Willpower and Creature Enchantment lift your Creature Enchantment skill (and, through the
  Focus/Self attributes, every magic skill). So once those land, NB3 casts them first, then re-checks
  the list at your now-higher skill and recasts anything that can go up a level — a Focus that landed
  at 6 comes back up to 7 — and the bump cascades to the banes and protections through their masteries,
  repeating until nothing improves. Needs the skill cap on; turn it off with `/nbset bootstrap 0`.

- **Auto-wield a caster.** Press Start with an empty casting hand and NB3 wields a wand/staff/orb
  from your pack first, so the run can enter Magic stance and its casts actually land. It picks the
  first caster it finds and never disturbs one you're already holding; turn it off with
  `/nbset autowield 0`. (A profile can still name a specific focus to equip in the Editor.)

- **Mana & vital recovery.** By default NB3 keeps mana up with spells — Stamina→Mana, Cannibalize
  (Health→Mana), Revitalize (restore stamina) and Heal Self (restore health) — respecting per-vital
  floors so it never bleeds a vital dry, and only touching potions/kits if you opt in. It also
  auto-detects health/stamina/mana potions, food, and kits from live item properties. See
  [`docs/MANA_RECOVERY.md`](docs/MANA_RECOVERY.md).

- **Smart rebuffing.** A re-run skips buffs you already have up (saving mana), so right after a
  full cycle `/nbuff` may report nothing to do — that's expected, not a lost profile. Use
  `/nbuff <profile> force` to recast everything, or `/nbset rebuffmins <n>` so a periodic re-run
  tops up only the buffs about to expire. See [`docs/REBUFF_BEHAVIOR.md`](docs/REBUFF_BEHAVIOR.md).

- **Item enchants.** On modern servers banes are self-cast whole-suit (the shield inherits) and
  weapon buffs are self-cast auras, so they resolve as ordinary self buffs. The Editor's Item tab
  still supports deliberate direct casts on a named item / GUID / cover-mask-selected armor.

## Profiles & data

Profiles and per-character settings live in `%AppData%\NerfusBuffus3` as XML files (one per
profile; `config_<charGUID>.xml` for settings). You can back them up or hand-edit them; deletes
from the Editor go to a `_deleted` subfolder unless "Permanently delete files in editor" is on.

## Troubleshooting

- **Window missing or blank?** Run `/nbdiag` — it reports the UI backend (VVS vs the DecalInject
  fallback), wireup health, and which controls resolved. `/nbreset` restores a window that VVS's
  saved state (pinned, click-through, dragged to its title bar) has made invisible.
- **`/nbuff` says nothing to cast?** You're already buffed — see *Smart rebuffing* above.
- **Buffs fizzling a lot?** Your magic skill is low for the level; leave `skillcap` on (default),
  or raise `mincast`. `/nbset skillcap 0` forces highest-known and will fizzle.
- **Running out of mana mid-cycle?** The default recovery (mode 6) should prevent it. Check
  `/nbset` shows `regen=6`; an older saved config may still be on `0` (none) — set `regen 6` and
  it sticks. Details and tuning: [`docs/MANA_RECOVERY.md`](docs/MANA_RECOVERY.md).
- **Editor tabs empty / other oddities?** `/nbdebug` logs cast timings and status ids to
  `%AppData%\NerfusBuffus3\nb3-debug.txt`; errors go to `nb3-errors.txt`.

## Project layout

```
src/NB3.Core/      pure, testable buff logic — no Decal references (netstandard2.0)
src/NB3.Plugin/    the Decal 3 shell: PluginBase, the VVS views, /nb* commands (net48/x86/COM)
tests/             dependency-free offline test harness (182 cases)
tools/shimcheck/   offline type-check of the plugin glue against API shims
tools/viewlint/    static geometry/clipping lints for the view XML
docs/              deeper design/reference docs (below)
```

Deeper docs (in [`docs/`](docs/)):
[`BUILD_ON_WINDOWS.md`](docs/BUILD_ON_WINDOWS.md) (build detail + offline gates),
[`MANA_RECOVERY.md`](docs/MANA_RECOVERY.md) (the recovery system),
[`AUTO_ONBOARD.md`](docs/AUTO_ONBOARD.md) (login profile generation + selection),
[`SKILL_BASED_LEVEL.md`](docs/SKILL_BASED_LEVEL.md),
[`REBUFF_BEHAVIOR.md`](docs/REBUFF_BEHAVIOR.md),
[`MODERN_SPELL_MODEL.md`](docs/MODERN_SPELL_MODEL.md),
[`COVER_MASK_RECOVERY.md`](docs/COVER_MASK_RECOVERY.md),
[`DECAL_API_REFERENCE.md`](docs/DECAL_API_REFERENCE.md),
[`DEVELOPMENT_HISTORY.md`](docs/DEVELOPMENT_HISTORY.md) (the findings→fixes dev log).

## Credits

Original **Nerfus Buffus II** by **Pascal Jolin / Nerf Soft** (2001–2003). Nerfus Buffus III is
the Decal 3 revival. The bundled MetaViewWrappers UI files are MIT, © 2010 VirindiPlugins.
