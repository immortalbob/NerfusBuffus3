# Skill-based buff level selection (the fizzle fix)

**Symptom (owner, live ACE):** level 7 buffs land fine around 300 skill, but level 8s
fizzle constantly until ~410 skill. The bot was casting level 8 because it *knew* the
spell — with no regard for whether the character's skill could land it.

## The mechanic (ACE server source, verbatim)

Cast success in AC/ACE (`Player_Magic.GetCastingPreCheckStatus` + `SkillCheck`):

```
difficulty = spell.Power                       // the spell's Power
if (magicSkill >= difficulty - 50)             // hard floor: below this, guaranteed fizzle
    chance = 1 - 1 / (1 + e^(0.07 * (magicSkill - difficulty)))   // GetMagicSkillChance, factor 0.07
    success = chance > rng(0,1)
```

Two facts confirmed against the ACE tree and the 2012 dump:

- **`spell.Power` is the fizzle difficulty AND the stacking power** — the same number as the
  dump's `difficulty` column (`SpellInfo.Level` in this codebase). ACE's fizzle check reads
  `spell.Power`; its stacking orders enchantments by that same `spell.Power`
  (`EnchantmentManager`: `entry.PowerLevel = spell.Power`, `OrderByDescending(PowerLevel)`).
- **Magic uses the steep 0.07 sigmoid**, not the 0.03 used by vital/heal checks, and adds a
  hard `Power − 50` floor. So a small skill deficit fizzles hard, and a spell more than 50
  over your skill can't be cast at all.

Real Strength-line powers from the 2012 catalog (stacking group 1, Self variants):

| Level | Spell | Power | ~skill for 90% cast |
|---|---|---|---|
| VI | Strength Self VI | 250 | ~275 |
| 7 | Might of the Lugians | 300 | ~325 |
| 8 | Incantation of Strength Self | 400 | ~425 |

At 300 skill: level 8 (Power 400) is gated out entirely (need ≥350 even to try), level 7
(Power 300) is a coin-flip, level VI (Power 250) is ~97%. That's exactly the reported
behaviour.

## The fix

`ModernBuffSelector` gained a `SkillPolicy`: when enabled, for each category it picks the
**highest-level known spell the character can cast at or above a minimum success chance**
(default 90%), using the ACE formula in `NB3.Core/Modern/CastChance.cs` against the
character's **effective (buffed) magic skill** for that spell's school. Cast chance falls
monotonically as Power rises within a category, so the reliable set is a prefix from the
bottom — it takes the top of that prefix, and falls back to the lowest known (most
castable) if even that misses the threshold. The stacking skip now compares against the
level it will actually cast.

- **Effective skill** comes from `CharacterFilter.EffectiveSkill[CharFilterSkillType]`
  (`DecalGameState.EffectiveMagicSkill`), mapping school → skill: Creature →
  CreatureEnchantment, Life → LifeMagic, Item → ItemEnchantment, War → WarMagic, Void →
  VoidMagic. Read reflectively over the indexer (targeting the `…SkillType` key), so a
  missing/renamed member degrades to `0` → capping **off** for that school (fail-open to the
  old highest-known pick), never a crash.
- **Per-cast feedback:** when a buff is capped, the plan emits a chat line — e.g.
  *"skill-capped Strength Self: casting power 250 (you know 400, but your skill can't land it
  at ≥90%)."* — so it's obvious why a level 8 isn't being thrown.

## Controls

Defaults: **on**, at **90%**. Persisted per character (`skillBasedLevel`,
`minCastChancePercent` in the config XML).

```
/nbset                 -> shows: skillcap=1  mincast=90%
/nbset skillcap 0      -> force highest-known (the old behaviour; will fizzle a lot)
/nbset mincast 80      -> push higher levels sooner (more fizzles, faster leveling)
/nbset mincast 95      -> be more conservative
```

As your magic skill grows, the bot automatically promotes buffs to higher levels the moment
they cross the reliability threshold — no manual re-capping.

## Level bootstrap (recast at the higher level once your own skill is up)

The skill cap creates a chicken-and-egg on the buffs that raise your casting skill: at low skill you
can only land **Focus 6**, but Focus — with Willpower and the Creature Enchantment mastery — is
exactly what lifts your Creature Enchantment skill (and, through the Focus/Self attributes, every
magic skill). Once those land, **Focus 7** is castable. Reading skill once per run would leave Focus
stuck at 6 until the next `/nbuff`.

So a run is **phased** (`bootstrapLevels`, default on, active only when the skill cap is on):

1. **Cast the casting-stat prefix first** — the buffs up to and including Creature Enchantment
   mastery (`ModernProfile.CastingStatPrefixCount()` finds the checkpoint by the generator's
   `"Creature Magic Self"` family name, which persists in the profile XML).
2. **Re-plan the rest at the now-higher skill**, so it's cast at the level your buffed skill allows
   from the start rather than the capped level.
3. **Keep re-checking (with ForceAll off) until nothing improves.** Each pass recasts only genuine
   level-ups. The run tracks the highest level (ACE Power) it has cast per stacking category, and an
   upgrade pass casts a category **only when it can now go strictly higher than that** — so a buff
   whose active enchantment the stacking read can't re-match (some auras/banes/item-target casts
   create an enchantment the coverage check misses) is cast once in the initial buff and never
   re-cast. Without that guard those buffs re-appear in every re-plan and the loop spins to its cap;
   with it, the loop stops the moment no category can climb. The bump cascades — the Item/Life/War
   masteries let their banes/protections rise a level on the following pass — and it always
   terminates, because each pass strictly raises at least one category's level, bounded by the max
   spell level (hard cap: a handful of passes as a backstop).

Turn it off with `/nbset bootstrap 0` (buffs then cast once at the level your skill allows at the
start). A profile with no Creature Enchantment mastery entry — a hand-built one without the
checkpoint — skips straight to a single pass.

## Scope / notes

- Applies to the category-based **buff** selection (Creature/Life/Item self+other). The
  recovery spells (S2M/H2M/Revit) keep their existing `MaxRecoveryLevel` cap; a skill cap
  there is a possible follow-up.
- The skill is read fresh at the start of every pass (see *Level bootstrap* above), so when a
  self-buff raises the very skill that casts later buffs, the promotion is picked up **within the
  same `/nbuff`** — the run recasts at the higher level once your casting stats land.
- **Reading the effective skill.** Two live shapes are tried in order (adapter dump):
  `CharacterFilter.EffectiveSkill[CharFilterSkillType]` (int), then
  `CharacterFilter.Skills[type].Current`/`.Buffed` (SkillInfoWrapper). If BOTH read `0`, the cap
  fails open (reverts to highest-known) rather than blocking casts. **`/nbdiag` prints the
  numbers the cap actually reads** ("magic skill (effective): Creature=… Life=… Item=…"): real
  numbers → the cap is working (check `skillcap`/`mincast` if a level still looks wrong); all 0 →
  the read failed on that SDK build and the cap is off.

## Verification

Part of the `NB3.Core.Tests` harness (currently **197 passed, 0 failed**); cases exercise this fix
and the level bootstrap: the ACE formula incl. the Power−50 floor, the 0.07 sigmoid landmarks,
monotonicity, skill-capped downgrade on the real Strength line, high-skill no-cap, skill-0
fail-open, policy-off parity, the planner's cap warning, and the bootstrap re-plan (a raised skill
recasts a level-6 buff at 7, and the loop terminates once at the skill's max). `shimcheck: PASS`,
`viewlint: PASS`.

> The `mincast` threshold is also exposed on the **NB3 Options** panel now (the *"Min cast
> chance %"* box), in addition to `/nbset mincast`.
