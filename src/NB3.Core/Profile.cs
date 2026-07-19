using System;
using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace NB3.Core
{
    /// <summary>Reads and writes the buff-profile XML in the exact shape the original plugin's
    /// editor produced (see the recovered <c>sample-buff-profile.xml</c>). Round-tripping this
    /// format is the doc-08 §6.2 persistence gate: it lets a 2026 build load a player's real
    /// twenty-year-old profile unchanged.</summary>
    public static class ProfileXml
    {
        public static Profile Load(string path) => Parse(File.ReadAllText(path));

        public static Profile Parse(string xml)
        {
            var doc = XDocument.Parse(xml);
            var root = doc.Root;
            if (root == null || root.Name.LocalName != "Profile")
                throw new FormatException("Profile XML missing <Profile> root node.");

            var p = new Profile
            {
                Name = (string)root.Attribute("name") ?? "",
                Version = (string)root.Attribute("version") ?? "0.1.0.0"
            };

            foreach (var e in root.Elements())
            {
                switch (e.Name.LocalName)
                {
                    case "Equip":
                        p.Nodes.Add(new EquipNode
                        {
                            ItemName = (string)e.Attribute("itemname") ?? "",
                            EquipBy = (string)e.Attribute("equipby") ?? "name"
                        });
                        break;

                    case "Spell":
                        p.Nodes.Add(new SpellNode
                        {
                            SpellId = ParseId((string)e.Attribute("spellid")),
                            TargetType = ParseTarget((string)e.Attribute("targettype"))
                        });
                        break;

                    case "Spellgroup":
                        var g = new SpellGroupNode
                        {
                            TargetCover = ParseId((string)e.Attribute("targetcover"))
                        };
                        foreach (var s in e.Elements("Spell"))
                            g.SpellIds.Add(ParseId((string)s.Attribute("spellid")));
                        p.Nodes.Add(g);
                        break;
                }
            }
            return p;
        }

        /// <summary>Serialize back to the original attribute layout (including the
        /// <c>nodetype</c> attributes the editor wrote), so saved files stay compatible.</summary>
        public static string ToXml(Profile p)
        {
            var root = new XElement("Profile",
                new XAttribute("version", p.Version),
                new XAttribute("name", p.Name));

            foreach (var node in p.Nodes)
            {
                switch (node)
                {
                    case EquipNode en:
                        root.Add(new XElement("Equip",
                            new XAttribute("nodetype", "Equip"),
                            new XAttribute("itemname", en.ItemName ?? ""),
                            new XAttribute("equipby", en.EquipBy ?? "name")));
                        break;
                    case SpellNode sn:
                        root.Add(new XElement("Spell",
                            new XAttribute("nodetype", "Spell"),
                            new XAttribute("spellid", Hex(sn.SpellId)),
                            new XAttribute("targettype", sn.TargetType.ToString().ToLowerInvariant())));
                        break;
                    case SpellGroupNode gn:
                        var ge = new XElement("Spellgroup",
                            new XAttribute("nodetype", "Spellgroup"),
                            new XAttribute("targetcover", Hex(gn.TargetCover)),
                            new XAttribute("targettype", "cover"));
                        foreach (var id in gn.SpellIds)
                            ge.Add(new XElement("Spell",
                                new XAttribute("nodetype", "Spell"),
                                new XAttribute("spellid", Hex(id))));
                        root.Add(ge);
                        break;
                }
            }

            var doc = new XDocument(new XDeclaration("1.0", null, null), root);
            using (var sw = new Utf8StringWriter())
            {
                doc.Save(sw);
                return sw.ToString();
            }
        }

        private static string Hex(int id) => "0x" + id.ToString("X4");

        private static int ParseId(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            s = s.Trim();
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return int.Parse(s.Substring(2), NumberStyles.HexNumber);
            return int.Parse(s);
        }

        private static TargetType ParseTarget(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "other": return TargetType.Other;
                case "cover": return TargetType.Cover;
                case "item": return TargetType.Item;
                default: return TargetType.Self;
            }
        }

        private sealed class Utf8StringWriter : StringWriter
        {
            public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        }
    }
}
