using System;
using System.Collections.Generic;
using MyClasses.MetaViewWrappers;
using NB3.Core;
using NB3.Core.Modern;

namespace NB3.Plugin
{
    /// <summary>
    /// The recovered "NB3 Editor" view (nb3-editor.xml) — the original's Profile Editor,
    /// rebuilt on the modern profile model. Layout, control names and workflow are the
    /// v1.52 originals: a profile chooser + New/Clear/Revert/Copy/Delete/Save, the profile
    /// content list on the left, and the five spell-pick tabs (Creature(S)/(O), Life(S)/(O),
    /// Item) plus Equip and Extras (includes) on the right.
    ///
    /// What changed underneath (MODERN_SPELL_MODEL): profiles store stacking CATEGORIES, not
    /// spell ids, and the cycle casts the best level the character knows — so the original's
    /// seven per-level icon columns collapse into one "add" column per family. Per-tab target
    /// modes (By Name / By GUID / Current Target, and the Item tab's cover-mask checkboxes)
    /// are honoured and stored per entry. Unequip/Restore are retired: on modern servers
    /// banes are self-cast whole-suit, which is the only thing they existed for.
    ///
    /// UI discipline: doc 14 §4–§6 throughout — per-name populate flags, lazy realization
    /// retries, poll-diffed combo/checkbox state, and a chat acknowledgment for every button.
    /// </summary>
    [MVView("NB3.Plugin.Resources.nb3-editor.xml")]
    public partial class PluginCore
    {
        // ---- editor state ----------------------------------------------------------------
        private ModernProfile _edProfile;         // the profile being edited (in memory)
        private string _edLoadedName;             // file name it came from ("" = new/unsaved)
        private bool _edDirty;
        private bool _edGroupListStale;           // content list needs a rebuild on next poll
        private int _edLastGroupSel;              // choiceGroup poll-diff
        private IList<EditorFamily> _edCatalog;   // all pick-list families (built post-login)
        private readonly Dictionary<string, IList<EditorFamily>> _edTabRows =
            new Dictionary<string, IList<EditorFamily>>(StringComparer.OrdinalIgnoreCase);
        // Row map for listSpellsInGroup: kind 'I'nclude / 'E'quip / 'B'uff + index in its list.
        private readonly List<KeyValuePair<char, int>> _edRowMap = new List<KeyValuePair<char, int>>();
        // Last-seen checkbox state for the mutual-exclusion poll (name -> checked).
        private readonly Dictionary<string, bool> _edCbState = new Dictionary<string, bool>();

        private void ResetEditorViewState()
        {
            _edGroupListStale = _edProfile != null;
            _edLastGroupSel = 0;
            _edCatalog = null;
            _edTabRows.Clear();
            _edRowMap.Clear();
            _edCbState.Clear();
        }

        private void ToggleEditorView()
        {
            if (ViewByTitle(EditorTitle) == null)
            { Say("the Editor view isn't available (see /nbdiag) - /nbnew + editing the XML in %AppData%\\NerfusBuffus3 still works."); return; }
            bool show = !IsViewVisible(EditorTitle);
            ShowManagedView(EditorTitle, show);        // show/hide + add/drop the Virindi bar entry
            if (show)
            {
                _edGroupListStale = true;
                Say(_edProfile == null
                    ? "Editor open. Select a profile to edit, or type a name and click New Profile."
                    : $"Editor open. Editing '{CurrentEditName()}'.");
            }
        }

        private string CurrentEditName() =>
            _edProfile == null ? "(none)" : (_edProfile.Name ?? _edLoadedName ?? "(unnamed)");

        // ---- the ~4 Hz poll --------------------------------------------------------------

        private void PollEditorView()
        {
            if (!IsViewVisible(EditorTitle)) return;
            EnsureEditorCombos();
            EnsureEditorSpellLists();
            if (_edGroupListStale) RefreshGroupList();
            PollGroupSelection();
            PollModeExclusivity();
        }

        private void EnsureEditorCombos()
        {
            EnsureProfileComboIn("choiceGroup", "(Select a profile to edit)");
            EnsureProfileComboIn("choiceInclude", "(Select a profile to include)");
        }

        private void EnsureProfileComboIn(string name, string header)
        {
            if (_populated.Contains(name)) return;
            var combo = CtlIn<ICombo>(EditorTitle, name);
            if (combo == null) return;                  // not realized yet — retry next poll
            try
            {
                combo.Clear();
                combo.Add(header);
                foreach (var f in ListProfiles()) combo.Add(f);
                combo.Selected = 0;
                if (name == "choiceGroup") _edLastGroupSel = 0;
                _populated.Add(name);
            }
            catch { }
        }

        // ---- spell-pick tabs ---------------------------------------------------------------

        /// <summary>tab list name -> (school filter, classic target) per the original's tabs.</summary>
        private static readonly string[,] EditorTabs =
        {
            { "listCreatureSelf",  "Creature", "Self"  },
            { "listCreatureOther", "Creature", "Other" },
            { "listLifeSelf",      "Life",     "Self"  },
            { "listLifeOther",     "Life",     "Other" },
            { "listItem",          "",         "Item"  },
        };

        // The editor's family list is resolved against the embedded 2012 retail dump, NOT the
        // live client table. Reason (the era break, docs 08/16 §7 + the NB3 case study): NB3's
        // classic spell ids are 2003 Dark-Majesty ids; the 2012 retail dump matches them
        // EXACTLY (id 2 = Strength Self I, 1161 = Heal Self VI), while a modern/ACE server's
        // live table may renumber the ids — so resolving the classic table against the LIVE
        // table finds almost nothing and the tabs come up empty. Stacking CATEGORIES are
        // era-stable (MODERN_SPELL_MODEL), and the profile stores the category, so a family
        // picked here still resolves to the right live spell at cast time (the planner asks the
        // live table for the best spell in that category). One source (the dump) that reliably
        // matches the classic ids is exactly what the editor's family→category map needs.
        private ILiveSpellTable _dumpCatalog;

        /// <summary>Build (or REBUILD-while-empty) the family catalog — the family → era-stable
        /// stacking category map used by BOTH the editor pick lists and <c>/nbgen</c>. A once-only
        /// guard would latch an empty catalog forever if a resource load hiccuped; rebuilding
        /// while empty is cheap and self-heals. Never null.</summary>
        private IList<EditorFamily> EnsureFamilyCatalog()
        {
            if (_edCatalog == null || _edCatalog.Count == 0)
            {
                try
                {
                    if (_classicTable == null)
                        _classicTable = SpellTable.Parse(ReadResource("NB3.Plugin.Resources.nb3-spells.xml"));
                    if (_dumpCatalog == null)
                    {
                        var tsv = ReadResource("NB3.Plugin.Resources.spellcat-2012.tsv");
                        _dumpCatalog = SpellCatalog.Parse(tsv.Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries));
                    }
                    var unresolved = new List<string>();
                    _edCatalog = EditorCatalog.Build(_classicTable, _dumpCatalog, unresolved);
                }
                catch (Exception ex) { LogException(ex); _edCatalog = new List<EditorFamily>(); }
            }
            return _edCatalog;
        }

        private void EnsureEditorSpellLists()
        {
            EnsureFamilyCatalog();
            if (_edCatalog.Count == 0) return;          // nothing to populate yet — retry next poll

            for (int i = 0; i < EditorTabs.GetLength(0); i++)
            {
                var listName = EditorTabs[i, 0];
                if (_populated.Contains(listName)) continue;
                var list = CtlIn<IList>(EditorTitle, listName);
                if (list == null) continue;             // tab not shown yet — lazy (doc 14 §4)
                try
                {
                    var target = (TargetType)Enum.Parse(typeof(TargetType), EditorTabs[i, 2]);
                    var rows = EditorCatalog.ForTab(_edCatalog, EditorTabs[i, 1], target);
                    list.Clear();
                    foreach (var fam in rows)
                    {
                        var r = list.AddRow();
                        r[0][0] = "+";
                        r[1][0] = fam.DisplayName;
                    }
                    _edTabRows[listName] = rows;
                    // Mark populated only once rows actually landed — an empty result means the
                    // control isn't really ready (or the catalog raced), so retry next poll
                    // rather than latching a blank tab (doc 14 §4/§6.1).
                    if (rows.Count > 0) _populated.Add(listName);
                }
                catch { }
            }
        }

        // ---- profile chooser -----------------------------------------------------------------

        private void PollGroupSelection()
        {
            var combo = CtlIn<ICombo>(EditorTitle, "choiceGroup");
            if (combo == null) return;
            int sel;
            try { sel = combo.Selected; } catch { return; }
            if (sel == _edLastGroupSel) return;
            _edLastGroupSel = sel;
            if (sel <= 0) return;
            string name = null;
            try { name = combo.Text[sel]; } catch { }
            if (string.IsNullOrEmpty(name)) return;
            LoadEditorProfile(name);
        }

        private void LoadEditorProfile(string name)
        {
            try
            {
                if (!Store().Exists(name)) { Say($"Profile not found: {name}"); return; }
                _edProfile = Store().Load(name);
                _edLoadedName = name;
                _edDirty = false;
                _edGroupListStale = true;
                Say($"Editing '{name}': {_edProfile.Buffs.Count} buff(s), {_edProfile.EquipItems.Count} equip(s), {_edProfile.Includes.Count} include(s).");
            }
            catch (Exception ex)
            {
                Say($"ERROR: Invalid Profile: '{name}'");
                LogException(ex);
            }
        }

        // ---- the profile content list ---------------------------------------------------------

        /// <summary>Rebuild listSpellsInGroup from the in-memory profile. Columns:
        /// [X] delete, [^] up, [v] down, description. Order: includes, equips, buffs.</summary>
        private void RefreshGroupList()
        {
            var list = CtlIn<IList>(EditorTitle, "listSpellsInGroup");
            if (list == null) return;                   // not realized yet — poll retries
            try
            {
                list.Clear();
                _edRowMap.Clear();
                if (_edProfile == null) { _edGroupListStale = false; return; }

                for (int i = 0; i < _edProfile.Includes.Count; i++)
                    AddGroupRow(list, 'I', i, $"Include Profile: '{_edProfile.Includes[i]}'");
                for (int i = 0; i < _edProfile.EquipItems.Count; i++)
                    AddGroupRow(list, 'E', i, $"Equip '{_edProfile.EquipItems[i]}'");
                for (int i = 0; i < _edProfile.Buffs.Count; i++)
                    AddGroupRow(list, 'B', i, DescribeEntry(_edProfile.Buffs[i]));
                _edGroupListStale = false;
            }
            catch { /* keep stale; retry next poll */ }
        }

        private void AddGroupRow(IList list, char kind, int index, string text)
        {
            var r = list.AddRow();
            r[0][0] = "X";
            r[1][0] = "^";
            r[2][0] = "v";
            r[3][0] = text;
            _edRowMap.Add(new KeyValuePair<char, int>(kind, index));
        }

        private static string DescribeEntry(ModernBuffEntry b)
        {
            var name = string.IsNullOrEmpty(b.DisplayName) ? $"category {b.Category}" : b.DisplayName;
            switch (b.Target)
            {
                case SpellTarget.Other:
                    if (b.TargetGuid != 0) return $"Cast '{name}' on GUID 0x{b.TargetGuid:X8}";
                    if (!string.IsNullOrEmpty(b.TargetName)) return $"Cast '{name}' on '{b.TargetName}'";
                    return $"Cast '{name}' on your target";
                case SpellTarget.Item:
                    if (b.ItemGuid != 0) return $"Cast '{name}' on item 0x{b.ItemGuid:X8}";
                    if (!string.IsNullOrEmpty(b.ItemName)) return $"Cast '{name}' on '{b.ItemName}'";
                    if (b.CoverMask != 0) return $"Cast '{name}' by cover mask 0x{b.CoverMask:X8}";
                    return $"Cast '{name}' on all worn items";
                default:
                    return $"Cast '{name}' on yourself";
            }
        }

        [MVControlEvent("listSpellsInGroup", "Click")]
        private void EdGroupListClick(object sender, int row, int col)
        {
            try
            {
                if (_edProfile == null || row < 0 || row >= _edRowMap.Count) return;
                var entry = _edRowMap[row];
                switch (col)
                {
                    case 0: EdDeleteEntry(entry.Key, entry.Value); break;
                    case 1: EdMoveEntry(entry.Key, entry.Value, -1); break;
                    case 2: EdMoveEntry(entry.Key, entry.Value, +1); break;
                    default:
                        string text = "";
                        try { text = Convert.ToString(CtlIn<IList>(EditorTitle, "listSpellsInGroup")[row][3][0]); } catch { }
                        Say(text.Length > 0 ? text : "(row)");
                        break;
                }
            }
            catch (Exception ex) { LogException(ex); }
        }

        private void EdDeleteEntry(char kind, int index)
        {
            string what = null;
            if (kind == 'I' && index < _edProfile.Includes.Count)
            { what = $"Include '{_edProfile.Includes[index]}'"; _edProfile.Includes.RemoveAt(index); }
            else if (kind == 'E' && index < _edProfile.EquipItems.Count)
            { what = $"Equip '{_edProfile.EquipItems[index]}'"; _edProfile.EquipItems.RemoveAt(index); }
            else if (kind == 'B' && index < _edProfile.Buffs.Count)
            { what = $"'{_edProfile.Buffs[index].DisplayName}'"; _edProfile.Buffs.RemoveAt(index); }
            if (what == null) { Say("ERROR: Could not delete from list"); return; }
            _edDirty = true;
            _edGroupListStale = true;
            Say($"removed {what} ({_edProfile.Buffs.Count} buff(s) left). Save writes it.");
        }

        private void EdMoveEntry(char kind, int index, int delta)
        {
            bool moved = false;
            if (kind == 'I') moved = SwapAt(_edProfile.Includes, index, index + delta);
            else if (kind == 'E') moved = SwapAt(_edProfile.EquipItems, index, index + delta);
            else if (kind == 'B') moved = SwapAt(_edProfile.Buffs, index, index + delta);
            if (!moved) return;                          // edge of its section: silent no-op
            _edDirty = true;
            _edGroupListStale = true;
        }

        private static bool SwapAt<T>(List<T> list, int a, int b)
        {
            if (a < 0 || b < 0 || a >= list.Count || b >= list.Count || a == b) return false;
            var t = list[a]; list[a] = list[b]; list[b] = t;
            return true;
        }

        // ---- adding buffs from the tabs --------------------------------------------------------

        [MVControlEvent("listCreatureSelf", "Click")]
        private void EdCreatureSelfClick(object sender, int row, int col)
        { try { AddFamilyFromTab("listCreatureSelf", row); } catch (Exception ex) { LogException(ex); } }

        [MVControlEvent("listCreatureOther", "Click")]
        private void EdCreatureOtherClick(object sender, int row, int col)
        { try { AddFamilyFromTab("listCreatureOther", row); } catch (Exception ex) { LogException(ex); } }

        [MVControlEvent("listLifeSelf", "Click")]
        private void EdLifeSelfClick(object sender, int row, int col)
        { try { AddFamilyFromTab("listLifeSelf", row); } catch (Exception ex) { LogException(ex); } }

        [MVControlEvent("listLifeOther", "Click")]
        private void EdLifeOtherClick(object sender, int row, int col)
        { try { AddFamilyFromTab("listLifeOther", row); } catch (Exception ex) { LogException(ex); } }

        [MVControlEvent("listItem", "Click")]
        private void EdItemListClick(object sender, int row, int col)
        { try { AddFamilyFromTab("listItem", row); } catch (Exception ex) { LogException(ex); } }

        private void AddFamilyFromTab(string listName, int row)
        {
            if (_edProfile == null)
            { Say("Please create a new profile or clear the current one! (select or New Profile first)"); return; }
            IList<EditorFamily> rows;
            if (!_edTabRows.TryGetValue(listName, out rows) || row < 0 || row >= rows.Count) return;
            var fam = rows[row];

            var entry = new ModernBuffEntry
            {
                Category = fam.Category,
                DisplayName = fam.DisplayName,
                Target = fam.LiveTarget,
                MaxLevel = 0,                            // cast the best level known (era norm)
            };
            string note = "";

            bool otherTab = listName == "listCreatureOther" || listName == "listLifeOther";
            if (otherTab && entry.Target == SpellTarget.Other)
            {
                var prefix = listName == "listCreatureOther" ? "Creature" : "Life";
                if (Checked($"cb{prefix}OtherTargetNamed"))
                {
                    entry.TargetName = TextOf($"edit{prefix}OtherName");
                    if (IsPlaceholder(entry.TargetName)) { Say("enter the target player's name first (or use the ? wizard)."); return; }
                    note = $" on '{entry.TargetName}'";
                }
                else if (Checked($"cb{prefix}OtherTargetGUID"))
                {
                    entry.TargetGuid = ParseGuid(TextOf($"edit{prefix}OtherGUID"));
                    if (entry.TargetGuid == 0) { Say("enter a target GUID first (or use the ? wizard)."); return; }
                    note = $" on 0x{entry.TargetGuid:X8}";
                }
                else note = " on your current target at launch";
            }
            else if (listName == "listItem")
            {
                // The Item tab has TWO modern meanings (docs 16 §7 / MODERN_SPELL_MODEL + the
                // NB3 case study). A DIRECT target mode ticked -> the legacy per-item cast
                // (Target=Item), kept for servers/data that still carry item enchants and for
                // deliberate casts on a specific piece. NOTHING ticked -> the modern default:
                // banes and weapon-buff auras are self-cast WHOLE-SUIT now, so the family
                // lands as an ordinary Self buff (which is also what the era-stable category
                // resolves to on a live modern table).
                if (Checked("cbItemTargetNamed"))
                {
                    entry.Target = SpellTarget.Item;
                    entry.ItemName = TextOf("editItemName");
                    if (IsPlaceholder(entry.ItemName)) { Say("enter the target item's name first (or use the ? wizard)."); return; }
                    note = $" directly on '{entry.ItemName}'";
                }
                else if (Checked("cbItemTargetGUID"))
                {
                    entry.Target = SpellTarget.Item;
                    entry.ItemGuid = ParseGuid(TextOf("editItemGUID"));
                    if (entry.ItemGuid == 0) { Say("enter an item GUID first (or use the ? wizard)."); return; }
                    note = $" directly on 0x{entry.ItemGuid:X8}";
                }
                else if (Checked("cbItemTargetCover"))
                {
                    entry.Target = SpellTarget.Item;
                    entry.CoverMask = unchecked((int)ReadItemCoverMask());
                    if (entry.CoverMask == 0) { Say("tick at least one cover checkbox first."); return; }
                    note = $" directly by cover 0x{entry.CoverMask:X8}";
                }
                else if (Checked("cbItemTargetWeapon") || Checked("cbItemTargetShield") || Checked("cbItemTargetCurrent"))
                {
                    // Original semantics: "your target's weapon/shield" / "your target" —
                    // resolved to a concrete GUID when you add it (echoed so it's auditable).
                    entry.Target = SpellTarget.Item;
                    int target = _state.SelectedTargetId;
                    if (target == 0) { Say("select a target first."); return; }
                    int guid = Checked("cbItemTargetWeapon") ? _state.WieldedWeapon(target)
                             : Checked("cbItemTargetShield") ? _state.WieldedShield(target)
                             : target;
                    if (guid == 0) { Say("couldn't resolve the target's weapon/shield."); return; }
                    entry.ItemGuid = guid;
                    note = $" directly on 0x{guid:X8} (resolved now)";
                }
                else
                {
                    entry.Target = SpellTarget.Self;     // modern: self-cast whole-suit
                    note = " (self-cast whole-suit on this server)";
                }
            }

            _edProfile.Buffs.Add(entry);
            _edDirty = true;
            _edGroupListStale = true;
            Say($"added '{fam.DisplayName}'{note} - {_edProfile.Buffs.Count} buff(s). Save writes it.");
        }

        /// <summary>OR together the eleven recovered cover checkboxes using the disassembled
        /// per-checkbox masks (CoverageCheckboxes — docs/COVER_MASK_RECOVERY.md).</summary>
        private uint ReadItemCoverMask()
        {
            uint mask = 0;
            if (Checked("cbICoverCoat")) mask |= CoverageCheckboxes.Coat;
            if (Checked("cbICoverLegs")) mask |= CoverageCheckboxes.Legs;
            if (Checked("cbICoverGirth")) mask |= CoverageCheckboxes.Girth;
            if (Checked("cbICoverHands")) mask |= CoverageCheckboxes.Hands;
            if (Checked("cbICoverHead")) mask |= CoverageCheckboxes.Head;
            if (Checked("cbICoverFeet")) mask |= CoverageCheckboxes.Feet;
            if (Checked("cbICoverPants")) mask |= CoverageCheckboxes.Pants;
            if (Checked("cbICoverShirt")) mask |= CoverageCheckboxes.Shirt;
            if (Checked("cbICoverWeapon")) mask |= CoverageCheckboxes.Weapon;
            if (Checked("cbICoverShield")) mask |= CoverageCheckboxes.Shield;
            if (Checked("cbICoverWand")) mask |= CoverageCheckboxes.Wand;
            return mask;
        }

        // ---- profile CRUD buttons --------------------------------------------------------------

        [MVControlEvent("pbNewGroup", "Click")]
        private void PbEdNew(object sender, MVControlEventArgs e)
        {
            try
            {
                var name = ModernProfileStore.Canon(TextOf("editNewGroupName"));
                if (!ModernProfileStore.ValidName(name))
                { Say("Profile name may not be empty or contain any of: \\ / : * ? \" < > |"); return; }
                var p = Store().Create(name);
                if (p == null) { Say("A file with the name you've chosen already exists!"); return; }
                _edProfile = p;
                _edLoadedName = name;
                _edDirty = false;
                _edGroupListStale = true;
                RefreshProfileLists(false);
                Say($"Created and now editing '{name}'. Add spells from the tabs on the right.");
            }
            catch (Exception ex) { LogException(ex); }
        }

        [MVControlEvent("pbClearGroup", "Click")]
        private void PbEdClear(object sender, MVControlEventArgs e)
        {
            try
            {
                if (_edProfile == null) { Say("You must have a profile selected!"); return; }
                _edProfile.Buffs.Clear();
                _edProfile.EquipItems.Clear();
                _edProfile.Includes.Clear();
                _edDirty = true;
                _edGroupListStale = true;
                Say($"Cleared '{CurrentEditName()}' (in memory - Save writes it).");
            }
            catch (Exception ex) { LogException(ex); }
        }

        [MVControlEvent("pbResetGroup", "Click")]
        private void PbEdRevert(object sender, MVControlEventArgs e)
        {
            try
            {
                if (_edProfile == null || string.IsNullOrEmpty(_edLoadedName))
                { Say("You must have a saved profile selected!"); return; }
                LoadEditorProfile(_edLoadedName);
                Say($"Reverted '{_edLoadedName}' to the saved file.");
            }
            catch (Exception ex) { LogException(ex); }
        }

        [MVControlEvent("pbDuplicate", "Click")]
        private void PbEdCopy(object sender, MVControlEventArgs e)
        {
            try
            {
                if (_edProfile == null || string.IsNullOrEmpty(_edLoadedName))
                { Say("Cannot copy an invalid or non-existent profile!"); return; }
                var newName = ModernProfileStore.Canon(TextOf("editNewGroupName"));
                if (!ModernProfileStore.ValidName(newName))
                { Say("type the copy's name into 'New profile name' first."); return; }
                if (!Store().Duplicate(_edLoadedName, newName))
                { Say("Unable to duplicate profile! (name exists or source missing)"); return; }
                RefreshProfileLists(false);
                Say($"Copied '{_edLoadedName}' to '{newName}'.");
            }
            catch (Exception ex) { LogException(ex); }
        }

        [MVControlEvent("pbDeleteGroup", "Click")]
        private void PbEdDelete(object sender, MVControlEventArgs e)
        {
            try
            {
                if (_edProfile == null || string.IsNullOrEmpty(_edLoadedName))
                { Say("No profile to delete!"); return; }
                bool perma = _settings != null && _settings.EditorPermaDelete;
                if (!Store().Delete(_edLoadedName, perma)) { Say("delete failed."); return; }
                Say($"Deleted profile: {_edLoadedName}" + (perma ? " (permanently)." : " (moved to _deleted)."));
                _edProfile = null;
                _edLoadedName = null;
                _edDirty = false;
                _edGroupListStale = true;
                RefreshProfileLists(false);
            }
            catch (Exception ex) { LogException(ex); }
        }

        [MVControlEvent("pbSaveGroup", "Click")]
        private void PbEdSave(object sender, MVControlEventArgs e)
        {
            try
            {
                if (_edProfile == null) { Say("You must have a profile selected!"); return; }
                if (string.IsNullOrEmpty(_edProfile.Name)) _edProfile.Name = _edLoadedName ?? "unnamed";
                Store().Save(_edProfile);
                _edLoadedName = _edProfile.Name;
                _edDirty = false;
                RefreshProfileLists(false);
                Say($"Profile saved as: {_edProfile.Name}");
            }
            catch (Exception ex) { Say("Unable to save the profile"); LogException(ex); }
        }

        [MVControlEvent("pbEdDismiss", "Click")]
        private void PbEdDismiss(object sender, MVControlEventArgs e)
        {
            try
            {
                ShowManagedView(EditorTitle, false);   // hide + drop the Virindi bar entry
                Say(_edDirty ? "Editor closed - unsaved changes stay in memory until Save (or a plugin reload)." : "Editor closed.");
            }
            catch (Exception ex) { LogException(ex); }
        }

        // ---- Equip tab -------------------------------------------------------------------------

        [MVControlEvent("pbEqAdd", "Click")]
        private void PbEdEquipAdd(object sender, MVControlEventArgs e)
        {
            try
            {
                if (_edProfile == null) { Say("You must have a profile selected!"); return; }
                string name = null;
                if (Checked("cbEqGUID"))
                {
                    int guid = ParseGuid(TextOf("editEqGUID"));
                    if (guid == 0) { Say("enter an item GUID (or use the ? wizard)."); return; }
                    try { name = Decal.Adapter.CoreManager.Current.WorldFilter[guid]?.Name; } catch { }
                    if (string.IsNullOrEmpty(name)) { Say($"0x{guid:X8} isn't in range - equips are stored by NAME; use By Name instead."); return; }
                }
                else
                {
                    name = TextOf("editEqName");
                    if (IsPlaceholder(name)) { Say("enter the item's name first (or use the ? wizard)."); return; }
                }
                _edProfile.EquipItems.Add(name);
                _edDirty = true;
                _edGroupListStale = true;
                Say($"added Equip '{name}' ({_edProfile.EquipItems.Count} equip(s)). Save writes it.");
            }
            catch (Exception ex) { LogException(ex); }
        }

        [MVControlEvent("pbUnEqAdd", "Click")]
        private void PbEdUnequipAdd(object sender, MVControlEventArgs e)
        {
            try { Say("Unequip steps are retired: on modern servers banes are self-cast whole-suit, so nothing needs to come off. (Not supported in this build.)"); }
            catch (Exception ex) { LogException(ex); }
        }

        [MVControlEvent("pbRestoreAdd", "Click")]
        private void PbEdRestoreAdd(object sender, MVControlEventArgs e)
        {
            try { Say("Restore-equipment steps are retired along with Unequip (banes are self-cast whole-suit now)."); }
            catch (Exception ex) { LogException(ex); }
        }

        // ---- Extras tab (includes) ------------------------------------------------------------

        [MVControlEvent("pbAddInclude", "Click")]
        private void PbEdAddInclude(object sender, MVControlEventArgs e)
        {
            try
            {
                var combo = CtlIn<ICombo>(EditorTitle, "choiceInclude");
                if (combo == null || combo.Selected <= 0)
                { Say("select a profile to include in the dropdown first."); return; }
                string name = null;
                try { name = combo.Text[combo.Selected]; } catch { }
                AddIncludeToEditedProfile(name);
            }
            catch (Exception ex) { LogException(ex); }
        }

        /// <summary>/nbinclude ProfileName[.xml] — the original chat command; editor must be open.</summary>
        private void AddIncludeFromChat(string arg)
        {
            if (!IsViewVisible(EditorTitle)) { Say("Command only available while editor is opened."); return; }
            if (string.IsNullOrEmpty(arg)) { Say("useage: /nbinclude ProfileName[.xml]"); return; }
            AddIncludeToEditedProfile(arg);
        }

        private void AddIncludeToEditedProfile(string name)
        {
            if (_edProfile == null) { Say("You must have a profile selected!"); return; }
            name = ModernProfileStore.Canon(name);
            if (string.IsNullOrEmpty(name)) return;
            if (string.Equals(name, _edProfile.Name, StringComparison.OrdinalIgnoreCase))
            { Say("Inclusion of this profile will result in infinite recursion!"); return; }
            if (!Store().Exists(name)) { Say($"Profile not found: {name}"); return; }
            foreach (var inc in _edProfile.Includes)
                if (string.Equals(inc, name, StringComparison.OrdinalIgnoreCase))
                { Say($"ERROR: '{name}' is already in the Profile / Includes list, ignoring duplicate entry."); return; }
            _edProfile.Includes.Add(name);
            _edDirty = true;
            _edGroupListStale = true;
            Say($"added Include '{name}'. (Deeper recursion is caught at launch.) Save writes it.");
        }

        // ---- the ? wizards (fill an edit from the current selection) ---------------------------

        [MVControlEvent("pbCreatureOtherWizNamed", "Click")]
        private void WizCreatureName(object s, MVControlEventArgs e) { WizardFill("editCreatureOtherName", false); }
        [MVControlEvent("pbCreatureOtherWizGUID", "Click")]
        private void WizCreatureGuid(object s, MVControlEventArgs e) { WizardFill("editCreatureOtherGUID", true); }
        [MVControlEvent("pbLifeOtherWizNamed", "Click")]
        private void WizLifeName(object s, MVControlEventArgs e) { WizardFill("editLifeOtherName", false); }
        [MVControlEvent("pbLifeOtherWizGUID", "Click")]
        private void WizLifeGuid(object s, MVControlEventArgs e) { WizardFill("editLifeOtherGUID", true); }
        [MVControlEvent("pbItemWizNamed", "Click")]
        private void WizItemName(object s, MVControlEventArgs e) { WizardFill("editItemName", false); }
        [MVControlEvent("pbItemWizGUID", "Click")]
        private void WizItemGuid(object s, MVControlEventArgs e) { WizardFill("editItemGUID", true); }
        [MVControlEvent("pbEqWizNamed", "Click")]
        private void WizEqName(object s, MVControlEventArgs e) { WizardFill("editEqName", false); }
        [MVControlEvent("pbEqWizGUID", "Click")]
        private void WizEqGuid(object s, MVControlEventArgs e) { WizardFill("editEqGUID", true); }
        [MVControlEvent("pbUnEqWizNamed", "Click")]
        private void WizUnEqName(object s, MVControlEventArgs e) { WizardFill("editUnEqName", false); }
        [MVControlEvent("pbUnEqWizGUID", "Click")]
        private void WizUnEqGuid(object s, MVControlEventArgs e) { WizardFill("editUnEqGUID", true); }

        /// <summary>Fill an edit box from the current in-game selection (the original's "?"
        /// buttons). Name mode writes the object's name, GUID mode its 0x-hex id.</summary>
        private void WizardFill(string editName, bool guidMode)
        {
            try
            {
                int sel = _state != null ? _state.SelectedTargetId : 0;
                if (sel == 0) { Say("select something in-game first, then click ?"); return; }
                string value;
                if (guidMode) value = $"0x{sel:X8}";
                else
                {
                    string n = null;
                    try { n = Decal.Adapter.CoreManager.Current.WorldFilter[sel]?.Name; } catch { }
                    if (string.IsNullOrEmpty(n)) { Say("couldn't read the selection's name."); return; }
                    value = n;
                }
                var box = CtlIn<ITextBox>(EditorTitle, editName);
                if (box == null) { Say("that edit box isn't on-screen yet (open its tab)."); return; }
                box.Text = value;
                Say($"{editName} = {value}");
            }
            catch (Exception ex) { LogException(ex); }
        }

        [MVControlEvent("pbItemAddSpells", "Click")]
        private void PbEdItemAddSpells(object sender, MVControlEventArgs e)
        {
            try { Say("click a family in the list above to add it - it uses the target mode ticked here (name / GUID / cover / target)."); }
            catch (Exception ex) { LogException(ex); }
        }

        // ---- checkbox mutual exclusion (poll-diffed; Change events are advisory) ---------------

        private static readonly string[][] ExclusiveGroups =
        {
            new[] { "cbCreatureOtherTargetNamed", "cbCreatureOtherTargetGUID", "cbCreatureOtherTargetCurrent" },
            new[] { "cbLifeOtherTargetNamed", "cbLifeOtherTargetGUID", "cbLifeOtherTargetCurrent" },
            new[] { "cbItemTargetNamed", "cbItemTargetGUID", "cbItemTargetCurrent", "cbItemTargetWeapon", "cbItemTargetShield", "cbItemTargetCover" },
            new[] { "cbEqNamed", "cbEqGUID" },
            new[] { "cbUnEqNamed", "cbUnEqGUID", "cbUnEqCover" },
        };

        /// <summary>Enforce the original's radio-like behaviour: ticking one target-mode box
        /// clears its group siblings. Detection is poll-diff (doc 14 §5): the box that CHANGED
        /// to checked since the last poll wins.</summary>
        private void PollModeExclusivity()
        {
            foreach (var group in ExclusiveGroups)
            {
                string newlyChecked = null;
                foreach (var name in group)
                {
                    var cb = CtlIn<ICheckBox>(EditorTitle, name);
                    if (cb == null) continue;
                    bool now;
                    try { now = cb.Checked; } catch { continue; }
                    bool before;
                    if (_edCbState.TryGetValue(name, out before) && !before && now)
                        newlyChecked = name;
                    _edCbState[name] = now;
                }
                if (newlyChecked == null) continue;
                foreach (var name in group)
                {
                    if (name == newlyChecked) continue;
                    var cb = CtlIn<ICheckBox>(EditorTitle, name);
                    if (cb == null) continue;
                    try { if (cb.Checked) { cb.Checked = false; _edCbState[name] = false; } } catch { }
                }
            }
        }

        // ---- small helpers ---------------------------------------------------------------------

        private bool Checked(string name)
        {
            var cb = CtlIn<ICheckBox>(EditorTitle, name);
            if (cb == null) return false;
            try { return cb.Checked; } catch { return false; }
        }

        private string TextOf(string name)
        {
            var box = CtlIn<ITextBox>(EditorTitle, name);
            if (box == null) return "";
            try { return (box.Text ?? "").Trim(); } catch { return ""; }
        }

        /// <summary>True for an empty edit or one still showing its XML placeholder text.</summary>
        private static bool IsPlaceholder(string s)
        {
            s = (s ?? "").Trim();
            return s.Length == 0 || s.StartsWith("(enter ", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Parse "0x8000ABCD" / decimal. AC item GUIDs live above 0x80000000, so the
        /// hex path goes through uint and reinterprets — a plain Convert.ToInt32 would throw
        /// on every inventory item.</summary>
        private static int ParseGuid(string s)
        {
            s = (s ?? "").Trim();
            if (s.Length == 0) return 0;
            try
            {
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    return unchecked((int)Convert.ToUInt32(s.Substring(2), 16));
                long d;
                return long.TryParse(s, out d) ? unchecked((int)(uint)d) : 0;
            }
            catch { return 0; }
        }
    }
}
