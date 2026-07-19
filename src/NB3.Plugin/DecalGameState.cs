using System;
using System.Collections.Generic;
using System.Reflection;
using Decal.Adapter;
using Decal.Adapter.Wrappers;
using NB3.Core;

namespace NB3.Plugin
{
    /// <summary>
    /// The single adapter that replaces the entire old NerfusFilter. Every query the cycle
    /// runner needs comes straight from the Decal 3 managed API.
    ///
    /// Value-key discipline (docs 13 §6 / 16 §1): the game-truth properties are read by their
    /// REAL property ids — coverage is CLOTHING_PRIORITY_INT = key 4, equip slots are
    /// LOCATIONS_INT = key 9, the currently-wielded slot is CURRENT_WIELDED_LOCATION_INT =
    /// key 10, the wielder is WIELDER (raw IID 255 surfaced by Decal as the synthetic
    /// LongValueKey.Wielder = 218103818, doc 13 §9). Decal's *named* synthetic "Coverage" key
    /// in the 218103808+ range is NOT the coverage mask — that exact misread is the shipped
    /// bug doc 13 §6 records, so it is deliberately not used anywhere in this file.
    /// Raw game masks are translated into NB3's own scheme by <see cref="NB3Coverage"/>
    /// before any profile-mask match (docs/COVER_MASK_RECOVERY.md).
    /// </summary>
    internal sealed class DecalGameState : IGameState
    {
        // Real AC property ids used as LongValueKey (doc 16 §1: the ids ARE the enum values).
        private const int KeyClothingPriority = 4;   // CLOTHING_PRIORITY_INT — coverage
        private const int KeyLocations        = 9;   // LOCATIONS_INT — equippable slots
        private const int KeyWieldedLocation  = 10;  // CURRENT_WIELDED_LOCATION_INT — slot in use

        // Consumable properties for the vital auto-scan (doc 19 §1, ace-world-property-ids.tsv):
        private const int KeyBoosterEnum = 89;       // which vital a Food/Healer restores (2/4/6)
        private const int KeyBoostValue  = 90;       // points restored (Food) / +skill (Healer)
        private const int KeyHealkitMod  = 100;      // FLOAT — a kit's heal multiplier
        // Decal ObjectClass ids (adapter_dump.txt): a drinkable/edible vs a healing kit.
        private const int ObjClassFood       = 6;
        private const int ObjClassHealingKit = 29;

        // Game LOCATIONS bits (doc 16 §3.1) for the /nbid weapon/shield probes.
        private const int LocMelee   = 1048576;
        private const int LocShield  = 2097152;
        private const int LocMissile = 4194304;
        private const int LocCaster  = 16777216;

        private readonly PluginHost _host;
        private readonly CoreManager _core;
        private readonly Func<int, NB3.Core.Modern.SpellInfo> _spellLookup;

        public DecalGameState(PluginHost host, CoreManager core,
                              Func<int, NB3.Core.Modern.SpellInfo> spellLookup)
        { _host = host; _core = core; _spellLookup = spellLookup; }

        public int SelfId => _core.CharacterFilter.Id;                     // confirmed
        public int SelectedTargetId => _host.Actions.CurrentSelection;     // confirmed

        // Combat mode gate (confirmed: HooksWrapper.CombatMode / SetCombatMode). The getter is
        // documented as the value 1/2/4/8 (doc 01 §4.2a); compare through int so the code is
        // correct whether the SDK types it as CombatState or int.
        public bool InMagicCombatMode
        { get { try { return (int)_host.Actions.CombatMode == (int)CombatState.Magic; } catch { return false; } } }

        public void EnsureMagicMode() { try { _host.Actions.SetCombatMode(CombatState.Magic); } catch { } }

        /// <summary>Actions.BusyState == 0 — REQUIRED before every UseItem/MoveItem
        /// (doc 13 §10.4; doc 01 calls busy-stacking the "#1 cause of actions randomly not
        /// happening"). A transient throw reads as busy (retry next tick); if the member
        /// proves unavailable on this SDK build the gate degrades to open rather than
        /// wedging the cycle forever — the timer's 300 ms pacing still applies.</summary>
        private bool _busyStateBroken;
        public bool ClientIdle
        {
            get
            {
                if (_busyStateBroken) return true;
                try { return _host.Actions.BusyState == 0; }
                catch { _busyStateBroken = true; return true; }
            }
        }

        // Busy detection for CASTING (BusyState covers item manipulation, not casts): track our
        // own cast-in-flight window (set on CastSpell, cleared on the cast-done chat/event).
        public bool IsCasting { get; set; }

        // The player's actual spellbook. CharacterFilter.SpellBook is a ReadOnlyCollection<int> of
        // KNOWN spell ids (adapter dump §CharacterFilter: "prop ReadOnlyCollection`1<int> SpellBook").
        // We enumerate it ONCE per cycle into a set (RefreshSpellBook) and answer membership from
        // that — instead of the old per-id IsSpellKnown(), which fails OPEN (returns true on any read
        // hiccup) and thereby leaks the entire client spell table (every monster/boss/quest spell)
        // into buff selection, so the picker grabs an 800-power boss enchant. The spellbook can't
        // fail that way: a bad read yields an EMPTY book, so NB3 casts NOTHING rather than junk.
        // This is the guarantee that it only ever casts spells the character actually knows.
        private HashSet<int> _knownSpells;

        /// <summary>Re-read <c>CharacterFilter.SpellBook</c> into the known-spell set. Called at
        /// cycle start (a newly-learned spell is picked up on the next /nbuff — same cadence as the
        /// skill read). Returns how many spells were read; 0 means the book was unreadable or not yet
        /// synced, and the caller should refuse to cast rather than fall back to the whole table.</summary>
        public int RefreshSpellBook()
        {
            var set = new HashSet<int>();
            try
            {
                var book = _core.CharacterFilter.SpellBook;   // ReadOnlyCollection<int>
                if (book != null) foreach (int id in book) set.Add(id);
            }
            catch { /* leave empty — fail CLOSED, never open */ }
            _knownSpells = set;
            return set.Count;
        }

        /// <summary>Is this spell in the player's spellbook? Answered from the enumerated
        /// <c>SpellBook</c> set (see <see cref="RefreshSpellBook"/>), never per-id IsSpellKnown — so
        /// an unreadable book casts nothing instead of leaking the monster-spell table.</summary>
        public bool SpellKnown(int spellId)
        {
            if (_knownSpells == null) RefreshSpellBook();
            return _knownSpells.Contains(spellId);
        }

        /// <summary>The player's known spell ids (the enumerated spellbook), for /nbdiag-style dumps.</summary>
        public IEnumerable<int> KnownSpellIds
        {
            get { if (_knownSpells == null) RefreshSpellBook(); return _knownSpells; }
        }

        public int FindItemByName(string name)
        {
            try
            {
                foreach (WorldObject wo in _core.WorldFilter.GetInventory())   // confirmed
                    if (string.Equals(wo.Name, name, StringComparison.OrdinalIgnoreCase))
                        return wo.Id;
            }
            catch { }
            return 0;
        }

        public int FindItemBySubstring(string nameFragment)
        {
            if (string.IsNullOrEmpty(nameFragment)) return 0;
            try
            {
                foreach (WorldObject wo in _core.WorldFilter.GetInventory())
                {
                    var n = wo.Name;
                    if (n != null && n.IndexOf(nameFragment, StringComparison.OrdinalIgnoreCase) >= 0)
                        return wo.Id;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>Auto-scan inventory for the strongest DRINKABLE that restores <paramref name="vital"/>.
        /// Property-based (doc 19 §1/§5): a Food weenie (<see cref="ObjClassFood"/>) whose
        /// <c>BoosterEnum</c> (key 89) equals the vital, ranked by <c>BoostValue</c> (key 90) — so it
        /// finds health, stamina AND mana potions/food of any name. Falls back to the doc-19 §5 name
        /// fragments only if no Food object exposes readable properties.</summary>
        public int FindBestPotion(Vital vital)
        {
            int want = (int)vital;              // 2 Health / 4 Stamina / 6 Mana
            int bestGuid = 0, bestBoost = 0;
            try
            {
                foreach (WorldObject wo in _core.WorldFilter.GetInventory())
                {
                    if (ObjClassOf(wo) != ObjClassFood) continue;         // a drinkable/edible only
                    if (ValueOr(wo, KeyBoosterEnum) != want) continue;    // restores THIS vital?
                    int boost = ValueOr(wo, KeyBoostValue);
                    if (boost > bestBoost) { bestBoost = boost; bestGuid = wo.Id; }
                }
            }
            catch { }
            if (bestGuid != 0) return bestGuid;

            // No-appraisal fallback: the doc-19 §5 name ladder.
            switch (vital)
            {
                case Vital.Mana:    return RegenItems.FindManaElixir(this);
                case Vital.Stamina: return RegenItems.FindStaminaElixir(this);
                case Vital.Health:  return RegenItems.FindHealthElixir(this);
            }
            return 0;
        }

        /// <summary>Auto-scan inventory for the best HEALTH-restoring healing kit (Healer weenie,
        /// <see cref="ObjClassHealingKit"/>), ranked by a live expected-heal proxy: <c>BoostValue</c>
        /// (key 90, +skill) plus <c>HealkitMod</c> (key 100, ×heal) — doc 19 §1/§4. Falls back to the
        /// tier name table when properties can't be read.</summary>
        public int FindBestHealingKit()
        {
            int bestGuid = 0;
            double bestScore = -1;
            try
            {
                foreach (WorldObject wo in _core.WorldFilter.GetInventory())
                {
                    if (ObjClassOf(wo) != ObjClassHealingKit) continue;
                    int booster = ValueOr(wo, KeyBoosterEnum);
                    if (booster != (int)Vital.Health && booster != 0) continue;  // health kit (or unset)
                    double score = ValueOr(wo, KeyBoostValue) + DoubleOr(wo, KeyHealkitMod, 1.0) * 100.0;
                    if (score > bestScore) { bestScore = score; bestGuid = wo.Id; }
                }
            }
            catch { }
            if (bestGuid != 0) return bestGuid;

            // Name fallback: best of the known retail tiers present (doc 19 §4 order).
            return RegenItems.FindHealingKit(this,
                HealingKitTiers.Plentiful | HealingKitTiers.Treated | HealingKitTiers.Peerless);
        }

        private static int ObjClassOf(WorldObject wo)
        {
            try { return (int)wo.ObjectClass; } catch { return -1; }
        }

        /// <summary>Exact-name lookup across everything in range — players, NPCs, items
        /// (confirmed: WorldFilter.GetByName in the adapter dump). Serves the editor's
        /// "By Name:" Other-target mode and the ?-wizard buttons.</summary>
        public int FindWorldByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            try
            {
                foreach (WorldObject wo in _core.WorldFilter.GetByName(name))
                    return wo.Id;
            }
            catch { }
            return 0;
        }

        /// <summary>Currently worn/wielded items only (CURRENT_WIELDED_LOCATION != 0 — key 10,
        /// doc 16 §2.1; unworn armor in packs must NOT match a spellgroup), with the live game
        /// masks translated into NB3's own coverage scheme for the profile-mask AND-match.</summary>
        public IEnumerable<WornItem> WornItems
        {
            get
            {
                var list = new List<WornItem>();
                try
                {
                    foreach (WorldObject wo in _core.WorldFilter.GetInventory())
                    {
                        int wielded = ValueOr(wo, KeyWieldedLocation);
                        if (wielded == 0) continue;                       // carried, not worn
                        int clothing = ValueOr(wo, KeyClothingPriority);  // key 4, never "Coverage"
                        uint nb3 = NB3Coverage.FromGame(clothing, wielded);
                        if (nb3 != 0) list.Add(new WornItem(wo.Id, wo.Name, unchecked((int)nb3)));
                    }
                }
                catch { }
                return list;
            }
        }

        /// <summary>CURRENT_WIELDED_LOCATION != 0 (key 10). Guards the equip step: acting
        /// on an already-wielded item is a double-click UNequip.</summary>
        public bool IsWielded(int guid)
        {
            if (guid == 0) return false;
            try
            {
                var wo = _core.WorldFilter[guid];
                return wo != null && ValueOr(wo, KeyWieldedLocation) != 0;
            }
            catch { return false; }
        }

        // ---- /nbid probes: what a creature has in its weapon/shield slots ----------------

        public int WieldedWeapon(int ownerId) => FindWielded(ownerId, LocMelee | LocMissile | LocCaster);
        public int WieldedShield(int ownerId) => FindWielded(ownerId, LocShield);

        /// <summary>Find an object wielded by <paramref name="ownerId"/> occupying one of the
        /// given LOCATIONS slots. Another creature's worn equipment surfaces with the parent
        /// relationship in Wielder/Container (doc 13 §9); slot classification falls back from
        /// the currently-wielded key to the equippable-slots mask (doc 16 §3.1).</summary>
        private int FindWielded(int ownerId, int slotMask)
        {
            if (ownerId == 0) return 0;
            try
            {
                foreach (WorldObject wo in _core.WorldFilter.GetByContainer(ownerId)) // confirmed
                {
                    int slot = ValueOr(wo, KeyWieldedLocation);
                    if (slot == 0) slot = ValueOr(wo, KeyLocations);
                    if ((slot & slotMask) != 0) return wo.Id;
                }
            }
            catch { }
            return 0;
        }

        // Active enchantments → their spell ids, for the modern stacking check.
        // (confirmed: CharacterFilter.Enchantments → EnchantmentWrapper.SpellId)
        public IEnumerable<int> ActiveEnchantmentSpellIds
        {
            get
            {
                var ids = new List<int>();
                try { foreach (var e in _core.CharacterFilter.Enchantments) ids.Add(e.SpellId); }
                catch { }
                return ids;
            }
        }

        /// <summary>Active enchantments with remaining seconds (SpellId + TimeRemaining,
        /// confirmed EnchantmentWrapper members). Feeds the rebuff-window decision: a buff
        /// expiring within the window is recast instead of skipped.</summary>
        public IEnumerable<NB3.Core.Modern.ActiveEnchant> ActiveEnchantments
        {
            get
            {
                var list = new List<NB3.Core.Modern.ActiveEnchant>();
                try
                {
                    foreach (var e in _core.CharacterFilter.Enchantments)
                    {
                        int secs;
                        try { secs = e.TimeRemaining; } catch { secs = int.MaxValue; } // unknown -> treat as permanent
                        list.Add(new NB3.Core.Modern.ActiveEnchant(e.SpellId, secs));
                    }
                }
                catch { }
                return list;
            }
        }

        /// <summary>Mana cost for the "% of Spell Cost" gate. Resolved through the same spell
        /// table the planner uses (live FileService.SpellTable with the doc-16 §7.5 dump
        /// fallback inside <see cref="DecalSpellTable"/>) — one source of truth, no second
        /// FileService access path.</summary>
        public int SpellManaCost(int spellId)
        {
            try
            {
                var s = _spellLookup != null ? _spellLookup(spellId) : null;
                return s != null ? s.Mana : 0;
            }
            catch { return 0; }
        }

        // Spell school -> the magic skill that casts it (CharFilterSkillType, adapter dump):
        // CreatureEnchantment 31, ItemEnchantment 32, LifeMagic 33, WarMagic 34, VoidMagic 43.
        private const int SkillCreatureEnchantment = 31;
        private const int SkillItemEnchantment     = 32;
        private const int SkillLifeMagic           = 33;
        private const int SkillWarMagic            = 34;
        private const int SkillVoidMagic           = 43;

        /// <summary>Effective (buffed) magic skill for a spell's school — the same "current"
        /// value ACE's fizzle check reads (`GetCreatureSkill(school).Current`). Two live shapes
        /// exist (adapter dump); try both so a build where one is absent still reports a real
        /// number instead of 0 (0 disables the skill cap — fail-open, never a throw):
        ///   1. <c>CharacterFilter.EffectiveSkill[CharFilterSkillType]</c> → int
        ///   2. <c>CharacterFilter.Skills[CharFilterSkillType].Current</c> (SkillInfoWrapper;
        ///      <c>.Buffed</c> as a further fallback).</summary>
        public int EffectiveMagicSkill(string school)
        {
            int skillType = SchoolToSkillType(school);
            if (skillType == 0) return 0;

            int v = ReadIndexedInt("EffectiveSkill", skillType);
            if (v > 0) return v;

            // Fallback: Skills[type] is a SkillInfoWrapper — read .Current (then .Buffed).
            try
            {
                var wrapper = ReadIndexedObject("Skills", skillType);
                if (wrapper != null)
                {
                    int c = ReadIntProp(wrapper, "Current");
                    if (c > 0) return c;
                    int b = ReadIntProp(wrapper, "Buffed");
                    if (b > 0) return b;
                }
            }
            catch { }
            return 0;
        }

        /// <summary>Training rank (0-3) of a skill by CharFilterSkillType id — read off the same
        /// <c>CharacterFilter.Skills[type]</c> SkillInfoWrapper as the effective-skill path, via its
        /// <c>Training</c> (TrainingType: Unusable0/Untrained1/Trained2/Specialized3). 0 when the
        /// wrapper or property is absent, so /nbgen fails safe (skips a skill it can't read).</summary>
        public int SkillTrainingLevel(int charFilterSkillType)
        {
            try
            {
                var w = ReadIndexedObject("Skills", charFilterSkillType);
                if (w == null) return 0;
                var p = w.GetType().GetProperty("Training", BindingFlags.Public | BindingFlags.Instance);
                var v = p != null ? p.GetValue(w, null) : null;
                return v != null ? Convert.ToInt32(v) : 0;   // enum -> underlying int
            }
            catch { return 0; }
        }

        private object ReadIndexedObject(string propName, int skillType)
        {
            try
            {
                var cf = _core.CharacterFilter;
                var prop = cf.GetType().GetProperty(propName, BindingFlags.Public | BindingFlags.Instance);
                var coll = prop != null ? prop.GetValue(cf, null) : null;
                if (coll == null) return null;
                var idx = FindIndexer(coll.GetType(), skillType);
                if (idx == null) return null;
                object key = skillType;
                var keyType = idx.GetIndexParameters()[0].ParameterType;
                if (keyType.IsEnum) key = Enum.ToObject(keyType, skillType);
                return idx.GetValue(coll, new[] { key });
            }
            catch { return null; }
        }

        private int ReadIndexedInt(string propName, int skillType)
        {
            var v = ReadIndexedObject(propName, skillType);
            try { return v != null ? Convert.ToInt32(v) : 0; } catch { return 0; }
        }

        private static int ReadIntProp(object o, string name)
        {
            try
            {
                var p = o.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
                var v = p != null ? p.GetValue(o, null) : null;
                return v != null ? Convert.ToInt32(v) : 0;
            }
            catch { return 0; }
        }

        private static PropertyInfo FindIndexer(Type collType, int probe)
        {
            // The value indexer is keyed by CharFilterSkillType. IndexedCollection is generic over
            // TWO enums (CharFilterIndex AND CharFilterSkillType), so there can be more than one
            // enum-typed indexer — target the SKILL-TYPE one by name first. Order of preference:
            // an enum indexer whose type name contains "SkillType"; then any other enum indexer;
            // then an int indexer as a last resort (positional — least trustworthy for a key).
            PropertyInfo skillEnum = null, anyEnum = null, intIdx = null;
            foreach (var p in collType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var ps = p.GetIndexParameters();
                if (ps.Length != 1) continue;
                var t = ps[0].ParameterType;
                if (t.IsEnum)
                {
                    if (t.Name.IndexOf("SkillType", StringComparison.OrdinalIgnoreCase) >= 0) skillEnum = skillEnum ?? p;
                    else anyEnum = anyEnum ?? p;
                }
                else if (t == typeof(int)) intIdx = intIdx ?? p;
            }
            return skillEnum ?? anyEnum ?? intIdx;
        }

        /// <summary>Map a spell's school name to its casting skill. Contains-based, so it accepts
        /// the dump's bare "Creature"/"Life"/… AND the live record's fuller "Creature
        /// Enchantment"/"Life Magic"/… — the exact-match version silently returned 0 (cap off)
        /// on any build whose School.Name carried the suffix.</summary>
        private static int SchoolToSkillType(string school)
        {
            var s = (school ?? "").Trim().ToLowerInvariant();
            if (s.Length == 0) return 0;
            if (s.IndexOf("creature", StringComparison.Ordinal) >= 0) return SkillCreatureEnchantment;
            if (s.IndexOf("life", StringComparison.Ordinal) >= 0)     return SkillLifeMagic;
            if (s.IndexOf("item", StringComparison.Ordinal) >= 0)     return SkillItemEnchantment;
            if (s.IndexOf("void", StringComparison.Ordinal) >= 0)     return SkillVoidMagic; // before "war" (no overlap, but explicit)
            if (s.IndexOf("war", StringComparison.Ordinal) >= 0)      return SkillWarMagic;
            return 0;
        }

        // Vitals — confirmed: HooksWrapper.Vital[VitalType]. (Method named ReadVital, NOT Vital, so
        // it can't shadow the NB3.Core.Vital enum used by the consumable auto-scan above — a plain
        // 'Vital' member makes the compiler read 'Vital.Health' as this method, not the enum.)
        public int CurrentMana    => ReadVital(VitalType.CurrentMana);
        public int MaxMana        => ReadVital(VitalType.MaximumMana);
        public int CurrentStamina => ReadVital(VitalType.CurrentStamina);
        public int MaxStamina     => ReadVital(VitalType.MaximumStamina);
        public int CurrentHealth  => ReadVital(VitalType.CurrentHealth);
        public int MaxHealth      => ReadVital(VitalType.MaximumHealth);

        private int ReadVital(VitalType v) { try { return _host.Actions.Vital[v]; } catch { return 0; } }

        private static int ValueOr(WorldObject wo, int rawKey)
        {
            try { return wo.Values((LongValueKey)rawKey, 0); } catch { return 0; }
        }

        private static double DoubleOr(WorldObject wo, int rawKey, double dflt)
        {
            try { return wo.Values((DoubleValueKey)rawKey, dflt); } catch { return dflt; }
        }
    }
}
