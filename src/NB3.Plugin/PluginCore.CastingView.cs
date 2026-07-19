using System;
using MyClasses.MetaViewWrappers;
using NB3.Core;

namespace NB3.Plugin
{
    /// <summary>
    /// The recovered "NB3 Spells" casting view (nb3-casting.xml): the live list of what the
    /// cycle still has to do, opened when a cycle starts and closed when it ends — exactly the
    /// original's behaviour ("Loading NB3 Casting xml schema" at launch, "Removing Casting
    /// view" at termination; here open/close toggle Visible). The current action is marked
    /// with '»'; completed actions drop off the top. Dismiss hides the window without
    /// touching the cycle; Pause/Resume mirror the main view's buttons.
    /// </summary>
    [MVView("NB3.Plugin.Resources.nb3-casting.xml")]
    public partial class PluginCore
    {
        private int _castViewCursor = -1;   // last rendered cycle cursor (-1 = list is stale)
        private int _castViewCount = -1;    // last rendered action count

        private void ResetCastingViewState() { _castViewCursor = _castViewCount = -1; }

        private void ShowCastingView()
        {
            ResetCastingViewState();
            ShowManagedView(CastingTitle, true);       // show + add to the Virindi bar for the cycle's life
            RefreshCastingView();
        }

        private void HideCastingView()
        {
            ResetCastingViewState();
            ShowManagedView(CastingTitle, false);      // hide + drop the Virindi bar entry
        }

        /// <summary>Poll hook (~4 Hz): rebuild only when the cycle advanced (cursor/count diff)
        /// — a quiet frame costs two int compares.</summary>
        private void PollCastingView()
        {
            if (_cycle != null && IsViewVisible(CastingTitle)) RefreshCastingView();
        }

        private void RefreshCastingView()
        {
            if (_cycle == null) return;
            if (!IsViewVisible(CastingTitle)) return;
            int cursor = _cycle.Cursor;
            int count = _cycle.Actions.Count;
            if (cursor == _castViewCursor && count == _castViewCount) return;

            var list = CtlIn<IList>(CastingTitle, "listSpellsToCast");
            if (list == null) return;                    // not realized yet — next poll retries
            try
            {
                list.Clear();
                for (int i = cursor; i < count; i++)
                {
                    var a = _cycle.Actions[i];
                    var r = list.AddRow();
                    r[0][0] = i == cursor ? "»" : "";   // » = the action in flight
                    r[1][0] = a.Description ?? (a.Kind == CastKind.Equip ? "Equip item" : $"Cast 0x{a.SpellId:X4}");
                }
                _castViewCursor = cursor;
                _castViewCount = count;
            }
            catch { /* transient — retry next poll */ }
        }

        [MVControlEvent("pbCastDismiss", "Click")]
        private void PbCastDismiss(object sender, MVControlEventArgs e)
        {
            try
            {
                ShowManagedView(CastingTitle, false);  // hide + drop the Virindi bar entry
                Say("casting list hidden (the cycle keeps running - /nbabort stops it).");
            }
            catch (Exception ex) { LogException(ex); }
        }

        [MVControlEvent("pbCastPause", "Click")]
        private void PbCastPause(object sender, MVControlEventArgs e)
        {
            try
            {
                if (_cycle == null) { Say("no cycle running."); return; }
                _cycle.Pause(); Say("Paused.");
            }
            catch (Exception ex) { LogException(ex); }
        }

        [MVControlEvent("pbCastResume", "Click")]
        private void PbCastResume(object sender, MVControlEventArgs e)
        {
            try
            {
                if (_cycle == null) { Say("no cycle to resume."); return; }
                _cycle.Resume(); _lastCastResolvedTick = Environment.TickCount; _timer.Start(); Say("Resumed.");
            }
            catch (Exception ex) { LogException(ex); }
        }
    }
}
