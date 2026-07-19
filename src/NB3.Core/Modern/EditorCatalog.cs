using System;
using System.Collections.Generic;
using System.Linq;

namespace NB3.Core.Modern
{
    /// <summary>One row in an editor spell-pick list: a buff family the player can add to the
    /// profile, resolved to its modern stacking category. Built by <see cref="EditorCatalog"/>.</summary>
    public sealed class EditorFamily
    {
        /// <summary>The curated display name from the recovered 275-family table where the
        /// family maps ("Strength Self", "Impenetrability"), else derived from the live name.</summary>
        public string DisplayName { get; set; } = "";
        /// <summary>Magic school of the family's spells ("Creature" / "Life" / "Item").</summary>
        public string School { get; set; } = "";
        /// <summary>The live stacking category the family resolves to.</summary>
        public int Category { get; set; }
        /// <summary>The aim of the family's spells per the LIVE table (which is what selection
        /// keys on) — on a modern server former item buffs can resolve as Self (banes/auras).</summary>
        public SpellTarget LiveTarget { get; set; }
        /// <summary>The tab the family belongs on, per the CLASSIC table's target attribute
        /// (Self / Other / Item) — how the original editor grouped its five tabs.</summary>
        public TargetType ClassicTarget { get; set; }
    }

    /// <summary>
    /// Builds the editor's spell-pick lists: the recovered 275-family table gives the curated
    /// family set and the tab assignment (Self/Other/Item — the original's five tabs), the LIVE
    /// spell table resolves each family to its modern stacking category and real target. The
    /// school split (Creature vs Life) comes from the live record too — the original read it
    /// from Portal.dat's spell table, exactly the same source. Families whose ids the live
    /// table doesn't know are dropped (a renamed/renumbered server); the count is reported so
    /// the editor can say so. Pure + testable against the 2012 catalog fixture.
    /// </summary>
    public static class EditorCatalog
    {
        /// <summary>Build the pick lists. <paramref name="unresolved"/> receives the editor
        /// names of families the live table couldn't resolve.</summary>
        public static IList<EditorFamily> Build(
            SpellTable classicTable, ILiveSpellTable live, IList<string> unresolved)
        {
            var rows = new List<EditorFamily>();
            if (classicTable == null || live == null) return rows;

            foreach (var fam in classicTable.Families)
            {
                // Resolve via the first defined level id — every level of a classic family
                // shares one stacking category, so any resolvable id will do.
                SpellInfo info = null;
                for (int lvl = 1; lvl <= 7 && info == null; lvl++)
                {
                    int id = fam.IdAtLevel(lvl);
                    if (id != 0) info = live.ById(id);
                }
                if (info == null || info.Category == 0)
                {
                    if (unresolved != null) unresolved.Add(fam.EditorName);
                    continue;
                }

                rows.Add(new EditorFamily
                {
                    DisplayName = string.IsNullOrEmpty(fam.EditorName) ? BaseName(info.Name) : fam.EditorName,
                    School = info.School ?? "",
                    Category = info.Category,
                    LiveTarget = info.Target,
                    ClassicTarget = fam.Target,
                });
            }

            rows.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase));
            return rows;
        }

        /// <summary>The rows for one editor tab. Tabs mirror the original: Creature(S), Creature(O),
        /// Life(S), Life(O), Item — school from the live record, S/O/Item from the classic target.</summary>
        public static IList<EditorFamily> ForTab(
            IEnumerable<EditorFamily> all, string school, TargetType classicTarget)
        {
            return all.Where(f =>
                    f.ClassicTarget == classicTarget &&
                    (classicTarget == TargetType.Item ||
                     string.Equals(f.School, school, StringComparison.OrdinalIgnoreCase)))
                .ToList();
        }

        /// <summary>Strip a trailing level word ("… I".."… VIII") from a live spell name to make
        /// a family display name.</summary>
        public static string BaseName(string liveName)
        {
            var n = (liveName ?? "").Trim();
            int sp = n.LastIndexOf(' ');
            if (sp > 0)
            {
                var tail = n.Substring(sp + 1);
                if (tail.Length <= 4 && tail.All(c => c == 'I' || c == 'V' || c == 'X'))
                    n = n.Substring(0, sp);
            }
            return n;
        }
    }
}
