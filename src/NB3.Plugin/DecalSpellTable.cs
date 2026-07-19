using System;
using System.Collections.Generic;
using System.Reflection;
using Decal.Adapter;
using NB3.Core.Modern;

namespace NB3.Plugin
{
    /// <summary>
    /// <see cref="ILiveSpellTable"/> over the live client's spell table (Decal
    /// <c>FileService.SpellTable</c>), with the shipped 2012 retail dump as the per-field
    /// fallback — the doc-16 §7.5 posture: live table authoritative, dump fills gaps.
    ///
    /// Shapes confirmed against the real assemblies (doc 18 §4 / vendor metadata dumps):
    ///  - FileService is reached by CAST (<c>CoreManager.FileService as
    ///    Decal.Filters.FileService</c>) — doc 13 §3;
    ///  - the Spell record carries <c>Family</c> (stacking group), <c>Difficulty</c>
    ///    (stacking power), <c>Mana</c>, <c>School</c> (Id/Name), <c>Duration</c>,
    ///    <c>IsUntargetted</c>, <c>IsFastWindup</c>, <c>ComponentIDs</c>;
    ///  - SpellTable : IdNameTable&lt;Spell&gt; — <c>Length</c> + indexer enumerate it exactly.
    ///  Fields are still read REFLECTIVELY so the same glue survives other SDK builds
    ///  (doc 13 §3); on this build every candidate name resolves on the first record.
    ///
    /// Target classification (doc 18 §4/§6 — the aura correction): the live record's
    /// <c>IsUntargetted == true</c> wins and means SELF. The modern weapon-buff "aura" lines
    /// (Blood Drinker Self I..VIII = 35/1612–1616/2096/4395, group 154; displayed
    /// "Aura of …" on EoR DATs) exist in the 2012 dump under these same ids but with a
    /// dump-computed <c>target=item</c> — mechanics of their era, wrong aim today. Banes are
    /// likewise untargetted self-casts (whole suit + shield, ACE-verified). The dump's target
    /// is used only when the live flag is unreadable; the <c>…Other</c> targeted lines and
    /// deliberate direct item casts classify from the dump/name as before.
    /// </summary>
    internal sealed class DecalSpellTable : ILiveSpellTable
    {
        // Fallback sweep bound for SDK builds where Length/indexer can't be resolved
        // (classic ids top out < 7000; doc 13 §3).
        private const int MaxProbeId = 8192;

        // Candidate property names per field (doc 13 §3; all confirmed on this build, doc 18 §4).
        private static readonly string[] CategoryNames    = { "Family", "Category" };
        private static readonly string[] DifficultyNames  = { "Difficulty", "Power", "Level" };
        private static readonly string[] SchoolNames      = { "School" };
        private static readonly string[] ManaNames        = { "Mana", "ManaCost", "BaseMana" };
        private static readonly string[] UntargettedNames = { "IsUntargetted", "IsUntargeted" };
        private static readonly string[] DurationNames    = { "Duration" };

        private readonly Dictionary<int, SpellInfo> _byId = new Dictionary<int, SpellInfo>();

        public DecalSpellTable(CoreManager core)
        {
            SpellCatalog fallback = LoadFallback();

            object spellTable = null;
            try
            {
                var fs = core.FileService as Decal.Filters.FileService; // the cast, doc 13 §3
                if (fs != null) spellTable = fs.SpellTable;
            }
            catch { /* Decal.FileService not loaded — dump-only mode below */ }

            if (spellTable == null)
            {
                foreach (var d in fallback.All) _byId[d.Id] = d;   // documented fallback posture
                return;
            }

            // Reflective per-field accessors, resolved once from the first live record.
            PropertyInfo pId = null, pName = null, pCat = null, pDiff = null,
                         pSchool = null, pMana = null, pUnt = null, pDur = null;
            bool resolved = false;

            foreach (object live in EnumerateLiveSpells(spellTable))
            {
                if (live == null) continue;
                if (!resolved)
                {
                    var t = live.GetType();
                    pId     = FirstProp(t, new[] { "Id" });
                    pName   = FirstProp(t, new[] { "Name" });
                    pCat    = FirstProp(t, CategoryNames);
                    pDiff   = FirstProp(t, DifficultyNames);
                    pSchool = FirstProp(t, SchoolNames);
                    pMana   = FirstProp(t, ManaNames);
                    pUnt    = FirstProp(t, UntargettedNames);
                    pDur    = FirstProp(t, DurationNames);
                    resolved = true;
                }

                int id = ReadInt(live, pId, 0);
                if (id == 0) continue;
                SpellInfo dump = fallback.ById(id);

                string name  = ReadString(live, pName, dump != null ? dump.Name : null);
                if (string.IsNullOrEmpty(name)) continue;
                int category = ReadInt(live, pCat, dump != null ? dump.Category : 0);
                if (category == 0) continue; // no stacking group -> not selectable

                _byId[id] = new SpellInfo(id, name,
                    category,
                    ReadInt(live, pDiff, dump != null ? dump.Level : 0),
                    ReadSchool(live, pSchool, dump),
                    ReadInt(live, pMana, dump != null ? dump.Mana : 0),
                    ClassifyTarget(live, pUnt, dump, name),
                    ReadInt(live, pDur, dump != null ? dump.Duration : 0));  // <0 = instantaneous (burst), not a buff
            }

            if (_byId.Count == 0)                                   // live walk yielded nothing
                foreach (var d in fallback.All) _byId[d.Id] = d;
        }

        public SpellInfo ById(int spellId) => _byId.TryGetValue(spellId, out var s) ? s : null;
        public IReadOnlyCollection<SpellInfo> All => _byId.Values;

        // ---- live enumeration --------------------------------------------------------------

        /// <summary>Exact enumeration via IdNameTable's Length + indexer (doc 18 §4); falls
        /// back to a bounded GetById probe when those members can't be resolved.</summary>
        private static IEnumerable<object> EnumerateLiveSpells(object spellTable)
        {
            var t = spellTable.GetType();
            PropertyInfo pLen = null, pItem = null;
            try
            {
                pLen  = t.GetProperty("Length", BindingFlags.Public | BindingFlags.Instance);
                pItem = t.GetProperty("Item", new[] { typeof(int) });
            }
            catch { }

            if (pLen != null && pItem != null)
            {
                int len = 0;
                try { len = Convert.ToInt32(pLen.GetValue(spellTable, null)); } catch { }
                for (int i = 0; i < len; i++)
                {
                    object rec = null;
                    try { rec = pItem.GetValue(spellTable, new object[] { i }); } catch { }
                    if (rec != null) yield return rec;
                }
                yield break;
            }

            MethodInfo getById = null;
            try { getById = t.GetMethod("GetById", new[] { typeof(int) }); } catch { }
            if (getById == null) yield break;
            for (int id = 1; id <= MaxProbeId; id++)
            {
                object rec = null;
                try { rec = getById.Invoke(spellTable, new object[] { id }); } catch { }
                if (rec != null) yield return rec;
            }
        }

        // ---- target classification (doc 18 §4/§6) -------------------------------------------

        private static SpellTarget ClassifyTarget(object live, PropertyInfo pUnt,
                                                  SpellInfo dump, string name)
        {
            // The whole rule lives in NB3.Core so it's unit-tested off-client: the live
            // IsUntargetted flag is authoritative both ways, the dump fills gaps, and the name
            // heuristic keys on the " Self"/" Other" tokens before the "Aura of" prefix so the
            // modern "Aura of X Other" weapon buffs aren't mis-filed as Self and cast on the player.
            bool? untargetted = ReadBool(live, pUnt);
            SpellTarget? dumpTarget = dump != null ? dump.Target : (SpellTarget?)null;
            return SpellTargetClassifier.Classify(untargetted, dumpTarget, name);
        }

        private static bool? ReadBool(object obj, PropertyInfo p)
        {
            if (obj == null || p == null) return null;
            try { var v = p.GetValue(obj, null); return v is bool b ? b : (bool?)null; }
            catch { return null; }
        }

        // ---- helpers ------------------------------------------------------------------------

        /// <summary>The offline fallback catalog: the authoritative end-of-retail spell table
        /// (file 16 §7.7 — correct `isUntargeted` targets + `Duration`), with mana costs overlaid
        /// from the 2012 dump (EoR carries no mana column). Live `FileService.SpellTable` remains
        /// authoritative when present; this fills any field the SDK build won't surface, and is the
        /// whole table in the rare dump-only mode.</summary>
        private static SpellCatalog LoadFallback()
        {
            try
            {
                var eor = SpellCatalog.Parse(SplitLines(PluginCore.ReadResource("NB3.Plugin.Resources.spell-table-eor.tsv")));
                SpellCatalog mana = null;
                try { mana = SpellCatalog.Parse(SplitLines(PluginCore.ReadResource("NB3.Plugin.Resources.spellcat-2012.tsv"))); }
                catch { }
                return eor.Count > 0 ? eor.WithManaFrom(mana) : mana ?? eor;
            }
            catch
            {
                // EoR resource missing/unreadable — fall back to the 2012 dump alone.
                try { return SpellCatalog.Parse(SplitLines(PluginCore.ReadResource("NB3.Plugin.Resources.spellcat-2012.tsv"))); }
                catch { return SpellCatalog.Parse(Array.Empty<string>()); }
            }
        }

        private static string[] SplitLines(string s) =>
            (s ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);

        private static PropertyInfo FirstProp(Type t, string[] names)
        {
            foreach (var n in names)
            {
                try
                {
                    var p = t.GetProperty(n, BindingFlags.Public | BindingFlags.Instance);
                    if (p != null && p.CanRead) return p;
                }
                catch { }
            }
            return null;
        }

        private static int ReadInt(object obj, PropertyInfo p, int fallback)
        {
            if (obj == null || p == null) return fallback;
            try { return Convert.ToInt32(p.GetValue(obj, null)); } catch { return fallback; }
        }

        private static string ReadString(object obj, PropertyInfo p, string fallback)
        {
            if (obj == null || p == null) return fallback;
            try { var v = p.GetValue(obj, null); return v != null ? v.ToString() : fallback; }
            catch { return fallback; }
        }

        /// <summary>School is the load-bearing field for the skill cap (school -> magic skill).
        /// The dump's `school` column is a clean string ("Creature"/"Life"/"Item"/…) and its ids
        /// match the retail lineage, so it's the reliable source; use it when the id is known.
        /// The LIVE record's `School` is a `SpellSchool` OBJECT ({Id, Name}), NOT a string — a
        /// plain ToString() yields the type name, which is exactly the bug that silently disabled
        /// the skill cap. So for a renumbered server (no dump id) read the object's `.Name`
        /// sub-property, never ToString().</summary>
        private static string ReadSchool(object live, PropertyInfo pSchool, SpellInfo dump)
        {
            if (dump != null && IsKnownSchool(dump.School)) return dump.School;   // reliable by id
            // Fallback: extract .Name from the live SpellSchool object (or accept a raw string).
            try
            {
                var sv = pSchool != null ? pSchool.GetValue(live, null) : null;
                if (sv is string str) { if (IsKnownSchool(str)) return str; }
                else if (sv != null)
                {
                    var np = sv.GetType().GetProperty("Name", BindingFlags.Public | BindingFlags.Instance);
                    var nm = np != null ? np.GetValue(sv, null) as string : null;
                    if (IsKnownSchool(nm)) return nm;
                }
            }
            catch { }
            return dump != null ? dump.School : "";
        }

        /// <summary>Does this string name one of the five magic schools (any casing / suffix like
        /// "Creature Enchantment")? Contains-based so the mapping in DecalGameState agrees.</summary>
        private static bool IsKnownSchool(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            s = s.ToLowerInvariant();
            return s.IndexOf("creature", StringComparison.Ordinal) >= 0
                || s.IndexOf("life", StringComparison.Ordinal) >= 0
                || s.IndexOf("item", StringComparison.Ordinal) >= 0
                || s.IndexOf("war", StringComparison.Ordinal) >= 0
                || s.IndexOf("void", StringComparison.Ordinal) >= 0;
        }
    }
}
