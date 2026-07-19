using System.Collections.Generic;

namespace NB3.Core
{
    public enum CastKind { Equip, CastSelf, CastTarget, CastItem }

    /// <summary>One resolved step in a buff cycle. The Decal 3 shell turns these into
    /// <c>CoreManager.Current.Actions</c> calls; the engine itself never touches Decal.</summary>
    public sealed class CastAction
    {
        public CastKind Kind { get; set; }
        public int SpellId { get; set; }
        /// <summary>Item to equip, or the item/target a spell is cast on. 0 = self / n/a.</summary>
        public int TargetGuid { get; set; }
        public string Description { get; set; }
    }

    /// <summary>Why a requested node produced no action — surfaced to chat exactly like the
    /// original ("You can equip one yourself and resume…", "spell that isn't in Portal.dat").</summary>
    public sealed class PlanWarning
    {
        public string Message { get; set; }
        public int SpellId { get; set; }
    }

    public sealed class BuffPlan
    {
        public List<CastAction> Actions { get; } = new List<CastAction>();
        public List<PlanWarning> Warnings { get; } = new List<PlanWarning>();

        /// <summary>Requested buffs that produced no cast because an equal-or-higher enchantment
        /// is already active and the run isn't recasting active buffs. Distinguishes "you're
        /// already buffed" from "couldn't resolve" when the plan is empty.</summary>
        public int SkippedAlreadyActive { get; set; }

        /// <summary>Display names of requested buffs that produced no cast because NO castable
        /// spell was found in their category (unknown to the spellbook, or the category doesn't
        /// exist in this server's live spell table). The honest reason an empty plan is empty.</summary>
        public List<string> Unresolved { get; } = new List<string>();
    }

    public sealed class BuffOptions
    {
        /// <summary>Cap applied to self/other spell levels (the Options "Maximum level" edit).</summary>
        public int MaxSpellLevel { get; set; } = 7;
    }

    /// <summary>
    /// Turns a <see cref="Profile"/> plus the live <see cref="IGameState"/> into an ordered,
    /// fully-resolved <see cref="BuffPlan"/>. This is the pure heart of Nerfus Buffus III:
    /// the same profile-driven scheduling the original did, minus everything that touched the
    /// wire or the screen. Deterministic and side-effect free, so it unit-tests offline.
    /// </summary>
    public sealed class BuffEngine
    {
        private readonly SpellTable _table;
        public BuffEngine(SpellTable table) { _table = table; }

        public BuffPlan BuildPlan(Profile profile, IGameState state, BuffOptions options = null)
        {
            options = options ?? new BuffOptions();
            var plan = new BuffPlan();

            foreach (var node in profile.Nodes)
            {
                switch (node)
                {
                    case EquipNode en:
                        BuildEquip(en, state, plan);
                        break;
                    case SpellNode sn:
                        BuildSpell(sn, state, options, plan);
                        break;
                    case SpellGroupNode gn:
                        BuildSpellGroup(gn, state, options, plan);
                        break;
                }
            }
            return plan;
        }

        private static void BuildEquip(EquipNode en, IGameState state, BuffPlan plan)
        {
            int guid = state.FindItemByName(en.ItemName);
            if (guid == 0)
            {
                plan.Warnings.Add(new PlanWarning
                {
                    Message = $"Missing item to equip: '{en.ItemName}'. " +
                              "(You can equip one yourself and resume - You can also add a command to equip a focus in your profile.)"
                });
                return;
            }
            if (state.IsWielded(guid)) return; // already equipped — acting again would UNequip
            plan.Actions.Add(new CastAction
            {
                Kind = CastKind.Equip,
                TargetGuid = guid,
                Description = $"Equip {en.ItemName}"
            });
        }

        private void BuildSpell(SpellNode sn, IGameState state, BuffOptions options, BuffPlan plan)
        {
            int castable = _table.ResolveCastableId(sn.SpellId, state.SpellKnown, options.MaxSpellLevel);
            if (castable == 0)
            {
                plan.Warnings.Add(new PlanWarning
                {
                    SpellId = sn.SpellId,
                    Message = _table.TryLocate(sn.SpellId, out _)
                        ? $"No known level for spell 0x{sn.SpellId:X4} (skipped)."
                        : $"Profile references a spell that isn't in Portal.dat: 0x{sn.SpellId:X4}."
                });
                return;
            }

            bool self = sn.TargetType != TargetType.Other;
            plan.Actions.Add(new CastAction
            {
                Kind = self ? CastKind.CastSelf : CastKind.CastTarget,
                SpellId = castable,
                TargetGuid = self ? state.SelfId : state.SelectedTargetId,
                Description = $"Cast 0x{castable:X4} on {(self ? "self" : "target")}"
            });
        }

        private void BuildSpellGroup(SpellGroupNode gn, IGameState state, BuffOptions options, BuffPlan plan)
        {
            // For every worn item whose coverage intersects the group's target-cover mask,
            // apply each item-enchant spell in the group, resolved to the best known level.
            foreach (var item in state.WornItems)
            {
                if ((item.CoverageMask & gn.TargetCover) == 0) continue;

                foreach (int reqId in gn.SpellIds)
                {
                    // Item-enchant families cap at 7 regardless of the self/other max-level knob.
                    int castable = _table.ResolveCastableId(reqId, state.SpellKnown, 7);
                    if (castable == 0)
                    {
                        plan.Warnings.Add(new PlanWarning
                        {
                            SpellId = reqId,
                            Message = $"No known level for item spell 0x{reqId:X4} on '{item.Name}' (skipped)."
                        });
                        continue;
                    }
                    plan.Actions.Add(new CastAction
                    {
                        Kind = CastKind.CastItem,
                        SpellId = castable,
                        TargetGuid = item.Guid,
                        Description = $"Cast 0x{castable:X4} on {item.Name}"
                    });
                }
            }
        }
    }
}
