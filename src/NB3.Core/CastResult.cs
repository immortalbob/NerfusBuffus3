using System.Text.RegularExpressions;

namespace NB3.Core
{
    /// <summary>The resolution of one cast attempt, as the doc-18 §3 chat truth reports it.
    /// <see cref="None"/> means "the line said nothing about the cast in flight".</summary>
    public enum CastOutcome
    {
        None = 0,
        /// <summary>"You cast …" — landed (covers the plain, S2M/H2M-recovery and
        /// self-heal variants: they all begin "You cast").</summary>
        Success,
        /// <summary>"Your spell fizzled." — retry the same spell (the Fizzles counter's input).</summary>
        Fizzle,
        /// <summary>"You failed to affect …" — count and advance (doc 18 §3 policy).</summary>
        FailedToAffect,
        /// <summary>"&lt;target&gt; resists your spell" — count and advance.</summary>
        Resisted,
        /// <summary>"You don't have all the components …" — skip the spell (retrying can't
        /// help). CLIENT-STRING, CONFIRM-BY-TEST: if the live wording differs the line simply
        /// never matches and the timeout path covers it instead — soft-fail by design.</summary>
        MissingComponents,
        /// <summary>No outcome line arrived inside the watchdog window (cast never started,
        /// was interrupted, or the message wasn't recognised). Retry, capped.</summary>
        Timeout
    }

    /// <summary>
    /// The doc-18 §3 chat catalog — UtilityBelt's field-proven compiled regex set (verbatim
    /// where given), plus the ACE-corroborated failure strings. This is the *outcome* half of
    /// the casting loop's state machine; <see cref="CastResultMonitor"/> is the correlation
    /// half. Pure static, unit-tested offline.
    /// </summary>
    public static class CastChat
    {
        private static readonly Regex Success =
            new Regex(@"^You cast.*$", RegexOptions.Compiled);
        private static readonly Regex Fizzle =
            new Regex(@"^Your spell fizzled\.$", RegexOptions.Compiled);          // exact (UB)
        // UB's table has past tense ("You failed to affect…"); the ACE server source sends
        // present tense ("You fail to affect {target} with {spell}", doc 18 §3) — accept both,
        // or every unaffectable target would burn the full timeout+retry budget (~80 s).
        private static readonly Regex FailedToAffect =
            new Regex(@"^You fail(ed)? to affect.*$", RegexOptions.Compiled);
        private static readonly Regex YouResistTheirs =
            new Regex(@"^You resist.*$", RegexOptions.Compiled);                  // NOT our cast
        private static readonly Regex TargetResists =
            new Regex(@"^.* resists your spell.*$", RegexOptions.Compiled);
        private static readonly Regex ComponentsConsumed =
            new Regex(@"^The spell consumed the following components\:.*$", RegexOptions.Compiled);
        private static readonly Regex MissingComponents =
            new Regex(@"^You don't have all the components", RegexOptions.Compiled); // confirm-by-test
        private static readonly Regex Expired =
            new Regex(@".* has expired\.$", RegexOptions.Compiled);

        /// <summary>Classify a chat line as the terminal outcome of the cast in flight, or
        /// <see cref="CastOutcome.None"/> if the line resolves nothing (chatter, expiry
        /// notices, the components-burned line, someone else's spell resisted by YOU).</summary>
        public static CastOutcome ClassifyOutcome(string chatLine)
        {
            var line = (chatLine ?? "").Trim();
            if (line.Length == 0) return CastOutcome.None;

            if (Fizzle.IsMatch(line)) return CastOutcome.Fizzle;
            if (Success.IsMatch(line)) return CastOutcome.Success;
            if (FailedToAffect.IsMatch(line)) return CastOutcome.FailedToAffect;
            if (YouResistTheirs.IsMatch(line)) return CastOutcome.None; // you resisted THEIR spell
            if (TargetResists.IsMatch(line)) return CastOutcome.Resisted;
            if (MissingComponents.IsMatch(line)) return CastOutcome.MissingComponents;
            return CastOutcome.None;
        }

        /// <summary>"&lt;spell&gt; has expired." — the rebuff trigger's chat sibling
        /// (informational here; the enchantment-delta event is the structured source).</summary>
        public static bool IsEnchantmentExpired(string chatLine) =>
            Expired.IsMatch((chatLine ?? "").Trim());

        /// <summary>"The spell consumed the following components: …" — the §5 casting-economy
        /// probe: seeing this once on an unknown ACE server means comps ARE burned there.</summary>
        public static bool IsComponentsConsumed(string chatLine) =>
            ComponentsConsumed.IsMatch((chatLine ?? "").Trim());
    }

    /// <summary>
    /// The correlation half of the casting loop (doc 18 §3, the sequential-runner shape):
    /// NB3 keeps exactly ONE cast in flight, so the next outcome line resolves it — no
    /// attempt-queue arithmetic. Belt and braces, the §2 enchantment-add delta also resolves
    /// a matching pending cast (a self-buff can land while its chat line is eaten by a
    /// filter). A watchdog turns a never-answered cast into <see cref="CastOutcome.Timeout"/>
    /// instead of wedging the cycle. Pure: the clock is injected as a tick count (the shell
    /// passes <c>Environment.TickCount</c>; subtraction is wrap-safe int arithmetic, the same
    /// discipline as the portal-gate watchdog).
    /// </summary>
    public sealed class CastResultMonitor
    {
        /// <summary>Outcome watchdog. Doc 18 §6 bounds a modern cast at ~1–2 s self /
        /// ~4 s other-targeted (windup at CastSpeed 2.0 + turn-to-target), so 10 s of
        /// silence means the cast is not coming back.</summary>
        public int TimeoutMs { get; set; } = 10000;

        /// <summary>The shorter watchdog once PARTIAL feedback has arrived (the original's
        /// "5 seconds timeout with partial feedback. Probably a dud."): we've seen a related
        /// line for this cast (e.g. components consumed) but no terminal outcome, so the cast
        /// clearly started — give it less rope before calling it a Timeout.</summary>
        public int PartialTimeoutMs { get; set; } = 5000;

        public bool CastInFlight { get; private set; }
        public int PendingSpellId { get; private set; }
        private int _beganAtTick;
        private bool _partial;

        /// <summary>Record that the shell just issued <c>CastSpell</c> for this id.</summary>
        public void BeginCast(int spellId, int nowTick)
        {
            CastInFlight = true;
            PendingSpellId = spellId;
            _beganAtTick = nowTick;
            _partial = false;
        }

        /// <summary>Note that partial feedback for the in-flight cast has arrived (a related but
        /// non-terminal line) — switches the watchdog to the shorter <see cref="PartialTimeoutMs"/>.</summary>
        public void NotePartialFeedback() { if (CastInFlight) _partial = true; }

        /// <summary>Forget the pending cast (cycle aborted/finished).</summary>
        public void Reset()
        {
            CastInFlight = false;
            PendingSpellId = 0;
            _partial = false;
        }

        /// <summary>Feed one chat line. Returns the terminal outcome it resolved the pending
        /// cast to, or <see cref="CastOutcome.None"/> (nothing pending / line says nothing).</summary>
        public CastOutcome ObserveChat(string chatLine)
        {
            if (!CastInFlight) return CastOutcome.None;
            var outcome = CastChat.ClassifyOutcome(chatLine);
            if (outcome != CastOutcome.None) Reset();
            return outcome;
        }

        /// <summary>Feed an enchantment-ADD delta (<c>CharacterFilter.ChangeEnchantments</c>).
        /// True if it matched and resolved the pending cast as a success.</summary>
        public bool ObserveEnchantmentAdded(int spellId)
        {
            if (!CastInFlight || spellId != PendingSpellId) return false;
            Reset();
            return true;
        }

        /// <summary>True (once) when the pending cast has gone unanswered past
        /// <see cref="TimeoutMs"/> — report it as <see cref="CastOutcome.Timeout"/>.
        /// Wrap-safe: <c>now - began</c> in unchecked int space.</summary>
        public bool CheckTimeout(int nowTick)
        {
            if (!CastInFlight) return false;
            int budget = _partial ? PartialTimeoutMs : TimeoutMs;
            if (unchecked(nowTick - _beganAtTick) <= budget) return false;
            Reset();
            return true;
        }
    }
}
