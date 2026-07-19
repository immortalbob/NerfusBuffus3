using System.Collections.Generic;

namespace NB3.Core
{
    /// <summary>The three vitals, valued to match ACE's <c>PropertyAttribute2nd</c> / kit-and-potion
    /// <c>BoosterEnum</c> (int property/value-key <b>89</b>): a Food or Healer weenie's key-89 value
    /// says which of these it restores — <b>2 = Health, 4 = Stamina, 6 = Mana</b> (doc 19 §1).</summary>
    public enum Vital { Health = 2, Stamina = 4, Mana = 6 }

    /// <summary>
    /// The seam between the pure buff logic and the live client. Everything the old
    /// <c>NerfusFilter</c> hand-parsed off the wire is expressed here as a handful of queries;
    /// on Decal 3 a single adapter implements this over
    /// <c>CoreManager.Current.CharacterFilter</c> / <c>.WorldFilter</c>. Keeping the engine
    /// behind this interface is what makes it testable without Decal (doc 15).
    /// </summary>
    public interface IGameState
    {
        /// <summary>The player's own object id.</summary>
        int SelfId { get; }

        /// <summary>The currently selected target's object id, or 0 if none.</summary>
        int SelectedTargetId { get; }

        /// <summary>Does the player's spellbook contain this exact spell id?
        /// (Decal 3: <c>CharacterFilter.SpellBook</c>; old plugin: <c>spellbook_isInBook</c>.)</summary>
        bool SpellKnown(int spellId);

        /// <summary>Find an item in inventory by (case-insensitive) name; 0 if not found.
        /// (Old plugin: <c>inv_GUIDfromName</c>.)</summary>
        int FindItemByName(string name);

        /// <summary>Find an item in inventory whose name CONTAINS the fragment
        /// (case-insensitive); 0 if none. Serves the regen consumables, where the exact name
        /// varies by variant ("Mana Elixir" / "Trade Mana Elixir").</summary>
        int FindItemBySubstring(string nameFragment);

        /// <summary>Auto-scan inventory for the best DRINKABLE (Food weenie) that restores
        /// <paramref name="vital"/>, found by live item properties — <c>BoosterEnum</c> (key 89)
        /// == the vital, ranked by <c>BoostValue</c> (key 90). This detects every potion/food
        /// variant (health, stamina AND mana) without a hardcoded name table (doc 19 §1/§5);
        /// the adapter falls back to name fragments only when properties can't be read.
        /// 0 when nothing suitable is carried.</summary>
        int FindBestPotion(Vital vital);

        /// <summary>Auto-scan inventory for the best HEALING KIT (Healer weenie) that restores
        /// Health, ranked by expected heal from live properties (<c>BoostValue</c> key 90 +
        /// <c>HealkitMod</c> key 100; doc 19 §1/§4). 0 when none is carried. (The Options kit
        /// tiers remain a name-based fallback inside the adapter.)</summary>
        int FindBestHealingKit();

        /// <summary>Find any world object (typically another player) by exact name; 0 if not
        /// in range. Serves the recovered editor's "By Name:" Other-target mode.
        /// (Decal 3: <c>WorldFilter.GetByName</c>.)</summary>
        int FindWorldByName(string name);

        /// <summary>All worn/wielded items with their combined coverage mask.
        /// (Old plugin: the Inventory model's <c>Wields</c> list + cover masks.)</summary>
        IEnumerable<WornItem> WornItems { get; }

        /// <summary>Is this item currently worn/wielded (CURRENT_WIELDED_LOCATION != 0)?
        /// The planners skip an Equip step for an already-wielded item — using it again
        /// would UNequip it (double-click semantics).</summary>
        bool IsWielded(int guid);

        /// <summary>Spell ids of the player's currently-active enchantments (Decal 3:
        /// <c>CharacterFilter.Enchantments</c> → <c>SpellId</c>). The modern selector uses these
        /// to skip a stacking category that's already covered at an equal-or-higher level.</summary>
        IEnumerable<int> ActiveEnchantmentSpellIds { get; }

        /// <summary>Active enchantments with their remaining duration (Decal 3:
        /// <c>CharacterFilter.Enchantments</c> → <c>SpellId</c> + <c>TimeRemaining</c>). Lets the
        /// planner recast buffs that are expiring within the rebuff window on a maintenance
        /// re-run, rather than skipping every already-active buff forever.</summary>
        IEnumerable<NB3.Core.Modern.ActiveEnchant> ActiveEnchantments { get; }

        /// <summary>True while a spell cast is in flight (the client is "busy"). The original
        /// surfaced this as the cycle view's "Busy" counter — a cast attempted while busy is
        /// retried, not stacked.</summary>
        bool IsCasting { get; }

        /// <summary>True when the character is in Magic combat mode (required to cast). The
        /// shell flips this via <c>Host.Actions.SetCombatMode</c> at cycle start.</summary>
        bool InMagicCombatMode { get; }

        /// <summary>Base mana cost of a spell, from the Portal.dat spell table
        /// (<c>FileService.SpellTable</c>). Drives NB3's "Expected % of Spell Cost" mana gate.
        /// 0 for equipment/unknown.</summary>
        int SpellManaCost(int spellId);

        /// <summary>Training rank of a skill by its CharFilterSkillType id (adapter dump): 0
        /// Unusable, 1 Untrained, 2 Trained, 3 Specialized (0 when unavailable). Drives /nbgen's
        /// "buffs for the skills I actually have" filter — a skill-mastery buff is generated only
        /// when its skill is Trained or Specialized (rank &gt;= 2).</summary>
        int SkillTrainingLevel(int charFilterSkillType);

        /// <summary>The character's EFFECTIVE (buffed) magic skill for a spell's school —
        /// "Creature"/"Life"/"Item"/"War"/"Void" (Decal <c>CharacterFilter.EffectiveSkill</c>;
        /// ACE <c>GetCreatureSkill(school).Current</c>). Feeds the fizzle-chance level cap so
        /// the planner casts the highest level it can actually LAND, not just the highest known.
        /// 0 when unavailable (which disables skill capping, failing open to the old behaviour).</summary>
        int EffectiveMagicSkill(string school);

        // ---- vitals, for the mana-management stages (used later) ----
        int CurrentMana { get; }
        int MaxMana { get; }
        int CurrentStamina { get; }
        int MaxStamina { get; }
        int CurrentHealth { get; }
        int MaxHealth { get; }
    }
}
