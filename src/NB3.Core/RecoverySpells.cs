using System;

namespace NB3.Core
{
    /// <summary>The recovery spells the mana-regen modes need, resolved to the exact level the
    /// character will actually cast, honouring the Options view's "Use S2M7/H2M7/Revit7",
    /// "Fallback to level 6 on unknown level 7", and "Maximum level for H2M, S2M and Revit".
    /// Ids come from NB3's own recovered spell table (Stamina to Mana Self, Health to Mana Self,
    /// Revitalize Self, Heal Self). 0 = the character can't cast that recovery.</summary>
    public sealed class RecoverySpells
    {
        public int StaminaToMana { get; private set; } // S2M
        public int HealthToMana { get; private set; }  // H2M
        public int Revitalize { get; private set; }
        public int HealSelf { get; private set; }

        public static RecoverySpells Resolve(SpellTable table, Func<int, bool> known, NB3Settings s)
        {
            return new RecoverySpells
            {
                StaminaToMana = ResolveFamily(table, known, "Stamina to Mana Self", s.UseS2M7, s),
                HealthToMana  = ResolveFamily(table, known, "Health to Mana Self",  s.UseH2M7, s),
                Revitalize    = ResolveFamily(table, known, "Revitalize Self",        s.UseRevit7, s),
                HealSelf      = ResolveFamily(table, known, "Heal Self",              use7: true, s: s)
            };
        }

        private static int ResolveFamily(SpellTable table, Func<int, bool> known,
                                         string editorName, bool use7, NB3Settings s)
        {
            var fam = table.ByEditorName(editorName);
            if (fam == null) return 0;

            // Cap: max-level slider, and level 7 only if that toggle is on.
            int cap = Math.Min(s.MaxRecoveryLevel, use7 ? 7 : 6);

            // If level 7 is requested but the player doesn't know it and fallback is OFF,
            // don't silently drop to 6 — that's exactly what the "Fallback to 6" toggle governs.
            if (use7 && cap == 7 && !s.FallbackTo6OnUnknown7)
            {
                int l7 = fam.IdAtLevel(7);
                if (l7 != 0 && known(l7)) return l7;
                cap = 6; // fall through to the normal walk-down only if fallback allowed... it isn't:
                return 0;
            }

            // Normal: highest known level at or below the cap.
            return table.ResolveCastableId(fam.IdAtLevel(cap), known, cap);
        }
    }
}
