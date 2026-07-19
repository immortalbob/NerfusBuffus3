using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace NB3.Core.Modern
{
    /// <summary>One buff a modern profile wants kept up, identified by its **stacking category**
    /// (era-proof — ids renumber and names drift, the category is stable) plus which variant to
    /// cast. <see cref="DisplayName"/> is informational (the family name when the profile was
    /// built); the category is what actually resolves at runtime.
    ///
    /// The optional targeting fields reproduce the original editor's per-tab target modes
    /// (recovered from the v1.52 string table: <c>targetname</c>/<c>targetguid</c>/
    /// <c>itemname</c>/<c>itemguid</c>/<c>targetcover</c> profile attributes):
    /// for <see cref="SpellTarget.Other"/> — a named player, an explicit GUID, or (both empty)
    /// the current selection at cycle start; for <see cref="SpellTarget.Item"/> — a named item,
    /// an explicit GUID, an NB3 cover mask matched against worn items, or (all empty) every
    /// worn/wielded item.</summary>
    public sealed class ModernBuffEntry
    {
        public int Category { get; set; }
        public SpellTarget Target { get; set; } = SpellTarget.Self;
        public int MaxLevel { get; set; }          // 0 = no cap
        public string DisplayName { get; set; } = "";
        /// <summary>Magic school this buff should resolve within ("Creature"/"Life"/"Item"), when
        /// known. Disambiguates a stacking group that mixes schools — group 67 holds both the Life
        /// burst "Heal Self" and the Creature skill buff "Healing Mastery Self", so a Creature entry
        /// must not resolve onto Heal Self. Empty = no constraint (older profiles).</summary>
        public string School { get; set; } = "";

        // Other-target detail (editor "By Name:" / "By GUID:" / "Your Current Target").
        public string TargetName { get; set; } = "";
        public int TargetGuid { get; set; }

        // Item-target detail (editor Item tab: named item / GUID / cover-mask checkboxes).
        public string ItemName { get; set; } = "";
        public int ItemGuid { get; set; }
        /// <summary>NB3's own cover-mask scheme (docs/COVER_MASK_RECOVERY.md) — nonzero limits an
        /// Item cast to worn items whose translated coverage intersects this mask.</summary>
        public int CoverMask { get; set; }
    }

    /// <summary>The modern (EoR/ACE) buff profile: equips + category-identified buffs. Replaces the
    /// classic profile's hardcoded spell ids and per-piece cover-mask spellgroups with era-proof
    /// stacking categories, so a profile survives spell renumbering and the level-7/8 naming mess.
    /// <see cref="Includes"/> reproduces the original's <c>&lt;Include profile="..."/&gt;</c>
    /// nodes (the Extras tab / <c>/nbinclude</c>); resolve them with
    /// <see cref="ResolveIncludes"/> before planning.</summary>
    public sealed class ModernProfile
    {
        public string Name { get; set; } = "";
        public string Version { get; set; } = "2.0.0.0";
        public List<string> EquipItems { get; } = new List<string>();
        public List<ModernBuffEntry> Buffs { get; } = new List<ModernBuffEntry>();
        /// <summary>Names of other profiles whose entries are merged in at plan time.</summary>
        public List<string> Includes { get; } = new List<string>();

        // ---- XML I/O -------------------------------------------------------------------

        public static ModernProfile Load(string path) => Parse(File.ReadAllText(path));

        public static ModernProfile Parse(string xml)
        {
            var root = XDocument.Parse(xml).Root;
            if (root == null || root.Name.LocalName != "ModernProfile")
                throw new FormatException("Profile XML missing <ModernProfile> root.");
            var p = new ModernProfile
            {
                Name = (string)root.Attribute("name") ?? "",
                Version = (string)root.Attribute("version") ?? "2.0.0.0"
            };
            foreach (var e in root.Elements())
            {
                switch (e.Name.LocalName)
                {
                    case "Equip":
                        p.EquipItems.Add((string)e.Attribute("itemname") ?? "");
                        break;
                    case "Include":
                        var inc = (string)e.Attribute("profile") ?? "";
                        if (inc.Length > 0) p.Includes.Add(inc);
                        break;
                    case "Buff":
                        p.Buffs.Add(new ModernBuffEntry
                        {
                            Category = ParseInt((string)e.Attribute("category")),
                            Target = ParseTarget((string)e.Attribute("target")),
                            MaxLevel = ParseInt((string)e.Attribute("maxlevel")),
                            DisplayName = (string)e.Attribute("name") ?? "",
                            School = (string)e.Attribute("school") ?? "",
                            TargetName = (string)e.Attribute("targetname") ?? "",
                            TargetGuid = ParseInt((string)e.Attribute("targetguid")),
                            ItemName = (string)e.Attribute("itemname") ?? "",
                            ItemGuid = ParseInt((string)e.Attribute("itemguid")),
                            CoverMask = ParseInt((string)e.Attribute("targetcover")),
                        });
                        break;
                }
            }
            return p;
        }

        public string ToXml()
        {
            var root = new XElement("ModernProfile",
                new XAttribute("version", Version),
                new XAttribute("name", Name));
            foreach (var inc in Includes)
                root.Add(new XElement("Include", new XAttribute("profile", inc)));
            foreach (var item in EquipItems)
                root.Add(new XElement("Equip", new XAttribute("itemname", item)));
            foreach (var b in Buffs)
            {
                var e = new XElement("Buff",
                    new XAttribute("name", b.DisplayName ?? ""),
                    new XAttribute("category", b.Category),
                    new XAttribute("target", b.Target.ToString().ToLowerInvariant()),
                    new XAttribute("maxlevel", b.MaxLevel));
                if (!string.IsNullOrEmpty(b.School)) e.Add(new XAttribute("school", b.School));
                if (!string.IsNullOrEmpty(b.TargetName)) e.Add(new XAttribute("targetname", b.TargetName));
                if (b.TargetGuid != 0) e.Add(new XAttribute("targetguid", "0x" + b.TargetGuid.ToString("X8")));
                if (!string.IsNullOrEmpty(b.ItemName)) e.Add(new XAttribute("itemname", b.ItemName));
                if (b.ItemGuid != 0) e.Add(new XAttribute("itemguid", "0x" + b.ItemGuid.ToString("X8")));
                if (b.CoverMask != 0) e.Add(new XAttribute("targetcover", "0x" + b.CoverMask.ToString("X8")));
                root.Add(e);
            }
            return new XDocument(new XDeclaration("1.0", null, null), root).ToString();
        }

        // ---- include resolution (the original's <Include> semantics) --------------------

        /// <summary>Flatten <paramref name="profile"/> plus its <see cref="Includes"/> chain into
        /// one plan-ready profile. <paramref name="loadByName"/> maps an include name (with or
        /// without .xml) to a loaded profile, or null if missing. Faithful to the original:
        /// duplicates and cycles are skipped with a warning (the v1.52 "Inclusion of this profile
        /// will result in infinite recursion!" guard), never fatal.</summary>
        public static ModernProfile ResolveIncludes(
            ModernProfile profile, Func<string, ModernProfile> loadByName, IList<string> warnings)
        {
            var merged = new ModernProfile { Name = profile.Name, Version = profile.Version };
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Canon(profile.Name) };
            MergeInto(merged, profile, loadByName, seen, warnings, depth: 0);
            return merged;
        }

        private static void MergeInto(ModernProfile into, ModernProfile src,
            Func<string, ModernProfile> loadByName, HashSet<string> seen,
            IList<string> warnings, int depth)
        {
            if (depth > 16) { Warn(warnings, "Include nesting too deep, stopping."); return; }
            foreach (var eq in src.EquipItems)
                if (!into.EquipItems.Contains(eq)) into.EquipItems.Add(eq);
            foreach (var b in src.Buffs)
                if (!into.Buffs.Any(x => SameEntry(x, b))) into.Buffs.Add(b);
            foreach (var inc in src.Includes)
            {
                var key = Canon(inc);
                if (!seen.Add(key))
                {
                    Warn(warnings, $"Include '{inc}' would recurse or repeat - skipped.");
                    continue;
                }
                ModernProfile child = null;
                try { child = loadByName != null ? loadByName(inc) : null; } catch { }
                if (child == null)
                {
                    Warn(warnings, $"Included profile not found: '{inc}'.");
                    continue;
                }
                MergeInto(into, child, loadByName, seen, warnings, depth + 1);
            }
        }

        /// <summary>Two entries that would produce the exact same cast (the original's
        /// duplicate-include guard: "already in the Profile / Includes list, ignoring").</summary>
        private static bool SameEntry(ModernBuffEntry a, ModernBuffEntry b) =>
            a.Category == b.Category && a.Target == b.Target && a.MaxLevel == b.MaxLevel &&
            string.Equals(a.TargetName ?? "", b.TargetName ?? "", StringComparison.OrdinalIgnoreCase) &&
            a.TargetGuid == b.TargetGuid &&
            string.Equals(a.ItemName ?? "", b.ItemName ?? "", StringComparison.OrdinalIgnoreCase) &&
            a.ItemGuid == b.ItemGuid && a.CoverMask == b.CoverMask;

        private static void Warn(IList<string> warnings, string msg)
        { if (warnings != null) warnings.Add(msg); }

        private static string Canon(string name)
        {
            name = (name ?? "").Trim();
            if (name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);
            return name;
        }

        private static int ParseInt(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return int.TryParse(s.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var h) ? h : 0;
            return int.TryParse(s, out var d) ? d : 0;
        }

        private static SpellTarget ParseTarget(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "other": return SpellTarget.Other;
                case "item": return SpellTarget.Item;
                case "none": return SpellTarget.None;
                default: return SpellTarget.Self;
            }
        }
    }
}
