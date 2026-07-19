namespace NB3.Core
{
    /// <summary>The five mana-regeneration strategies from NB3's own Options view
    /// (<c>choiceManaMode</c>). Faithful to the original — not imported from anywhere.</summary>
    public enum ManaRegenMode
    {
        None = 0,
        TradeManaElixir = 1,    // "Drink Trade Mana Elixirs"
        StaminaElixirS2M = 2,   // "Drink Stamina Elixirs / Cast S2M"
        RestS2M = 3,            // "Rest for Stamina / Cast S2M"
        HealKitH2M = 4,         // "Heal with Healing Kits / Cast H2M"
        RevitalizeS2M = 5,      // "Cast Revitalize / Cast S2M"

        /// <summary>The spell-first "three-vital dance" and the DEFAULT: convert Stamina to Mana
        /// (S2M) as the primary refill; when stamina falls below its floor, cast Revitalize to
        /// restore it so S2M can continue; use Cannibalize (Health to Mana — the level-7 H2M is
        /// literally named "Cannibalize") as the second mana source from health. Requires NO
        /// consumables — potions and healing kits are optional opt-in fallbacks only
        /// (<see cref="RegenConsumables"/>).</summary>
        SpellRecovery = 6       // "Cast S2M + Cannibalize + Revitalize (spells; potions/kits optional)"
    }

    public enum CycleState { Idle, Running, Paused, Done, Aborted }

    public enum StepKind
    {
        EnterMagicMode, // must be in Magic combat mode before casting
        Busy,           // client busy — retry (increments the Busy counter)
        Equip,          // perform an equipment change
        Cast,           // cast the current spell
        RegenMana,      // mana below the gate — run the configured regen mode
        Paused,
        Done
    }

    public sealed class CycleStep
    {
        public StepKind Kind { get; set; }
        public CastAction Action { get; set; }
        public ManaRegenMode RegenMode { get; set; }
        /// <summary>For <see cref="StepKind.RegenMana"/>: the mana the gate wants before the
        /// next cast (cost × aggressiveness%) — the regen micro-sequence's target.</summary>
        public int RequiredMana { get; set; }
        public string Reason { get; set; }
    }

    public sealed class CycleOptions
    {
        /// <summary>"Expected % of Spell Cost" (the AR edit): only cast while current mana is at
        /// least this percent of the next spell's cost; otherwise regen. 100 = full cost.</summary>
        public int AggressivenessPercent { get; set; } = 100;

        /// <summary>Reserve floor (PERCENT of max mana): also regen once mana drops below this, not
        /// just when the next spell is unaffordable — keeps a reserve instead of buffing to empty.
        /// 0 = disabled (pure per-spell gate).</summary>
        public int ManaFloorPercent { get; set; } = 25;

        /// <summary>High-water mark (PERCENT of max mana): once regen starts, top back up to this
        /// before resuming. Above the floor so a single spell can't re-trigger regen.</summary>
        public int ManaRegenTargetPercent { get; set; } = 90;

        public ManaRegenMode ManaRegenMode { get; set; } = ManaRegenMode.None;

        /// <summary>Resolved recovery spell ids (chosen for the character's known levels, capped
        /// by the Options "Maximum level for H2M, S2M and Revit"). 0 = not available.</summary>
        public int S2MSpellId { get; set; }
        public int H2MSpellId { get; set; }
        public int RevitalizeSpellId { get; set; }

        /// <summary>Give up on one action after this many consecutive fizzles/timeouts and
        /// move on (skip is counted). Guards against burning the component pouch dry on a
        /// spell that will never land. 0 = retry forever (the original's behaviour).</summary>
        public int MaxAttemptsPerAction { get; set; } = 8;
    }

    /// <summary>
    /// NB3's buff cycle: a stateful, sequential runner over a resolved <see cref="BuffPlan"/>.
    /// It walks the plan in order, one action per tick, waiting while the client is busy,
    /// re-casting on fizzle, and dropping into the configured mana-regen mode when the next
    /// spell is unaffordable — exactly the behaviour the recovered control/options views and
    /// string table describe (Spells / Left / Fizzles / Busy / Time, Pause/Resume/Abort). Pure
    /// and deterministic: the shell calls <see cref="Tick"/> on a timer, performs the returned
    /// step, then reports the outcome via <see cref="ReportCastResult"/>.
    /// </summary>
    public sealed class BuffCycle
    {
        private readonly System.Collections.Generic.List<CastAction> _actions;
        private readonly CycleOptions _opts;
        private int _cursor;
        private int _attemptsOnCurrent; // consecutive fizzles/timeouts on the current action
        private bool _inBusyRun;        // dedupes Busy counting to one hit per busy episode

        public CycleState State { get; private set; } = CycleState.Idle;
        public int TotalSpells { get; }
        public int TotalEquips { get; }
        public int SpellsCast { get; private set; }
        public int Fizzles { get; private set; }
        public int BusyHits { get; private set; }
        /// <summary>Resist + failed-to-affect outcomes (doc 18 §3: count and advance).</summary>
        public int Resists { get; private set; }
        /// <summary>Casts that produced no outcome line inside the watchdog window.</summary>
        public int Timeouts { get; private set; }
        /// <summary>Actions abandoned (attempt cap reached, or components missing).</summary>
        public int Skipped { get; private set; }

        public int SpellsLeft
        {
            get
            {
                int left = 0;
                for (int i = _cursor; i < _actions.Count; i++)
                    if (_actions[i].Kind != CastKind.Equip) left++;
                return left;
            }
        }

        public CastAction Current => _cursor < _actions.Count ? _actions[_cursor] : null;

        /// <summary>The plan's actions, in order (read-only) — feeds the recovered
        /// "NB3 Spells" casting view's list.</summary>
        public System.Collections.Generic.IReadOnlyList<CastAction> Actions => _actions;

        /// <summary>Index of the action currently being performed (== Actions.Count when done).</summary>
        public int Cursor => _cursor;

        public BuffCycle(BuffPlan plan, CycleOptions opts = null)
        {
            _actions = new System.Collections.Generic.List<CastAction>(plan.Actions);
            _opts = opts ?? new CycleOptions();
            foreach (var a in _actions)
                if (a.Kind == CastKind.Equip) TotalEquips++; else TotalSpells++;
        }

        public void Start()  { if (State == CycleState.Idle || State == CycleState.Done || State == CycleState.Aborted) { _cursor = 0; _attemptsOnCurrent = 0; _inBusyRun = false; SpellsCast = Fizzles = BusyHits = Resists = Timeouts = Skipped = 0; } State = CycleState.Running; }
        public void Pause()  { if (State == CycleState.Running) State = CycleState.Paused; }
        public void Resume() { if (State == CycleState.Paused) State = CycleState.Running; }
        public void Abort()  { State = CycleState.Aborted; }

        /// <summary>Decide the single next step. Does not mutate the cursor for casts/equips —
        /// call <see cref="ReportCastResult"/> after performing the step so a fizzle recasts.</summary>
        public CycleStep Tick(IGameState state)
        {
            if (State == CycleState.Paused)  return Step(StepKind.Paused, reason: "paused");
            if (State == CycleState.Aborted) return Step(StepKind.Done, reason: "aborted");
            if (State != CycleState.Running || _cursor >= _actions.Count)
            {
                if (State == CycleState.Running) State = CycleState.Done;
                return Step(StepKind.Done, reason: "cycle complete");
            }

            // A cast in flight holds the client busy — wait it out before ANY step (equip or cast).
            // Busy counts EPISODES (contiguous busy runs), not ticks: with chat-driven resolution our
            // own cast holds IsCasting for its whole 1–4 s windup, and counting every 300 ms poll would
            // inflate the view's Busy readout.
            if (state.IsCasting)
            {
                if (!_inBusyRun) { BusyHits++; _inBusyRun = true; }
                return Step(StepKind.Busy, reason: "client busy");
            }
            _inBusyRun = false;

            var a = _actions[_cursor];

            // Equips run BEFORE the Magic-mode gate. Entering the Magic stance requires a caster
            // already in hand (SetCombatMode needs the proper weapon type), so a caster/focus equip —
            // including the planner's auto-wield — must happen first; otherwise EnterMagicMode would
            // loop forever with an empty hand and the equip after it would never be reached. Magic
            // mode is still entered below, ahead of every Cast.
            if (a.Kind == CastKind.Equip)
                return Step(StepKind.Equip, a, reason: "equip");

            if (!state.InMagicCombatMode) return Step(StepKind.EnterMagicMode, reason: "need Magic mode");

            // Mana gate: regen (if configured) when the next spell is unaffordable OR mana has
            // fallen below the reserve floor (% of max) — so it keeps a reserve instead of buffing
            // down to empty. Once regen starts it tops back up to the higher target%, giving
            // hysteresis so a single spell doesn't re-trigger it every cast.
            if (_opts.ManaRegenMode != ManaRegenMode.None)
            {
                int cost = state.SpellManaCost(a.SpellId);
                int max = state.MaxMana;
                int perSpell = cost * _opts.AggressivenessPercent / 100;
                int floor = max > 0 ? max * _opts.ManaFloorPercent / 100 : 0;

                // Trigger = the larger of "afford the next spell" and "stay above the floor",
                // clamped to the pool: an unreachable requirement (high-cost spell on a small pool,
                // or >100% aggressiveness) would otherwise wedge the cycle in RegenMana forever.
                int trigger = perSpell > floor ? perSpell : floor;
                if (max > 0 && trigger > max) trigger = max;

                if (cost > 0 && state.CurrentMana < trigger)
                {
                    // Regen back up to the target high-water mark, but always at least enough for
                    // the next spell. Clamp to the pool.
                    int target = max > 0 ? max * _opts.ManaRegenTargetPercent / 100 : 0;
                    int regenTo = perSpell > target ? perSpell : target;
                    if (max > 0 && regenTo > max) regenTo = max;
                    return new CycleStep
                    {
                        Kind = StepKind.RegenMana,
                        RegenMode = _opts.ManaRegenMode,
                        RequiredMana = regenTo,
                        Reason = $"mana {state.CurrentMana} < {trigger} (floor {floor}) for 0x{a.SpellId:X4}"
                    };
                }
            }

            return Step(StepKind.Cast, a, reason: "cast");
        }

        /// <summary>Report the outcome of the Cast/Equip the shell just performed. Success
        /// advances to the next action; a fizzle keeps the cursor and bumps the Fizzle count.
        /// (Kept as the simple boolean seam — equips and tests use it; the chat-driven handler
        /// reports through <see cref="ReportCastOutcome"/>.)</summary>
        public void ReportCastResult(bool success) =>
            ReportCastOutcome(success ? CastOutcome.Success : CastOutcome.Fizzle);

        /// <summary>The doc-18 §3 outcome policy, verbatim: <c>You cast</c> → advance;
        /// <c>fizzled</c> → retry the same spell; <c>failed to affect</c>/<c>resists</c> →
        /// count and advance; missing components → skip (retrying can't help); timeout →
        /// retry. Fizzles and timeouts on one action are capped by
        /// <see cref="CycleOptions.MaxAttemptsPerAction"/>, after which the action is skipped
        /// so a hopeless spell can't wedge the cycle (or empty the component pouch).</summary>
        public void ReportCastOutcome(CastOutcome outcome)
        {
            if (_cursor >= _actions.Count) return;
            var a = _actions[_cursor];
            switch (outcome)
            {
                case CastOutcome.Success:
                    if (a.Kind != CastKind.Equip) SpellsCast++;
                    Advance();
                    break;

                case CastOutcome.Fizzle:
                    if (a.Kind == CastKind.Equip) break; // equips don't fizzle; retry next tick
                    Fizzles++;
                    RetryOrSkip();
                    break;

                case CastOutcome.Timeout:
                    Timeouts++;
                    RetryOrSkip();
                    break;

                case CastOutcome.Resisted:
                case CastOutcome.FailedToAffect:
                    Resists++;
                    Advance();
                    break;

                case CastOutcome.MissingComponents:
                    Skipped++;
                    Advance();
                    break;

                // CastOutcome.None: the line said nothing — no state change.
            }
        }

        private void Advance()
        {
            _attemptsOnCurrent = 0;
            _cursor++;
            if (_cursor >= _actions.Count) State = CycleState.Done;
        }

        private void RetryOrSkip()
        {
            _attemptsOnCurrent++;
            if (_opts.MaxAttemptsPerAction > 0 && _attemptsOnCurrent >= _opts.MaxAttemptsPerAction)
            {
                Skipped++;
                Advance();
            }
        }

        private static CycleStep Step(StepKind k, CastAction a = null, string reason = null) =>
            new CycleStep { Kind = k, Action = a, Reason = reason };
    }
}
