using System;

namespace NB3.Core.Modern
{
    /// <summary>
    /// Pure target classification (Self / Other / Item) for a live spell record, shared by the
    /// plugin's live-table adapter (<c>DecalSpellTable</c>) and the offline tests. Kept here in
    /// NB3.Core precisely so the rule is unit-testable off-client.
    ///
    /// Priority, most authoritative first:
    ///  1. The live record's <c>IsUntargetted</c> flag. On EoR/ACE this is the real mechanic:
    ///     untargetted == cast on yourself (ordinary self buffs, self-cast auras, whole-suit
    ///     banes). It is authoritative in BOTH directions — a spell the live client says is
    ///     TARGETED can never be a Self buff, no matter what its name or the stale dump says.
    ///  2. The shipped 2012 dump's computed target (when we have that id).
    ///  3. A name heuristic, LAST — and it checks the " Self" / " Other" tokens BEFORE the
    ///     "Aura of …" prefix. That ordering is the fix for the weapon-aura trap: the modern
    ///     weapon buffs ship as matched pairs "Aura of Blood Drinker Self VII" (cast on you) and
    ///     "Aura of Blood Drinker Other VII" (cast on a fellow). Keying on the "Aura of" prefix
    ///     alone files the Other one as Self, and the bot then casts it on itself → the client
    ///     rejects it with "you cannot cast this spell upon yourself" and the cycle hangs.
    /// </summary>
    public static class SpellTargetClassifier
    {
        /// <param name="isUntargetted">the live record's IsUntargetted flag, or null when it
        /// can't be read (dump-only / older SDK) — null just falls through to dump/name.</param>
        /// <param name="dumpTarget">the shipped dump's target for this id, or null when the id
        /// isn't in the dump (a renumbered/modern-only spell).</param>
        /// <param name="name">the spell's display name.</param>
        public static SpellTarget Classify(bool? isUntargetted, SpellTarget? dumpTarget, string name)
        {
            name = name ?? "";

            // 1. Live untargetted flag: definitive Self when set true.
            if (isUntargetted == true) return SpellTarget.Self;

            bool hasOther = name.IndexOf(" Other", StringComparison.OrdinalIgnoreCase) >= 0;
            bool hasSelf  = name.IndexOf(" Self",  StringComparison.OrdinalIgnoreCase) >= 0;

            // 2/3. Classify from name tokens, then dump, then the "Aura of" prefix.
            SpellTarget t;
            if (hasOther && !hasSelf)
                t = SpellTarget.Other;                 // "... Other ..." is aimed at another, incl. "Aura of X Other"
            else if (dumpTarget.HasValue)
                t = dumpTarget.Value;                  // shipped 2012 classification (reliable by id)
            else if (hasSelf)
                t = SpellTarget.Self;                  // "... Self ..."
            else if (name.StartsWith("Aura of", StringComparison.OrdinalIgnoreCase))
                t = SpellTarget.Self;                  // bare "Aura of X" (no Self/Other token) — a self aura
            else
                t = SpellTarget.Other;                 // default for an unknown targeted spell

            // 4. Guard: the live client says this spell is TARGETED (untargetted == false), so it
            //    cannot be a Self buff — downgrade a name/dump-derived Self to Other so a self-buff
            //    profile skips it rather than casting it on the player and hanging. An explicit
            //    "... Self" name is trusted over a possibly-misread flag and is left alone.
            if (isUntargetted == false && t == SpellTarget.Self && !hasSelf)
                t = SpellTarget.Other;

            return t;
        }
    }
}
