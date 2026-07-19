# Modern (EoR / ACE) spell model — the target, and what it changes

Target chosen: **modern End-of-Retail / ACE servers** (what 2026 players run). NB3's *feel* —
its views, its cycle, its Options, its profiles — stays faithful. The **spell layer** is rebuilt
to modern mechanics, because three things changed after NB3 shipped (2003):

## 1. Spell ids were renumbered — resolve against the live table, never hardcode

NB3's `nb3-spells.xml` holds Dark-Majesty-era ids. Cross-checking all 1,865 of them against the
EoR dump: **1 still matches.** The buff spells all moved. So the buffbot must read the **live
client spell table** (`FileService.SpellTable` on Decal 3; ACE's spell data underneath) and
resolve by the stable identity, not by a 2003 id. NB3 already validated ids against Portal.dat
at cast time ("spell that isn't in Portal.dat!!!"), so runtime resolution is faithful in spirit.

## 2. Level naming is irregular — use category+level fields, not name parsing

From the EoR dump: levels **1–6** are the plain numbered names, level **7** got **unique bespoke
names** per spell, and level **8** is **"Incantation of &lt;the level-7 name&gt;"** (52 of them).
So you cannot recover a family/level by parsing the name. The live spell record carries numeric
**Category** and **Level** fields — those are the load-bearing data. `SpellInfo`/`ILiveSpellTable`
model exactly that.

## 3. Stacking is by category — and banes are now self-cast whole-suit

Spells belong to **categories (stacking groups)**: within a category only the highest level is
active (higher surpasses lower); different categories stack. `ModernBuffSelector` implements this
directly — for each desired category, pick the highest level the player knows, skip if an
equal-or-higher enchantment in that category is already up.

The **bane change** folds in here: you no longer cast each element bane on each armor piece via
coverage. You cast **one bane per element on yourself**, covering the whole suit — **and the
shield inherits from the banes cast on you**, so shields need no separate cast either. A bane is
just another self-cast category. That **retires NB3's `Spellgroup` + cover-mask targeting** for
the modern path.

The **weapon-buff change** is the same shape: weapon buffs are no longer cast on the weapon.
They are **self-cast auras** (e.g. "Aura of Infected Caress" — the level-7 Blood Drinker self
line), not weapon-specific — one more self-cast stacking category. The data story (doc 18 §6,
cross-checked ACE `SpellId` ↔ the 2012 dump): the aura ids are the *classic* self-line ids —
Blood Drinker Self I..VIII = 35, 1612–1616, 2096 ("Infected Caress"), 4395, all stacking
group 154 — present in `spellcat-2012.tsv` with pre-aura names and a dump-computed
`target=item`. **The trap is the target column, not missing ids:** the live record's
`IsUntargetted == true` is the modern classifier, and `DecalSpellTable` applies it before the
dump's target. (The `…Other` targeted lines remain separate, for casting onto another
creature's weapon.)

**Direct item casts still work if you really want them.** You *can* cast the old item-target
enchants directly on a shield or armor piece; the planner's Item path (and `/nbid`'s
weapon/shield GUID probe) exists for exactly that deliberate, optional use — it is no longer
the default route for anything.

## 4. Cantrips are not castable

Minor/Major/Epic/Legendary `<Family>` cantrips (310 of the 3,250) are **passive item-loot
enchantments** — a player never casts them. `EoRSpellCatalog` classifies them (validated exactly
against doc 16's counts: Minor 86 / Major 76 / Epic 79 / Legendary 69; 79 families) so the buffbot
can **exclude** them and work only from the 2,940 castable/other entries.

---

## What this makes *legacy reference* vs *modern live*

| Concern | Classic modules (kept, faithful record) | Modern path (the build target) |
|---|---|---|
| spell id source | `SpellTable` (nb3-spells.xml, 275 fams) | `ILiveSpellTable` ← `FileService.SpellTable` |
| level selection | `SpellTable.ResolveCastableId` (numeric I–VII) | `ModernBuffSelector` (Category + Level) |
| item banes | `BuffEngine` `Spellgroup` + `CoverageMask` per-piece | self-cast category, no coverage |
| cantrips | n/a (didn't exist) | `EoRSpellCatalog` → excluded |

**Unchanged and still correct** (they were never era-specific): `BuffCycle` (the sequential
Spells/Left/Fizzles/Busy runner), `NB3Settings`, `RecoverySpells` + `ManaRegen` (the mana-regen
modes — the original's five plus a spell-recovery mode that is now the default; see
[`MANA_RECOVERY.md`](MANA_RECOVERY.md)), `ProfileEditor`, `ProfileStore`. The classic `CoverageMask`
recovery stays in the tree as the documented disassembly result and for classic-era servers.

## Still needs live-client data (not in the provided dump)

The `id/name/description`-only xlsx was superseded by a full retail spell dump
(`acspells 2012-02`), which carries the load-bearing fields:

- **stacking group** -> `SpellInfo.Category`, and **difficulty** -> `SpellInfo.Level` — confirmed
  against the dump's own stacking file (Strength Self I..VI = power 1/50/100/150/200/250). 519
  real stacking groups.
- **school** (Creature/Life/Item/War/Void) -> the editor's tab categorisation.
- **mana** -> the "% of Spell Cost" gate.

Parsed into `spellcat-2012.tsv` (5,891 spells, with a computed **target** column —
self/other/item from the record's target-mask + range), loaded by `SpellCatalog : ILiveSpellTable`.

## The pipeline is now connected end-to-end

`ModernProfile` (equips + buffs identified by **stacking category + target**, never a hardcoded id)
→ `ModernBuffPlanner` (resolves each category to the highest castable spell of the right variant via
`ModernBuffSelector`, stacking-aware) → `BuffPlan` → `BuffCycle` (the faithful sequential runner).
Tested against the real 2012 catalog, including a full profile→plan→cycle run. The plugin shell
wires the same shape live: `DecalSpellTable : ILiveSpellTable` over `FileService.SpellTable`,
`DecalGameState.ActiveEnchantmentSpellIds` from `CharacterFilter.Enchantments`, and `PluginCore`
now plans via `ModernBuffPlanner` instead of the classic `BuffEngine`.

`ModernBuffSelector` is validated against **real stacking ladders**. **ID reconciliation:** this
dump uses the **same classic ids as NB3** (id 1157 = Heal Self II, id 2 = Strength Self I) — so
NB3's own table lines up with retail-based data; the earlier xlsx was a renumbered outlier. In the
live plugin the identical shape is filled from `FileService.SpellTable`; the TSV ships as the
offline test fixture and a data fallback. The only piece still needing the live client is the
level-7 bespoke-name -> identity map for editor *display* (selection is name-independent already).
