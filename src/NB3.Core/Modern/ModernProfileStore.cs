using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NB3.Core.Modern
{
    /// <summary>Named-profile file management for <see cref="ModernProfile"/> — the model behind
    /// the editor view's profile chooser (<c>choiceGroup</c>) and its New / Clear / Revert /
    /// Copy / Delete / Save buttons, and behind the control view's <c>choiceLoadConfig</c>.
    /// One XML per profile in the NB3 data folder (the modern analogue of the original's
    /// <c>\Configs\*.xml</c> scan). Per-character settings files (<c>config_*.xml</c>) are
    /// excluded from listings. The Options view's "Permanently delete files in editor" maps to
    /// <see cref="Delete"/>'s <c>permanent</c> flag: off → move to a <c>_deleted</c> subfolder
    /// (the original renamed to <c>.deleted</c>), on → remove.</summary>
    public sealed class ModernProfileStore
    {
        private readonly string _dir;
        public ModernProfileStore(string directory)
        {
            _dir = directory;
            Directory.CreateDirectory(_dir);
        }

        public string RootDirectory => _dir;

        public string PathFor(string name) => Path.Combine(_dir, Sanitize(Canon(name)) + ".xml");

        public bool Exists(string name) => File.Exists(PathFor(name));

        /// <summary>Profile names, sorted, excluding per-character settings files.</summary>
        public IList<string> List()
        {
            var names = new List<string>();
            try
            {
                foreach (var f in Directory.EnumerateFiles(_dir, "*.xml"))
                {
                    var n = Path.GetFileNameWithoutExtension(f);
                    if (n.StartsWith("config_", StringComparison.OrdinalIgnoreCase)) continue;
                    names.Add(n);
                }
            }
            catch { }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }

        public ModernProfile Load(string name) => ModernProfile.Load(PathFor(name));

        public void Save(ModernProfile profile) =>
            File.WriteAllText(PathFor(profile.Name), profile.ToXml());

        /// <summary>Create a new empty profile (the editor's "New Profile"). Returns null if a
        /// profile with that name already exists or the name is invalid.</summary>
        public ModernProfile Create(string name)
        {
            name = Canon(name);
            if (!ValidName(name) || Exists(name)) return null;
            var p = new ModernProfile { Name = name };
            Save(p);
            return p;
        }

        /// <summary>Copy an existing profile under a new name (the editor's "Copy Profile").</summary>
        public bool Duplicate(string sourceName, string newName)
        {
            newName = Canon(newName);
            if (!ValidName(newName) || !Exists(sourceName) || Exists(newName)) return false;
            var p = Load(sourceName);
            p.Name = newName;
            Save(p);
            return true;
        }

        public bool Delete(string name, bool permanent)
        {
            if (!Exists(name)) return false;
            var path = PathFor(name);
            try
            {
                if (permanent) { File.Delete(path); return true; }
                var trash = Path.Combine(_dir, "_deleted");
                Directory.CreateDirectory(trash);
                var dest = Path.Combine(trash, Path.GetFileName(path));
                if (File.Exists(dest)) File.Delete(dest);
                File.Move(path, dest);
                return true;
            }
            catch { return false; }
        }

        /// <summary>The original's profile-name gate: not empty, no path-hostile characters
        /// (<c>\ / : * ? " &lt; &gt; |</c>).</summary>
        public static bool ValidName(string name)
        {
            name = Canon(name);
            if (string.IsNullOrWhiteSpace(name)) return false;
            foreach (var c in "\\/:*?\"<>|")
                if (name.IndexOf(c) >= 0) return false;
            return true;
        }

        /// <summary>Accept names with or without the .xml extension (the original's
        /// "/nbuff Profile Name[.xml]" convention).</summary>
        public static string Canon(string name)
        {
            name = (name ?? "").Trim();
            if (name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);
            return name;
        }

        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
    }
}
