using System;
using System.Collections.Generic;

namespace NB3.Core.Modern
{
    /// <summary>Casting school of a buff — i.e. which magic skill the character must have trained
    /// to cast it. Attribute and skill-mastery buffs are ALL Creature Enchantment spells (the
    /// school is what casts them, not the skill they raise); vitals/protections are Life; the
    /// banes/impen/weapon-auras are Item.</summary>
    public enum BuffSchool { Creature, Life, Item }

    /// <summary>Grouping used only to let the caller include/exclude whole optional blocks.
    /// (Weapon auras are NOT here — they're chosen by combat archetype, below.)</summary>
    public enum BuffGroup { Core, Utility, Vital, Protection, Bane }

    /// <summary>One entry in the generator's curated self-buff set: the family (matched against the
    /// editor catalog's DisplayName to get its era-stable stacking category), the school that casts
    /// it, and — for skill-mastery buffs — the CharFilterSkillType it RAISES (0 = universal:
    /// attributes / vitals / armor / banes / auras, added whenever their school is castable).</summary>
    internal sealed class GenBuff
    {
        public readonly string Family;
        public readonly BuffSchool School;
        public readonly int BoostSkill;   // CharFilterSkillType raised by this buff; 0 = universal
        public readonly BuffGroup Group;
        public GenBuff(string family, BuffSchool school, int boostSkill, BuffGroup group)
        { Family = family; School = school; BoostSkill = boostSkill; Group = group; }
    }

    /// <summary>Which optional blocks the generator includes (all on by default = the "complete"
    /// set). The Core block — attributes, the magic-school masteries, the three defenses, and the
    /// per-weapon masteries — is always included (subject to skill training).</summary>
    public sealed class GeneratorOptions
    {
        public bool IncludeUtilitySkills = true;   // lore, movement, tradeskills, tinkering, leadership…
        public bool IncludeVitals = true;          // Armor / Regeneration / Rejuvenation / Mana Renewal (regen-rate buffs)
        public bool IncludeLifeProtections = true; // the 7 "X Protection Self" (Life), alongside the Item banes
        public bool IncludeBanes = true;           // the 7 banes + Impenetrability (Item)
        public bool IncludeWeaponAuras = true;     // Blood Drinker / Defender / Heart Seeker / Swift Killer (Item)
    }

    public sealed class GenerationResult
    {
        public ModernProfile Profile = new ModernProfile();
        public readonly List<string> Included = new List<string>();
        public readonly List<string> SkippedUntrained = new List<string>(); // boosted skill not trained/spec
        public readonly List<string> Unresolved = new List<string>();       // family has no live category here
        public bool CreatureCastable, ItemCastable, LifeCastable;
    }

    /// <summary>
    /// Builds a character-specific self-buff profile: every buff the character can actually use,
    /// filtered by the live client's TRAINED/SPECIALIZED skills. The buff-family → skill map is
    /// derived from the recovered 275-family names (see UI notes); each family resolves to its
    /// era-stable stacking category through the same editor catalog the Editor view uses, so the
    /// profile survives spell renumbering. Pure and deterministic — the live queries (skill
    /// training) come in as a delegate, so this is fully unit-tested off-client.
    ///
    /// Cast ORDER is load-bearing and fixed at the front (owner's spec): Focus → Willpower →
    /// Creature Enchantment → Mana Conversion → Item Enchantment → Hermetic Link → Life Magic.
    /// Buffing the casting stats first means every buff after them is checked/cast at the higher
    /// skill, so they land instead of fizzling. After that prefix the order is immaterial and the
    /// set is grouped for readability.
    /// </summary>
    public static class ProfileGenerator
    {
        // CharFilterSkillType ids (adapter dump) used for the casting-school gate.
        private const int SkCreature = 31, SkItem = 32, SkLife = 33;
        private const int TrainedOrBetter = 2; // TrainingType: 0 Unusable, 1 Untrained, 2 Trained, 3 Specialized

        // The curated, ORDERED self-buff set. BoostSkill values are CharFilterSkillType ids.
        private static readonly GenBuff[] Set =
        {
            // ---- required prefix: bootstrap the casting stats first (order is load-bearing) ----
            new GenBuff("Focus Self",              BuffSchool.Creature, 0,  BuffGroup.Core),
            new GenBuff("Willpower Self",          BuffSchool.Creature, 0,  BuffGroup.Core),
            new GenBuff("Creature Magic Self",     BuffSchool.Creature, 31, BuffGroup.Core), // Creature Enchantment
            new GenBuff("Mana Conversion Self",    BuffSchool.Creature, 16, BuffGroup.Core),
            new GenBuff("Item Magic Self",         BuffSchool.Creature, 32, BuffGroup.Core), // Item Enchantment mastery (owner: cast right after Mana Conversion)
            new GenBuff("Hermetic Link",           BuffSchool.Item,     0,  BuffGroup.Core), // caster mana-leech aura (owner: cast right after Item Enchantment; benefits every archetype)
            new GenBuff("Life Magic Mastery Self", BuffSchool.Creature, 33, BuffGroup.Core),
            // ---- remaining attributes (universal, Creature) ----
            new GenBuff("Strength Self",           BuffSchool.Creature, 0,  BuffGroup.Core),
            new GenBuff("Endurance Self",          BuffSchool.Creature, 0,  BuffGroup.Core),
            new GenBuff("Coordination Self",       BuffSchool.Creature, 0,  BuffGroup.Core),
            new GenBuff("Quickness Self",          BuffSchool.Creature, 0,  BuffGroup.Core),
            // ---- remaining magic-school mastery (Item Enchantment moved up into the prefix above) ----
            new GenBuff("War Magic Mastery Self",  BuffSchool.Creature, 34, BuffGroup.Core),
            // ---- the three defenses ----
            new GenBuff("Invulnerability Self",    BuffSchool.Creature, 6,  BuffGroup.Core), // Melee Defense
            new GenBuff("Impregnability Self",     BuffSchool.Creature, 7,  BuffGroup.Core), // Missile Defense
            new GenBuff("Magic Resistance Self",   BuffSchool.Creature, 15, BuffGroup.Core), // Magic Defense
            // ---- per-weapon masteries (classic skill set) ----
            new GenBuff("Axe Mastery Self",        BuffSchool.Creature, 1,  BuffGroup.Core),
            new GenBuff("Bow Mastery Self",        BuffSchool.Creature, 2,  BuffGroup.Core),
            new GenBuff("Crossbow Mastery Self",   BuffSchool.Creature, 3,  BuffGroup.Core),
            new GenBuff("Dagger Mastery Self",     BuffSchool.Creature, 4,  BuffGroup.Core),
            new GenBuff("Mace Mastery Self",       BuffSchool.Creature, 5,  BuffGroup.Core),
            new GenBuff("Spear Mastery Self",      BuffSchool.Creature, 9,  BuffGroup.Core),
            new GenBuff("Staff Mastery Self",      BuffSchool.Creature, 10, BuffGroup.Core),
            new GenBuff("Sword Mastery Self",      BuffSchool.Creature, 11, BuffGroup.Core),
            new GenBuff("Thrown Weapons Self",     BuffSchool.Creature, 12, BuffGroup.Core),
            new GenBuff("Unarmed Combat Self",     BuffSchool.Creature, 13, BuffGroup.Core),
            // ---- utility / lore / movement / tradeskill / tinkering masteries ----
            new GenBuff("Arcane Lore Self",        BuffSchool.Creature, 14, BuffGroup.Utility),
            new GenBuff("Person Attunement Self",  BuffSchool.Creature, 19, BuffGroup.Utility), // Assess Person
            new GenBuff("Deception Mastery Self",  BuffSchool.Creature, 20, BuffGroup.Utility),
            new GenBuff("Healing Mastery Self",    BuffSchool.Creature, 21, BuffGroup.Utility),
            new GenBuff("Jumping Mastery Self",    BuffSchool.Creature, 22, BuffGroup.Utility),
            new GenBuff("Lockpick Mastery Self",   BuffSchool.Creature, 23, BuffGroup.Utility),
            new GenBuff("Sprint Self",             BuffSchool.Creature, 24, BuffGroup.Utility), // Run
            new GenBuff("Monster Attunement Self", BuffSchool.Creature, 27, BuffGroup.Utility), // Assess Creature
            new GenBuff("Weapon Expertise Self",   BuffSchool.Creature, 28, BuffGroup.Utility), // Weapon Tinkering
            new GenBuff("Armor Expertise Self",    BuffSchool.Creature, 29, BuffGroup.Utility), // Armor Tinkering
            new GenBuff("Magic Item Expertise Self", BuffSchool.Creature, 30, BuffGroup.Utility), // Magic Item Tinkering
            new GenBuff("Item Expertise Self",     BuffSchool.Creature, 18, BuffGroup.Utility), // Item Tinkering
            new GenBuff("Leadership Mastery Self", BuffSchool.Creature, 35, BuffGroup.Utility),
            new GenBuff("Fealty Self",             BuffSchool.Creature, 36, BuffGroup.Utility), // Loyalty
            new GenBuff("Fletching Mastery Self",  BuffSchool.Creature, 37, BuffGroup.Utility),
            new GenBuff("Alchemy Mastery Self",    BuffSchool.Creature, 38, BuffGroup.Utility),
            new GenBuff("Cooking Mastery Self",    BuffSchool.Creature, 39, BuffGroup.Utility),
            // ---- Life vitals + base armor ----
            new GenBuff("Armor Self",              BuffSchool.Life, 0, BuffGroup.Vital),
            new GenBuff("Regeneration Self",       BuffSchool.Life, 0, BuffGroup.Vital), // health regen RATE (persistent)
            new GenBuff("Rejuvenation Self",       BuffSchool.Life, 0, BuffGroup.Vital), // stamina regen RATE (persistent)
            new GenBuff("Mana Renewal Self",       BuffSchool.Life, 0, BuffGroup.Vital), // mana regen RATE (persistent)
            // NOT Revitalize/Heal/Mana Boost: those are one-shot BURST restores (a fixed amount),
            // not persistent buffs — they belong to the recovery system, not the buff profile.
            // ---- Life elemental/physical protections (stack with the Item banes) ----
            new GenBuff("Fire Protection Self",      BuffSchool.Life, 0, BuffGroup.Protection),
            new GenBuff("Cold Protection Self",      BuffSchool.Life, 0, BuffGroup.Protection),
            new GenBuff("Acid Protection Self",      BuffSchool.Life, 0, BuffGroup.Protection),
            new GenBuff("Lightning Protection Self", BuffSchool.Life, 0, BuffGroup.Protection),
            new GenBuff("Blade Protection Self",     BuffSchool.Life, 0, BuffGroup.Protection),
            new GenBuff("Piercing Protection Self",  BuffSchool.Life, 0, BuffGroup.Protection),
            new GenBuff("Bludgeon Protection Self",  BuffSchool.Life, 0, BuffGroup.Protection),
            // ---- Item banes + Impenetrability ----
            new GenBuff("Flame Bane",         BuffSchool.Item, 0, BuffGroup.Bane),
            new GenBuff("Frost Bane",         BuffSchool.Item, 0, BuffGroup.Bane),
            new GenBuff("Acid Bane",          BuffSchool.Item, 0, BuffGroup.Bane),
            new GenBuff("Lightning Bane",     BuffSchool.Item, 0, BuffGroup.Bane),
            new GenBuff("Blade Bane",         BuffSchool.Item, 0, BuffGroup.Bane),
            new GenBuff("Piercing Bane",      BuffSchool.Item, 0, BuffGroup.Bane),
            new GenBuff("Bludgeon Bane",      BuffSchool.Item, 0, BuffGroup.Bane),
            new GenBuff("Impenetrability",    BuffSchool.Item, 0, BuffGroup.Bane),
            // Weapon-buff auras are NOT here — they're chosen by combat archetype (AddWeaponAuras).
        };

        // Combat archetype, from the character's trained/specialized skills (CharFilterSkillType
        // ids). Weapon auras are Item Enchantment spells, but WHICH ones a character wants depends
        // on what they fight with — hence these sets rather than a flat include-all.
        private static readonly int[] MeleeSkills   = { 1, 4, 5, 9, 10, 11, 13, 41, 44, 45, 46, 49 };
        // Axe/Dagger/Mace/Spear/Staff/Sword/Unarmed + TwoHanded/Heavy/Light/Finesse/DualWield.
        private static readonly int[] MissileSkills = { 2, 3, 12, 47 };   // Bow/Crossbow/Thrown/MissileWeapons
        private static readonly int[] CasterSkills  = { 34, 43 };         // War Magic / Void Magic

        /// <summary>Generate the profile. <paramref name="catalog"/> is the editor family catalog
        /// (family → era-stable category); <paramref name="skillTraining"/> returns a skill's
        /// TrainingType rank (0-3) by CharFilterSkillType id — a buff is kept only when the skill
        /// it raises is Trained/Specialized (attributes/vitals/banes/auras have no skill gate) AND
        /// its casting school is itself trained.</summary>
        public static GenerationResult Generate(
            IList<EditorFamily> catalog, Func<int, int> skillTraining, GeneratorOptions opts = null)
        {
            opts = opts ?? new GeneratorOptions();
            skillTraining = skillTraining ?? (_ => 0);
            var result = new GenerationResult { Profile = new ModernProfile() };

            // Build a case-insensitive family→category index once.
            var byName = new Dictionary<string, EditorFamily>(StringComparer.OrdinalIgnoreCase);
            if (catalog != null)
                foreach (var f in catalog)
                    if (f != null && !string.IsNullOrEmpty(f.DisplayName) && !byName.ContainsKey(f.DisplayName))
                        byName[f.DisplayName] = f;

            result.CreatureCastable = skillTraining(SkCreature) >= TrainedOrBetter;
            result.ItemCastable     = skillTraining(SkItem)     >= TrainedOrBetter;
            result.LifeCastable     = skillTraining(SkLife)     >= TrainedOrBetter;

            var seenCategory = new HashSet<int>();

            foreach (var gb in Set)
            {
                if (!GroupEnabled(gb.Group, opts)) continue;

                // Casting-school gate: can't self-cast this school at all → skip the whole family.
                if (gb.School == BuffSchool.Creature && !result.CreatureCastable) continue;
                if (gb.School == BuffSchool.Item && !result.ItemCastable) continue;
                if (gb.School == BuffSchool.Life && !result.LifeCastable) continue;

                // Skill gate: a mastery is only useful if its skill is Trained/Specialized.
                if (gb.BoostSkill != 0 && skillTraining(gb.BoostSkill) < TrainedOrBetter)
                {
                    result.SkippedUntrained.Add(gb.Family);
                    continue;
                }

                EditorFamily fam;
                if (!byName.TryGetValue(gb.Family, out fam) || fam.Category == 0)
                {
                    result.Unresolved.Add(gb.Family);
                    continue;
                }
                if (!seenCategory.Add(fam.Category)) continue; // one entry per stacking category

                result.Profile.Buffs.Add(new ModernBuffEntry
                {
                    Category = fam.Category,
                    Target = SpellTarget.Self,
                    DisplayName = gb.Family,
                    School = SchoolName(gb.School)   // disambiguates cross-school groups (Healing Mastery vs Heal Self)
                });
                result.Included.Add(gb.Family);
            }

            // Weapon-buff auras — chosen by combat archetype (all Item Enchantment, so they need
            // Item magic castable). Melee: damage + accuracy + defence + speed; missile: the same
            // minus Heart Seeker (accuracy is melee-only); pure war/void caster: only the caster
            // pair, Spirit Drinker + Hermetic Link (no melee/missile auras).
            if (opts.IncludeWeaponAuras && result.ItemCastable)
                AddWeaponAuras(result, byName, skillTraining, seenCategory);

            return result;
        }

        private static void AddWeaponAuras(GenerationResult r, Dictionary<string, EditorFamily> byName,
            Func<int, int> tr, HashSet<int> seen)
        {
            bool melee = AnyTrained(tr, MeleeSkills);
            bool missile = AnyTrained(tr, MissileSkills);
            bool caster = AnyTrained(tr, CasterSkills);
            bool physical = melee || missile;

            // Damage aura: Blood Drinker (physical) and Spirit Drinker (caster) are the same
            // stacking group (154 — they converge to "Infected Caress" at level 7), so this is ONE
            // entry; at cast time it resolves to whichever the character actually knows. Label it
            // for the wielder. A pure caster gets it as Spirit Drinker; anyone with a weapon, as
            // Blood Drinker.
            if (physical)      AddAura(r, byName, seen, "Blood Drinker", "Blood Drinker");
            else if (caster)   AddAura(r, byName, seen, "Blood Drinker", "Spirit Drinker");

            if (melee)         AddAura(r, byName, seen, "Heart Seeker", "Heart Seeker");   // accuracy — melee only
            if (physical)      AddAura(r, byName, seen, "Defender", "Defender");           // weapon defence
            if (physical)      AddAura(r, byName, seen, "Swift Killer", "Swift Killer");   // attack speed
            // Hermetic Link is no longer added here — it moved up into the fixed prefix (right after
            // Item Enchantment mastery, owner's spec) as a Core Item entry, so it's always included
            // when Item magic is castable, regardless of archetype. See the Set above.
        }

        /// <summary>Add one aura, resolving its category through <paramref name="viaFamily"/> in the
        /// catalog but storing <paramref name="label"/> as the display name (lets the caster damage
        /// aura resolve via the Blood Drinker family yet read as "Spirit Drinker").</summary>
        private static void AddAura(GenerationResult r, Dictionary<string, EditorFamily> byName,
            HashSet<int> seen, string viaFamily, string label)
        {
            EditorFamily f;
            if (!byName.TryGetValue(viaFamily, out f) || f.Category == 0) { r.Unresolved.Add(label); return; }
            if (!seen.Add(f.Category)) return;
            r.Profile.Buffs.Add(new ModernBuffEntry
            {
                Category = f.Category, Target = SpellTarget.Self, DisplayName = label,
                School = "Item"   // weapon auras are Item Enchantment
            });
            r.Included.Add(label);
        }

        /// <summary>Map the generator's casting school to the school-string selection filters on.</summary>
        private static string SchoolName(BuffSchool s) =>
            s == BuffSchool.Creature ? "Creature" : s == BuffSchool.Life ? "Life" : "Item";

        private static bool AnyTrained(Func<int, int> tr, int[] skills)
        {
            foreach (var s in skills) if (tr(s) >= TrainedOrBetter) return true;
            return false;
        }

        private static bool GroupEnabled(BuffGroup g, GeneratorOptions o)
        {
            switch (g)
            {
                case BuffGroup.Core: return true;
                case BuffGroup.Utility: return o.IncludeUtilitySkills;
                case BuffGroup.Vital: return o.IncludeVitals;
                case BuffGroup.Protection: return o.IncludeLifeProtections;
                case BuffGroup.Bane: return o.IncludeBanes;
                default: return true;
            }
        }
    }
}
