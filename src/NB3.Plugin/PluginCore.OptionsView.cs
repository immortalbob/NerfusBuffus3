using System;
using MyClasses.MetaViewWrappers;

namespace NB3.Plugin
{
    /// <summary>
    /// The recovered "NB3 Options" view (nb3-charconfig.xml), wired one-for-one to
    /// <see cref="NB3.Core.NB3Settings"/> — the same option set the original persisted under
    /// HKCU\software\NerfSoft\NerfusBuffus3\Options\0x&lt;charGUID&gt;, now in the per-character
    /// XML, plus the everyday knobs that used to be /nbset-only. Controls, by section:
    ///   Mana Regeneration — choiceManaMode (default 6 = Spells), checkboxUseKits (single on/off —
    ///     the scan auto-picks the best kit), checkboxUsePotions, checkboxUseHealthToMana (Cannibalize/H2M);
    ///   Recovery thresholds — editManaFloor, editManaTarget, editStamPct, editHealthPct,
    ///     editMaxSpellLevel (H2M/S2M/Revit/Heal level cap);
    ///   Buffing — editAR (Expected % of Spell Cost), checkboxSkillCap, editMinCast, checkboxRecast, editRebuffMins;
    ///   Misc — checkboxAutogen, checkboxQuietMode, checkboxEditorPermaDelete;
    ///   plus staticGUIDName, pbSave, pbRescan, pbOptDismiss. (The original's per-tier kit checkboxes
    /// and the level-7 toggles were folded into the single kit checkbox and the max-level field.)
    ///
    /// Per docs 03 §10.6 / 14 §6: Change events are advisory only. Each control is SEEDED from
    /// the model exactly once (the first poll it resolves; tracked in <see cref="_optSeeded"/>)
    /// and then left alone so the user's edits hold, and the whole set is READ back when Save is
    /// clicked. Seeding is per-control — a single missing/lazy control must never wedge the seed
    /// into a re-push loop that reverts every change (the "options don't stick" bug).
    /// </summary>
    [MVView("NB3.Plugin.Resources.nb3-charconfig.xml")]
    public partial class PluginCore
    {
        // Per-control "seeded once" flags (doc 14 §6.1/§6.2). CRITICAL: the old all-or-nothing
        // seed returned false if ANY control was null, which left seeding "pending" and re-pushed
        // the saved values into every control ~4x/second — silently reverting whatever the user
        // just clicked ("options don't stick"). Seeding each control exactly once, and never
        // again, is what lets a change hold until Save.
        private readonly System.Collections.Generic.HashSet<string> _optSeeded =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private void ResetOptionsViewState() { _optSeeded.Clear(); }

        // The original's "Editor Color Scheme" dropdown (four Decal-era palettes: "No Theme" /
        // "Default Theme" / "Doc's Fusion" / "Forest Gump") was retired in v3.0.7. Those were
        // native-Decal per-control palettes with no clean VVS equivalent, and VVS already themes
        // each window natively via the title-bar theme dropdown — so a plugin re-implementation
        // would have been a redundant, worse version of a built-in feature. The choiceEditorScheme
        // control and the EditorColorScheme setting are gone; nothing references them.

        private void OpenOptionsView()
        {
            if (ViewByTitle(OptionsTitle) == null)
            { Say("the Options view isn't available (see /nbdiag) - /nbset still works from chat."); return; }

            // Re-read the live character id and reload if it differs from the cached settings —
            // a settings load that happened before LoginComplete would have cached CharacterId=0
            // (the "(0x00000000)" header). Loading here, per-character, corrects that.
            int liveId = 0;
            try { liveId = Decal.Adapter.CoreManager.Current.CharacterFilter.Id; } catch { }
            if (_settings == null || _settings.CharacterId != liveId)
            {
                _settings = null;
                LoadCharacterConfig();
            }
            _optSeeded.Clear();                        // re-seed from the (possibly just-saved) model, once per open
            ShowManagedView(OptionsTitle, true);       // show + add to the Virindi bar
        }

        /// <summary>~4 Hz: seed each control the first time it resolves, and NEVER again — so a
        /// missing/lazy control can't wedge the others, and a seeded control is never overwritten
        /// out from under the user (doc 14 §6.1–6.2).</summary>
        private void PollOptionsView()
        {
            if (!IsViewVisible(OptionsTitle)) return;
            if (_settings == null) { if (LoggedIn()) LoadCharacterConfig(); else return; }
            SeedOptionsControls();
        }

        private void SeedOptionsControls()
        {
            var s = _settings;
            if (s == null) return;

            // ===== Mana Regeneration =====
            SeedCombo("choiceManaMode", (int)s.ManaRegenMode, null);          // options 0-6 ship in the XML; default 6 = Spells
            // One "Use Healing Kits" checkbox — the inventory scan auto-picks the best kit carried,
            // so there's no per-tier choice; ON == the full tier set, OFF == none.
            SeedCheck("checkboxUseKits", s.HealingKits != NB3.Core.HealingKitTiers.None);
            SeedCheck("checkboxUsePotions", s.UsePotions);
            SeedCheck("checkboxUseHealthToMana", s.UseHealthToMana);          // Cannibalize / H2M allowed?

            // ===== Recovery thresholds =====
            SeedText("editManaFloor", s.ManaFloorPercent.ToString());         // regen when mana below this %
            SeedText("editManaTarget", s.ManaRegenTargetPercent.ToString());  // regen back up to this %
            SeedText("editStamPct", s.StaminaFloorPercent.ToString());        // replenish stamina below this %
            SeedText("editHealthPct", s.HealthFloorPercent.ToString());       // heal below this % before H2M
            SeedText("editMaxSpellLevel", s.MaxRecoveryLevel.ToString());     // H2M/S2M/Revit/Heal level cap

            // ===== Buffing =====
            SeedText("editAR", s.ExpectedPctSpellCost.ToString());            // "Expected % of Spell Cost"
            SeedCheck("checkboxSkillCap", s.SkillBasedLevel);                 // cap buff level to reliably-castable
            SeedText("editMinCast", s.MinCastChancePercent.ToString());       // "barely reachable" buff threshold
            SeedCheck("checkboxRecast", s.RecastActiveBuffs);                 // recast buffs already active
            SeedText("editRebuffMins", s.RebuffMinutesRemaining.ToString());  // (recast off) refresh window, minutes

            // ===== Misc =====
            SeedCheck("checkboxAutogen", s.AutoGenerateOnLogin);              // auto-generate profile at login
            SeedCheck("checkboxQuietMode", s.QuietMode);
            SeedCheck("checkboxEditorPermaDelete", s.EditorPermaDelete);

            // The header label is diff-guarded, so it's cheap to refresh every poll.
            string name = "";
            try { name = Decal.Adapter.CoreManager.Current.CharacterFilter.Name; } catch { }
            SetLabelIn(OptionsTitle, "staticGUIDName", $"(0x{s.CharacterId:X8}) {name}");
        }

        private void SeedCheck(string ctlName, bool value)
        {
            if (_optSeeded.Contains(ctlName)) return;
            var cb = CtlIn<ICheckBox>(OptionsTitle, ctlName);
            if (cb == null) return;                                // not realized yet — retry next poll
            try { cb.Checked = value; _optSeeded.Add(ctlName); } catch { }
        }

        private void SeedText(string ctlName, string value)
        {
            if (_optSeeded.Contains(ctlName)) return;
            var tb = CtlIn<ITextBox>(OptionsTitle, ctlName);
            if (tb == null) return;
            try { tb.Text = value; _optSeeded.Add(ctlName); } catch { }
        }

        private void SeedCombo(string ctlName, int selected, string[] fillIfEmpty)
        {
            if (_optSeeded.Contains(ctlName)) return;
            var cb = CtlIn<ICombo>(OptionsTitle, ctlName);
            if (cb == null) return;
            try
            {
                if (fillIfEmpty != null && cb.Count == 0)
                    foreach (var it in fillIfEmpty) cb.Add(it);
                cb.Selected = (selected >= 0 && selected < cb.Count) ? selected : 0;
                _optSeeded.Add(ctlName);
            }
            catch { }
        }

        [MVControlEvent("pbSave", "Click")]
        private void PbOptionsSave(object sender, MVControlEventArgs e)
        {
            try
            {
                LoadCharacterConfigIfNeeded();
                if (_settings == null) { Say("log in first - options are per character."); return; }
                if (!ReadOptionsControls()) { Say("couldn't read the Options controls (see /nbdiag)."); return; }
                _settings.Save(NB3.Core.NB3Settings.PathFor(_settings.CharacterId));
                // Echo what was saved so the values are verifiable at a glance (doc 14 §6.3).
                var s = _settings;
                Say($"Options saved (0x{s.CharacterId:X8}): regen={(int)s.ManaRegenMode}({s.ManaRegenMode}) "
                    + $"kits={(s.HealingKits != NB3.Core.HealingKitTiers.None ? 1 : 0)} potions={(s.UsePotions ? 1 : 0)} "
                    + $"cannibalize={(s.UseHealthToMana ? 1 : 0)} manafloor={s.ManaFloorPercent}% manatarget={s.ManaRegenTargetPercent}% "
                    + $"stampct={s.StaminaFloorPercent}% healthpct={s.HealthFloorPercent}% maxrec={s.MaxRecoveryLevel}.");
                Say($"...aggr={s.ExpectedPctSpellCost}% skillcap={(s.SkillBasedLevel ? 1 : 0)} mincast={s.MinCastChancePercent}% "
                    + $"recast={(s.RecastActiveBuffs ? 1 : 0)} rebuffmins={s.RebuffMinutesRemaining} autogen={(s.AutoGenerateOnLogin ? 1 : 0)}. Takes effect next /nbuff.");
            }
            catch (Exception ex) { LogException(ex); }
        }

        /// <summary>The "Rescan Character" button: rebuild THIS character's profile from its
        /// CURRENT trained/specialized skills (the /nbgen path) and re-select it — so a character who
        /// trains new skills over time can refresh the generated set in place, with no need to delete
        /// the profile and relog. Overwrites the character-named profile.</summary>
        [MVControlEvent("pbRescan", "Click")]
        private void PbOptionsRescan(object sender, MVControlEventArgs e)
        {
            try
            {
                if (!LoggedIn()) { Say("log in first - Rescan reads your trained/specialized skills."); return; }
                string charName = null;
                try { charName = NB3.Core.Modern.ModernProfileStore.Canon(Decal.Adapter.CoreManager.Current.CharacterFilter.Name); }
                catch { }
                if (string.IsNullOrEmpty(charName))
                { Say("couldn't read your character name for the rescan - use /nbgen <name> from chat instead."); return; }
                Say($"Rescanning {charName}: rebuilding the profile from your current trained/specialized skills...");
                GenerateProfile(charName);   // overwrites the character-named profile, re-selects it, echoes the result
            }
            catch (Exception ex) { LogException(ex); }
        }

        /// <summary>Read every control back into <see cref="_settings"/>. Numeric edits are
        /// validated with a chat echo (doc 14 §6.3), clamped to the original's ranges.</summary>
        private bool ReadOptionsControls()
        {
            var s = _settings;
            var useKits = CtlIn<ICheckBox>(OptionsTitle, "checkboxUseKits");
            var potions = CtlIn<ICheckBox>(OptionsTitle, "checkboxUsePotions");
            var hp2m = CtlIn<ICheckBox>(OptionsTitle, "checkboxUseHealthToMana");
            var skillCap = CtlIn<ICheckBox>(OptionsTitle, "checkboxSkillCap");
            var recast = CtlIn<ICheckBox>(OptionsTitle, "checkboxRecast");
            var autogen = CtlIn<ICheckBox>(OptionsTitle, "checkboxAutogen");
            var quiet = CtlIn<ICheckBox>(OptionsTitle, "checkboxQuietMode");
            var perma = CtlIn<ICheckBox>(OptionsTitle, "checkboxEditorPermaDelete");
            var ar = CtlIn<ITextBox>(OptionsTitle, "editAR");
            var manaFloor = CtlIn<ITextBox>(OptionsTitle, "editManaFloor");
            var manaTarget = CtlIn<ITextBox>(OptionsTitle, "editManaTarget");
            var stamPct = CtlIn<ITextBox>(OptionsTitle, "editStamPct");
            var healthPct = CtlIn<ITextBox>(OptionsTitle, "editHealthPct");
            var maxLvl = CtlIn<ITextBox>(OptionsTitle, "editMaxSpellLevel");
            var minCast = CtlIn<ITextBox>(OptionsTitle, "editMinCast");
            var rebuffMins = CtlIn<ITextBox>(OptionsTitle, "editRebuffMins");
            var mode = CtlIn<ICombo>(OptionsTitle, "choiceManaMode");
            if (ar == null || maxLvl == null || mode == null) return false;

            try
            {
                // Kits: single on/off — ON stores the full tier set (the inventory scan picks the
                // best kit carried), OFF stores none.
                if (useKits != null)
                    s.HealingKits = useKits.Checked
                        ? (NB3.Core.HealingKitTiers.Plentiful | NB3.Core.HealingKitTiers.Treated | NB3.Core.HealingKitTiers.Peerless)
                        : NB3.Core.HealingKitTiers.None;
                if (potions != null) s.UsePotions = potions.Checked;
                if (hp2m != null) s.UseHealthToMana = hp2m.Checked;
                if (skillCap != null) s.SkillBasedLevel = skillCap.Checked;
                if (recast != null) s.RecastActiveBuffs = recast.Checked;
                if (autogen != null) s.AutoGenerateOnLogin = autogen.Checked;
                if (quiet != null) s.QuietMode = quiet.Checked;
                if (perma != null) s.EditorPermaDelete = perma.Checked;

                int n;
                if (int.TryParse((ar.Text ?? "").Trim(), out n) && n >= 1 && n <= 400)
                    s.ExpectedPctSpellCost = n;
                else
                    Say($"'{ar.Text}' isn't a valid % of Spell Cost (1-400) - keeping {s.ExpectedPctSpellCost}.");

                // Mana floor / target are a linked pair: the floor must sit below the target, or 0
                // disables the reserve (per-spell-cost gate only). Parse both, then commit together
                // only if the relationship holds — mirrors the /nbset manafloor/manatarget cross-check.
                int floorVal = s.ManaFloorPercent;
                if (manaFloor != null)
                {
                    if (int.TryParse((manaFloor.Text ?? "").Trim(), out n) && n >= 0 && n <= 99)
                        floorVal = n;
                    else
                        Say($"'{manaFloor.Text}' isn't a valid mana floor % (0-99) - keeping {s.ManaFloorPercent}.");
                }
                int targetVal = s.ManaRegenTargetPercent;
                if (manaTarget != null)
                {
                    if (int.TryParse((manaTarget.Text ?? "").Trim(), out n) && n >= 1 && n <= 100)
                        targetVal = n;
                    else
                        Say($"'{manaTarget.Text}' isn't a valid mana target % (1-100) - keeping {s.ManaRegenTargetPercent}.");
                }
                if (floorVal == 0 || floorVal < targetVal)
                { s.ManaFloorPercent = floorVal; s.ManaRegenTargetPercent = targetVal; }
                else
                    Say($"mana floor ({floorVal}%) must be below target ({targetVal}%) - keeping floor={s.ManaFloorPercent}% target={s.ManaRegenTargetPercent}%.");

                if (stamPct != null)
                {
                    if (int.TryParse((stamPct.Text ?? "").Trim(), out n) && n >= 1 && n <= 99)
                        s.StaminaFloorPercent = n;
                    else
                        Say($"'{stamPct.Text}' isn't a valid stamina floor % (1-99) - keeping {s.StaminaFloorPercent}.");
                }

                if (healthPct != null)
                {
                    if (int.TryParse((healthPct.Text ?? "").Trim(), out n) && n >= 1 && n <= 99)
                        s.HealthFloorPercent = n;
                    else
                        Say($"'{healthPct.Text}' isn't a valid health floor % (1-99) - keeping {s.HealthFloorPercent}.");
                }

                if (int.TryParse((maxLvl.Text ?? "").Trim(), out n) && n >= 1 && n <= 7)
                    s.MaxRecoveryLevel = n;
                else
                    Say($"'{maxLvl.Text}' isn't a valid max recovery level (1-7) - keeping {s.MaxRecoveryLevel}.");

                if (minCast != null)
                {
                    if (int.TryParse((minCast.Text ?? "").Trim(), out n) && n >= 1 && n <= 100)
                        s.MinCastChancePercent = n;
                    else
                        Say($"'{minCast.Text}' isn't a valid min cast chance % (1-100) - keeping {s.MinCastChancePercent}.");
                }

                if (rebuffMins != null)
                {
                    if (int.TryParse((rebuffMins.Text ?? "").Trim(), out n) && n >= 0 && n <= 240)
                        s.RebuffMinutesRemaining = n;
                    else
                        Say($"'{rebuffMins.Text}' isn't a valid rebuff window (0-240 min) - keeping {s.RebuffMinutesRemaining}.");
                }

                int m = mode.Selected;
                if (m >= 0 && m <= 6) s.ManaRegenMode = (NB3.Core.ManaRegenMode)m;
                return true;
            }
            catch { return false; }
        }

        [MVControlEvent("pbOptDismiss", "Click")]
        private void PbOptionsDismiss(object sender, MVControlEventArgs e)
        {
            try
            {
                ShowManagedView(OptionsTitle, false);  // hide + drop the Virindi bar entry
                Say("Options closed (unsaved changes discarded - Save writes them).");
            }
            catch (Exception ex) { LogException(ex); }
        }
    }
}
