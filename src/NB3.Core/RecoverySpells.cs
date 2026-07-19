using System;

namespace NB3.Core
{
    /// <summary>The recovery spells the mana-regen modes need, each resolved to the exact level the
    /// character will actually cast: the highest level it KNOWS at or below the Options view's
    /// "Maximum level for H2M, S2M, Revit and Heal" cap, walking down so an unknown top level falls
    /// back to the best known below it. Ids come from NB3's own recovered spell table (Stamina to
    /// Mana Self, Health to Mana Self, Revitalize Self, Heal Self). 0 = the character can't cast that
    /// recovery.</summary>
    public sealed class RecoverySpells
    {
        public int StaminaToMana { get; private set; } // S2M
        public int HealthToMana { get; private set; }  // H2M (level 7 is "Cannibalize")
        public int Revitalize { get; private set; }
        public int HealSelf { get; private set; }

        public static RecoverySpells Resolve(SpellTable table, Func<int, bool> known, NB3Settings s)
        {
            int cap = s != null ? s.MaxRecoveryLevel : 7;
            return new RecoverySpells
            {
                StaminaToMana = ResolveFamily(table, known, "Stamina to Mana Self", cap),
                HealthToMana  = ResolveFamily(table, known, "Health to Mana Self",  cap),
                Revitalize    = ResolveFamily(table, known, "Revitalize Self",      cap),
                HealSelf      = ResolveFamily(table, known, "Heal Self",            cap)
            };
        }

        /// <summary>Highest level of a family the character knows at or below <paramref name="cap"/>
        /// (1-7), walking down — the automatic "fall back to a lower level" behaviour. 0 if the
        /// family isn't in the table or no level at or below the cap is known.</summary>
        private static int ResolveFamily(SpellTable table, Func<int, bool> known, string editorName, int cap)
        {
            var fam = table.ByEditorName(editorName);
            if (fam == null) return 0;
            if (cap < 1) cap = 1; else if (cap > 7) cap = 7;
            return table.ResolveCastableId(fam.IdAtLevel(cap), known, cap);
        }
    }
}
