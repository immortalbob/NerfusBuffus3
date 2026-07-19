using System;

namespace NB3.Core.Modern
{
    /// <summary>
    /// The AC/ACE spell fizzle model, transcribed verbatim from the server source
    /// (ACE.Server.WorldObjects.SkillCheck + Player_Magic.GetCastingPreCheckStatus). This is
    /// what makes a buff *land*, and it's the missing piece behind "the bot casts level 8s I
    /// know but my skill can't reliably cast": selection must cap by castability, not just by
    /// what's in the spellbook.
    ///
    /// The two load-bearing facts from ACE:
    ///  - the fizzle difficulty is the spell's **Power** — the SAME number as the dump's
    ///    `difficulty`/stacking-power column (<see cref="SpellInfo.Level"/> here). Strength Self
    ///    VI = 250, its level-7 self line ("Might of the Lugians") = 300, level-8 ("Incantation
    ///    of Strength Self") = 400 — so higher levels are dramatically harder;
    ///  - magic uses the STEEP sigmoid: <c>GetMagicSkillChance</c> = GetSkillChance with factor
    ///    **0.07** (not the 0.03 the vital/heal checks use), plus a hard floor: a spell whose
    ///    Power exceeds your effective magic skill by more than 50 **cannot be cast at all**
    ///    (auto-fail), and below that the chance climbs the sigmoid.
    ///
    /// Skill is the character's EFFECTIVE (buffed) skill in the spell's school — Decal's
    /// <c>CharacterFilter.EffectiveSkill</c>, ACE's <c>GetCreatureSkill(school).Current</c>.
    /// </summary>
    public static class CastChance
    {
        /// <summary>ACE's magic skill factor (SkillCheck.GetMagicSkillChance). Steeper than the
        /// 0.03 used for vital/heal checks, so a small skill deficit fizzles hard.</summary>
        public const double MagicFactor = 0.07;

        /// <summary>ACE's hard gate: <c>magicSkill &gt;= (int)difficulty - 50</c>. Below this the
        /// pre-check returns CastFailed (guaranteed fizzle), so it isn't worth attempting.</summary>
        public const int AttemptFloor = 50;

        /// <summary>P(cast succeeds) for an effective magic <paramref name="skill"/> against a
        /// spell of the given <paramref name="power"/>. 0 when below the attempt floor
        /// (ACE gates it out entirely there). Range [0,1].</summary>
        public static double SuccessChance(int skill, int power)
        {
            if (!CanAttempt(skill, power)) return 0.0;
            double c = 1.0 - (1.0 / (1.0 + Math.Exp(MagicFactor * (skill - power))));
            return c < 0 ? 0 : (c > 1 ? 1 : c);
        }

        /// <summary>Would ACE even let this cast be attempted? (The Power−50 floor.)</summary>
        public static bool CanAttempt(int skill, int power) => skill > 0 && skill >= power - AttemptFloor;
    }
}
