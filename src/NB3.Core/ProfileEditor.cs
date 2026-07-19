using System.Collections.Generic;

namespace NB3.Core
{
    /// <summary>Editing operations behind NB3's Profile Editor view (add/remove/reorder spells,
    /// spellgroups and equips; clear). Pure list manipulation over a <see cref="Profile"/> — the
    /// view's buttons and the recovered control names map onto these. Deterministic and testable.
    ///
    /// NOTE: the per-spell "Other" target detail (named player / GUID / current target — the
    /// editCreatureOtherName / GUID / Current controls) is not exercised here because no shipped
    /// profile in hand uses it, so its exact serialized attribute names are unconfirmed. Adding
    /// that is gated on a real Other-targeting profile (flagged in the status doc).</summary>
    public sealed class ProfileEditor
    {
        public Profile Profile { get; }
        public ProfileEditor(Profile profile) { Profile = profile; }

        public int Count => Profile.Nodes.Count;

        public SpellNode AddSelfSpell(int spellId)
        {
            var n = new SpellNode { SpellId = spellId, TargetType = TargetType.Self };
            Profile.Nodes.Add(n); return n;
        }

        public SpellNode AddOtherSpell(int spellId)
        {
            var n = new SpellNode { SpellId = spellId, TargetType = TargetType.Other };
            Profile.Nodes.Add(n); return n;
        }

        public EquipNode AddEquip(string itemName)
        {
            var n = new EquipNode { ItemName = itemName, EquipBy = "name" };
            Profile.Nodes.Add(n); return n;
        }

        public SpellGroupNode AddItemSpellGroup(int coverMask, IEnumerable<int> spellIds)
        {
            var g = new SpellGroupNode { TargetCover = coverMask };
            if (spellIds != null) g.SpellIds.AddRange(spellIds);
            Profile.Nodes.Add(g); return g;
        }

        public bool RemoveAt(int index)
        {
            if (index < 0 || index >= Profile.Nodes.Count) return false;
            Profile.Nodes.RemoveAt(index); return true;
        }

        public bool MoveUp(int index)
        {
            if (index <= 0 || index >= Profile.Nodes.Count) return false;
            Swap(index, index - 1); return true;
        }

        public bool MoveDown(int index)
        {
            if (index < 0 || index >= Profile.Nodes.Count - 1) return false;
            Swap(index, index + 1); return true;
        }

        public void Clear() => Profile.Nodes.Clear();

        private void Swap(int a, int b)
        {
            var t = Profile.Nodes[a];
            Profile.Nodes[a] = Profile.Nodes[b];
            Profile.Nodes[b] = t;
        }
    }
}
