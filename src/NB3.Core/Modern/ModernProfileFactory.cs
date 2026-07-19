using System;
using System.Collections.Generic;

namespace NB3.Core.Modern
{
    /// <summary>
    /// Builds a starter <see cref="ModernProfile"/> from the live spell table, so a fresh
    /// install has something to cast before the Editor view exists. Buff lines are located
    /// by their LEVEL-1 names — the era-stable plain names (doc 16 §7: levels 1–6 are the
    /// plain numbered names; only 7/8 went bespoke) — and stored as stacking categories,
    /// never ids (MODERN_SPELL_MODEL §1–2). Names verified verbatim against the 2012 retail
    /// catalog (note the real names: "Fire/Cold/Bludgeoning Protection", not Flame/Frost/
    /// Bludgeon). Unresolvable lines (a server with renamed data) are reported, not fatal.
    /// </summary>
    public static class ModernProfileFactory
    {
        /// <summary>The classic self-buff set: six attributes, the four life-magic
        /// staples, and the seven protections.</summary>
        public static readonly string[] DefaultSelfBuffLines =
        {
            "Strength Self I", "Endurance Self I", "Coordination Self I",
            "Quickness Self I", "Focus Self I", "Willpower Self I",
            "Armor Self I", "Regeneration Self I", "Rejuvenation Self I", "Mana Renewal Self I",
            "Fire Protection Self I", "Cold Protection Self I", "Acid Protection Self I",
            "Lightning Protection Self I", "Blade Protection Self I",
            "Piercing Protection Self I", "Bludgeoning Protection Self I",
        };

        /// <summary>Create the default self-buff profile against <paramref name="table"/>.
        /// Each resolved line contributes its stacking category with Target=Self (the
        /// modern classifier fixes any dump-computed target quirk at plan time — the
        /// selector picks the Self VARIANT within the category regardless of which level-1
        /// record happened to classify oddly). <paramref name="unresolved"/> receives any
        /// line the table can't name-resolve.</summary>
        public static ModernProfile CreateDefaultSelf(
            ILiveSpellTable table, out List<string> unresolved)
        {
            unresolved = new List<string>();
            var profile = new ModernProfile { Name = "default" };
            var seenCategories = new HashSet<int>();

            foreach (var line in DefaultSelfBuffLines)
            {
                var info = FindByName(table, line);
                if (info == null) { unresolved.Add(line); continue; }
                if (!seenCategories.Add(info.Category)) continue; // one entry per category

                profile.Buffs.Add(new ModernBuffEntry
                {
                    Category = info.Category,
                    Target = SpellTarget.Self,
                    DisplayName = TrimLevel(line)
                });
            }
            return profile;
        }

        private static SpellInfo FindByName(ILiveSpellTable table, string name)
        {
            foreach (var s in table.All)
                if (string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase))
                    return s;
            return null;
        }

        private static string TrimLevel(string line) =>
            line.EndsWith(" I", StringComparison.Ordinal)
                ? line.Substring(0, line.Length - 2)
                : line;
    }
}
