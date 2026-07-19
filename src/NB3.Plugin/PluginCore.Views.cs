using System;
using System.Collections.Generic;
using MyClasses.MetaViewWrappers;

namespace NB3.Plugin
{
    /// <summary>
    /// Shared machinery for the four recovered views. The original created its Options /
    /// Editor / Casting views on demand and tore them down on Dismiss ("Removing Editor
    /// view:" … in the v1.52 string table); with the wrapper all four are created once at
    /// wireup (so [MVControlEvent] bindings hold for the plugin's lifetime) and Dismiss/open
    /// toggle both <c>Visible</c> AND the VVS bar entry (<c>HudView.ShowInBar</c>) in lockstep
    /// — so a secondary window is out of the Virindi bar until it's opened and gone from the
    /// bar again on Dismiss, matching the original's create-on-demand feel with no rebind churn.
    ///
    /// [MVView] attribute enumeration order is unspecified, so views are matched by their
    /// XML <c>title</c>, never by index (see MVWireupHelper.GetViews).
    /// </summary>
    public partial class PluginCore
    {
        private const string MainTitle = "NB3";
        private const string OptionsTitle = "NB3 Options";
        private const string EditorTitle = "NB3 Editor";
        private const string CastingTitle = "NB3 Spells";

        /// <summary>XML-default geometry per view (doc 03 §10.14: /nbreset's known-good state).</summary>
        private static readonly System.Drawing.Rectangle OptionsHome = new System.Drawing.Rectangle(75, 50, 270, 410);
        private static readonly System.Drawing.Rectangle EditorHome = new System.Drawing.Rectangle(5, 25, 650, 402);
        private static readonly System.Drawing.Rectangle CastingHome = new System.Drawing.Rectangle(5, 25, 380, 409);

        private readonly Dictionary<string, IView> _viewByTitle =
            new Dictionary<string, IView>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Map the created views by title and hide the secondary ones. Called after
        /// every WireupStart (initial and the §10.13 VVS rebuild).</summary>
        private void InitViews()
        {
            _viewByTitle.Clear();
            foreach (var v in MVWireupHelper.GetViews(this))
            {
                if (v == null) continue;
                string title = null;
                try { title = v.Title; } catch { }
                if (string.IsNullOrEmpty(title)) continue;
                _viewByTitle[title] = v;
            }

            IView main;
            _view = _viewByTitle.TryGetValue(MainTitle, out main) ? main : MVWireupHelper.GetDefaultView(this);

            // Secondary views start hidden AND out of the Virindi bar — the original only
            // created them on demand, so there's nothing to click until a command/button opens
            // one. The main "NB3" view keeps its bar icon (the plugin's handle); we never touch it.
            ShowManagedView(OptionsTitle, false);
            ShowManagedView(EditorTitle, false);
            ShowManagedView(CastingTitle, false);
        }

        private IView ViewByTitle(string title)
        {
            IView v;
            return _viewByTitle.TryGetValue(title, out v) ? v : null;
        }

        private bool SetViewVisible(string title, bool visible)
        {
            var v = ViewByTitle(title);
            if (v == null) return false;
            try { v.Visible = visible; return true; } catch { return false; }
        }

        private bool IsViewVisible(string title)
        {
            var v = ViewByTitle(title);
            if (v == null) return false;
            try { return v.Visible; } catch { return false; }
        }

        /// <summary>Open/close a SECONDARY view: move its on-screen visibility and its Virindi
        /// bar entry together, so "in the bar" always means "open". <c>HudView.ShowInBar</c>
        /// (VVS, confirmed present in the vendored metadata) is the bar toggle; the underlying
        /// object is absent on the Decal backend, where <see cref="SetProp"/> is a safe no-op
        /// (the window still shows/hides via <c>Visible</c>). Never call this for the main view —
        /// it must keep its bar icon as the plugin's permanent handle.</summary>
        private void ShowManagedView(string title, bool show)
        {
            var v = ViewByTitle(title);
            if (v == null) return;
            try { v.Visible = show; } catch { }
            SetProp(UnderlyingHudOf(v), "ShowInBar", show);
        }

        /// <summary>Defensive control resolution inside ONE view (doc 10 §8) — names repeat
        /// across plugins' views in principle, so secondary-view code resolves per-view.</summary>
        private T CtlIn<T>(string viewTitle, string name) where T : class, IControl
        {
            var v = ViewByTitle(viewTitle);
            if (v == null) return null;
            try { return v[name] as T; } catch { return null; }
        }

        /// <summary>Set a StaticText in a specific view, diffed via the shared label cache.</summary>
        private void SetLabelIn(string viewTitle, string name, string value)
        {
            var key = viewTitle + ":" + name;
            string last;
            if (_labelCache.TryGetValue(key, out last) && last == value) return;
            var st = CtlIn<IStaticText>(viewTitle, name);
            if (st == null) return;
            try { st.Text = value; _labelCache[key] = value; } catch { }
        }

        /// <summary>Clear all secondary-view session state when the views are about to be
        /// rebuilt (the §10.13 VVS rebuild) — cached rows/flags die with the old views.</summary>
        private void ResetSecondaryViewState()
        {
            _viewByTitle.Clear();
            ResetOptionsViewState();
            ResetEditorViewState();
            ResetCastingViewState();
        }

        // ---- BuffComplete.wav (recovered from the installer; embedded as nb3-notify.wav) ----
        // Doc 06: SoundPlayer is the zero-dependency floor; Play() is async (never PlaySync
        // from a Decal callback). Loaded once from the embedded resource, failures swallowed.

        private System.Media.SoundPlayer _notify;
        private bool _notifyBroken;

        private void PlayBuffComplete()
        {
            if (_notifyBroken) return;
            try
            {
                if (_notify == null)
                {
                    var s = System.Reflection.Assembly.GetExecutingAssembly()
                        .GetManifestResourceStream("NB3.Plugin.Resources.nb3-notify.wav");
                    if (s == null) { _notifyBroken = true; return; }
                    _notify = new System.Media.SoundPlayer(s);
                    _notify.Load();
                }
                _notify.Play();
            }
            catch { _notifyBroken = true; }
        }
    }
}
