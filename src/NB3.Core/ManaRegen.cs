namespace NB3.Core
{
    public enum RegenActionKind
    {
        Done,                    // mana restored to target — resume the cycle
        Unavailable,             // this mode can't run (missing recovery spell) — give up regen
        DrinkTradeManaElixir,    // UseItem a trade mana elixir (legacy modes; shell finds by name)
        DrinkStaminaElixir,      // UseItem a stamina elixir (legacy modes; shell finds by name)
        Rest,                    // rest to recover stamina
        UseHealingKit,           // UseItem a healing kit on self (ItemGuid if pre-found, else by tier)
        DrinkPotion,             // UseItem the pre-found ItemGuid (health/stamina/mana drink; auto-scan)
        Cast                     // cast SpellId (S2M / H2M / Cannibalize / Revitalize / Heal Self)
    }

    public sealed class RegenStep
    {
        public RegenActionKind Kind { get; set; }
        public int SpellId { get; set; }
        /// <summary>For <see cref="RegenActionKind.DrinkPotion"/> and a pre-found
        /// <see cref="RegenActionKind.UseHealingKit"/>: the inventory guid the auto-scan chose.</summary>
        public int ItemGuid { get; set; }
        /// <summary>For <see cref="RegenActionKind.DrinkPotion"/>: which vital the drink restores.</summary>
        public Vital Vital { get; set; }
        public string Reason { get; set; }
    }

    /// <summary>Tunable thresholds for the regen micro-sequences, expressed as a PERCENT of the
    /// vital's maximum (1-99). The *structure* (which action in which mode) is faithful to NB3's
    /// five Options modes. NOTE these are percentages, not absolute points: a fixed point floor
    /// is meaningless across characters (50 HP is death for a 1500-HP mage and never reached by a
    /// 90-HP one), and H2M/S2M convert a vital that scales with the character.</summary>
    public sealed class RegenThresholds
    {
        public int StaminaFloor { get; set; } = 50; // below this % of max stamina, replenish before S2M
        public int HealthFloor { get; set; } = 50;  // below this % of max health, heal before H2M
    }

    /// <summary>Which OPTIONAL consumables the spell-first <see cref="ManaRegenMode.SpellRecovery"/>
    /// mode may fall back on when spells alone can't recover (both stamina and health below their
    /// floors, or mana too low to afford any recovery cast). Both default OFF — the whole point of
    /// the spell mode is that potions and kits are optional, not required.</summary>
    public sealed class RegenConsumables
    {
        /// <summary>Drink a mana elixir as a last-resort fallback (skill-free; doc 19 §5).</summary>
        public bool Potions { get; set; }
        /// <summary>Use a healing kit as a last-resort fallback to heal, so Cannibalize/H2M can run
        /// (the server still requires the Healing skill be Trained; doc 19 §2).</summary>
        public bool Kits { get; set; }

        public static readonly RegenConsumables None = new RegenConsumables();
    }

    /// <summary>
    /// The mana-regeneration micro-sequence for a single cycle interruption — the execution
    /// half of NB3's five Options modes. The cycle detects "next spell unaffordable" and hands
    /// off here; each tick returns the next atomic action until <see cref="RegenActionKind.Done"/>
    /// (mana ≥ target) or <see cref="RegenActionKind.Unavailable"/>. Pure and deterministic.
    /// </summary>
    public sealed class ManaRegenController
    {
        private readonly ManaRegenMode _mode;
        private readonly RecoverySpells _rec;
        private readonly int _targetMana;
        private readonly RegenThresholds _th;
        private readonly RegenConsumables _cons;
        private readonly bool _allowHp2Mana;

        public ManaRegenController(ManaRegenMode mode, RecoverySpells rec, int targetMana,
                                   RegenThresholds thresholds = null, RegenConsumables consumables = null,
                                   bool allowHealthToMana = true)
        {
            _mode = mode; _rec = rec; _targetMana = targetMana;
            _th = thresholds ?? new RegenThresholds();
            _cons = consumables ?? RegenConsumables.None;
            _allowHp2Mana = allowHealthToMana;
        }

        public RegenStep Next(IGameState state)
        {
            if (state.CurrentMana >= _targetMana) return Step(RegenActionKind.Done, reason: "mana restored");

            switch (_mode)
            {
                case ManaRegenMode.TradeManaElixir:
                    return Step(RegenActionKind.DrinkTradeManaElixir);

                case ManaRegenMode.StaminaElixirS2M:
                    if (_rec.StaminaToMana == 0) return Unavail("no S2M known");
                    return Pct(state.CurrentStamina, state.MaxStamina) < _th.StaminaFloor
                        ? Step(RegenActionKind.DrinkStaminaElixir, reason: "top up stamina")
                        : Cast(_rec.StaminaToMana);

                case ManaRegenMode.RestS2M:
                    if (_rec.StaminaToMana == 0) return Unavail("no S2M known");
                    return Pct(state.CurrentStamina, state.MaxStamina) < _th.StaminaFloor
                        ? Step(RegenActionKind.Rest, reason: "rest for stamina")
                        : Cast(_rec.StaminaToMana);

                case ManaRegenMode.HealKitH2M:
                    if (_rec.HealthToMana == 0) return Unavail("no H2M known");
                    return Pct(state.CurrentHealth, state.MaxHealth) < _th.HealthFloor
                        ? Step(RegenActionKind.UseHealingKit, reason: "heal before H2M")
                        : Cast(_rec.HealthToMana);

                case ManaRegenMode.RevitalizeS2M:
                    if (_rec.StaminaToMana == 0) return Unavail("no S2M known");
                    if (Pct(state.CurrentStamina, state.MaxStamina) < _th.StaminaFloor)
                        return _rec.Revitalize != 0
                            ? Cast(_rec.Revitalize)
                            : Unavail("stamina low and no Revitalize known");
                    return Cast(_rec.StaminaToMana);

                case ManaRegenMode.SpellRecovery:
                {
                    // The spell-first "three-vital dance" (the default). Make mana from whichever
                    // source vital is above its floor (stamina first, then health); when a source
                    // vital is spent, restore it — stamina with Revitalize, health with Heal Self —
                    // so the engine keeps running. Consumables (auto-scanned per vital) are optional
                    // and preferred over the restore-casts only because they don't cost mana. Every
                    // step is gated so it never issues a cast the character can't pay for.
                    int staPct = Pct(state.CurrentStamina, state.MaxStamina);
                    int hpPct  = Pct(state.CurrentHealth, state.MaxHealth);

                    // Restore stamina so S2M can resume: a stamina drink (opt-in, no mana) then Revitalize.
                    RegenStep RestoreStamina()
                    {
                        if (_cons.Potions) { int g = state.FindBestPotion(Vital.Stamina); if (g != 0) return Drink(g, Vital.Stamina, "stamina potion -> S2M"); }
                        if (_rec.Revitalize != 0 && Affordable(state, _rec.Revitalize)) return Cast(_rec.Revitalize);
                        return null;
                    }
                    // Restore health so Cannibalize/H2M can resume: a kit or health drink (opt-in, no
                    // mana) then Heal Self — the spell-based recovery used when no kits/potions are on.
                    RegenStep RestoreHealth()
                    {
                        if (!_allowHp2Mana) return null;   // stamina-only recovery: never touch health
                        if (_cons.Kits)    { int g = state.FindBestHealingKit(); if (g != 0) return UseKit(g); }
                        if (_cons.Potions) { int g = state.FindBestPotion(Vital.Health); if (g != 0) return Drink(g, Vital.Health, "health potion -> Cannibalize"); }
                        if (_rec.HealSelf != 0 && Affordable(state, _rec.HealSelf)) return Cast(_rec.HealSelf);
                        return null;
                    }

                    // 1) PRIMARY — Stamina -> Mana while stamina is above its floor.
                    if (staPct >= _th.StaminaFloor && _rec.StaminaToMana != 0 && Affordable(state, _rec.StaminaToMana))
                        return Cast(_rec.StaminaToMana);

                    // 2) Health -> Mana via Cannibalize (the level-7 H2M) while health can spare it —
                    //    unless health->mana is turned off (stamina-only recovery). Also the mana
                    //    source for a character who doesn't know S2M.
                    if (_allowHp2Mana && hpPct >= _th.HealthFloor && _rec.HealthToMana != 0 && Affordable(state, _rec.HealthToMana))
                        return Cast(_rec.HealthToMana);

                    // 3) Both source vitals below floor -> restore the MORE-depleted one (keeps both
                    //    pools balanced so both S2M and Cannibalize keep contributing); fall back to
                    //    the other if the first has no available means.
                    bool staLow = staPct < _th.StaminaFloor, hpLow = hpPct < _th.HealthFloor;
                    if (staLow || hpLow)
                    {
                        bool healthFirst = hpLow && (!staLow || hpPct <= staPct);
                        RegenStep r = healthFirst ? RestoreHealth() : RestoreStamina();
                        if (r != null) return r;
                        r = healthFirst ? RestoreStamina() : RestoreHealth();
                        if (r != null) return r;
                    }

                    // 4) Direct mana drink as a last resort (opt-in only).
                    if (_cons.Potions) { int g = state.FindBestPotion(Vital.Mana); if (g != 0) return Drink(g, Vital.Mana, "mana potion (direct)"); }

                    // Nothing available — wait on natural regen. Never a deadlock: the reserve floor
                    // keeps mana off zero, so a recovery cast becomes affordable as mana trickles up.
                    return Unavail("stamina & health below floors and no affordable spell/consumable — waiting on natural regen");
                }

                default:
                    return Unavail("no regen mode set");
            }
        }

        /// <summary>Current vital as a whole-number percent of its max. Guards the no-data case
        /// (max &lt;= 0, e.g. vitals not yet read) by reporting 100% — "full" — so a bad read
        /// never spams consumables or drains health via H2M on phantom low vitals.</summary>
        private static int Pct(int cur, int max) => max > 0 ? (int)((long)cur * 100 / max) : 100;

        /// <summary>True when the character can pay a spell's mana cost right now. An unknown or
        /// zero cost (bad/absent lookup) is treated as affordable, so a missing cost never blocks
        /// recovery; the reserve floor and natural regen keep this from becoming a deadlock.</summary>
        private static bool Affordable(IGameState state, int spellId)
        {
            int cost;
            try { cost = state.SpellManaCost(spellId); } catch { cost = 0; }
            return cost <= 0 || state.CurrentMana >= cost;
        }

        private static RegenStep Cast(int id) => new RegenStep { Kind = RegenActionKind.Cast, SpellId = id, Reason = $"cast 0x{id:X4}" };
        private static RegenStep Step(RegenActionKind k, string reason = null) => new RegenStep { Kind = k, Reason = reason };
        private static RegenStep Unavail(string r) => new RegenStep { Kind = RegenActionKind.Unavailable, Reason = r };
        private static RegenStep Drink(int guid, Vital v, string reason) =>
            new RegenStep { Kind = RegenActionKind.DrinkPotion, ItemGuid = guid, Vital = v, Reason = "drink " + reason };
        private static RegenStep UseKit(int guid) =>
            new RegenStep { Kind = RegenActionKind.UseHealingKit, ItemGuid = guid, Reason = "healing kit -> Cannibalize" };
    }
}
