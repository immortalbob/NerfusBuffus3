using System;
using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace NB3.Core
{
    /// <summary>Which healing-kit tiers the cycle may use (the Options "Healing Kits to use"
    /// checkboxes, in the original view's order). Selection preference is data-driven
    /// (doc 19 §4): Plentiful → Treated → Peerless — see <see cref="RegenItems"/>.</summary>
    [Flags]
    public enum HealingKitTiers
    {
        None = 0,
        Plentiful = 1,
        Treated = 2,
        Peerless = 4
    }

    /// <summary>
    /// Per-character configuration, one-for-one with the recovered <c>nb3-charconfig.xml</c>
    /// Options view. Faithful to the original's option set and persisted per character (the
    /// view's header shows the character GUID), saved on change to a per-user location so a
    /// client crash never loses it (doc 08 §8).
    /// </summary>
    public sealed class NB3Settings
    {
        public int CharacterId { get; set; }

        // Healing kits to use
        public HealingKitTiers HealingKits { get; set; } = HealingKitTiers.None;

        // Level 7 spells
        public bool UseRevit7 { get; set; }
        public bool UseS2M7 { get; set; }
        public bool UseH2M7 { get; set; }
        public bool FallbackTo6OnUnknown7 { get; set; } = true;

        /// <summary>"Expected % of Spell Cost" (the AR edit): buff while mana ≥ this % of the
        /// next spell's cost.</summary>
        public int ExpectedPctSpellCost { get; set; } = 100;

        /// <summary>Reserve floor: interrupt buffing to regen when mana falls below this PERCENT of
        /// max (so it doesn't buff down to empty). 25 = "regen when under a quarter". 0 = disabled
        /// (fall back to the pure per-spell-cost gate). Only used when a regen mode is set.</summary>
        public int ManaFloorPercent { get; set; } = 25;

        /// <summary>Once regen kicks in (below <see cref="ManaFloorPercent"/>), regen back up to this
        /// PERCENT of max before resuming — the high-water mark. Must be above the floor or every
        /// spell would re-trigger regen (flapping). 90 = "top back up to nearly full".</summary>
        public int ManaRegenTargetPercent { get; set; } = 90;

        /// <summary>DEFAULT is <see cref="ManaRegenMode.SpellRecovery"/>: a fresh character
        /// regenerates mana with S2M / Cannibalize / Revitalize out of the box instead of buffing
        /// itself to empty. (The old default was <see cref="ManaRegenMode.None"/> — no regen at all,
        /// which is exactly why the cycle ran the character down to 0 and never recovered.)</summary>
        public ManaRegenMode ManaRegenMode { get; set; } = ManaRegenMode.SpellRecovery;

        /// <summary>OPTIONAL fallback for the spell-recovery mode: may drink a mana elixir as a last
        /// resort when spells alone can't recover. Off by default — potions are opt-in, not required.
        /// (Healing kits are the other optional fallback, gated by the <see cref="HealingKits"/>
        /// tier selection.)</summary>
        public bool UsePotions { get; set; } = false;

        /// <summary>"Maximum level for H2M, S2M and Revit".</summary>
        public int MaxRecoveryLevel { get; set; } = 7;

        public bool QuietMode { get; set; }
        public bool EditorPermaDelete { get; set; }

        /// <summary>Cap each buff to the highest level the character can cast reliably (ACE's
        /// fizzle model, <see cref="NB3.Core.Modern.CastChance"/>) instead of the highest level
        /// merely known. On by default — the fix for "it defaults to level 8s I know but my
        /// skill is too low to land them."</summary>
        public bool SkillBasedLevel { get; set; } = true;

        /// <summary>Minimum acceptable cast-success chance (%) when <see cref="SkillBasedLevel"/>
        /// is on: the planner picks the highest level whose predicted chance is at least this.
        /// 90 by default — high enough to avoid the fizzle grind, low enough to still push level.</summary>
        public int MinCastChancePercent { get; set; } = 90;

        /// <summary>When true (the DEFAULT, and the original NB2's behaviour), <c>/nbuff</c> casts
        /// the whole list every time you ask — buffs you already have are recast (refreshing their
        /// duration). Turn it OFF for the mana-saving mode that skips buffs still active (honouring
        /// <see cref="RebuffMinutesRemaining"/>). Default on because "I ran it, it should buff" is
        /// what people expect; skipping is the opt-in optimisation.</summary>
        public bool RecastActiveBuffs { get; set; } = true;

        /// <summary>Only when <see cref="RecastActiveBuffs"/> is OFF: recast a buff that's already
        /// active only if it has fewer than this many MINUTES of duration left (0 = skip every
        /// active buff; raise it for a maintenance re-run that tops up buffs about to expire).</summary>
        public int RebuffMinutesRemaining { get; set; } = 0;

        /// <summary>Heal (in the "kits + H2M" regen mode) when health drops below this PERCENT of
        /// max — H2M drains health for mana, so this is the safety floor. 50 = "under half".
        /// A percent, not absolute points, so it scales with the character's vital.</summary>
        public int HealthFloorPercent { get; set; } = 50;

        /// <summary>Replenish stamina (in the S2M / rest / revit regen modes) when stamina drops
        /// below this PERCENT of max, before casting S2M. 50 = "under half".</summary>
        public int StaminaFloorPercent { get; set; } = 50;

        /// <summary>Onboarding: when true (the DEFAULT), NB3 auto-generates a character-specific
        /// self-buff profile named after the character on login — but ONLY if one doesn't already
        /// exist (so it never clobbers an edited profile) — and selects it in the main window, so a
        /// brand-new user is ready to <c>/nbuff</c> without ever opening the Editor or running a
        /// command. It is the login-time equivalent of typing <c>/nbgen &lt;charname&gt;</c>. Turn
        /// it off with <c>/nbset autogen 0</c> if you'd rather pick a profile yourself. Per
        /// character, like every other setting.</summary>
        public bool AutoGenerateOnLogin { get; set; } = true;

        // ---- persistence ---------------------------------------------------------------

        public static string PathFor(int characterId)
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NerfusBuffus3");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, $"config_{characterId:X8}.xml");
        }

        /// <summary>Load a per-character config, or fresh defaults if the file is missing. A file
        /// that exists but can't be read or parsed (e.g. truncated by a client crash mid-write, the
        /// exact failure the per-user save location is meant to survive) falls back to defaults
        /// rather than throwing — the login auto-onboard now parses this on the render-frame poll,
        /// so a corrupt file must never turn into an exception storm (or take a callback down).</summary>
        public static NB3Settings Load(string path)
        {
            try { return File.Exists(path) ? Parse(File.ReadAllText(path)) : new NB3Settings(); }
            catch { return new NB3Settings(); }
        }

        public void Save(string path) => File.WriteAllText(path, ToXml());

        public static NB3Settings Parse(string xml)
        {
            var r = XDocument.Parse(xml).Root;
            var s = new NB3Settings();
            if (r == null) return s;

            s.CharacterId = ParseInt((string)r.Attribute("character"));
            var kits = HealingKitTiers.None;
            if (B(r, "usePlentiful")) kits |= HealingKitTiers.Plentiful;
            if (B(r, "useTreated"))   kits |= HealingKitTiers.Treated;
            if (B(r, "usePeerless"))  kits |= HealingKitTiers.Peerless;
            s.HealingKits = kits;

            s.UseRevit7 = B(r, "useRevit7");
            s.UseS2M7 = B(r, "useS2M7");
            s.UseH2M7 = B(r, "useH2M7");
            s.FallbackTo6OnUnknown7 = B(r, "fallbackTo6", true);
            s.ExpectedPctSpellCost = I(r, "expectedPctSpellCost", 100);
            s.ManaFloorPercent = I(r, "manaFloorPercent", 25);
            s.ManaRegenTargetPercent = I(r, "manaRegenTargetPercent", 90);
            s.ManaRegenMode = (ManaRegenMode)I(r, "manaRegenMode", (int)ManaRegenMode.SpellRecovery);
            s.UsePotions = B(r, "usePotions", false);
            s.MaxRecoveryLevel = I(r, "maxRecoveryLevel", 7);
            s.QuietMode = B(r, "quietMode");
            s.EditorPermaDelete = B(r, "editorPermaDelete");
            s.SkillBasedLevel = B(r, "skillBasedLevel", true);
            s.MinCastChancePercent = I(r, "minCastChancePercent", 90);
            s.RecastActiveBuffs = B(r, "recastActiveBuffs", true);
            s.RebuffMinutesRemaining = I(r, "rebuffMinutesRemaining", 0);
            s.HealthFloorPercent = I(r, "healthFloorPercent", 50);
            s.StaminaFloorPercent = I(r, "staminaFloorPercent", 50);
            s.AutoGenerateOnLogin = B(r, "autoGenerateOnLogin", true);
            return s;
        }

        public string ToXml()
        {
            var r = new XElement("NB3Config",
                new XAttribute("character", "0x" + CharacterId.ToString("X8")),
                new XAttribute("usePlentiful", HealingKits.HasFlag(HealingKitTiers.Plentiful)),
                new XAttribute("useTreated", HealingKits.HasFlag(HealingKitTiers.Treated)),
                new XAttribute("usePeerless", HealingKits.HasFlag(HealingKitTiers.Peerless)),
                new XAttribute("useRevit7", UseRevit7),
                new XAttribute("useS2M7", UseS2M7),
                new XAttribute("useH2M7", UseH2M7),
                new XAttribute("fallbackTo6", FallbackTo6OnUnknown7),
                new XAttribute("expectedPctSpellCost", ExpectedPctSpellCost),
                new XAttribute("manaFloorPercent", ManaFloorPercent),
                new XAttribute("manaRegenTargetPercent", ManaRegenTargetPercent),
                new XAttribute("manaRegenMode", (int)ManaRegenMode),
                new XAttribute("usePotions", UsePotions),
                new XAttribute("maxRecoveryLevel", MaxRecoveryLevel),
                new XAttribute("quietMode", QuietMode),
                new XAttribute("editorPermaDelete", EditorPermaDelete),
                new XAttribute("skillBasedLevel", SkillBasedLevel),
                new XAttribute("minCastChancePercent", MinCastChancePercent),
                new XAttribute("recastActiveBuffs", RecastActiveBuffs),
                new XAttribute("rebuffMinutesRemaining", RebuffMinutesRemaining),
                new XAttribute("healthFloorPercent", HealthFloorPercent),
                new XAttribute("staminaFloorPercent", StaminaFloorPercent),
                new XAttribute("autoGenerateOnLogin", AutoGenerateOnLogin));
            return new XDocument(new XDeclaration("1.0", null, null), r).ToString();
        }

        private static bool B(XElement r, string n, bool def = false)
        {
            var a = (string)r.Attribute(n);
            return a == null ? def : (a == "1" || a.Equals("true", StringComparison.OrdinalIgnoreCase));
        }
        private static int I(XElement r, string n, int def)
        {
            var a = (string)r.Attribute(n);
            return a == null ? def : ParseInt(a, def);
        }
        private static int ParseInt(string s, int def = 0)
        {
            if (string.IsNullOrWhiteSpace(s)) return def;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return int.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : def;
            return int.TryParse(s, out var d) ? d : def;
        }
    }
}
