using System;
using System.Collections.Generic;

namespace NB3.Core
{
    /// <summary>
    /// NB3's own item-coverage bit scheme — recovered by disassembly of the Profile Editor's
    /// cover-mask builder (the checkbox reader at .text:0x1C3F2) and cross-checked against the
    /// named bit table in .rdata:0x3B480. NOT the game's raw CLOTHING_PRIORITY bits; NB3 used
    /// this compact scheme of its own. Confirmed against the shipped sample profile: the
    /// "all armor" spellgroup mask 0x00007F21 is exactly Coat|Legs|Girth|Head|Feet|Hands, and
    /// the weapon spellgroup mask 0x00100000 is the melee-weapon bit.
    ///
    /// Recovery discipline (doc 08 §6.1): three independent sources agree — the checkbox
    /// disassembly, the .rdata name/bit table, and the real sample masks.
    /// </summary>
    [Flags]
    public enum CoverageBits : uint
    {
        None = 0,

        // Outer / armor layer (.rdata group 1)
        Head       = 0x00000001,
        Hands      = 0x00000020,
        Feet       = 0x00000100,
        ChestArmor = 0x00000200,   // "Chst"
        Girth      = 0x00000400,   // "Grth"
        UpperArms  = 0x00000800,   // "UA"
        LowerArms  = 0x00001000,   // "LA"
        UpperLegs  = 0x00002000,   // "UL"
        LowerLegs  = 0x00004000,   // "LL"

        // Under / clothing layer (.rdata group 2)
        ChestUnder     = 0x00000002,
        GirthUnder     = 0x00000004,
        UpperArmsUnder = 0x00000008,
        LowerArmsUnder = 0x00000010,
        UpperLegsUnder = 0x00000040,
        LowerLegsUnder = 0x00000080,

        // Jewelry (.rdata group 4)
        Neck          = 0x00008000,
        RightBracelet = 0x00010000,
        LeftBracelet  = 0x00020000,
        RightRing     = 0x00040000,
        LeftRing      = 0x00080000,

        // Held / weapon slots (.rdata group 3 — matches the game's LOCATIONS bits)
        MeleeWeapon   = 0x00100000,
        Shield        = 0x00200000,
        MissileWeapon = 0x00400000,
        Ammo          = 0x00800000,
        Caster        = 0x01000000   // "Focs"
    }

    /// <summary>The eleven Editor coverage checkboxes and the exact mask each contributes,
    /// as recovered from the cover-mask builder. A checkbox may set several atomic bits (e.g.
    /// "Coat" covers chest + both arm segments; "Weapon" covers all held weapon slots).</summary>
    public static class CoverageCheckboxes
    {
        public const uint Coat   = 0x00001A00; // ChestArmor | UpperArms | LowerArms
        public const uint Legs   = 0x00006000; // UpperLegs  | LowerLegs
        public const uint Girth  = 0x00000400; // Girth
        public const uint Hands  = 0x00000020; // Hands
        public const uint Head   = 0x00000001; // Head
        public const uint Feet   = 0x00000100; // Feet
        public const uint Shirt  = 0x00000002; // ChestUnder
        public const uint Pants  = 0x00000040; // UpperLegsUnder
        public const uint Weapon = 0x01500000; // MeleeWeapon | MissileWeapon | Caster
        public const uint Shield = 0x00200000; // Shield
        public const uint Wand   = 0x01000000; // Caster

        /// <summary>Checkbox label → contributed mask, in the Editor's on-screen order.</summary>
        public static readonly IReadOnlyList<KeyValuePair<string, uint>> InOrder =
            new[]
            {
                Kv("Coat", Coat), Kv("Legs", Legs), Kv("Girth", Girth), Kv("Hands", Hands),
                Kv("Head", Head), Kv("Feet", Feet), Kv("Pants", Pants), Kv("Shirt", Shirt),
                Kv("Weapon", Weapon), Kv("Shield", Shield), Kv("Casting Tool", Wand)
            };

        /// <summary>Combine a set of checked boxes (by label) into a targetcover mask —
        /// the Editor's "build the spellgroup cover" operation.</summary>
        public static uint FromCheckboxes(IEnumerable<string> checkedLabels)
        {
            uint mask = 0;
            foreach (var kv in InOrder)
                foreach (var lbl in checkedLabels)
                    if (string.Equals(lbl, kv.Key, StringComparison.OrdinalIgnoreCase))
                        mask |= kv.Value;
            return mask;
        }

        private static KeyValuePair<string, uint> Kv(string k, uint v) =>
            new KeyValuePair<string, uint>(k, v);
    }
}
