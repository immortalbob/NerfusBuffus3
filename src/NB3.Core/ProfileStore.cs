using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace NB3.Core
{
    /// <summary>Manages the set of named buff profiles as .xml files in a folder — the model
    /// behind the editor's profile chooser (<c>choiceGroup</c>) and its New / Clear / Revert /
    /// Copy / delete actions. Faithful to how NB3 stored profiles (one XML per profile). The
    /// "Permanently delete files in editor" Option maps to <see cref="Delete"/>'s
    /// <c>permanent</c> flag: off → move to a trash subfolder, on → remove.</summary>
    public sealed class ProfileStore
    {
        private readonly string _dir;
        public ProfileStore(string directory)
        {
            _dir = directory;
            Directory.CreateDirectory(_dir);
        }

        private string PathFor(string name) => Path.Combine(_dir, Sanitize(name) + ".xml");

        public bool Exists(string name) => File.Exists(PathFor(name));

        public IEnumerable<string> List() =>
            Directory.EnumerateFiles(_dir, "*.xml")
                     .Select(Path.GetFileNameWithoutExtension)
                     .OrderBy(n => n, System.StringComparer.OrdinalIgnoreCase)
                     .ToList();

        public Profile Load(string name) => ProfileXml.Load(PathFor(name));

        public void Save(Profile profile)
        {
            File.WriteAllText(PathFor(profile.Name), ProfileXml.ToXml(profile));
        }

        /// <summary>Create a new empty profile with the given name (the editor's "New Profile").</summary>
        public Profile Create(string name)
        {
            var p = new Profile { Name = name };
            Save(p);
            return p;
        }

        /// <summary>Copy an existing profile under a new name (the editor's "Copy Profile").</summary>
        public bool Duplicate(string sourceName, string newName)
        {
            if (!Exists(sourceName) || Exists(newName)) return false;
            var p = Load(sourceName);
            p.Name = newName;
            Save(p);
            return true;
        }

        public bool Rename(string oldName, string newName)
        {
            if (!Exists(oldName) || Exists(newName)) return false;
            var p = Load(oldName);
            p.Name = newName;
            Save(p);
            RemoveFile(oldName, permanent: true);
            return true;
        }

        public bool Delete(string name, bool permanent)
        {
            if (!Exists(name)) return false;
            RemoveFile(name, permanent);
            return true;
        }

        private void RemoveFile(string name, bool permanent)
        {
            var path = PathFor(name);
            if (permanent) { File.Delete(path); return; }
            var trash = Path.Combine(_dir, "_deleted");
            Directory.CreateDirectory(trash);
            var dest = Path.Combine(trash, Sanitize(name) + ".xml");
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(path, dest);
        }

        private static string Sanitize(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars())
                name = name.Replace(c, '_');
            return name;
        }
    }
}
