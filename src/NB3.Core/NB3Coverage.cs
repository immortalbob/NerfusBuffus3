using System;

namespace NB3.Core
{
    /// <summary>
    /// Translates the game's live item masks into NB3's own compact coverage scheme
    /// (<see cref="CoverageBits"/>) so a profile's <c>targetcover</c> can be AND-matched
    /// against worn items — the translation the old NerfusFilter did internally
    /// (docs/COVER_MASK_RECOVERY.md, "the one remaining item").
    ///
    /// Game-side sources (doc 16 §3.1–3.2, both live-confirmed masks):
    ///  - CLOTHING_PRIORITY_INT (property/value-key 4): what the item covers.
    ///    NOT Decal's named synthetic "Coverage" key in the 218103808+ range — using that named
    ///    key is the exact shipped bug doc 13 §6 records.
    ///  - LOCATIONS_INT (key 9) / CURRENT_WIELDED_LOCATION_INT (key 10): where it equips /
    ///    is currently wielded. NB3's held-slot bits are IDENTICAL to the game's LOCATIONS bits
    ///    (recovered .rdata table: Mele=0x100000, Shld=0x200000, Rng=0x400000, Ammo=0x800000,
    ///    Focs=0x1000000), so the held/jewelry range passes through by value.
    ///
    /// Pure and unit-tested offline (doc 09 §1 discipline). The one thing that still deserves a
    /// live-item spot check on Windows is jewelry handedness (game left/right bracelet + ring
    /// bits vs NB3's Left*/Right* names) — irrelevant to matching whenever a profile targets
    /// both of a pair, and no shipped profile targets jewelry at all.
    /// </summary>
    public static class NB3Coverage
    {
        // ---- game CLOTHING_PRIORITY_INT bits (doc 16 §3.2, complete) ----
        private const int G_UnderUpperLegs = 2;
        private const int G_UnderLowerLegs = 4;
        private const int G_UnderChest     = 8;
        private const int G_UnderAbdomen   = 16;
        private const int G_UnderUpperArms = 32;
        private const int G_UnderLowerArms = 64;
        private const int G_ArmorUpperLegs = 256;
        private const int G_ArmorLowerLegs = 512;
        private const int G_ArmorChest     = 1024;
        private const int G_ArmorAbdomen   = 2048;
        private const int G_ArmorUpperArms = 4096;
        private const int G_ArmorLowerArms = 8192;
        private const int G_Head           = 16384;
        private const int G_Hands          = 32768;
        private const int G_Feet           = 65536;
        // 131072 = back (cloak) — no NB3 equivalent (cloaks postdate NB3); dropped.

        // ---- game LOCATIONS / CURRENT_WIELDED_LOCATION bits (doc 16 §3.1) ----
        private const int G_Necklace      = 32768;
        private const int G_LeftBracelet  = 65536;
        private const int G_RightBracelet = 131072;
        private const int G_LeftRing      = 262144;
        private const int G_RightRing     = 524288;
        private const int G_MeleeWeapon   = 1048576;   // == NB3 MeleeWeapon
        private const int G_Shield        = 2097152;   // == NB3 Shield
        private const int G_MissileWeapon = 4194304;   // == NB3 MissileWeapon
        private const int G_Ammo          = 8388608;   // == NB3 Ammo
        private const int G_Caster        = 16777216;  // == NB3 Caster

        private const uint HeldPassThrough =
            (uint)(G_MeleeWeapon | G_Shield | G_MissileWeapon | G_Ammo | G_Caster);

        /// <summary>Translate a game CLOTHING_PRIORITY mask (value-key 4) into NB3 coverage bits.</summary>
        public static uint FromClothingPriority(int clothingPriority)
        {
            uint nb3 = 0;
            void Map(int gameBit, CoverageBits nb3Bit)
            { if ((clothingPriority & gameBit) != 0) nb3 |= (uint)nb3Bit; }

            // under / clothing layer
            Map(G_UnderChest,     CoverageBits.ChestUnder);
            Map(G_UnderAbdomen,   CoverageBits.GirthUnder);
            Map(G_UnderUpperArms, CoverageBits.UpperArmsUnder);
            Map(G_UnderLowerArms, CoverageBits.LowerArmsUnder);
            Map(G_UnderUpperLegs, CoverageBits.UpperLegsUnder);
            Map(G_UnderLowerLegs, CoverageBits.LowerLegsUnder);
            // outer / armor layer
            Map(G_ArmorChest,     CoverageBits.ChestArmor);
            Map(G_ArmorAbdomen,   CoverageBits.Girth);
            Map(G_ArmorUpperArms, CoverageBits.UpperArms);
            Map(G_ArmorLowerArms, CoverageBits.LowerArms);
            Map(G_ArmorUpperLegs, CoverageBits.UpperLegs);
            Map(G_ArmorLowerLegs, CoverageBits.LowerLegs);
            Map(G_Head,           CoverageBits.Head);
            Map(G_Hands,          CoverageBits.Hands);
            Map(G_Feet,           CoverageBits.Feet);
            return nb3;
        }

        /// <summary>Translate a game LOCATIONS-style mask (value-key 9, or the currently-wielded
        /// slot from key 10) into NB3 coverage bits: held slots pass through by value; jewelry
        /// slots map to NB3's jewelry bits.</summary>
        public static uint FromLocations(int locations)
        {
            uint nb3 = (uint)locations & HeldPassThrough;
            void Map(int gameBit, CoverageBits nb3Bit)
            { if ((locations & gameBit) != 0) nb3 |= (uint)nb3Bit; }

            Map(G_Necklace,      CoverageBits.Neck);
            Map(G_LeftBracelet,  CoverageBits.LeftBracelet);
            Map(G_RightBracelet, CoverageBits.RightBracelet);
            Map(G_LeftRing,      CoverageBits.LeftRing);
            Map(G_RightRing,     CoverageBits.RightRing);
            return nb3;
        }

        /// <summary>The full worn-item translation: coverage from CLOTHING_PRIORITY (key 4) plus
        /// the held/jewelry slot the item currently occupies (key 10). Either input may be 0.</summary>
        public static uint FromGame(int clothingPriority, int currentWieldedLocation) =>
            FromClothingPriority(clothingPriority) | FromLocations(currentWieldedLocation);
    }
}
