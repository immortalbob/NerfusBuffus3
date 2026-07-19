using System;
using MyClasses.MetaViewWrappers;

namespace NB3.Plugin
{
    /// <summary>
    /// The recovered "NB3 Options" view (nb3-charconfig.xml), wired one-for-one to
    /// <see cref="NB3.Core.NB3Settings"/> — the same option set the original persisted under
    /// HKCU\software\NerfSoft\NerfusBuffus3\Options\0x&lt;charGUID&gt;, now in the per-character
    /// XML. Control names are the original's: checkboxUse*, checkboxFallbackTo6,
    /// checkboxQuietMode, checkboxEditorPermaDelete, editAR, editMaxSpellLevel,
    /// choiceManaMode, staticGUIDName, pbSave, pbOptDismiss.
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

            SeedCheck("checkboxUsePlentiful", (s.HealingKits & NB3.Core.HealingKitTiers.Plentiful) != 0);
            SeedCheck("checkboxUseTreated",   (s.HealingKits & NB3.Core.HealingKitTiers.Treated) != 0);
            SeedCheck("checkboxUsePeerless",  (s.HealingKits & NB3.Core.HealingKitTiers.Peerless) != 0);
            SeedCheck("checkboxUseRevit7", s.UseRevit7);
            SeedCheck("checkboxUseS2M7", s.UseS2M7);
            SeedCheck("checkboxUseH2M7", s.UseH2M7);
            SeedCheck("checkboxFallbackTo6", s.FallbackTo6OnUnknown7);
            SeedCheck("checkboxQuietMode", s.QuietMode);
            SeedCheck("checkboxEditorPermaDelete", s.EditorPermaDelete);
            SeedCheck("checkboxUsePotions", s.UsePotions);
            SeedText("editAR", s.ExpectedPctSpellCost.ToString());
            SeedText("editMaxSpellLevel", s.MaxRecoveryLevel.ToString());
            SeedText("editMinCast", s.MinCastChancePercent.ToString());       // "barely reachable" buff threshold
            SeedCombo("choiceManaMode", (int)s.ManaRegenMode, null);          // options 0-6 ship in the XML

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
                Say($"Options saved (0x{s.CharacterId:X8}): kits={s.HealingKits} potions={(s.UsePotions ? 1 : 0)} aggr={s.ExpectedPctSpellCost}% "
                    + $"regen={(int)s.ManaRegenMode}({s.ManaRegenMode}) maxrec={s.MaxRecoveryLevel} mincast={s.MinCastChancePercent}% "
                    + $"revit7={(s.UseRevit7 ? 1 : 0)} s2m7={(s.UseS2M7 ? 1 : 0)} h2m7={(s.UseH2M7 ? 1 : 0)} fb6={(s.FallbackTo6OnUnknown7 ? 1 : 0)}. Takes effect next /nbuff.");
            }
            catch (Exception ex) { LogException(ex); }
        }

        /// <summary>Read every control back into <see cref="_settings"/>. Numeric edits are
        /// validated with a chat echo (doc 14 §6.3), clamped to the original's ranges.</summary>
        private bool ReadOptionsControls()
        {
            var s = _settings;
            var kitP = CtlIn<ICheckBox>(OptionsTitle, "checkboxUsePlentiful");
            var kitT = CtlIn<ICheckBox>(OptionsTitle, "checkboxUseTreated");
            var kitE = CtlIn<ICheckBox>(OptionsTitle, "checkboxUsePeerless");
            var rev7 = CtlIn<ICheckBox>(OptionsTitle, "checkboxUseRevit7");
            var s2m7 = CtlIn<ICheckBox>(OptionsTitle, "checkboxUseS2M7");
            var h2m7 = CtlIn<ICheckBox>(OptionsTitle, "checkboxUseH2M7");
            var fb6 = CtlIn<ICheckBox>(OptionsTitle, "checkboxFallbackTo6");
            var quiet = CtlIn<ICheckBox>(OptionsTitle, "checkboxQuietMode");
            var perma = CtlIn<ICheckBox>(OptionsTitle, "checkboxEditorPermaDelete");
            var ar = CtlIn<ITextBox>(OptionsTitle, "editAR");
            var maxLvl = CtlIn<ITextBox>(OptionsTitle, "editMaxSpellLevel");
            var minCast = CtlIn<ITextBox>(OptionsTitle, "editMinCast");        // optional (older XML lacks it)
            var potions = CtlIn<ICheckBox>(OptionsTitle, "checkboxUsePotions"); // optional (older XML lacks it)
            var mode = CtlIn<ICombo>(OptionsTitle, "choiceManaMode");
            if (kitP == null || ar == null || maxLvl == null || mode == null) return false;

            try
            {
                var kits = NB3.Core.HealingKitTiers.None;
                if (kitP.Checked) kits |= NB3.Core.HealingKitTiers.Plentiful;
                if (kitT != null && kitT.Checked) kits |= NB3.Core.HealingKitTiers.Treated;
                if (kitE != null && kitE.Checked) kits |= NB3.Core.HealingKitTiers.Peerless;
                s.HealingKits = kits;
                if (rev7 != null) s.UseRevit7 = rev7.Checked;
                if (s2m7 != null) s.UseS2M7 = s2m7.Checked;
                if (h2m7 != null) s.UseH2M7 = h2m7.Checked;
                if (fb6 != null) s.FallbackTo6OnUnknown7 = fb6.Checked;
                if (quiet != null) s.QuietMode = quiet.Checked;
                if (perma != null) s.EditorPermaDelete = perma.Checked;
                if (potions != null) s.UsePotions = potions.Checked;

                int n;
                if (int.TryParse((ar.Text ?? "").Trim(), out n) && n >= 1 && n <= 400)
                    s.ExpectedPctSpellCost = n;
                else
                    Say($"'{ar.Text}' isn't a valid % of Spell Cost (1-400) - keeping {s.ExpectedPctSpellCost}.");

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
