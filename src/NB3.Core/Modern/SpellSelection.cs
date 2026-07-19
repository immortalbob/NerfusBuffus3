using System.Collections.Generic;
using System.Linq;

namespace NB3.Core.Modern
{
    /// <summary>How a spell is aimed, classified from the live spell record (target mask + range),
    /// not from its name. Item = cast onto a held/worn item (armor/weapon/wand); None = not a
    /// buff (portal/lifestone/empty target).</summary>
    public enum SpellTarget { None, Self, Other, Item }

    /// <summary>What a live-client spell record gives us for buff selection. On the modern
    /// (EoR/ACE) client this comes from <c>FileService.SpellTable</c> — NOT from NB3's 2003
    /// hardcoded ids (those were renumbered) and NOT by parsing names (level 7 has bespoke
    /// names, level 8 is "Incantation of …"). The two load-bearing fields are <see cref="Category"/>
    /// (the stacking group) and <see cref="Level"/> (which member of the group is stronger).</summary>
    public sealed class SpellInfo
    {
        public int Id { get; }
        public string Name { get; }
        /// <summary>The spell's stacking category. Spells in the same category do NOT stack —
        /// the higher <see cref="Level"/> surpasses the lower. Different categories stack.</summary>
        public int Category { get; }
        public int Level { get; }
        /// <summary>Magic school: Creature / Life / Item / War / Void. Drives the editor's tabs.</summary>
        public string School { get; }
        /// <summary>Base mana cost (from the spell record) — feeds the "% of Spell Cost" gate.</summary>
        public int Mana { get; }
        /// <summary>Aim (Self/Other/Item), classified from the record's target mask + range.</summary>
        public SpellTarget Target { get; }
        /// <summary>Enchantment lifetime in seconds; <c>-1</c> (any value &lt; 0) marks an
        /// INSTANTANEOUS spell — a heal/harm/restore/convert/bolt, not a persistent enchantment
        /// (EoR spell table, file 16 §7.7). <c>0</c> = unknown (the 2012 dump carries no duration).
        /// A buff must be persistent, so instantaneous spells are excluded from buff selection.</summary>
        public int Duration { get; }
        /// <summary>True when the record says this spell is instantaneous (Duration &lt; 0) — never a
        /// buff. False when persistent OR unknown (Duration &gt;= 0), so absent data never excludes.</summary>
        public bool IsInstantaneous => Duration < 0;

        public SpellInfo(int id, string name, int category, int level,
                         string school = "", int mana = 0, SpellTarget target = SpellTarget.Self,
                         int duration = 0)
        {
            Id = id; Name = name ?? ""; Category = category; Level = level;
            School = school ?? ""; Mana = mana; Target = target; Duration = duration;
        }
    }

    /// <summary>The live client's spell table, keyed by id. Filled from
    /// <c>FileService.SpellTable</c> in the plugin; a fake stands in for offline tests.</summary>
    public interface ILiveSpellTable
    {
        SpellInfo ById(int spellId);
        IReadOnlyCollection<SpellInfo> All { get; }
    }

    /// <summary>One thing the player wants kept up — expressed by its stacking category, which is
    /// era-proof (ids and names drift, the category is the stable identity). The modern bane change
    /// folds in here too: a bane is just another self-cast category, no per-piece cover targeting.</summary>
    public sealed class DesiredBuff
    {
        public int Category { get; }
        /// <summary>Cap the level chosen for this category (the Options max-level knob). 0 = no cap.</summary>
        public int MaxLevel { get; }
        /// <summary>Which variant to cast within the category (Self vs Other vs Item). A category
        /// holds both — buffing yourself wants the Self spell, a fellow wants the Other spell.</summary>
        public SpellTarget Target { get; }
        /// <summary>Magic school to resolve within ("Creature"/"Life"/"Item"), or null/empty for no
        /// constraint. Disambiguates a stacking group that mixes schools (group 67: Creature
        /// "Healing Mastery" vs Life "Heal Self") so a buff can't resolve onto the wrong-school spell.</summary>
        public string School { get; }
        public DesiredBuff(int category, SpellTarget target = SpellTarget.Self, int maxLevel = 0, string school = null)
        { Category = category; Target = target; MaxLevel = maxLevel; School = school; }
    }

    public sealed class SelectedCast
    {
        public int SpellId { get; set; }
        public int Category { get; set; }
        public int Level { get; set; }
        public SpellTarget Target { get; set; }
        /// <summary>True when the pick was lowered from a higher-level known spell because the
        /// character's skill couldn't cast the higher one reliably (drives a chat notice).</summary>
        public bool SkillCapped { get; set; }
        /// <summary>The level (= Power) that WOULD have been chosen ignoring skill, when
        /// <see cref="SkillCapped"/> — for the "capped X to Y" message.</summary>
        public int UncappedLevel { get; set; }
    }

    /// <summary>An active enchantment as selection needs it: which spell, and how long it has
    /// left (seconds; <see cref="int.MaxValue"/> = "effectively permanent / unknown"). Used to
    /// decide whether a category is still covered or is expiring and should be recast.</summary>
    public sealed class ActiveEnchant
    {
        public int SpellId { get; }
        public int SecondsRemaining { get; }
        public ActiveEnchant(int spellId, int secondsRemaining)
        { SpellId = spellId; SecondsRemaining = secondsRemaining; }
    }

    /// <summary>When to recast a buff that's already up. Default (both fields at their zero
    /// values) reproduces the corpus recipe: skip a category any active enchantment covers.
    /// <see cref="MinSecondsRemaining"/> &gt; 0 recasts buffs expiring within that window (a
    /// maintenance re-run tops them up). <see cref="ForceAll"/> ignores active enchantments
    /// entirely — the explicit full rebuff (<c>/nbuff name force</c>).</summary>
    public sealed class RebuffPolicy
    {
        public bool ForceAll { get; set; }
        public int MinSecondsRemaining { get; set; }
    }

    /// <summary>How selection treats the caster's skill. Off (the historic behaviour) picks the
    /// highest level known; when <see cref="Enabled"/>, selection picks the highest level the
    /// character can cast at or above <see cref="MinChancePercent"/> success — ACE's fizzle
    /// model (<see cref="CastChance"/>) — reading effective magic skill per school from
    /// <see cref="SkillOfSchool"/>. This is the fix for "it defaults to level 8s I know but my
    /// skill is too low to land them."</summary>
    public sealed class SkillPolicy
    {
        public bool Enabled { get; set; }
        public int MinChancePercent { get; set; } = 90;
        /// <summary>school name ("Creature"/"Life"/"Item"/"War"/"Void") -> effective magic
        /// skill; returns 0 when unknown, which disables capping for that school.</summary>
        public System.Func<string, int> SkillOfSchool { get; set; }
    }

    /// <summary>
    /// Modern (EoR/ACE) buff selection: for each desired category, choose the highest-level spell
    /// the player actually knows, and skip it when an enchantment of the same category with an
    /// equal-or-higher level is already active (AC's "same category surpasses, different categories
    /// stack" rule — the "categories and groups for stacking" model). Entirely category/level
    /// driven, so it's immune to id renumbering and the level-7/8 naming mess. Pure + testable.
    /// </summary>
    public sealed class ModernBuffSelector
    {
        private readonly ILiveSpellTable _table;
        public ModernBuffSelector(ILiveSpellTable table) { _table = table; }

        /// <summary>The set of stacking categories that carry at least one Self- or Other-aimed
        /// spell. A bane / weapon-aura category is NOT among these: the 2012 retail table (and a
        /// live client that doesn't flag the spell untargetted) tags every spell in it
        /// <c>target=item</c>, so the whole category is "item-only". This is the guard that lets a
        /// Self-aimed request bridge onto the item-enchant spell for banes/auras (below) WITHOUT
        /// ever bridging inside a genuinely mixed group such as Mana Boost (Self) + Essence Lull
        /// (Item), or Missile Weapon Mastery (Self) + Rockslide (Item).</summary>
        internal static HashSet<int> SelfOrOtherCategories(ILiveSpellTable table)
        {
            var set = new HashSet<int>();
            if (table != null)
                foreach (var s in table.All)
                    if (s != null && (s.Target == SpellTarget.Self || s.Target == SpellTarget.Other))
                        set.Add(s.Category);
            return set;
        }

        private static readonly string[] CantripQualities =
            { "Minor ", "Major ", "Epic ", "Prodigal ", "Legendary " };

        /// <summary>Does this spell name mark the self-cast whole-suit ARMOR-protection line — the
        /// 7 elemental/physical banes + Impenetrability? These are the ONLY item-enchant spells the
        /// modern game casts on the player (whole suit + shield). The imbue cantrips
        /// ("Minor/Major/Epic/Prodigal … Bane", "Minor Impenetrability") share the tokens but are
        /// item imbues, not self-casts, so they're excluded by their quality prefix.</summary>
        internal static bool IsArmorBaneName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var q in CantripQualities)
                if (name.StartsWith(q, System.StringComparison.OrdinalIgnoreCase)) return false;
            return name.IndexOf(" Bane", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("Impenetrab", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Stacking categories that may bridge a Self request onto their item-enchant spell:
        /// exactly the armor-bane / Impenetrability groups (see <see cref="IsArmorBaneName"/>). A
        /// weapon-buff group (Blood Drinker, Heart Seeker, Defender, Swift Killer) is deliberately
        /// NOT here — its classic item spells do not self-cast; the self form is the separate
        /// "Aura of …" spell, and if that isn't known the buff must be skipped, never cast on the
        /// player (the "Infected Caress: you cannot cast this upon yourself" hang).</summary>
        internal static HashSet<int> BaneBridgeCategories(ILiveSpellTable table)
        {
            var set = new HashSet<int>();
            if (table != null)
                foreach (var s in table.All)
                    if (s != null && IsArmorBaneName(s.Name))
                        set.Add(s.Category);
            return set;
        }

        /// <summary>Does a live spell aimed <paramref name="spellTarget"/> satisfy a buff the
        /// profile wants aimed at <paramref name="desired"/>? An exact aim always matches. The one
        /// deliberate bridge is the modern item-enchant reality for ARMOR BANES: a self-cast bane
        /// (the 7 elemental/physical banes + Impenetrability) lives in an ITEM-ONLY category — every
        /// spell there is <c>target=item</c> on the dump / on a client that doesn't flag it
        /// untargetted — yet the modern game casts it on the player, whole-suit. So a SELF request
        /// resolves onto that Item spell, but ONLY for a bane category (<paramref
        /// name="baneBridgeCats"/>). Without this, /nbgen's self-cast banes (stored
        /// <c>Target=Self</c>) never match the live record (tagged Item) and are silently dropped —
        /// the "profile has no banes at the end" bug. Weapon buffs (Blood Drinker &amp; friends) are
        /// deliberately excluded: their classic item spells do NOT self-cast, so a Self request must
        /// find the genuine "Aura of …" self spell or skip — bridging one onto the player is exactly
        /// the "Infected Caress: you cannot cast this upon yourself" hang. The item-only guard also
        /// means this never bridges inside a mixed group, and Self&lt;-&gt;Other is never bridged.</summary>
        internal static bool TargetSatisfies(
            SpellTarget desired, SpellTarget spellTarget, int category,
            HashSet<int> selfOrOtherCats, HashSet<int> baneBridgeCats)
        {
            if (desired == spellTarget) return true;
            return desired == SpellTarget.Self
                && spellTarget == SpellTarget.Item
                && selfOrOtherCats != null
                && !selfOrOtherCats.Contains(category)
                && baneBridgeCats != null
                && baneBridgeCats.Contains(category);   // bane/impen only — never a weapon-buff item spell
        }

        /// <summary>Is <paramref name="s"/> OFF a Self buff's real progression ladder — a learned,
        /// non-progression spell that merely shares the stacking group? The ladder is the numbered
        /// levels plus the bespoke level-7 (Power &lt;= 300) and the level-8 "Incantation of …"
        /// (Power 400). A KNOWN Self candidate above 300 that isn't an Incantation is a
        /// fellowship/society/quest buff — "Potent Guardian of the Clutch" (325), "Harbinger Melee
        /// Defense" (400), "Aerbax's Melee Shield" (800) — many of them <c>target=other</c>, so
        /// casting them on yourself hangs. The spellbook can't drop these (the player DID learn
        /// them), so selection is bounded to the ladder by power+name. Only Self requests are
        /// bounded; Other/Item variants are untouched.</summary>
        /// <summary>Does a spell's school satisfy a desired school? Tolerant substring match so the
        /// generator's keyword ("Creature"/"Life"/"Item") matches a live record's longer school name
        /// ("Creature Enchantment"). Empty desired = matches anything.</summary>
        internal static bool SchoolMatches(string desired, string spellSchool)
        {
            if (string.IsNullOrEmpty(desired)) return true;
            if (string.IsNullOrEmpty(spellSchool)) return false;
            return spellSchool.IndexOf(desired, System.StringComparison.OrdinalIgnoreCase) >= 0
                || desired.IndexOf(spellSchool, System.StringComparison.OrdinalIgnoreCase) >= 0;
        }

        internal static bool OffLadder(SpellTarget desired, SpellInfo s)
        {
            return desired == SpellTarget.Self
                && s != null && s.Level > 300
                && (s.Name == null || s.Name.IndexOf("Incantation", System.StringComparison.OrdinalIgnoreCase) < 0);
        }

        /// <param name="known">predicate: is this spell id in the player's spellbook?</param>
        /// <param name="activeEnchantmentSpellIds">spell ids of currently-active enchantments
        /// (from CharacterFilter.Enchantments).</param>
        public IReadOnlyList<SelectedCast> Select(
            IEnumerable<DesiredBuff> desired,
            System.Func<int, bool> known,
            IEnumerable<int> activeEnchantmentSpellIds)
            => Select(desired, known, activeEnchantmentSpellIds, null);

        /// <summary>As above, but with an optional <see cref="SkillPolicy"/>: when enabled, the
        /// chosen spell is the highest-level KNOWN variant the character can cast at or above the
        /// policy's minimum success chance (ACE's fizzle model), instead of simply the highest
        /// known. Within a category, cast chance falls monotonically as level (Power) rises, so
        /// the reliable set is a prefix from the bottom — we pick the top of that prefix, and
        /// fall back to the lowest known (most castable) if even that is below the threshold.</summary>
        public IReadOnlyList<SelectedCast> Select(
            IEnumerable<DesiredBuff> desired,
            System.Func<int, bool> known,
            IEnumerable<int> activeEnchantmentSpellIds,
            SkillPolicy skill)
        {
            // Ids-only overload: treat every active enchantment as effectively permanent, so the
            // behaviour is the corpus default (skip a covered category). Richer time-aware
            // callers use the ActiveEnchant overload below.
            IEnumerable<ActiveEnchant> active = (activeEnchantmentSpellIds ?? Enumerable.Empty<int>())
                .Select(id => new ActiveEnchant(id, int.MaxValue));
            return Select(desired, known, active, skill, null);
        }

        /// <summary>The full selection: skill-capped level choice (<paramref name="skill"/>) and a
        /// <paramref name="rebuff"/> policy that decides when an already-active buff is recast.
        /// A category counts as "still covered" only when an active enchantment surpasses the
        /// pick AND has more than the rebuff threshold left (and the run isn't a forced rebuff).</summary>
        public IReadOnlyList<SelectedCast> Select(
            IEnumerable<DesiredBuff> desired,
            System.Func<int, bool> known,
            IEnumerable<ActiveEnchant> activeEnchantments,
            SkillPolicy skill,
            RebuffPolicy rebuff)
        {
            int minRemaining = rebuff != null ? rebuff.MinSecondsRemaining : 0;
            bool force = rebuff != null && rebuff.ForceAll;

            // Highest "still covered" active level per category — an enchantment only counts if
            // it has more than the rebuff threshold remaining (so near-expiry buffs get recast),
            // and never when forcing a full rebuff.
            var activeLevelByCat = new Dictionary<int, int>();
            if (!force)
            {
                foreach (var e in activeEnchantments ?? Enumerable.Empty<ActiveEnchant>())
                {
                    if (e == null || e.SecondsRemaining <= minRemaining) continue;   // expiring/expired -> recast
                    var info = _table.ById(e.SpellId);
                    if (info == null) continue;
                    if (!activeLevelByCat.TryGetValue(info.Category, out var lvl) || info.Level > lvl)
                        activeLevelByCat[info.Category] = info.Level;
                }
            }

            // Categories that carry a Self/Other spell, and the armor-bane categories that may
            // bridge a Self request onto their item spell — together they let a self-cast bane
            // resolve without ever bridging inside a mixed group or a weapon-buff group.
            var selfOrOtherCats = SelfOrOtherCategories(_table);
            var baneBridgeCats = BaneBridgeCategories(_table);

            var result = new List<SelectedCast>();
            foreach (var d in desired)
            {
                // All KNOWN variants in this category (right aim, under the per-buff cap),
                // ascending by level so we can walk the reliable prefix.
                var known_ = new List<SpellInfo>();
                foreach (var s in _table.All)
                {
                    if (s.Category != d.Category) continue;
                    if (!TargetSatisfies(d.Target, s.Target, s.Category, selfOrOtherCats, baneBridgeCats)) continue; // right variant (self/other/item), bridging self-cast banes only
                    if (s.IsInstantaneous) continue;                // Duration<0: a burst restore/bolt (Heal/Revitalize/Mana Boost), never a persistent buff
                    if (OffLadder(d.Target, s)) continue;           // learned non-ladder special (fellowship/society/quest buff) sharing the group
                    if (d.MaxLevel > 0 && s.Level > d.MaxLevel) continue;
                    if (!known(s.Id)) continue;
                    known_.Add(s);
                }
                if (known_.Count == 0) continue;                    // can't cast this variant at all

                // School disambiguation for a cross-school stacking group (group 67 mixes the Life
                // burst "Heal Self" with the Creature skill buff "Healing Mastery Self"): resolve
                // only within the entry's school. If the school is known but NOTHING matches, skip
                // the buff rather than cast the wrong-school pollutant (a burst restore is never a
                // buff). Only when no candidate carries school data at all do we keep the full set.
                if (!string.IsNullOrEmpty(d.School))
                {
                    var sameSchool = known_.Where(s => SchoolMatches(d.School, s.School)).ToList();
                    if (sameSchool.Count > 0)
                        known_ = sameSchool;
                    else if (known_.Exists(s => !string.IsNullOrEmpty(s.School)))
                        continue;                                   // schools known, none match -> skip (never cast Heal Self for a Healing Mastery entry)
                }
                known_.Sort((a, b) => a.Level.CompareTo(b.Level));

                SpellInfo highestKnown = known_[known_.Count - 1];
                SpellInfo pick = highestKnown;
                bool capped = false;

                if (skill != null && skill.Enabled && skill.SkillOfSchool != null)
                {
                    int eff = 0;
                    try { eff = skill.SkillOfSchool(highestKnown.School); } catch { eff = 0; }
                    if (eff > 0)                                    // 0 = skill unreadable -> don't cap
                    {
                        double min = skill.MinChancePercent / 100.0;
                        // Highest level whose success chance meets the threshold; else the
                        // lowest known (highest chance) as a best-effort floor.
                        SpellInfo reliable = null;
                        foreach (var s in known_)                   // ascending
                            if (CastChance.SuccessChance(eff, s.Level) >= min) reliable = s;
                        pick = reliable ?? known_[0];
                        capped = pick.Level < highestKnown.Level;
                    }
                }

                // Already covered by an equal-or-higher active enchantment? skip. Compare against
                // the level we'd actually CAST (the skill-capped pick): if a higher level is
                // already up, great — nothing to do; if only a lower level is up, recast the pick.
                if (activeLevelByCat.TryGetValue(d.Category, out var activeLvl) && activeLvl >= pick.Level)
                    continue;

                result.Add(new SelectedCast
                {
                    // Aim the cast where the PROFILE asked (d.Target), not where the table filed the
                    // spell (pick.Target): a bridged self-cast bane must land on the player (Self ->
                    // CastSelf), not be routed as an item cast. For an exact match the two are equal.
                    SpellId = pick.Id, Category = pick.Category, Level = pick.Level, Target = d.Target,
                    SkillCapped = capped, UncappedLevel = capped ? highestKnown.Level : pick.Level,
                });
            }
            return result;
        }
    }
}
