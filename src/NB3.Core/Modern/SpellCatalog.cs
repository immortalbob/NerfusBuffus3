using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NB3.Core.Modern
{
    /// <summary>
    /// A concrete <see cref="ILiveSpellTable"/> loaded from a real AC spell dump (the tab-separated
    /// <c>id, group, difficulty, school, mana, name</c> catalog derived from the 2012 retail spell
    /// data). <b>Category = stacking group</b> and <b>Level = difficulty</b> — confirmed against the
    /// dump's own stacking file (Strength Self I..VI = power 1/50/100/150/200/250). In the live
    /// plugin this same shape is filled from <c>FileService.SpellTable</c>; this loader gives the
    /// offline harness (and a shipped fallback) real data to resolve and test against.
    /// </summary>
    public sealed class SpellCatalog : ILiveSpellTable
    {
        private readonly Dictionary<int, SpellInfo> _byId = new Dictionary<int, SpellInfo>();
        private readonly Dictionary<int, List<SpellInfo>> _byGroup = new Dictionary<int, List<SpellInfo>>();

        public IReadOnlyCollection<SpellInfo> All => _byId.Values;
        public SpellInfo ById(int id) => _byId.TryGetValue(id, out var s) ? s : null;
        public int Count => _byId.Count;

        public IReadOnlyList<SpellInfo> InGroup(int group) =>
            _byGroup.TryGetValue(group, out var l) ? l : (IReadOnlyList<SpellInfo>)Array.Empty<SpellInfo>();

        public IEnumerable<SpellInfo> BySchool(string school) =>
            _byId.Values.Where(s => string.Equals(s.School, school, StringComparison.OrdinalIgnoreCase));

        public static SpellCatalog Load(string tsvPath) => Parse(File.ReadAllLines(tsvPath));

        /// <summary>Parse a spell dump. Auto-detects the format from the header: the authoritative
        /// end-of-retail export (`Id\tName\tFamily…\tisUntargeted…\tSchool.Id…`, file 16 §7.7) or the
        /// legacy 2012 tsv (`id\tgroup\tdifficulty\tschool\tmana\ttarget\tname`). The EoR export is
        /// richer — it carries `Duration` (the burst-vs-buff signal) and `isUntargeted` (the real
        /// target classifier) — but has no mana column; overlay it with <see cref="WithManaFrom"/>.</summary>
        public static SpellCatalog Parse(IEnumerable<string> lines)
        {
            var cat = new SpellCatalog();
            bool first = true;
            bool eor = false;
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (first)
                {
                    first = false;
                    if (line.IndexOf("isUntargeted", StringComparison.OrdinalIgnoreCase) >= 0
                        || line.StartsWith("Id\tName\tFamily", StringComparison.OrdinalIgnoreCase))
                    { eor = true; continue; }                 // EoR header — skip it
                    if (line.StartsWith("id\t", StringComparison.OrdinalIgnoreCase)) continue; // 2012 header
                    // otherwise: no header, fall through and treat this line as 2012 data
                }
                var f = line.Split('\t');
                var info = eor ? ParseEor(f) : Parse2012(f);
                if (info == null) continue;
                cat._byId[info.Id] = info;
                if (!cat._byGroup.TryGetValue(info.Category, out var l)) cat._byGroup[info.Category] = l = new List<SpellInfo>();
                l.Add(info);
            }
            return cat;
        }

        // 2012: id, group, difficulty, school, mana, target, name
        private static SpellInfo Parse2012(string[] f)
        {
            if (f.Length < 7 || !int.TryParse(f[0], out int id)) return null;
            int.TryParse(f[1], out int group);
            int.TryParse(f[2], out int diff);
            int.TryParse(f[4], out int mana);
            return new SpellInfo(id, f[6], group, diff, f[3], mana, ParseTarget(f[5]));
        }

        // EoR: Id, Name, Family, Family Override, Saying, Duration, Difficulty, isFellowship,
        //      isOffensive, isUntargeted, IsInstantCast, School.Id, Requires TurnTo
        private static SpellInfo ParseEor(string[] f)
        {
            if (f.Length < 12 || !int.TryParse(f[0], out int id)) return null;
            int.TryParse(f[2], out int family);
            int.TryParse(f[5], out int duration);
            int.TryParse(f[6], out int diff);
            int.TryParse(f[11], out int schoolId);
            bool untargeted = string.Equals((f[9] ?? "").Trim(), "True", StringComparison.OrdinalIgnoreCase);
            // isUntargeted is the authoritative target classifier (file 16 §7.7): untargeted -> Self;
            // targeted item-enchant (School 3: banes, weapon buffs) -> Item; other targeted -> Other.
            SpellTarget target = untargeted ? SpellTarget.Self
                : (schoolId == 3 ? SpellTarget.Item : SpellTarget.Other);
            return new SpellInfo(id, f[1], family, diff, SchoolName(schoolId), 0, target, duration);
        }

        /// <summary>Spell-record MagicSchool enum (file 16 §7.7): 1 War, 2 Life, 3 ItemEnchantment,
        /// 4 CreatureEnchantment, 5 VoidMagic.</summary>
        private static string SchoolName(int schoolId)
        {
            switch (schoolId)
            {
                case 1: return "War";
                case 2: return "Life";
                case 3: return "Item";
                case 4: return "Creature";
                case 5: return "Void";
                default: return "";
            }
        }

        /// <summary>Return a copy with mana costs overlaid by id from <paramref name="manaSource"/>
        /// wherever this catalog's mana is 0 — the EoR export has no mana column, so the 2012 dump
        /// fills it for the classic ids that overlap.</summary>
        public SpellCatalog WithManaFrom(SpellCatalog manaSource)
        {
            if (manaSource == null) return this;
            var merged = new SpellCatalog();
            foreach (var s in _byId.Values)
            {
                int mana = s.Mana;
                if (mana == 0) { var m = manaSource.ById(s.Id); if (m != null) mana = m.Mana; }
                var info = new SpellInfo(s.Id, s.Name, s.Category, s.Level, s.School, mana, s.Target, s.Duration);
                merged._byId[info.Id] = info;
                if (!merged._byGroup.TryGetValue(info.Category, out var l)) merged._byGroup[info.Category] = l = new List<SpellInfo>();
                l.Add(info);
            }
            return merged;
        }

        private static SpellTarget ParseTarget(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "self": return SpellTarget.Self;
                case "other": return SpellTarget.Other;
                case "item": return SpellTarget.Item;
                default: return SpellTarget.None;
            }
        }
    }
}
