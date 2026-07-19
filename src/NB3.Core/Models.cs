using System;
using System.Collections.Generic;

namespace NB3.Core
{
    /// <summary>How a spell/node is aimed. Mirrors the profile's <c>targettype</c> attribute
    /// and the spell table's <c>target</c> attribute.</summary>
    public enum TargetType
    {
        /// <summary>Cast on the player.</summary>
        Self,
        /// <summary>Cast on the current selected target (a fellow / other player).</summary>
        Other,
        /// <summary>An item-enchantment cast on a worn/wielded item, selected by coverage.</summary>
        Cover,
        /// <summary>An item-enchantment family, applied to a single named/equipped item.</summary>
        Item
    }

    /// <summary>One spell family from <c>nb3-spells.xml</c>: a name plus its seven level ids.
    /// A level id of 0 (0x0000 in the source) means "this family has no spell at that level."</summary>
    public sealed class SpellFamily
    {
        public string EditorName { get; }
        public string Name { get; }
        public TargetType Target { get; }
        /// <summary>Level ids indexed 1..7. Index 0 is unused/0.</summary>
        public IReadOnlyList<int> Levels { get; }

        public SpellFamily(string editorName, string name, TargetType target, int[] levels1To7)
        {
            EditorName = editorName;
            Name = name;
            Target = target;
            var l = new int[8];
            for (int i = 1; i <= 7 && i - 1 < levels1To7.Length; i++) l[i] = levels1To7[i - 1];
            Levels = l;
        }

        /// <summary>The spell id at <paramref name="level"/> (1..7), or 0 if the family has none there.</summary>
        public int IdAtLevel(int level) => (level >= 1 && level <= 7) ? Levels[level] : 0;

        /// <summary>The highest defined level (id != 0) in this family, or 0 if empty.</summary>
        public int HighestDefinedLevel
        {
            get { for (int i = 7; i >= 1; i--) if (Levels[i] != 0) return i; return 0; }
        }
    }

    /// <summary>Where a given spell id sits in the table: which family and which level.</summary>
    public readonly struct SpellLocation
    {
        public readonly SpellFamily Family;
        public readonly int Level;
        public SpellLocation(SpellFamily family, int level) { Family = family; Level = level; }
    }

    /// <summary>A worn or wielded item as the buff engine needs to see it (from WorldFilter on
    /// Decal 3, or from the old NerfusFilter's IObject). Coverage is the OR of the three
    /// cover-mask dwords the create-item message carried.</summary>
    public sealed class WornItem
    {
        public int Guid { get; }
        public string Name { get; }
        public int CoverageMask { get; }
        public WornItem(int guid, string name, int coverageMask)
        {
            Guid = guid; Name = name; CoverageMask = coverageMask;
        }
    }

    // ---- Profile model (mirrors sample-buff-profile.xml) --------------------------------

    public abstract class ProfileNode { }

    /// <summary>Equip a named item before buffing (e.g. a focusing stone / wand).</summary>
    public sealed class EquipNode : ProfileNode
    {
        public string ItemName { get; set; }
        /// <summary>How the original selects the item; only "name" is seen in shipped profiles.</summary>
        public string EquipBy { get; set; } = "name";
    }

    /// <summary>A single spell aimed at self or the current target.</summary>
    public sealed class SpellNode : ProfileNode
    {
        public int SpellId { get; set; }
        public TargetType TargetType { get; set; } = TargetType.Self;
    }

    /// <summary>A set of item-enchantment spells applied to every worn item whose coverage
    /// intersects <see cref="TargetCover"/>.</summary>
    public sealed class SpellGroupNode : ProfileNode
    {
        public int TargetCover { get; set; }
        public List<int> SpellIds { get; } = new List<int>();
    }

    public sealed class Profile
    {
        public string Name { get; set; } = "";
        public string Version { get; set; } = "0.1.0.0";
        public List<ProfileNode> Nodes { get; } = new List<ProfileNode>();
    }
}
