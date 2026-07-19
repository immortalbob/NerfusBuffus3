using System.Collections.Generic;
using System.Linq;

namespace NB3.Core.Modern
{
    /// <summary>
    /// The bridge that makes the modern pipeline run end-to-end: a
    /// <see cref="ModernProfile"/> (equips + category-identified buffs) is resolved against the
    /// live spell table + the player's spellbook + active enchantments into the ordinary
    /// <see cref="BuffPlan"/> that <see cref="BuffCycle"/> already executes. All era-proofing
    /// lives here — the profile never names a spell id, the planner asks the live table which
    /// concrete spell is the highest castable in each stacking category, and stacking skips
    /// anything already up. Pure + deterministic, tested against the real 2012 catalog.
    ///
    /// Per-entry targeting (recovered from the original editor's tab modes) is honoured here:
    /// Other-buffs aim at a named player / explicit GUID / the current selection; Item-buffs at
    /// a named item / explicit GUID / worn items filtered by an NB3 cover mask / all worn.
    /// </summary>
    public sealed class ModernBuffPlanner
    {
        private readonly ILiveSpellTable _table;
        private readonly ModernBuffSelector _selector;

        public ModernBuffPlanner(ILiveSpellTable table)
        {
            _table = table;
            _selector = new ModernBuffSelector(table);
        }

        public BuffPlan Plan(ModernProfile profile, IGameState state)
            => Plan(profile, state, null, null);

        public BuffPlan Plan(ModernProfile profile, IGameState state, SkillPolicy skill)
            => Plan(profile, state, skill, null);

        /// <summary>Plan with an optional skill policy and rebuff policy. Skill policy: each buff
        /// resolves to the highest level the character can cast at/above the policy's minimum
        /// success chance (ACE's fizzle model), not merely the highest known — a per-cast warning
        /// is emitted when a buff is skill-capped. Rebuff policy: decides whether an already-active
        /// buff is recast (force / expiring-within-window), so a re-run isn't silently empty just
        /// because you're currently buffed. <paramref name="autoWieldCaster"/>: when the casting
        /// hand is empty and there's something to cast, prepend an Equip that wields a wand/staff/orb
        /// from the pack — you can't cast (or even enter the Magic stance) without one.</summary>
        public BuffPlan Plan(ModernProfile profile, IGameState state, SkillPolicy skill, RebuffPolicy rebuff,
                             bool autoWieldCaster = false)
        {
            var plan = new BuffPlan();

            // 1. Equips first (a focus / wand / caster), in profile order. An item that is
            //    ALREADY wielded is skipped silently — success, and acting on it would
            //    unequip it (double-click semantics; the doc-19-era live-bug class).
            foreach (var name in profile.EquipItems)
            {
                int guid = state.FindItemByName(name);
                if (guid == 0)
                {
                    plan.Warnings.Add(new PlanWarning { Message = $"Missing item to equip: '{name}'." });
                    continue;
                }
                if (state.IsWielded(guid)) continue;
                plan.Actions.Add(new CastAction
                {
                    Kind = CastKind.Equip, TargetGuid = guid, Description = $"Equip {name}"
                });
            }

            // 2. Resolve every desired buff to the best castable spell in its category,
            //    stacking-aware (skips a category already covered at an equal/higher level).
            //    Selection runs per entry; the per-entry target detail is applied when the
            //    selected cast is expanded into actions.
            var desired = profile.Buffs
                .Select(b => new DesiredBuff(b.Category, b.Target, b.MaxLevel, b.School))
                .ToList();

            // Route effective-skill lookups through the game state when a policy is supplied.
            if (skill != null && skill.Enabled && skill.SkillOfSchool == null)
                skill.SkillOfSchool = school => state.EffectiveMagicSkill(school);

            var selected = _selector.Select(
                desired, state.SpellKnown, state.ActiveEnchantments, skill, rebuff);

            // Pair each selected cast back with the profile entries that asked for it (an
            // entry list can hold the same category twice with different targets/details).
            var used = new bool[profile.Buffs.Count];
            foreach (var s in selected)
            {
                ModernBuffEntry entry = null;
                for (int i = 0; i < profile.Buffs.Count; i++)
                {
                    var b = profile.Buffs[i];
                    if (used[i] || b.Category != s.Category || b.Target != s.Target) continue;
                    used[i] = true; entry = b; break;
                }

                if (s.SkillCapped)
                {
                    var nm = entry != null && !string.IsNullOrEmpty(entry.DisplayName) ? entry.DisplayName : $"category {s.Category}";
                    plan.Warnings.Add(new PlanWarning
                    {
                        SpellId = s.SpellId,
                        Message = $"skill-capped {nm}: casting power {s.Level} (you know {s.UncappedLevel}, but your skill can't land it at >={skill.MinChancePercent}%)."
                    });
                }

                switch (s.Target)
                {
                    case SpellTarget.Self:
                        plan.Actions.Add(Cast(CastKind.CastSelf, s.SpellId, state.SelfId,
                            Label(entry, s) + " on self"));
                        break;
                    case SpellTarget.Other:
                        PlanOtherCast(plan, s, entry, state);
                        break;
                    case SpellTarget.Item:
                        PlanItemCasts(plan, s, entry, state);
                        break;
                }
            }

            // Explain every requested buff that produced NO cast, so an empty plan is honest:
            // "already active" (skipped by the rebuff policy) vs "unresolved" (no castable spell
            // in that category — unknown to the spellbook, or absent from this server's table).
            // Same category/aim rule as selection (incl. the self-cast bane/aura bridge), so a
            // known-but-active bane reads as "active", not "unresolved".
            var selfOrOtherCats = ModernBuffSelector.SelfOrOtherCategories(_table);
            var baneBridgeCats = ModernBuffSelector.BaneBridgeCategories(_table);
            for (int i = 0; i < profile.Buffs.Count; i++)
            {
                if (used[i]) continue;
                var b = profile.Buffs[i];
                var nm = !string.IsNullOrEmpty(b.DisplayName) ? b.DisplayName : $"category {b.Category}";
                if (AnyCastable(b, state, selfOrOtherCats, baneBridgeCats))
                    plan.SkippedAlreadyActive++;                 // resolvable, so it was skipped as active
                else
                    plan.Unresolved.Add(nm);                     // no castable spell exists for it
            }

            // Auto-wield a caster (owner request): if the casting hand is empty, wield a wand/staff/orb
            // from the pack so the casts above can land — you can't enter the Magic stance, let alone
            // cast, without one. Only when there IS something to cast (no point wielding to do nothing),
            // and never a duplicate of a caster the profile's own equips already arrange. Prepended so
            // it runs first, ahead of the profile equips and every cast.
            if (autoWieldCaster && state.WieldedCasterId == 0 && plan.Actions.Any(a => a.Kind != CastKind.Equip))
            {
                int caster = state.FindWieldableCaster();
                if (caster != 0 && !plan.Actions.Any(a => a.Kind == CastKind.Equip && a.TargetGuid == caster))
                    plan.Actions.Insert(0, new CastAction
                    {
                        Kind = CastKind.Equip, TargetGuid = caster, Description = "Wield caster (auto)"
                    });
                else if (caster == 0)
                    plan.Warnings.Add(new PlanWarning
                    {
                        Message = "No caster wielded and none in your pack - casts may fizzle. Wield a wand/staff/orb."
                    });
            }
            return plan;
        }

        /// <summary>Is there ANY spell in this buff's category + target the player knows (under
        /// the per-buff cap)? If not, the buff can't be cast regardless of rebuff/skill policy —
        /// that's an "unresolved" empty, not an "already buffed" one.</summary>
        private bool AnyCastable(ModernBuffEntry b, IGameState state, HashSet<int> selfOrOtherCats,
            HashSet<int> baneBridgeCats)
        {
            foreach (var s in _table.All)
            {
                if (s.Category != b.Category) continue;
                if (!ModernBuffSelector.TargetSatisfies(b.Target, s.Target, s.Category, selfOrOtherCats, baneBridgeCats)) continue;
                if (s.IsInstantaneous) continue;                                        // burst restore/bolt, never a buff (match selection)
                if (!string.IsNullOrEmpty(b.School) && !string.IsNullOrEmpty(s.School)
                    && !ModernBuffSelector.SchoolMatches(b.School, s.School)) continue; // wrong-school pollutant (match selection)
                if (ModernBuffSelector.OffLadder(b.Target, s)) continue;   // ignore learned non-ladder specials (match selection)
                if (b.MaxLevel > 0 && s.Level > b.MaxLevel) continue;
                if (state.SpellKnown(s.Id)) return true;
            }
            return false;
        }

        /// <summary>Other-target casts: the entry's named player, its explicit GUID, or (the
        /// original's "Your Current Target (when you start)") the selection at plan time.</summary>
        private void PlanOtherCast(BuffPlan plan, SelectedCast s, ModernBuffEntry entry, IGameState state)
        {
            int target;
            string where;
            if (entry != null && entry.TargetGuid != 0)
            {
                target = entry.TargetGuid;
                where = $"0x{entry.TargetGuid:X8}";
            }
            else if (entry != null && !string.IsNullOrEmpty(entry.TargetName))
            {
                target = state.FindWorldByName(entry.TargetName);
                where = $"'{entry.TargetName}'";
                if (target == 0)
                {
                    plan.Warnings.Add(new PlanWarning { SpellId = s.SpellId, Message = $"Can't find target '{entry.TargetName}' (skipped)." });
                    return;
                }
            }
            else
            {
                target = state.SelectedTargetId;
                where = "target";
                if (target == 0)
                {
                    plan.Warnings.Add(new PlanWarning { SpellId = s.SpellId, Message = "Other-target buff but no target selected (skipped)." });
                    return;
                }
            }
            plan.Actions.Add(Cast(CastKind.CastTarget, s.SpellId, target, Label(entry, s) + " on " + where));
        }

        /// <summary>Item-enchant spells are cast per targeted item. On a modern server this is
        /// the OPTIONAL direct-cast route only: banes are self-cast whole-suit (the shield inherits
        /// from the banes on you) and weapon buffs are self-cast auras ("Aura of Infected Caress"),
        /// so the modern defaults resolve as <see cref="SpellTarget.Self"/> and never reach here.
        /// This path serves deliberate direct casts on a named item / GUID / cover-mask-matched
        /// worn items (the recovered editor's Item tab modes), and servers/data that still carry
        /// item-target enchants (e.g. the 2012 table).</summary>
        private void PlanItemCasts(BuffPlan plan, SelectedCast s, ModernBuffEntry entry, IGameState state)
        {
            // Named item or explicit GUID: one direct cast.
            if (entry != null && entry.ItemGuid != 0)
            {
                plan.Actions.Add(Cast(CastKind.CastItem, s.SpellId, entry.ItemGuid,
                    Label(entry, s) + $" on 0x{entry.ItemGuid:X8}"));
                return;
            }
            if (entry != null && !string.IsNullOrEmpty(entry.ItemName))
            {
                int guid = state.FindItemByName(entry.ItemName);
                if (guid == 0)
                {
                    plan.Warnings.Add(new PlanWarning { SpellId = s.SpellId, Message = $"Can't find item '{entry.ItemName}' (skipped)." });
                    return;
                }
                plan.Actions.Add(Cast(CastKind.CastItem, s.SpellId, guid,
                    Label(entry, s) + $" on {entry.ItemName}"));
                return;
            }

            // Worn items — the whole set, or filtered by the entry's NB3 cover mask (the
            // recovered Spellgroup semantics: item.coverage AND mask != 0).
            var worn = state.WornItems?.ToList() ?? new System.Collections.Generic.List<WornItem>();
            if (entry != null && entry.CoverMask != 0)
                worn = worn.Where(w => (w.CoverageMask & entry.CoverMask) != 0).ToList();
            if (worn.Count == 0)
            {
                plan.Warnings.Add(new PlanWarning { SpellId = s.SpellId, Message = "Item enchant but no matching worn/wielded item (skipped)." });
                return;
            }
            foreach (var item in worn)
                plan.Actions.Add(new CastAction
                {
                    Kind = CastKind.CastItem, SpellId = s.SpellId, TargetGuid = item.Guid,
                    Description = Label(entry, s) + $" on {item.Name}"
                });
        }

        private static string Label(ModernBuffEntry entry, SelectedCast s) =>
            entry != null && !string.IsNullOrEmpty(entry.DisplayName)
                ? $"Cast {entry.DisplayName}"
                : $"Cast 0x{s.SpellId:X4}";

        private static CastAction Cast(CastKind kind, int spellId, int guid, string description) =>
            new CastAction { Kind = kind, SpellId = spellId, TargetGuid = guid, Description = description };
    }
}
