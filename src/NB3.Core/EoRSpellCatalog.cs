using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NB3.Core
{
    public enum CantripTier { None, Minor, Major, Epic, Legendary }

    public sealed class EoRSpell
    {
        public int Id { get; }
        public string Name { get; }
        public CantripTier Tier { get; }
        /// <summary>Family = the name minus the leading tier word (e.g. "Acid Bane"). Empty for
        /// non-cantrip spells.</summary>
        public string Family { get; }
        public bool IsCantrip => Tier != CantripTier.None;
        public bool IsBane => Family.EndsWith("Bane", StringComparison.OrdinalIgnoreCase);
        public bool IsWard => Family.EndsWith("Ward", StringComparison.OrdinalIgnoreCase);

        public EoRSpell(int id, string name)
        {
            Id = id; Name = name ?? "";
            var sp = Name.IndexOf(' ');
            var lead = sp < 0 ? Name : Name.Substring(0, sp);
            switch (lead)
            {
                case "Minor": Tier = CantripTier.Minor; break;
                case "Major": Tier = CantripTier.Major; break;
                case "Epic": Tier = CantripTier.Epic; break;
                case "Legendary": Tier = CantripTier.Legendary; break;
                default: Tier = CantripTier.None; break;
            }
            Family = Tier != CantripTier.None && sp >= 0 ? Name.Substring(sp + 1).Trim() : "";
        }
    }

    /// <summary>
    /// The End-of-Retail spell table (from the live-client dump), and the name-structured
    /// cantrip classifier documented in decaldev doc 16 §4: item cantrips are `&lt;Tier&gt;
    /// &lt;Family&gt;`, four tiers, Bane ≠ Ward. This is an **era-awareness layer**, not a change
    /// to NB3's behaviour: NB3's own 2003 table (7 numeric levels) is the Dark-Majesty vocabulary;
    /// EoR renumbered and renamed everything into tiers. A resolver can bridge a profile's intent
    /// to whichever era's table the live client actually loaded (NB3 originally validated ids
    /// against Portal.dat — "spell that isn't in Portal.dat!!!" — so bridging is faithful in spirit).
    ///
    /// The classifier reproduces doc 16's published counts exactly (Minor 86 / Major 76 / Epic 79
    /// / Legendary 69; 79 families; 69 at all four tiers) — see the tests.
    /// </summary>
    public sealed class EoRSpellCatalog
    {
        private readonly List<EoRSpell> _spells = new List<EoRSpell>();
        private readonly Dictionary<int, EoRSpell> _byId = new Dictionary<int, EoRSpell>();

        public IReadOnlyList<EoRSpell> Spells => _spells;
        public int Count => _spells.Count;

        public static EoRSpellCatalog Load(string csvPath) => Parse(File.ReadAllLines(csvPath));

        public static EoRSpellCatalog Parse(IEnumerable<string> csvLines)
        {
            var cat = new EoRSpellCatalog();
            bool first = true;
            foreach (var line in csvLines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (first) { first = false; if (line.StartsWith("id,")) continue; }
                int comma = line.IndexOf(',');
                if (comma <= 0) continue;
                if (!int.TryParse(line.Substring(0, comma).Trim(), out int id)) continue;
                var name = line.Substring(comma + 1).Trim().Trim('"');
                var s = new EoRSpell(id, name);
                cat._spells.Add(s);
                cat._byId[id] = s;
            }
            return cat;
        }

        public EoRSpell ById(int id) => _byId.TryGetValue(id, out var s) ? s : null;

        public IEnumerable<EoRSpell> Cantrips => _spells.Where(s => s.IsCantrip);

        /// <summary>Distinct cantrip family names (leading-word classifier).</summary>
        public ISet<string> CantripFamilies =>
            new HashSet<string>(Cantrips.Select(s => s.Family), StringComparer.OrdinalIgnoreCase);

        public int TierCount(CantripTier tier) => _spells.Count(s => s.Tier == tier);

        /// <summary>Find a cantrip by tier + family, e.g. (Legendary, "Impenetrability").</summary>
        public EoRSpell FindCantrip(CantripTier tier, string family) =>
            _spells.FirstOrDefault(s =>
                s.Tier == tier && string.Equals(s.Family, family, StringComparison.OrdinalIgnoreCase));

        /// <summary>Families that exist at every one of the four tiers.</summary>
        public ISet<string> FamiliesAtAllTiers()
        {
            var tiers = new[] { CantripTier.Minor, CantripTier.Major, CantripTier.Epic, CantripTier.Legendary };
            ISet<string> acc = null;
            foreach (var t in tiers)
            {
                var fams = new HashSet<string>(
                    _spells.Where(s => s.Tier == t).Select(s => s.Family), StringComparer.OrdinalIgnoreCase);
                acc = acc == null ? fams : (ISet<string>)new HashSet<string>(acc.Intersect(fams, StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
            }
            return acc ?? new HashSet<string>();
        }
    }
}
