namespace NB3.Core
{
    /// <summary>
    /// Inventory lookups for the mana-regen micro-sequences' consumables. Matching is by
    /// name substring; exact names are [ACE-DB]-verified (doc 19 §4–5).
    ///
    /// Kit preference (doc 19 §4, from the ACE server formula + world DB): a kit's
    /// <c>BoostValue</c> adds to the HEALING SKILL for the success check and
    /// <c>HealkitMod</c> multiplies the heal. For an automated H2M loop the check is at its
    /// hardest exactly when healing matters (difficulty = missing vital × 2), so the boost
    /// dominates: <b>Plentiful (+100 skill, ×1.6, 100 uses) → Treated (+25, ×2.0, 50) →
    /// Peerless (+20, ×1.75, 40)</b>. Treated strictly dominates Peerless on every stat —
    /// the "Peerless is best" reading of the names is wrong. (This best-first order is also
    /// the original Options view's checkbox order.)
    ///
    /// Elixirs: restore is deterministic <c>BoostValue</c> (doc 19 §5); the Trade variants
    /// restore MORE (70 vs 65) and are the cheap bulk item (Value 10) — probe them first.
    /// </summary>
    public static class RegenItems
    {
        public const string ManaElixirFragment = "Mana Elixir";       // catches the Trade variant too
        public const string StaminaElixirFragment = "Stamina Elixir";
        public const string HealthElixirFragment = "Health Elixir";

        public static int FindManaElixir(IGameState state)
        {
            int g = state.FindItemBySubstring("Trade Mana Elixir");   // 70 pts, Value 10 (doc 19 §5)
            return g != 0 ? g : state.FindItemBySubstring(ManaElixirFragment);
        }

        public static int FindStaminaElixir(IGameState state)
        {
            int g = state.FindItemBySubstring("Trade Stamina Elixir");
            return g != 0 ? g : state.FindItemBySubstring(StaminaElixirFragment);
        }

        /// <summary>Name-fragment fallback for a Health drink. The health ladder doesn't all say
        /// "Health" ("Potion of Healing" is the level-2 name), so probe the known variants in
        /// descending strength (doc 19 §5). The property scan (BoosterEnum==Health) is preferred;
        /// this only runs when properties can't be read.</summary>
        public static int FindHealthElixir(IGameState state)
        {
            int g = state.FindItemBySubstring("Trade Health Elixir");             // 70 pts, Value 10
            if (g == 0) g = state.FindItemBySubstring(HealthElixirFragment);      // "Health Elixir" (65)
            if (g == 0) g = state.FindItemBySubstring("Health Tincture");         // 50
            if (g == 0) g = state.FindItemBySubstring("Potion of Healing");       // 25 (the odd-named one)
            if (g == 0) g = state.FindItemBySubstring("Health Draught");          // 10
            return g;
        }

        /// <summary>The best enabled healing kit present in inventory, best-first per the
        /// doc-19 §4 expected-heal ranking (Plentiful → Treated → Peerless); 0 when no
        /// enabled tier is carried (or no tier is enabled at all).</summary>
        public static int FindHealingKit(IGameState state, HealingKitTiers enabled)
        {
            if ((enabled & HealingKitTiers.Plentiful) != 0)
            {
                int g = state.FindItemBySubstring("Plentiful Healing Kit");
                if (g != 0) return g;
            }
            if ((enabled & HealingKitTiers.Treated) != 0)
            {
                int g = state.FindItemBySubstring("Treated Healing Kit");
                if (g != 0) return g;
            }
            if ((enabled & HealingKitTiers.Peerless) != 0)
            {
                int g = state.FindItemBySubstring("Peerless Healing Kit");
                if (g != 0) return g;
            }
            return 0;
        }
    }
}
