using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;

namespace NB3.Core
{
    /// <summary>The curated 275-family spell table recovered verbatim from the original
    /// plugin (<c>nb3-spells.xml</c>). Provides family lookup and the "cast the best level I
    /// actually know" resolution that the Options view exposed (max level, fallback-to-6).</summary>
    public sealed class SpellTable
    {
        private readonly List<SpellFamily> _families = new List<SpellFamily>();
        private readonly Dictionary<int, SpellLocation> _byId = new Dictionary<int, SpellLocation>();
        private readonly Dictionary<string, SpellFamily> _byEditorName =
            new Dictionary<string, SpellFamily>(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<SpellFamily> Families => _families;
        public int Count => _families.Count;

        public static SpellTable Load(string path) => Parse(File.ReadAllText(path));

        public static SpellTable Parse(string xml)
        {
            var table = new SpellTable();
            var doc = XDocument.Parse(xml);
            var root = doc.Root;
            if (root == null || root.Name.LocalName != "SpellTable")
                throw new FormatException("Spell Table resource missing <SpellTable> node.");

            foreach (var e in root.Elements("Spell"))
            {
                var levels = new int[7];
                for (int i = 1; i <= 7; i++)
                    levels[i - 1] = ParseId((string)e.Attribute("level" + i));

                var fam = new SpellFamily(
                    editorName: (string)e.Attribute("editorname") ?? "",
                    name: (string)e.Attribute("name") ?? "",
                    target: ParseTarget((string)e.Attribute("target")),
                    levels1To7: levels);

                table._families.Add(fam);
                if (!string.IsNullOrEmpty(fam.EditorName))
                    table._byEditorName[fam.EditorName] = fam;
                for (int lvl = 1; lvl <= 7; lvl++)
                {
                    int id = fam.IdAtLevel(lvl);
                    if (id != 0) table._byId[id] = new SpellLocation(fam, lvl);
                }
            }
            return table;
        }

        public SpellFamily ByEditorName(string name) =>
            _byEditorName.TryGetValue(name ?? "", out var f) ? f : null;

        /// <summary>Locate any spell id within the table (family + level). Returns false for
        /// ids the table doesn't know (e.g. a spell that isn't in Portal.dat).</summary>
        public bool TryLocate(int spellId, out SpellLocation loc) => _byId.TryGetValue(spellId, out loc);

        /// <summary>
        /// Given a requested spell id, return the id of the highest level in the same family
        /// that (a) is at or below <paramref name="maxLevel"/> and (b) the player knows,
        /// walking downward. This is the engine's core "use the best version I have" rule and
        /// the mechanism behind the Options "fallback to level 6 on unknown level 7" toggle.
        /// Returns 0 if the family has nothing castable.
        /// </summary>
        public int ResolveCastableId(int requestedId, Func<int, bool> spellKnown, int maxLevel = 7)
        {
            if (!_byId.TryGetValue(requestedId, out var loc))
                return spellKnown(requestedId) ? requestedId : 0; // unknown to table: trust caller
            int start = Math.Min(loc.Level, Math.Max(1, maxLevel));
            for (int lvl = start; lvl >= 1; lvl--)
            {
                int id = loc.Family.IdAtLevel(lvl);
                if (id != 0 && spellKnown(id)) return id;
            }
            return 0;
        }

        private static int ParseId(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return int.Parse(s.Substring(2), System.Globalization.NumberStyles.HexNumber);
            return int.Parse(s);
        }

        private static TargetType ParseTarget(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "self": return TargetType.Self;
                case "other": return TargetType.Other;
                case "item": return TargetType.Item;
                case "cover": return TargetType.Cover;
                default: return TargetType.Self;
            }
        }
    }
}
