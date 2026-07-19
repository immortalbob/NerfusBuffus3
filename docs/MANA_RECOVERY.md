# Mana & vital recovery — the three-vital dance

How Nerfus Buffus III keeps mana (and stamina and health) up so a buff cycle actually finishes,
instead of running the character down to empty.

## The mode: `SpellRecovery` (regen mode 6, the default)

`SpellRecovery` is a spell-first recovery that manages all three vitals with **no required
consumables**. It runs two conversion loops — stamina preferred — and restores whichever source
vital gets spent:

- **Make mana:** cast **Stamina→Mana** (S2M) while stamina is above its floor; cast **Cannibalize**
  (Health→Mana — the level-7 H2M is literally named Cannibalize) while health is above its floor.
- **Restore a spent source vital:** cast **Revitalize** to bring stamina back, or **Heal Self** to
  bring health back (recovering what Cannibalize/H2M drained). When both are low it restores the
  **more-depleted** one first, so both pools stay balanced and both loops keep contributing.
- **Optional consumables** (both off by default, opt-in): the right potion for the depleted vital,
  or a healing kit — see *Auto-scanned consumables* below.

Every recovery cast is guarded by an **affordability check** (it won't attempt a cast the character
can't pay for) and by the **percentage floors** (it won't drain a vital below its floor to make
mana). If everything is tapped out it waits on natural regen instead of burning components — and the
25% reserve floor keeps mana off zero, so it can't deadlock.

### The decision, each tick, while mana is below the target

1. **S2M** if stamina ≥ its floor — mana from stamina (primary).
2. **Cannibalize / H2M** if health ≥ its floor — mana from health.
3. Both source vitals below floor → restore the **more-depleted** one: **Revitalize** (stamina) or
   **Heal Self** (health). A kit/potion is used first only if you enabled it.
4. Direct **mana potion** if you enabled potions and nothing else worked.
5. Otherwise wait on natural regen.

### Why the old default drained to zero

The previous default was `ManaRegenMode.None`, and the cycle's mana gate is wrapped in
`if (ManaRegenMode != None)`. So with the default, the gate was skipped entirely: the bot never
checked mana and cast every buff until mana hit 0. Making `SpellRecovery` the default is the fix.

## "Cannibalize" = the level-7 Health-to-Mana self spell

Verified against the corpus spell data: the EoR spell table lists **`Cannibalize` (id 2332 =
`0x091C`)** in the **same family/word (`puishzhafeth`) as `Health to Mana Self`**, and
`nb3-spells.xml` maps `Health to Mana Self` level 7 to `0x091C`. So Cannibalize *is* H2M at max
level. The spell mode uses your best known H2M level; tick **Use H2M7** in Options to specifically
cast the spell named Cannibalize (it falls back to level 6 if you don't know 7). Same idea for
**Use S2M7 / Use Revit7**.

## Auto-scanned consumables (potions, food, kits — per vital)

Potions and food restore health, stamina, **or** mana depending on type, and recovery understands
all three. It **scans inventory automatically** rather than relying on a name list, the way the
corpus (doc 19 §1/§5) recommends: each candidate is read by its real ACE properties via Decal value
keys —

- **ObjectClass** (`Food = 6`, `HealingKit = 29`) tells a drink/food from a kit,
- **BoosterEnum** (int key **89** → `2 = Health, 4 = Stamina, 6 = Mana`) says which vital it
  restores,
- **BoostValue** (int key **90**) is the amount; kits also read **HealkitMod** (float key **100**).

So it finds *any* health/stamina/mana drink or food and the best healing kit you carry — including
variants a name table would miss — and uses the one matching the vital it needs (a stamina drink to
fuel S2M, a health drink or kit to fuel Cannibalize, a mana drink as a direct last resort). A
name-fragment match (the doc-19 §5 ladder) is the fallback if the properties can't be read.

## Options & chat controls

Options panel (**NB3 Options**):

- **Mana Regeneration Mode** dropdown — option 6, *"Spells: S2M + Cannibalize + Revitalize"*, the
  default selection.
- **Use potions as optional fallback** checkbox (off by default). Healing kits are the other
  optional fallback, gated by the **Healing Kits** tier checkboxes.
- **Min cast chance %** — the *"buffs you're barely able to reach"* threshold (`MinCastChancePercent`,
  default 90); see [`SKILL_BASED_LEVEL.md`](SKILL_BASED_LEVEL.md).

Chat (`/nbset`):

```
regen 0-6      6 = Spells: S2M + Cannibalize + Revitalize (default)
potions 0/1    allow a mana potion as a last-resort fallback (default 0)
stampct <1-99> restore stamina below this % of max (default 50)
healthpct <1-99> heal / H2M-floor below this % of max (default 50)
manafloor <0-99> / manatarget <1-100>   regen band (defaults 25 / 90)
s2m7 | h2m7 (= Cannibalize) | revit7 | fallback6   recovery-spell levels
```

## Migrating an existing character

A config previously **saved** with `manaRegenMode="0"` still loads as `None` (your saved choice is
respected). To adopt the default on an existing character: open **NB3 Options** → pick *"Spells:
S2M + Cannibalize + Revitalize"* → **Save**; or run `/nbset regen 6`; or delete the character's
`config_<GUID>.xml` in `%AppData%\NerfusBuffus3\`. New characters get it automatically.

## Where it lives in the code

- `NB3.Core/BuffCycle.cs` — the `ManaRegenMode` enum and the cycle's mana gate.
- `NB3.Core/ManaRegen.cs` — `ManaRegenController` (the per-tick decision) + `RegenConsumables`.
- `NB3.Core/RecoverySpells.cs` — resolves S2M / H2M(Cannibalize) / Revitalize / Heal Self to the
  level the character will actually cast.
- `NB3.Plugin/DecalGameState.cs` — `FindBestPotion` / `FindBestHealingKit`, the property-based
  inventory scan.
- Tested in `tests/NB3.Core.Tests` (the priority tree, affordability guard, per-vital selection,
  auto-scan ranking, and the default-mode regression).
