using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using SIO = System.IO; // 'Path' alone can bind to MetaViewWrappers' Extension.Path (doc 13 §5)
using Decal.Adapter;
using Decal.Adapter.Wrappers;
using MyClasses.MetaViewWrappers;
using NB3.Core;

namespace NB3.Plugin
{
    /// <summary>
    /// Nerfus Buffus III — Decal 3 revival shell, successor to the original Nerfus Buffus II
    /// by Pascal Jolin / Nerf Soft (2001–2003); rebuilt on the managed API. This class is
    /// deliberately thin: it owns the
    /// Decal lifecycle, the recovered VVS views, and the command handlers, and delegates ALL
    /// buff logic to NB3.Core (which is unit-tested offline).
    ///
    /// Doc-13 discipline applied here:
    ///  - every method Decal calls into is wrapped in try/catch (an escaped exception takes
    ///    the user's client down — doc 09 §4.4);
    ///  - all core events are wired MANUALLY in Startup (no [WireUpBaseEvents]): explicit
    ///    ordering, and the [BaseEvent] name/source convention can't misbind (doc 11 §1);
    ///  - the portal-space gate (doc 13 §10.3, the UtilityBelt-confirmed pattern): the flag
    ///    starts TRUE, ChangePortalMode drives it, LoginComplete clears it belt-and-braces,
    ///    a ~15 s watchdog releases it if ExitPortal is ever missed, and while it is set the
    ///    cycle performs NO Actions calls (cast/use/combat-mode are all deferred);
    ///  - Actions.BusyState gates every UseItem/equip (doc 13 §10.4 / doc 01 §4.2a).
    /// </summary>
    [FriendlyName("Nerfus Buffus III")]
    [Guid("915ED3D0-26CD-493D-80E8-34A3099FF511")] // fresh identity — never the dead native CLSID (doc 08/12)
    [ComVisible(true)]
    [MVView("NB3.Plugin.Resources.nb3-control.xml")]
    // The three recovered secondary views live on the partial parts:
    //   PluginCore.OptionsView.cs  — nb3-charconfig.xml ("NB3 Options")
    //   PluginCore.EditorView.cs   — nb3-editor.xml     ("NB3 Editor")
    //   PluginCore.CastingView.cs  — nb3-casting.xml    ("NB3 Spells")
    [MVWireUpControlEvents]
    public partial class PluginCore : PluginBase
    {
        private NB3.Core.Modern.ILiveSpellTable _liveTable; // built lazily, post-login (see EnsurePlanner)
        private NB3.Core.Modern.ModernBuffPlanner _planner;
        private DecalGameState _state;
        private CycleOptions _cycleOptions = new CycleOptions();

        private BuffCycle _cycle;                 // the running cycle, or null when idle
        private System.Windows.Forms.Timer _timer; // NB3's WatchTimer equivalent (client/UI thread)

        // ---- the cast-result state machine (step 4b; doc 18 §3) ------------------------
        // ONE cast in flight at a time (the sequential-runner shape): the monitor pairs the
        // CastSpell we issued with the next outcome chat line / enchantment-add delta, and
        // _pending records who asked (the plan cursor vs a mana-regen recovery cast).
        private enum PendingCast { None, Cycle, Regen }
        private readonly CastResultMonitor _monitor = new CastResultMonitor();
        private PendingCast _pending = PendingCast.None;

        // Per-character Options + resolved recovery spells, loaded at cycle start.
        private SpellTable _classicTable;         // recovery families; ids line up with live (MODERN_SPELL_MODEL)
        private NB3Settings _settings;
        private RecoverySpells _recovery;
        private bool _regenWarned;                // one chat notice per cycle, not per tick
        private int _regenCastFailures;           // comps-missing/timed-out recovery casts
        private const int MaxRegenCastFailures = 5; // then BACK OFF the cadence (not give up)
        private int _lastRegenCastTick;           // for the post-streak retry backoff
        private const int RegenRetryBackoffMs = 3000; // after the streak, retry recovery this often

        // /nbdebug instrumentation (doc 18 §7's two open measurements: cast timing and the
        // StatusMessage type catalog). Off by default; writes nb3-debug.txt when on.
        private bool _debug;
        private int _castBeganTick;               // wall-clock for the cast→outcome delta
        private int _pendingEnchantSecondsAtCast; // enchant remaining at cast start; a jump up = landed
        private int _lastCastResolvedTick;        // when the last cast finished; the next waits a settle
        // Min gap after a cast RESOLVES before the next is issued. AC drops a cast fired into
        // post-cast recovery (nb3-debug: a cast begun 9 ms after the previous landed was dropped
        // and timed out). Combined with the BusyState==0 gate, this spaces casts past recovery.
        private const int CastSettleMs = 500;

        // Portal-space gate (doc 13 §10.3). TRUE at startup: login itself ends with an ExitPortal.
        private bool _inPortalSpace = true;
        private int _portalEnteredAt;             // Environment.TickCount when the gate was set
        private const int PortalWatchdogMs = 15000;

        // ---- view / backend state (docs 03 §10.11–10.14, 10 §7, 14 §4–§6) ---------------
        private IView _view;                      // resolved lazily; null until wireup succeeds
        private string _wireupError;              // WireupStart itself threw (should not happen with the tolerant helper)
        private bool _vvsPresentAtStartup;        // §10.13: the auto-detect snapshot, recorded for /nbdiag
        private bool _vvsRunningAtStartup;
        private string _vvsVersionAtStartup = "(absent)";
        private bool _rebuildTried;               // §10.13 countermeasure 2: one re-probe at first login
        private int _lastPollTick;                // ~4 Hz render-frame poll throttle (doc 11 §3)
        private int _lastWireupRetryTick;         // pending-wireup retry, ~1 Hz (doc 03 §10.11)
        private readonly System.Collections.Generic.HashSet<string> _populated =
            new System.Collections.Generic.HashSet<string>();          // per-NAME populate flags (doc 14 §6.1)
        private readonly System.Collections.Generic.Dictionary<string, string> _labelCache =
            new System.Collections.Generic.Dictionary<string, string>(); // write labels only on change
        private string _currentProfileName;       // shown in staticCurrentConfig
        private int _cycleStartedAt;              // Environment.TickCount at /nbuff, for the m:ss label

        // ---- login auto-onboard (character-named profile, auto-selected) -----------------
        // On LoginComplete we generate a self-buff profile named after the character (if one
        // doesn't exist yet — never clobbering edits) and select it in the main window, so a
        // brand-new user is ready to buff without creating a profile. Serviced from the throttled
        // poll (not the event) because skill/name data settles a beat AFTER LoginComplete and the
        // catalog/file work is the same cheap one-shot the editor poll already does off this thread.
        private bool _autoOnboardPending;         // armed at LoginComplete, cleared once handled or timed out
        private int _loginCompleteTick;           // Environment.TickCount at LoginComplete (settle + timeout base)
        private string _autoSelectProfile;        // one-shot: EnsureProfileCombo selects this after (re)populate
        private const int AutoOnboardSettleMs = 1500;   // let CharacterFilter skills/name settle post-login
        private const int AutoOnboardTimeoutMs = 30000; // stop waiting for skill/name data after ~30 s

        /// <summary>Known-good geometry for /nbreset (doc 03 §10.14): the main view's XML
        /// defaults. Restores position AND size (UserW/UserH persist in vvs.s3db).</summary>
        private static readonly System.Drawing.Rectangle HomePosition =
            new System.Drawing.Rectangle(75, 50, 200, 219);

        // ---- lifecycle -----------------------------------------------------------------

        protected override void Startup()
        {
            try
            {
                MigrateLegacyData();   // carry NB2's profiles/settings over on first NB3 run

                _state = new DecalGameState(Host, CoreManager.Current, id => ResolveSpell(id));

                _timer = new System.Windows.Forms.Timer { Interval = 150 }; // ~7 Hz: tighter gap between a resolved cast and the next
                _timer.Tick += CycleTick;

                // Manual event wiring FIRST — explicit, unambiguous (doc 11 §1) — and
                // deliberately BEFORE any view work: if view creation misbehaves, chat
                // commands, the portal gate, and the cast monitor still come up (the old
                // order let a wireup failure silently kill all of these — doc 03 §10.11).
                CoreManager.Current.CommandLineText += OnCommandLine;
                CoreManager.Current.CharacterFilter.ChangePortalMode += OnChangePortalMode;
                CoreManager.Current.CharacterFilter.LoginComplete += OnLoginComplete;
                CoreManager.Current.ChatBoxMessage += OnChatBoxMessage;                       // outcome truth (doc 18 §3)
                CoreManager.Current.CharacterFilter.ChangeEnchantments += OnChangeEnchantments; // §2 add-delta, belt and braces
                CoreManager.Current.CharacterFilter.StatusMessage += OnStatusMessage;         // /nbdebug catalog (doc 18 §7)
                CoreManager.Current.RenderFrame += Core_RenderFrame;                          // throttled UI poll (docs 11 §3, 14 §4–6)

                // §10.13: record the backend-decision inputs at the moment of choice.
                ProbeVvs(out _vvsPresentAtStartup, out _vvsVersionAtStartup, out _vvsRunningAtStartup);

                // View creation + attribute wireup, isolated: a failure here must not take
                // down the (already wired) command/cycle machinery. The modified helper is
                // per-binding tolerant and never throws (doc 03 §10.11); this catch is belt
                // and braces against view-construction itself.
                try { MVWireupHelper.WireupStart(this, Host); }
                catch (Exception wex) { _wireupError = wex.Message; LogException(wex); }
                InitViews();   // map the four views by title; hide Options/Editor/Casting

                // Load banner: deferred to LoginComplete — at cold start we're inside the
                // portal gate and our own discipline defers ALL Actions calls (doc 13 §10.3).
                // Hot-enabled mid-world (LoginComplete already fired), print immediately.
                if (LoggedIn()) ShowBannerOnce();
            }
            catch (Exception ex) { LogException(ex); }
        }

        private bool _bannerShown;
        private void ShowBannerOnce()
        {
            if (_bannerShown) return;
            _bannerShown = true;
            Host.Actions.AddChatText("[NB3] Nerfus Buffus III revival loaded. /nbhelp for commands.", 5);

            // Doc 03 §10.13/§10.11: say which backend built the window and whether wireup is
            // healthy — at login, when chat exists. One line each; converts a multi-session
            // remote debug into a chat paste.
            bool presentNow, runningNow; string verNow;
            ProbeVvs(out presentNow, out verNow, out runningNow);
            Say($"UI backend: {BackendName()} (VVS {verNow}, running={runningNow}; at startup: {_vvsVersionAtStartup}, running={_vvsRunningAtStartup})");
            var report = MVWireupHelper.GetWireupReport(this);
            if (_wireupError != null)
                Say($"WARNING: view creation failed ({_wireupError}) - UI unavailable, commands still work. /nbdiag for details.");
            else if (report.Contains("FAILED") || report.Contains("pending"))
                Say($"WARNING: {report} - /nbdiag for details.");
        }

        /// <summary>Doc 03 §10.13 probe, by reflection so it works whether or not VVS is
        /// loaded at all: assembly presence + version from the AppDomain, and the live
        /// <c>VirindiViewService.Service.Running</c> flag.</summary>
        private static void ProbeVvs(out bool present, out string version, out bool running)
        {
            present = false; version = "(absent)"; running = false;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var n = asm.GetName();
                    if (n.Name != "VirindiViewService") continue;
                    present = true;
                    version = n.Version.ToString();
                    var svc = asm.GetType("VirindiViewService.Service");
                    var prop = svc != null ? svc.GetProperty("Running", BindingFlags.Public | BindingFlags.Static) : null;
                    if (prop != null) running = (bool)prop.GetValue(null, null);
                    return;
                }
            }
            catch { }
        }

        /// <summary>Classify by what the window IS, not what the detector would now say
        /// (doc 03 §10.13 countermeasure 3): the wrapper view carries its own backend tag.</summary>
        private string BackendName()
        {
            if (_view == null) return "none (view not created)";
            try { return _view.ViewType.ToString(); } catch { return _view.GetType().Name; }
        }

        private bool LoggedIn()
        {
            try { return CoreManager.Current.CharacterFilter.Id != 0; } catch { return false; }
        }

        protected override void Shutdown()
        {
            try
            {
                CoreManager.Current.CommandLineText -= OnCommandLine;
                CoreManager.Current.CharacterFilter.ChangePortalMode -= OnChangePortalMode;
                CoreManager.Current.CharacterFilter.LoginComplete -= OnLoginComplete;
                CoreManager.Current.ChatBoxMessage -= OnChatBoxMessage;
                CoreManager.Current.CharacterFilter.ChangeEnchantments -= OnChangeEnchantments;
                CoreManager.Current.CharacterFilter.StatusMessage -= OnStatusMessage;
                CoreManager.Current.RenderFrame -= Core_RenderFrame;
                if (_timer != null) { _timer.Stop(); _timer.Tick -= CycleTick; _timer.Dispose(); }
                MVWireupHelper.WireupEnd(this);
            }
            catch (Exception ex) { LogException(ex); }
        }

        // ---- portal-space gate (doc 13 §10) ----------------------------------------------

        private void OnChangePortalMode(object sender, ChangePortalModeEventArgs e)
        {
            try
            {
                _inPortalSpace = e.Type == PortalEventType.EnterPortal;
                if (_inPortalSpace) _portalEnteredAt = Environment.TickCount;
            }
            catch (Exception ex) { LogException(ex); }
        }

        private void OnLoginComplete(object sender, EventArgs e)
        {
            try
            {
                _inPortalSpace = false; // belt and braces (doc 13 §10.3)
                TryRebuildOnVvs();      // §10.13 countermeasure 2 — BEFORE the banner, so the banner reports the final backend
                ShowBannerOnce();

                // Arm the login auto-onboard (generate-if-missing + select the character's profile).
                // Re-armed on every LoginComplete so a character switch onboards the NEW character;
                // the actual work runs from the throttled poll once skill/name data has settled.
                _autoOnboardPending = true;
                _loginCompleteTick = Environment.TickCount;
            }
            catch (Exception ex) { LogException(ex); }
        }

        /// <summary>Doc 03 §10.13 countermeasure 2: if Startup's snapshot fell back to the
        /// legacy Decal-injected renderer but VVS is running by first login, dispose and
        /// re-create the view on VVS. One shot, wrapped — worst case is the status quo.
        /// Cached wrappers are nulled first (they'd point into the dead view) and the
        /// per-name populate flags reset so the poll re-seeds the fresh controls.</summary>
        private void TryRebuildOnVvs()
        {
            if (_rebuildTried) return;
            _rebuildTried = true;
            try
            {
                if (_view == null || _view.ViewType != ViewSystemSelector.eViewSystem.DecalInject) return;
                bool present, running; string ver;
                ProbeVvs(out present, out ver, out running);
                if (!present || !running) return;

                _view = null;                      // dead-view wrappers must not survive the swap
                _labelCache.Clear();
                _populated.Clear();
                ResetSecondaryViewState();         // editor/options/casting caches die with the views
                MVWireupHelper.WireupStart(this, Host);   // helper disposes the prior views on re-entry
                InitViews();
                Say($"UI rebuilt on VirindiViewService (Startup snapshot had fallen back to DecalInject; VVS {ver} is running now).");
            }
            catch (Exception ex) { LogException(ex); }
        }

        // ---- the throttled UI poll (docs 11 §3, 14 §4–§6, 03 §10.11) ---------------------
        // Cheap reads/writes only — never DAT or file work on the render thread (doc 01).

        private void Core_RenderFrame(object sender, EventArgs e)
        {
            try
            {
                int now = Environment.TickCount;
                if (now - _lastPollTick < 250) return;   // ~4 Hz
                _lastPollTick = now;

                // Pending wireups retry ~1 Hz (lazily-realized controls wire when first shown).
                if (now - _lastWireupRetryTick > 1000)
                {
                    _lastWireupRetryTick = now;
                    if (MVWireupHelper.GetPendingWireupCount(this) > 0)
                        MVWireupHelper.RetryPendingWireups(this);
                }

                ServiceAutoOnboard();   // login onboarding — BEFORE the combo so a new profile's selection applies now
                EnsureProfileCombo();
                UpdateStatusView();
                PollOptionsView();   // seed-then-diff, per doc 14 §6 (no-ops while hidden)
                PollEditorView();
                PollCastingView();
            }
            catch (Exception ex) { LogException(ex); }
        }

        /// <summary>Populate the profile dropdown on first successful resolve, keyed by
        /// control NAME (doc 14 §6.1 — never a shared flag), re-populated when the profile
        /// set changes (/nbnew).</summary>
        private void EnsureProfileCombo()
        {
            const string name = "choiceLoadConfig";
            if (_populated.Contains(name)) return;
            var combo = Ctl<ICombo>(name);
            if (combo == null) return;               // not realized yet — retry next poll
            try
            {
                combo.Clear();
                combo.Add("(Select a configuration to load)");
                // If a profile has been requested for auto-selection (login onboarding, or a fresh
                // /nbgen), pick its index as we add it — index 0 is the placeholder, so options are
                // 1-based. Falls back to the placeholder when nothing is pending or it isn't found.
                var profiles = ListProfiles();
                int selectIndex = 0;
                for (int i = 0; i < profiles.Length; i++)
                {
                    combo.Add(profiles[i]);
                    if (_autoSelectProfile != null &&
                        string.Equals(profiles[i], _autoSelectProfile, StringComparison.OrdinalIgnoreCase))
                        selectIndex = i + 1;
                }
                combo.Selected = selectIndex;
                _populated.Add(name);
                // One-shot: clear the request once honoured so we never fight a later manual pick.
                if (selectIndex > 0) _autoSelectProfile = null;
            }
            catch { }
        }

        private NB3.Core.Modern.ModernProfileStore _store;
        private NB3.Core.Modern.ModernProfileStore Store()
        {
            if (_store == null)
                _store = new NB3.Core.Modern.ModernProfileStore(SIO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NerfusBuffus3"));
            return _store;
        }

        private string[] ListProfiles()
        {
            try
            {
                var l = Store().List();
                var a = new string[l.Count];
                l.CopyTo(a, 0);
                return a;
            }
            catch { return new string[0]; }
        }

        /// <summary>Repopulate every profile dropdown (the original's /nbrefresh: "Scanning
        /// \Configs\*.xml ... Added %u entries to the Configuration list").</summary>
        private void RefreshProfileLists(bool announce)
        {
            _populated.Remove("choiceLoadConfig");
            _populated.Remove("choiceGroup");
            _populated.Remove("choiceInclude");
            if (announce) Say($"Rescanned profiles: {ListProfiles().Length} found.");
        }

        /// <summary>Defensive control resolution (doc 10 §8): the indexer throws on a
        /// missing/not-yet-realized control, so tolerate and re-resolve on use.</summary>
        private T Ctl<T>(string name) where T : class, IControl
        {
            try { return _view == null ? null : _view[name] as T; } catch { return null; }
        }

        /// <summary>Write a status label only when its text actually changed (cheap poll,
        /// no per-frame churn).</summary>
        private void SetLabel(string name, string value)
        {
            string last;
            if (_labelCache.TryGetValue(name, out last) && last == value) return;
            var st = Ctl<IStaticText>(name);
            if (st == null) return;
            try { st.Text = value; _labelCache[name] = value; } catch { }
        }

        /// <summary>True while Actions calls must be deferred. Includes the doc-13 §10.3
        /// watchdog: if ExitPortal is ever missed, release the gate after ~15 s rather than
        /// suspending the plugin until relog.</summary>
        private bool ActionsGated()
        {
            if (!_inPortalSpace) return false;
            if (Environment.TickCount - _portalEnteredAt > PortalWatchdogMs)
            {
                _inPortalSpace = false; // watchdog release
                return false;
            }
            return true;
        }

        // ---- the cast-result state machine (step 4b) -------------------------------------
        // The doc-18 §3 recipe, whole: outcomes come from CHAT (the UB-proven catalog inside
        // CastChat), successes are corroborated by the §2 enchantment ADD delta, and a
        // watchdog turns silence into a retriable Timeout. Both handlers stay on Decal's
        // client-thread callbacks — no foreign thread ever touches Actions (doc 13 §10).

        private void OnChatBoxMessage(object sender, ChatTextInterceptEventArgs e)
        {
            try
            {
                if (!_monitor.CastInFlight) return;            // fast path: nothing pending
                // /nbdebug: log EVERY chat line while a cast is pending, so a slow cast (one that
                // only resolves on the watchdog) shows exactly what the server actually sent —
                // the wording we must match. This is the doc-18 §7 open measurement.
                if (_debug) DebugLog($"  chat(in-flight): \"{e.Text}\"");
                var outcome = _monitor.ObserveChat(e.Text);
                if (outcome != CastOutcome.None) ResolveCast(outcome);
                else if (CastChat.IsComponentsConsumed(e.Text ?? "")) _monitor.NotePartialFeedback();
            }
            catch (Exception ex) { LogException(ex); }
        }

        private void OnChangeEnchantments(object sender, ChangeEnchantmentsEventArgs e)
        {
            try
            {
                if (e.Type != AddRemoveEventType.Add || e.Enchantment == null) return;
                if (_debug && _monitor.CastInFlight)
                    DebugLog($"  enchant-add 0x{e.Enchantment.SpellId:X4} (pending 0x{_monitor.PendingSpellId:X4}) match={(e.Enchantment.SpellId == _monitor.PendingSpellId)}");
                if (_monitor.ObserveEnchantmentAdded(e.Enchantment.SpellId))
                    ResolveCast(CastOutcome.Success);          // landed; chat line eaten/missed
            }
            catch (Exception ex) { LogException(ex); }
        }

        /// <summary>Route a resolved outcome to whoever issued the cast. Cycle casts feed the
        /// runner's §3 policy (advance / retry / count+advance / skip); regen recovery casts
        /// need no report — the controller re-reads the vitals next tick.</summary>
        private void ResolveCast(CastOutcome outcome)
        {
            // Doc 18 §7 instrumentation: cast → first-outcome wall-clock delta.
            DebugLog($"cast resolve {outcome} after {unchecked(Environment.TickCount - _castBeganTick)} ms (pending={_pending})");
            _lastCastResolvedTick = Environment.TickCount;   // start the recovery settle for the next cast
            _state.IsCasting = false;
            var pending = _pending;
            _pending = PendingCast.None;
            if (pending == PendingCast.Cycle && _cycle != null)
            {
                _cycle.ReportCastOutcome(outcome);
                UpdateStatusView();
            }
            else if (pending == PendingCast.Regen)
            {
                // Recovery casts need no cycle report (the controller re-reads the vitals),
                // but a cast that keeps NOT landing — comps missing, silence, or a fizzle
                // streak (skill too low) — must not re-fire forever, burning components each
                // try: after a few consecutive failures go passive and let natural regen
                // lift the gate. Any success resets the streak.
                if (outcome == CastOutcome.Success)
                    _regenCastFailures = 0;
                else if (++_regenCastFailures == MaxRegenCastFailures)
                    WarnRegenOnce("recovery casts keep failing - slowing the retry cadence but STILL trying (natural regen will let one land).");
            }
        }

        /// <summary>Doc 18 §7 leaves the StatusMessage type ids uncataloged — /nbdebug logs
        /// every (Type, Text) pair so one live session produces the corpus table for free.</summary>
        private void OnStatusMessage(object sender, StatusMessageEventArgs e)
        {
            try { DebugLog($"status type={e.Type} text=\"{e.Text}\""); }
            catch (Exception ex) { LogException(ex); }
        }

        /// <summary>Stop routing the in-flight cast to anyone (cycle finished/aborted/
        /// replaced) — but deliberately DON'T clear the monitor or IsCasting: the physical
        /// cast may still be executing in the client, and its outcome line must drain (or the
        /// watchdog fire) first. Otherwise a replacement cycle's first CastSpell would be
        /// swallowed by the still-casting client and the OLD cast's "You cast …" line would
        /// resolve the NEW cast as a false success, silently skipping a spell. While the
        /// orphan drains, IsCasting keeps the next cycle in Busy — exactly right.</summary>
        private void OrphanPendingCast()
        {
            _pending = PendingCast.None;
        }

        /// <summary>Remaining seconds on the active enchantment for <paramref name="spellId"/>,
        /// or -1 if it isn't active. Feeds the enchant-refresh success signal in CycleTick.</summary>
        private int EnchantSeconds(int spellId)
        {
            try
            {
                foreach (var en in _state.ActiveEnchantments)
                    if (en != null && en.SpellId == spellId) return en.SecondsRemaining;
            }
            catch { }
            return -1;
        }

        // ---- the cycle loop (NB3's sequential runner, driven by the timer) --------------

        private void CycleTick(object sender, EventArgs e)
        {
            try
            {
                if (_cycle == null) { _timer.Stop(); return; }

                // Portal space: keep the model, defer every side effect (doc 13 §10.3).
                if (ActionsGated()) return;

                // Chat-independent SUCCESS signal (doc 18 §2/§3 belt-and-braces): a self-buff that
                // lands makes its enchantment's remaining time JUMP UP (add: absent->full; refresh:
                // low->full). Enchant time otherwise only ticks DOWN, so any increase for the
                // pending spell is an unambiguous landed-cast — resolve Success without waiting on
                // the chat line. This is what makes RECASTING already-active buffs (refreshes, which
                // fire no add event) resolve fast instead of hitting the 10 s watchdog.
                if (_monitor.CastInFlight && _pending == PendingCast.Cycle)
                {
                    int nowSecs = EnchantSeconds(_monitor.PendingSpellId);
                    if (nowSecs > _pendingEnchantSecondsAtCast)
                    {
                        DebugLog($"  enchant-refresh 0x{_monitor.PendingSpellId:X4}: {_pendingEnchantSecondsAtCast}s -> {nowSecs}s (landed)");
                        _monitor.Reset();
                        ResolveCast(CastOutcome.Success);
                    }
                }

                // Watchdog: a cast whose outcome never arrived is a Timeout (retry, capped) —
                // never a wedged cycle. Wrap-safe tick arithmetic inside the monitor.
                if (_monitor.CheckTimeout(Environment.TickCount))
                    ResolveCast(CastOutcome.Timeout);

                var step = _cycle.Tick(_state);
                switch (step.Kind)
                {
                    case StepKind.EnterMagicMode: _state.EnsureMagicMode(); break;
                    case StepKind.Busy:           break; // cast in flight — wait a tick
                    case StepKind.Equip:
                        // BusyState gates every item manipulation (doc 13 §10.4); retry next tick.
                        // AutoWield, not UseItem: UseItem is double-click semantics and would
                        // UNequip an already-wielded item (the planner also skips those).
                        if (!_state.ClientIdle) { break; }
                        Host.Actions.AutoWield(step.Action.TargetGuid);        // confirmed: dump line "void AutoWield(int item)"
                        _cycle.ReportCastResult(true);
                        break;
                    case StepKind.Cast:
                        // DON'T fire a cast into a still-recovering client. AC silently DROPS a
                        // cast issued during post-cast recovery — the dropped cast then produces no
                        // windup/chat/enchant and waits out the full watchdog (the "~10 s on every
                        // other cast", confirmed in nb3-debug: a cast begun 9 ms after the previous
                        // landed timed out, and the retry cast fine). Gate on BusyState==0 (doc 13
                        // §10.4, the "ready" signal) AND a short settle after the last cast landed,
                        // so we don't depend solely on BusyState covering the recovery tail.
                        if (!_state.ClientIdle ||
                            unchecked(Environment.TickCount - _lastCastResolvedTick) < CastSettleMs)
                        {
                            if (_debug) DebugLog($"  cast deferred: {(_state.ClientIdle ? "settle" : "client busy")} (since resolve {unchecked(Environment.TickCount - _lastCastResolvedTick)} ms)");
                            break;
                        }
                        _monitor.BeginCast(step.Action.SpellId, Environment.TickCount);
                        _castBeganTick = Environment.TickCount;
                        _pending = PendingCast.Cycle;
                        _state.IsCasting = true;
                        _pendingEnchantSecondsAtCast = EnchantSeconds(step.Action.SpellId); // snapshot for the refresh signal
                        DebugLog($"cast begin 0x{step.Action.SpellId:X4} -> 0x{step.Action.TargetGuid:X8} (enchant now {_pendingEnchantSecondsAtCast}s)");
                        Host.Actions.CastSpell(step.Action.SpellId, step.Action.TargetGuid); // confirmed API
                        // Success/fizzle/resist + the IsCasting clear arrive via
                        // OnChatBoxMessage / OnChangeEnchantments → ResolveCast.
                        break;
                    case StepKind.RegenMana:      RunManaRegen(step); break;
                    case StepKind.Paused:         break;
                    case StepKind.Done:
                        _timer.Stop();
                        OrphanPendingCast();
                        int csecs = Math.Max(0, unchecked(Environment.TickCount - _cycleStartedAt)) / 1000;
                        // The original: "Buff cycle completed in %u:%02i." + BuffComplete.wav.
                        Say($"Buff cycle completed in {csecs / 60}:{csecs % 60:00}. " +
                            $"{_cycle.SpellsCast} cast, {_cycle.Fizzles} fizzle(s), " +
                            $"{_cycle.Resists} resist(s), {_cycle.Timeouts} timeout(s), " +
                            $"{_cycle.Skipped} skipped, {_cycle.BusyHits} busy.");
                        PlayBuffComplete();
                        HideCastingView();
                        _cycle = null;
                        break;
                }
                UpdateStatusView();
                RefreshCastingView();
            }
            catch (Exception ex) { LogException(ex); }
        }

        /// <summary>One atomic mana-regen action per tick — the execution half of NB3's five
        /// Options modes. The pure <see cref="ManaRegenController"/> decides WHAT (drink /
        /// rest / kit / recovery cast), this method performs it with the same discipline as
        /// the cycle: BusyState gate on every item use, recovery casts through the cast
        /// monitor, everything on the Decal timer thread. Rest and Unavailable are passive —
        /// the tick just waits (vitals regenerate on their own; the cycle's mana gate lifts
        /// the moment CurrentMana reaches the requirement).</summary>
        private void RunManaRegen(CycleStep step)
        {
            // Heal/replenish floors are per-character percentages of max (doc: H2M drains
            // health, so this is a survival gate that must scale with the character's vital).
            var thresholds = new RegenThresholds();
            if (_settings != null)
            {
                thresholds.HealthFloor = _settings.HealthFloorPercent;
                thresholds.StaminaFloor = _settings.StaminaFloorPercent;
            }
            // Optional consumable fallbacks for the spell-recovery mode — both off unless the player
            // opted in (potions via /nbset potions or the Options checkbox; kits via the Options kit
            // tiers). The pure controller only *chooses* a consumable; UseRegenItem finds it and
            // warns+waits if it isn't carried, so a disabled/absent item is harmless.
            var consumables = new RegenConsumables
            {
                Potions = _settings != null && _settings.UsePotions,
                Kits = _settings != null && _settings.HealingKits != HealingKitTiers.None,
            };
            var controller = new ManaRegenController(
                step.RegenMode, _recovery ?? new RecoverySpells(), step.RequiredMana, thresholds, consumables);
            var action = controller.Next(_state);

            switch (action.Kind)
            {
                case RegenActionKind.Done: break;      // gate passes on the next Tick
                case RegenActionKind.Rest: break;      // passive: stamina comes back by itself

                case RegenActionKind.Unavailable:
                    WarnRegenOnce($"mana regen unavailable ({action.Reason}) - waiting on natural regen.");
                    break;

                case RegenActionKind.DrinkTradeManaElixir:
                    UseRegenItem(RegenItems.FindManaElixir(_state), "mana elixir", onSelf: false);
                    break;

                case RegenActionKind.DrinkStaminaElixir:
                    UseRegenItem(RegenItems.FindStaminaElixir(_state), "stamina elixir", onSelf: false);
                    break;

                case RegenActionKind.UseHealingKit:
                    // Spell mode pre-finds the best kit (auto-scan) and passes its guid; the legacy
                    // "kits + H2M" mode leaves it 0, so fall back to the Options-tier name lookup.
                    UseRegenItem(
                        action.ItemGuid != 0
                            ? action.ItemGuid
                            : RegenItems.FindHealingKit(_state, _settings != null ? _settings.HealingKits : HealingKitTiers.None),
                        "healing kit (check the Options tiers)", onSelf: true);
                    break;

                case RegenActionKind.DrinkPotion:
                    // Auto-scanned per-vital drink (health / stamina / mana). The guid was chosen from
                    // live item properties (BoosterEnum/BoostValue), so just use it by itself.
                    UseRegenItem(action.ItemGuid, action.Vital + " potion", onSelf: false);
                    break;

                case RegenActionKind.Cast:
                    // Same post-cast settle as the buff path (doc 13 §10.4): a recovery cast fired
                    // into the client's post-cast recovery tail is SILENTLY DROPPED and waits out the
                    // full 10 s watchdog — the "healing takes forever to start the next spell" report.
                    // Gate on BusyState==0 AND the settle window after the last cast landed.
                    if (!_state.ClientIdle ||
                        unchecked(Environment.TickCount - _lastCastResolvedTick) < CastSettleMs)
                        break;
                    // Keep trying recovery casts — never abandon the mode and strand the cycle on
                    // slow natural regen (the owner's report). After a failure streak, only throttle
                    // the cadence: natural regen trickles up between tries, so a later attempt lands
                    // once there's enough mana for one cast. Any success resets the streak (ResolveCast).
                    if (_regenCastFailures >= MaxRegenCastFailures
                        && unchecked(Environment.TickCount - _lastRegenCastTick) < RegenRetryBackoffMs)
                        break;   // throttled this tick, not abandoned — retry after the backoff
                    // S2M/H2M/Revit are self-casts: the target argument is ignored for
                    // untargetted spells (doc 18 §1), so 0 is correct and unambiguous.
                    _lastRegenCastTick = Environment.TickCount;
                    _monitor.BeginCast(action.SpellId, Environment.TickCount);
                    _castBeganTick = Environment.TickCount;
                    _pending = PendingCast.Regen;
                    _state.IsCasting = true;
                    Host.Actions.CastSpell(action.SpellId, 0);
                    break;
            }
        }

        /// <summary>UseItem with the doc-13 §10.4 BusyState gate (busy → silently retry next
        /// tick). Elixirs are used "by themselves" (useState 0, the potion convention);
        /// healing kits apply to the CURRENT SELECTION (useState 1), so self-heal selects
        /// self first — both semantics straight from the shipped XML doc (doc 18 §1).</summary>
        private void UseRegenItem(int guid, string what, bool onSelf)
        {
            if (guid == 0) { WarnRegenOnce($"no {what} in inventory - waiting on natural regen."); return; }
            if (!_state.ClientIdle) return;
            if (onSelf)
            {
                Host.Actions.SelectItem(_state.SelfId);   // confirmed: HooksWrapper.SelectItem
                Host.Actions.UseItem(guid, 1);            // 1 = use on current selection
            }
            else
            {
                Host.Actions.UseItem(guid, 0);            // 0 = use by itself (potion)
            }
        }

        private void WarnRegenOnce(string msg)
        {
            if (_regenWarned) return;
            _regenWarned = true;
            Say(msg);
        }

        /// <summary>Push model state into the control-view labels (recovered names from
        /// nb3-control.xml). Called from the ~4 Hz poll and after cycle events; writes are
        /// diffed per label so a quiet frame costs a handful of string compares.</summary>
        private void UpdateStatusView()
        {
            if (_view == null) return;
            SetLabel("staticCurrentConfig", _currentProfileName ?? "(none loaded)");

            if (_cycle == null)
            {
                SetLabel("staticStatus", "Idle");
                SetLabel("staticCasting", "nothing");
                return;
            }

            SetLabel("staticStatus", ActionsGatedPeek() ? "Portal space (deferred)" : _cycle.State.ToString());
            SetLabel("staticSpellsCount", _cycle.TotalSpells.ToString());
            SetLabel("staticSpellsLeft", _cycle.SpellsLeft.ToString());
            SetLabel("staticFizzle", _cycle.Fizzles.ToString());
            SetLabel("staticBusy", _cycle.BusyHits.ToString());
            SetLabel("staticMana", _state.CurrentMana.ToString());

            int secs = Math.Max(0, unchecked(Environment.TickCount - _cycleStartedAt)) / 1000;
            SetLabel("staticTimer", $"{secs / 60}:{secs % 60:00}");

            var cur = _cycle.State == CycleState.Running ? _cycle.Current : null;
            SetLabel("staticCasting", cur != null ? (cur.Description ?? $"spell 0x{cur.SpellId:X4}") : "nothing");
        }

        /// <summary>Read-only view of the portal gate for display — must NOT trip the
        /// watchdog release the way <see cref="ActionsGated"/> deliberately does.</summary>
        private bool ActionsGatedPeek() =>
            _inPortalSpace && Environment.TickCount - _portalEnteredAt <= PortalWatchdogMs;

        // ---- spell table (built lazily: FileService data is only interesting post-login,
        //      and /nbuff can only run in-world; doc 16 §7.5 fallback lives inside) ---------

        private NB3.Core.Modern.SpellInfo ResolveSpell(int id)
        {
            EnsurePlanner();
            return _liveTable != null ? _liveTable.ById(id) : null;
        }

        private void EnsurePlanner()
        {
            if (_planner != null) return;
            _liveTable = new DecalSpellTable(CoreManager.Current);
            _planner = new NB3.Core.Modern.ModernBuffPlanner(_liveTable);
        }

        // ---- chat commands (recovered from the original string table) ------------------

        private void OnCommandLine(object sender, ChatParserInterceptEventArgs e)
        {
            try
            {
                var text = (e.Text ?? "").Trim();
                if (!text.StartsWith("/nb", StringComparison.OrdinalIgnoreCase)) return;

                var parts = text.Split(new[] { ' ' }, 2);
                var cmd = parts[0].ToLowerInvariant();
                var arg = parts.Length > 1 ? parts[1].Trim() : "";

                switch (cmd)
                {
                    case "/nbuff":   e.Eat = true; StartCycle(arg); break;
                    case "/nbnew":   e.Eat = true; CreateProfile(arg); break;
                    case "/nbgen":   e.Eat = true; GenerateProfile(arg); break;
                    case "/nbskills":e.Eat = true; PrintSkills(); break;
                    case "/nbset":   e.Eat = true; SetOption(arg); break;
                    case "/nbpause": e.Eat = true; _cycle?.Pause(); Say("Paused."); break;
                    case "/nbresume":e.Eat = true; _cycle?.Resume(); _lastCastResolvedTick = Environment.TickCount; _timer.Start(); Say("Resumed."); break;
                    case "/nbabort": e.Eat = true; AbortCycle(); break;
                    case "/nboptions": e.Eat = true; OpenOptionsView(); break;
                    case "/nbed":    e.Eat = true; ToggleEditorView(); break;
                    case "/nbinclude": e.Eat = true; AddIncludeFromChat(arg); break;
                    case "/nbrefresh": e.Eat = true; RefreshProfileLists(true); break;
                    case "/nbstatus":e.Eat = true; PrintStatus(); break;
                    case "/nbid":    e.Eat = true; PrintTargetId(); break;
                    case "/nbcovers":e.Eat = true; PrintCovers(); break;
                    case "/nbdebug": e.Eat = true; _debug = !_debug; Say($"Debug logging {(_debug ? "ON -> nb3-debug.txt" : "off")}."); break;
                    case "/nbdiag":  e.Eat = true; PrintDiag(); break;
                    case "/nbcat":   e.Eat = true; PrintCategory(arg); break;
                    case "/nbreset": e.Eat = true; ResetWindows(); break;
                    case "/nbhelp":  e.Eat = true; PrintHelp(); break;
                }
            }
            catch (Exception ex) { LogException(ex); }
        }

        /// <summary>Load one or more profiles (the original's
        /// "/nbuff Profile Name1[.xml],Profile Name2[.xml],..." syntax), resolve their
        /// &lt;Include&gt; chains, and start the cycle.</summary>
        private void StartCycle(string profileArg)
        {
            if (string.IsNullOrEmpty(profileArg)) { Say("(specify a profile name)"); return; }

            var store = Store();
            var warnings = new System.Collections.Generic.List<string>();

            // A trailing "force" (or "-f" / "all") token recasts every buff even if it's already
            // active — the explicit full rebuff. Strip it before parsing profile names.
            bool force = false;
            {
                var toks = profileArg.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                var kept = new System.Collections.Generic.List<string>();
                foreach (var t in toks)
                {
                    var tl = t.Trim().ToLowerInvariant();
                    if (tl == "force" || tl == "-f" || tl == "all") { force = true; continue; }
                    kept.Add(t);
                }
                profileArg = string.Join(" ", kept.ToArray());
            }

            // Merge the named profiles (comma syntax) into one synthetic top-level profile
            // whose Includes are the requested names, then flatten — the same duplicate/
            // recursion guards apply across both mechanisms. The synthetic "(launch)" name
            // can never collide with a real profile ('(' is filename-hostile), so the
            // recursion seed can't false-positive on the first profile.
            var top = new NB3.Core.Modern.ModernProfile { Name = "(launch)" };
            foreach (var raw in profileArg.Split(','))
            {
                var n = NB3.Core.Modern.ModernProfileStore.Canon(raw);
                if (n.Length == 0) continue;
                if (!store.Exists(n)) { Say($"Profile not found: {n}"); return; }
                top.Includes.Add(n);
            }
            if (top.Includes.Count == 0) { Say("(specify a profile name)"); return; }
            var seedName = string.Join("+", top.Includes.ToArray());

            EnsurePlanner();
            LoadCharacterConfig();
            OrphanPendingCast();               // orphan any stale cast: it drains via chat/watchdog first

            var profile = NB3.Core.Modern.ModernProfile.ResolveIncludes(
                top, name => store.Exists(name) ? store.Load(name) : null, warnings);
            profile.Name = seedName;

            // Skill-aware level cap (ACE fizzle model): pick the highest level the character can
            // land at >= MinCastChancePercent, not just the highest known. Skill read live from
            // CharacterFilter.EffectiveSkill; a 0 read fails open to the old highest-known pick.
            var skillPolicy = (_settings != null && _settings.SkillBasedLevel)
                ? new NB3.Core.Modern.SkillPolicy { Enabled = true, MinChancePercent = _settings.MinCastChancePercent }
                : null;

            // Rebuff policy. By DEFAULT (RecastActiveBuffs on, and the original NB2's behaviour)
            // /nbuff casts the whole list every time — buffs you already have are recast. Only
            // when the player turns that off does it skip active buffs (honouring rebuffmins).
            // 'force' always recasts. So the empty-plan-because-already-buffed case only happens
            // when the player has explicitly opted into skipping.
            bool recastAll = force || _settings == null || _settings.RecastActiveBuffs;
            var rebuff = new NB3.Core.Modern.RebuffPolicy
            {
                ForceAll = recastAll,
                MinSecondsRemaining = _settings != null ? _settings.RebuffMinutesRemaining * 60 : 0,
            };

            // Candidate spells come from the character's SPELLBOOK, read fresh here (same cadence as
            // the skill read). This is what keeps selection honest: only spells you actually know are
            // considered, so the client spell table's monster/boss/quest entries can never be chosen.
            // A 0 read means the book isn't available/synced — refuse rather than fall back to the
            // whole table (the old fail-open bug that let an 800-power boss enchant get cast).
            int knownCount = _state.RefreshSpellBook();
            if (knownCount == 0)
            {
                Say("couldn't read your spellbook (0 spells known) - not casting, so nothing bogus gets thrown. Try again in a moment; /nbdiag if it persists.");
                return;
            }

            var plan = _planner.Plan(profile, _state, skillPolicy, rebuff);

            // Empty plan? Say WHY, truthfully — read from the plan, don't assume "already buffed".
            if (plan.Actions.Count == 0)
            {
                int wanted = profile.Buffs.Count + profile.EquipItems.Count;
                if (wanted == 0)
                    Say($"'{seedName}' has no spells or equips yet. Open the editor (/nbed) and add some.");
                else if (plan.Unresolved.Count > 0 && plan.SkippedAlreadyActive == 0)
                {
                    // The real failure: none of the buffs resolve to a castable spell. This is
                    // NOT a deleted profile (the {wanted} count proves the buffs are all there) —
                    // the spells just aren't in your spellbook or this server's spell table.
                    var sample = string.Join(", ", plan.Unresolved.GetRange(0, Math.Min(4, plan.Unresolved.Count)).ToArray());
                    Say($"'{seedName}' has {wanted} buff(s) but none resolve to a castable spell right now: {plan.Unresolved.Count} unresolved ({sample}{(plan.Unresolved.Count > 4 ? ", …" : "")}).");
                    Say("Your profile is intact (open /nbed to see it). Likely: those spells aren't in your spellbook, or the profile was built against a different server's spell data. /nbnew makes a fresh profile resolved to THIS character.");
                }
                else if (!recastAll)
                    Say($"'{seedName}' — you're already buffed ({plan.SkippedAlreadyActive} active). Skipping is on (/nbset recast 1 to always recast, or /nbuff {seedName} force just this once).");
                else
                    Say($"'{seedName}' — {wanted} buff(s) but nothing to cast: {plan.SkippedAlreadyActive} already active, {plan.Unresolved.Count} unresolved. (Profile is intact — /nbed to view.)");
                return;   // no cycle to start
            }

            // Doc 14 §6.3 vs the Options "Reduced chat output": always say SOMETHING, but
            // quiet mode collapses the per-line warning spam into one count.
            foreach (var w in warnings) Say(w);
            if (_settings != null && _settings.QuietMode && plan.Warnings.Count > 3)
                Say($"{plan.Warnings.Count} plan warning(s) suppressed (quiet mode).");
            else
                foreach (var w in plan.Warnings) Say(w.Message);

            _cycle = new BuffCycle(plan, _cycleOptions);
            _cycle.Start();
            _cycleStartedAt = Environment.TickCount;
            _currentProfileName = profile.Name;
            _lastCastResolvedTick = unchecked(Environment.TickCount - CastSettleMs); // no recovery to wait for at a fresh start
            _timer.Start();
            Say($"Buffing '{profile.Name}': {_cycle.TotalSpells} spell(s), {_cycle.TotalEquips} equip(s).");
            ShowCastingView();                 // the original opened "NB3 Spells" for the cycle
            UpdateStatusView();
        }

        private void AbortCycle()
        {
            if (_cycle == null) { Say("Can't Abort, there's no cycle in progress!"); return; }
            _cycle.Abort();
            OrphanPendingCast();
            Say("Abort.");
            HideCastingView();
        }

        /// <summary>/nbnew [name] — write a starter self-buff profile (the 17-line classic
        /// set: attributes, life staples, protections) resolved against the LIVE table by
        /// the era-stable level-1 names, stored as stacking categories. Gives a fresh
        /// install something to /nbuff before the Editor view exists.</summary>
        private void CreateProfile(string name)
        {
            if (string.IsNullOrEmpty(name)) name = "default";
            var path = ProfilePath(name);
            if (File.Exists(path)) { Say($"Profile already exists: {name}"); return; }

            EnsurePlanner();
            var profile = NB3.Core.Modern.ModernProfileFactory.CreateDefaultSelf(
                _liveTable, out var unresolved);
            profile.Name = name;
            File.WriteAllText(path, profile.ToXml());

            RefreshProfileLists(false);              // data changed -> repopulate the dropdowns (doc 14 §4)
            Say($"Created profile '{name}' with {profile.Buffs.Count} self buff(s). Start it: /nbuff {name}");
            if (unresolved.Count > 0)
                Say($"(couldn't resolve {unresolved.Count} line(s) on this server: {string.Join(", ", unresolved.ToArray())})");
        }

        /// <summary>/nbgen [name] — generate a character-specific self-buff profile from the LIVE
        /// trained/specialized skills. Order is the casting-stat bootstrap first (Focus, Willpower,
        /// Creature Enchantment, Mana Conversion, Life Magic), then attributes, the three defences
        /// and every weapon/utility mastery the character has, Life vitals + the 7 protections, the
        /// 7 banes + Impenetrability, and the weapon auras that match how the character fights
        /// (melee: Blood Drinker/Heart Seeker/Defender/Swift Killer; missile: the same minus Heart
        /// Seeker; war/void caster: Spirit Drinker + Hermetic Link only). Overwrites the named
        /// profile (default 'generated'); fine-tune afterwards in /nbed.</summary>
        private void GenerateProfile(string name)
        {
            if (!LoggedIn()) { Say("log in first - /nbgen reads your trained/specialized skills."); return; }
            LoadCharacterConfigIfNeeded();
            EnsurePlanner();

            name = NB3.Core.Modern.ModernProfileStore.Canon(string.IsNullOrEmpty(name) ? "generated" : name);
            if (!NB3.Core.Modern.ModernProfileStore.ValidName(name)) { Say($"'{name}' isn't a valid profile name."); return; }

            bool existed = File.Exists(ProfilePath(name));
            var result = GenerateProfileFile(name);
            if (result == null)
            { Say("spell catalog isn't ready yet - try again in a moment (or see /nbdiag)."); return; }

            Say($"{(existed ? "Regenerated" : "Generated")} '{name}': {result.Profile.Buffs.Count} buff(s) "
                + $"[castable schools: Creature={(result.CreatureCastable ? "y" : "n")} Item={(result.ItemCastable ? "y" : "n")} Life={(result.LifeCastable ? "y" : "n")}]. Start it: /nbuff {name}");
            if (result.SkippedUntrained.Count > 0)
                Say($"skipped {result.SkippedUntrained.Count} skill buff(s) - skill not trained/spec: {CapList(result.SkippedUntrained)}");
            if (result.Unresolved.Count > 0)
                Say($"{result.Unresolved.Count} family/families not on this server (skipped): {CapList(result.Unresolved)}");
            if (!result.CreatureCastable && !result.ItemCastable && !result.LifeCastable)
                Say("note: no magic school reads as trained - is skill data readable on this build? check /nbskills.");

            // Leave the just-generated profile selected in the main window, ready to START.
            RequestMainProfileSelection(name);
        }

        /// <summary>Shared generator core behind <c>/nbgen</c> and the login auto-onboard: build the
        /// character-specific self-buff set from the LIVE trained/specialized skills and write it to
        /// <paramref name="canonName"/> (canonical form assumed). OVERWRITES — callers that must not
        /// clobber an existing profile check <see cref="ModernProfileStore.Exists"/> first. Returns
        /// the generation result, or <c>null</c> if the family catalog isn't ready yet (the caller
        /// decides whether to retry). Emits no chat — the caller owns messaging — but refreshes the
        /// dropdowns so the new file shows up.</summary>
        private NB3.Core.Modern.GenerationResult GenerateProfileFile(string canonName)
        {
            var catalog = EnsureFamilyCatalog();
            if (catalog == null || catalog.Count == 0) return null;   // not ready — caller waits/reports
            var result = NB3.Core.Modern.ProfileGenerator.Generate(
                catalog, _state.SkillTrainingLevel, new NB3.Core.Modern.GeneratorOptions());
            result.Profile.Name = canonName;
            File.WriteAllText(ProfilePath(canonName), result.Profile.ToXml());
            RefreshProfileLists(false);                               // data changed -> repopulate dropdowns (doc 14 §4)
            return result;
        }

        /// <summary>Select <paramref name="name"/> in the main window's profile dropdown and show it
        /// as the current config. Deferred through <see cref="_autoSelectProfile"/> so it survives
        /// the combo being lazily realized (or rebuilt on VVS) — the next populate honours it — and
        /// is one-shot, so it never overrides a later manual pick.</summary>
        private void RequestMainProfileSelection(string name)
        {
            if (string.IsNullOrEmpty(name)) return;
            _autoSelectProfile = name;
            _currentProfileName = name;              // reflect in the "Current:" label immediately
            _populated.Remove("choiceLoadConfig");   // force a repopulate so a NEW profile appears + selection applies
        }

        /// <summary>Login onboarding, serviced from the throttled poll: once the character's skills
        /// and name have settled after LoginComplete, generate a self-buff profile named after the
        /// character — but ONLY if one doesn't already exist, so an edited profile is never
        /// clobbered — and select it in the main window. Net effect: a brand-new user logs in and is
        /// immediately ready to press START, with no profile to create. Honours the per-character
        /// <c>autogen</c> option, waits (up to a timeout) for skill/name data to be readable, and
        /// refuses to write an empty profile for a character with no castable magic school.</summary>
        private void ServiceAutoOnboard()
        {
            if (!_autoOnboardPending) return;
            if (!LoggedIn()) return;                        // wait until we're actually in-world
            if (ActionsGatedPeek()) return;                 // still in portal space — nothing to do yet

            int since = unchecked(Environment.TickCount - _loginCompleteTick);
            if (since < AutoOnboardSettleMs) return;        // give CharacterFilter a beat to sync skills/name
            bool timedOut = since > AutoOnboardTimeoutMs;

            // Everything below can touch the filesystem (per-character config parse, profile write)
            // and now runs on the ~4 Hz poll, so a thrown exception (corrupt config_*.xml, a locked
            // or unwritable data folder) must NOT leave _autoOnboardPending set and re-throw every
            // tick forever. Bound it: log once and give up onboarding for this login (the manual
            // /nbgen path still works). The outer Core_RenderFrame catch is the last resort; this
            // inner one exists specifically to stop the retry storm and honour the timeout contract.
            try
            {
                // Load THIS character's settings (reload on a character switch — the cached ones may
                // be the previous character's), then honour the per-character opt-out.
                int liveId = 0;
                try { liveId = CoreManager.Current.CharacterFilter.Id; } catch { }
                if (_settings == null || _settings.CharacterId != liveId) { _settings = null; LoadCharacterConfig(); }
                if (_settings != null && !_settings.AutoGenerateOnLogin) { _autoOnboardPending = false; return; }

                // Character name -> profile name.
                string charName = null;
                try { charName = CoreManager.Current.CharacterFilter.Name; } catch { }
                charName = NB3.Core.Modern.ModernProfileStore.Canon(charName ?? "");
                if (charName.Length == 0 || !NB3.Core.Modern.ModernProfileStore.ValidName(charName))
                {
                    if (timedOut) _autoOnboardPending = false;  // name never read — give up quietly
                    return;
                }

                EnsurePlanner();

                // Returning user: a profile with this name already exists. Don't regenerate (keep
                // their edits) — just select it, silently, so it isn't chat spam on every login.
                if (Store().Exists(charName))
                {
                    _autoOnboardPending = false;
                    RequestMainProfileSelection(charName);
                    return;
                }

                // First time: need at least one trained/spec magic school to build a castable set. If
                // skills aren't readable yet, keep waiting until the timeout rather than writing an
                // empty profile (or one for a non-caster who has nothing to self-buff).
                bool anyMagicTrained =
                    _state.SkillTrainingLevel(31) >= 2 ||   // Creature Enchantment
                    _state.SkillTrainingLevel(32) >= 2 ||   // Item Enchantment
                    _state.SkillTrainingLevel(33) >= 2;     // Life Magic
                if (!anyMagicTrained)
                {
                    if (timedOut) _autoOnboardPending = false;
                    return;
                }

                var result = GenerateProfileFile(charName);
                if (result == null)
                {
                    if (timedOut) _autoOnboardPending = false;  // catalog never became ready
                    return;
                }

                _autoOnboardPending = false;
                RequestMainProfileSelection(charName);
                Say($"Welcome, {charName}! Auto-generated the self-buff profile '{charName}' "
                    + $"({result.Profile.Buffs.Count} buff(s)) and selected it - press START to buff, "
                    + "/nbed to customize. (/nbset autogen 0 to disable this.)");
                if (result.SkippedUntrained.Count > 0 || result.Unresolved.Count > 0)
                    Say($"(built from your trained/spec skills; {result.SkippedUntrained.Count} untrained "
                        + $"and {result.Unresolved.Count} unavailable buff(s) skipped)");
            }
            catch (Exception ex)
            {
                // Persistent failure (most likely a corrupt config or an unwritable folder): stop
                // trying so we don't re-run generation + config parse at ~4 Hz until logout.
                _autoOnboardPending = false;
                LogException(ex);
                Say("couldn't auto-generate a profile at login (see nb3-errors.txt) - use /nbgen when ready, or /nbset autogen 0 to silence this.");
            }
        }

        private static string CapList(System.Collections.Generic.List<string> xs, int max = 10) =>
            xs.Count <= max
                ? string.Join(", ", xs.ToArray())
                : string.Join(", ", xs.GetRange(0, max).ToArray()) + $", +{xs.Count - max} more";

        // Skill id (CharFilterSkillType) -> short label, for /nbskills. Magic + defences first.
        private static readonly int[] SkillIds =
            { 31,32,33,34,43,16, 6,7,15, 1,2,3,4,5,9,10,11,12,13, 41,44,45,46,47,48,49,
              14,18,19,20,21,22,23,24,27,28,29,30,35,36,37,38,39,40,42,50,51,52,54 };
        private static readonly string[] SkillNms =
            { "Creature Ench","Item Ench","Life Magic","War Magic","Void Magic","Mana Conversion",
              "Melee Def","Missile Def","Magic Def",
              "Axe","Bow","Crossbow","Dagger","Mace","Spear","Staff","Sword","Thrown","Unarmed",
              "TwoHanded","Heavy Wpn","Light Wpn","Finesse Wpn","Missile Wpn","Shield","DualWield",
              "Arcane Lore","Item Tinker","Assess Person","Deception","Healing","Jump","Lockpick","Run",
              "Assess Creature","Wpn Tinker","Armor Tinker","MagicItem Tinker","Leadership","Loyalty",
              "Fletching","Alchemy","Cooking","Salvaging","Gearcraft","Recklessness","Sneak Attack",
              "Dirty Fighting","Summoning" };

        /// <summary>/nbskills — print the character's Specialized and Trained skills as NB3 reads
        /// them, so the /nbgen filter can be sanity-checked against the in-game panel.</summary>
        private void PrintSkills()
        {
            if (!LoggedIn()) { Say("log in first - /nbskills reads your live skills."); return; }
            var spec = new System.Collections.Generic.List<string>();
            var trn = new System.Collections.Generic.List<string>();
            for (int i = 0; i < SkillIds.Length && i < SkillNms.Length; i++)
            {
                int t = _state.SkillTrainingLevel(SkillIds[i]);
                if (t >= 3) spec.Add(SkillNms[i]);
                else if (t == 2) trn.Add(SkillNms[i]);
            }
            Say($"Specialized: {(spec.Count > 0 ? string.Join(", ", spec.ToArray()) : "(none read)")}");
            Say($"Trained: {(trn.Count > 0 ? string.Join(", ", trn.ToArray()) : "(none read)")}");
            if (spec.Count == 0 && trn.Count == 0)
                Say("no trained/spec skills read - skill data may be unavailable on this build, so /nbgen would come up empty.");
        }

        /// <summary>/nbset [key value] — the Options that matter to the casting loop,
        /// settable from chat. Persists per character. Keys: regen 0-6 | aggr &lt;pct&gt; |
        /// kits [p][t][e] | potions 0/1 | maxrec 1-7 | s2m7|h2m7|revit7|fallback6 0/1 | skillcap 0/1 |
        /// mincast &lt;pct&gt; | recast 0/1 | rebuffmins &lt;n&gt; | healthpct 1-99 | stampct 1-99.</summary>
        private void SetOption(string arg)
        {
            LoadCharacterConfigIfNeeded();
            var parts = (arg ?? "").Split(new[] { ' ' }, 2);
            var key = parts[0].Trim().ToLowerInvariant();
            var val = parts.Length > 1 ? parts[1].Trim() : "";

            if (key.Length == 0)
            {
                Say($"regen={(int)_settings.ManaRegenMode} ({_settings.ManaRegenMode})  aggr={_settings.ExpectedPctSpellCost}%  " +
                    $"manafloor={_settings.ManaFloorPercent}%  manatarget={_settings.ManaRegenTargetPercent}%  " +
                    $"kits={_settings.HealingKits}  potions={(_settings.UsePotions ? 1 : 0)}  maxrec={_settings.MaxRecoveryLevel}  healthpct={_settings.HealthFloorPercent}%  stampct={_settings.StaminaFloorPercent}%");
                Say($"s2m7={(_settings.UseS2M7 ? 1 : 0)}  h2m7={(_settings.UseH2M7 ? 1 : 0)} (h2m7 = Cannibalize, level-7 H2M)  revit7={(_settings.UseRevit7 ? 1 : 0)}  fallback6={(_settings.FallbackTo6OnUnknown7 ? 1 : 0)}");
                Say($"skillcap={(_settings.SkillBasedLevel ? 1 : 0)}  mincast={_settings.MinCastChancePercent}%  recast={(_settings.RecastActiveBuffs ? 1 : 0)}  rebuffmins={_settings.RebuffMinutesRemaining}  autogen={(_settings.AutoGenerateOnLogin ? 1 : 0)}  (recast 1 = always cast the whole list; recast 0 = skip active buffs)");
                Say("Set: /nbset regen 0-6 | aggr <pct> | manafloor <0-99> | manatarget <1-100> | kits [p][t][e] | potions 0/1 | maxrec 1-7 | s2m7|h2m7|revit7|fallback6 0/1 | skillcap 0/1 | mincast <pct> | recast 0/1 | rebuffmins <n> | healthpct <1-99> | stampct <1-99> | autogen 0/1");
                return;
            }

            int n; int.TryParse(val, out n);
            switch (key)
            {
                case "regen":
                    if (n < 0 || n > 6) { Say("regen: 0 none, 1 trade elixirs, 2 stam elixirs+S2M, 3 rest+S2M, 4 kits+H2M, 5 revit+S2M, 6 spells: S2M+Cannibalize+Revitalize (default)"); return; }
                    _settings.ManaRegenMode = (ManaRegenMode)n; break;
                case "aggr":
                    if (n < 1 || n > 400) { Say("aggr: percent of next spell's cost required before casting (e.g. 100)"); return; }
                    _settings.ExpectedPctSpellCost = n; break;
                case "manafloor":
                    if (n < 0 || n > 99) { Say("manafloor: regen when mana drops below N% of max (0-99; 0 = off, use the per-spell gate only). Needs a regen mode."); return; }
                    if (n != 0 && n >= _settings.ManaRegenTargetPercent) { Say($"manafloor must be below manatarget ({_settings.ManaRegenTargetPercent}%) or regen would re-trigger every spell."); return; }
                    _settings.ManaFloorPercent = n; break;
                case "manatarget":
                    if (n < 1 || n > 100) { Say("manatarget: once regen starts, top mana back up to N% of max (1-100)."); return; }
                    if (n <= _settings.ManaFloorPercent) { Say($"manatarget must be above manafloor ({_settings.ManaFloorPercent}%)."); return; }
                    _settings.ManaRegenTargetPercent = n; break;
                case "kits":
                    var kits = HealingKitTiers.None;
                    var v = val.ToLowerInvariant();
                    if (v.Contains("p")) kits |= HealingKitTiers.Plentiful;
                    if (v.Contains("t")) kits |= HealingKitTiers.Treated;
                    if (v.Contains("e")) kits |= HealingKitTiers.Peerless;
                    _settings.HealingKits = kits; break;
                case "maxrec":
                    if (n < 1 || n > 7) { Say("maxrec: 1-7 (max level for S2M/H2M/Revit)"); return; }
                    _settings.MaxRecoveryLevel = n; break;
                case "s2m7":      _settings.UseS2M7 = n != 0; break;
                case "h2m7":      _settings.UseH2M7 = n != 0; break;
                case "revit7":    _settings.UseRevit7 = n != 0; break;
                case "fallback6": _settings.FallbackTo6OnUnknown7 = n != 0; break;
                case "skillcap":  _settings.SkillBasedLevel = n != 0;
                    Say(n != 0 ? "skill cap ON: buffs use the highest level you can land reliably." : "skill cap OFF: buffs use the highest level you know (may fizzle a lot)."); break;
                case "mincast":
                    if (n < 1 || n > 100) { Say("mincast: minimum cast-success chance %, 1-100 (default 90). Lower = pushes higher levels, more fizzles."); return; }
                    _settings.MinCastChancePercent = n; break;
                case "recast":  _settings.RecastActiveBuffs = n != 0;
                    Say(n != 0 ? "recast ON (default): /nbuff always casts the whole list, refreshing buffs you already have."
                               : "recast OFF: /nbuff skips buffs still active (mana-saving). rebuffmins sets the refresh window; 'force' overrides."); break;
                case "autogen":  _settings.AutoGenerateOnLogin = n != 0;
                    Say(n != 0 ? "autogen ON (default): on login, NB3 generates a profile named after your character (if none exists) and selects it in the main window."
                               : "autogen OFF: NB3 won't auto-create or auto-select a profile at login. Use /nbgen and the dropdown yourself."); break;
                case "rebuffmins":
                    if (n < 0 || n > 240) { Say("rebuffmins (only when recast=0): recast buffs with fewer than N minutes left (0 = skip all active)."); return; }
                    _settings.RebuffMinutesRemaining = n; break;
                case "healthpct":
                    if (n < 1 || n > 99) { Say("healthpct: in the kits+H2M mode, heal when health drops below N% of max (1-99, default 50 = under half)."); return; }
                    _settings.HealthFloorPercent = n; break;
                case "stampct":
                    if (n < 1 || n > 99) { Say("stampct: in the S2M/rest/revit modes, replenish stamina below N% of max (1-99, default 50)."); return; }
                    _settings.StaminaFloorPercent = n; break;
                case "potions":
                    _settings.UsePotions = n != 0;
                    Say(n != 0 ? "potions ON: the spell-recovery mode may drink a mana elixir as a last-resort fallback."
                               : "potions OFF (default): spell-recovery uses spells only (S2M/Cannibalize/Revitalize)."); break;
                default: Say($"Unknown option '{key}'. /nbset for the list."); return;
            }

            _settings.Save(NB3Settings.PathFor(_settings.CharacterId));
            Say($"Set {key}. (Takes effect at the next /nbuff.)");
        }

        private void LoadCharacterConfigIfNeeded()
        {
            if (_settings == null) LoadCharacterConfig();
        }

        /// <summary>Read the per-character Options and resolve the recovery spells for THIS
        /// character's spellbook — at cycle start, so an Options change or a newly-learned
        /// S2M level is honoured without a relog. The recovery families come from NB3's own
        /// recovered table; its ids line up with the live/2012 numbering (MODERN_SPELL_MODEL
        /// §"ID reconciliation"), so the resolved ids are directly castable.</summary>
        private void LoadCharacterConfig()
        {
            if (_classicTable == null)
                _classicTable = SpellTable.Parse(ReadResource("NB3.Plugin.Resources.nb3-spells.xml"));

            int charId = 0;
            try { charId = CoreManager.Current.CharacterFilter.Id; } catch { }
            _settings = NB3Settings.Load(NB3Settings.PathFor(charId));
            _settings.CharacterId = charId;

            _recovery = RecoverySpells.Resolve(_classicTable, _state.SpellKnown, _settings);
            _cycleOptions = new CycleOptions
            {
                AggressivenessPercent = _settings.ExpectedPctSpellCost,
                ManaFloorPercent = _settings.ManaFloorPercent,
                ManaRegenTargetPercent = _settings.ManaRegenTargetPercent,
                ManaRegenMode = _settings.ManaRegenMode,
                S2MSpellId = _recovery.StaminaToMana,
                H2MSpellId = _recovery.HealthToMana,
                RevitalizeSpellId = _recovery.Revitalize,
            };
            _regenWarned = false;
            _regenCastFailures = 0;
            _lastRegenCastTick = 0;
        }

        // ---- recovered control events (names come from the recovered view XML) ---------
        // Every button echoes a chat line — success or state-gated no-op (doc 14 §6.3:
        // a silent no-op is indistinguishable from a broken button or a rendering bug).

        [MVControlEvent("pbStart", "Click")]
        private void PbStart(object s, MVControlEventArgs e)
        {
            try
            {
                var combo = Ctl<ICombo>("choiceLoadConfig");
                if (combo == null || combo.Selected <= 0) { Say("select a configuration in the dropdown first (or /nbnew to create one)."); return; }
                string name = null;
                try { name = combo.Text[combo.Selected]; } catch { }
                if (string.IsNullOrEmpty(name)) { Say("couldn't read the selected configuration - try /nbuff <name>."); return; }
                StartCycle(name);                                    // StartCycle echoes success/failure
            }
            catch (Exception ex) { LogException(ex); }
        }

        [MVControlEvent("pbPause", "Click")]
        private void PbPause(object s, MVControlEventArgs e)
        {
            try
            {
                if (_cycle == null) { Say("no cycle running."); return; }
                _cycle.Pause(); Say("Paused.");
            }
            catch (Exception ex) { LogException(ex); }
        }

        [MVControlEvent("pbResume", "Click")]
        private void PbResume(object s, MVControlEventArgs e)
        {
            try
            {
                if (_cycle == null) { Say("no cycle to resume."); return; }
                _cycle.Resume(); _lastCastResolvedTick = Environment.TickCount; _timer.Start(); Say("Resumed.");
            }
            catch (Exception ex) { LogException(ex); }
        }

        [MVControlEvent("pbAbort", "Click")]
        private void PbAbort(object s, MVControlEventArgs e)
        {
            try { AbortCycle(); }
            catch (Exception ex) { LogException(ex); }
        }

        [MVControlEvent("pbEditor", "Click")]
        private void PbEditor(object s, MVControlEventArgs e)
        {
            try { ToggleEditorView(); }
            catch (Exception ex) { LogException(ex); }
        }

        [MVControlEvent("pbOptions", "Click")]
        private void PbOptions(object s, MVControlEventArgs e)
        {
            try { OpenOptionsView(); }
            catch (Exception ex) { LogException(ex); }
        }

        // ---- diagnostics + window recovery (docs 03 §10.5, §10.13, §10.14, §10.16) ------

        /// <summary>The control names of the registered main view, for the §10.5 one-shot
        /// resolve diagnostic. Keep in sync with nb3-control.xml.</summary>
        private static readonly string[] MainViewControls =
        {
            "staticCurrentConfig", "choiceLoadConfig", "staticStatus", "staticSpellsCount",
            "staticSpellsLeft", "staticTimer", "staticFizzle", "staticBusy", "staticMana",
            "staticCasting", "pbStart", "pbPause", "pbResume", "pbAbort", "pbEditor", "pbOptions",
        };

        private void PrintDiag()
        {
            // Backend: what the window IS, plus the detection inputs then and now (§10.13).
            bool presentNow, runningNow; string verNow;
            ProbeVvs(out presentNow, out verNow, out runningNow);
            Say($"backend: {BackendName()} | VVS now: {verNow}, running={runningNow} | at Startup: {_vvsVersionAtStartup}, running={_vvsRunningAtStartup}");

            // Wireup health (§10.11).
            Say(_wireupError != null ? $"view creation FAILED: {_wireupError}" : MVWireupHelper.GetWireupReport(this));

            // ViewKey for surgical vvs.s3db support (§10.16): <AssemblyName>:<creation title>.
            var asmName = Assembly.GetExecutingAssembly().GetName().Name;
            Say($"ViewKey: {asmName}:NB3 (vvs.s3db, VVS install dir; edit only with the client closed)");

            if (_view == null) { Say("view: none - the UI never came up; commands remain functional."); return; }

            // Window geometry + presentation state (§10.14): Ghosted/ClickThrough/Alpha exist
            // on the VVS HudView only — read by reflection off the wrapper's Underlying;
            // "n/a" on the Decal backend is itself §10.13 fallback evidence.
            try { Say($"window: pos={_view.Location.X},{_view.Location.Y} size={_view.Size.Width}x{_view.Size.Height} visible={_view.Visible}"); } catch { }
            var hud = UnderlyingHudView();
            Say(hud == null
                ? "presentation: n/a (no VVS HudView - Decal backend)"
                : $"presentation: Ghosted={GetProp(hud, "Ghosted")} ClickThrough={GetProp(hud, "ClickThrough")} Alpha={GetProp(hud, "Alpha")}");

            // §10.5 one-shot control-resolution diagnostic: names that throw are in a
            // container the parser didn't descend (or on a tab not realized yet).
            var missing = new StringBuilderLite();
            foreach (var n in MainViewControls)
                if (Ctl<IControl>(n) == null) missing.Append(n);
            Say(missing.Empty ? "controls: 16/16 resolve." : $"controls MISSING: {missing}");

            PrintSkillDiag();
        }

        /// <summary>What the skill cap actually reads — the fix for "it's throwing level 8s my
        /// skill can't land." If these come back 0 the effective-skill read failed on this SDK
        /// build and the cap is silently OFF (fail-open to highest-known); if they're real
        /// numbers but 8s still fly, check <c>skillcap</c>/<c>mincast</c> below.</summary>
        private void PrintSkillDiag()
        {
            int cr = _state.EffectiveMagicSkill("Creature");
            int li = _state.EffectiveMagicSkill("Life");
            int it = _state.EffectiveMagicSkill("Item");
            int wr = _state.EffectiveMagicSkill("War");
            int vd = _state.EffectiveMagicSkill("Void");
            Say($"magic skill (effective): Creature={cr} Life={li} Item={it} War={wr} Void={vd}");
            if (cr == 0 && li == 0 && it == 0)
                Say("  ^ all 0 = skill read FAILED on this build -> skill cap is OFF (casting highest-known). Tell Nerf; /nbset skillcap 0 to silence, or expect fizzles.");
            var s = _settings;
            if (s != null)
                Say($"skillcap={(s.SkillBasedLevel ? "on" : "OFF")}  mincast={s.MinCastChancePercent}%  (a spell needs >= mincast predicted chance to be chosen)");
            else
                Say("skillcap/mincast: (log in and /nbset to view)");

            // Spellbook read — THE candidate pool. Selection only ever considers spells in here, not
            // the full client spell table. 0 = unreadable/unsynced (NB3 then casts nothing on purpose).
            int knownCount = _state.RefreshSpellBook();
            Say($"spellbook: {knownCount} spell(s) known -- candidates are drawn from HERE, not the full spell table.");
            if (knownCount == 0)
                Say("  ^ 0 = spellbook unreadable or not synced yet; NB3 casts nothing until it reads. Retry after a full login.");

            // Sample the cap's decision on a real spell — the highest-power Creature self buff YOU
            // ACTUALLY KNOW (filtered by the spellbook, so it is never a monster/boss table entry).
            // Shows the school string as read, its power, and the predicted cast chance at your skill.
            try
            {
                EnsurePlanner();
                NB3.Core.Modern.SpellInfo sample = null;
                if (_liveTable != null)
                    foreach (var sp in _liveTable.All)
                    {
                        if (sp.Target != NB3.Core.Modern.SpellTarget.Self) continue;
                        if ((sp.School ?? "").IndexOf("creature", StringComparison.OrdinalIgnoreCase) < 0) continue;
                        if (!_state.SpellKnown(sp.Id)) continue;   // only what's in YOUR spellbook
                        if (sample == null || sp.Level > sample.Level) sample = sp;
                    }
                if (sample != null)
                {
                    int sk = _state.EffectiveMagicSkill(sample.School);
                    double ch = NB3.Core.Modern.CastChance.SuccessChance(sk, sample.Level);
                    Say($"sample (highest KNOWN Creature self): '{sample.Name}' power={sample.Level} @ skill {sk} -> {(int)(ch * 100)}% cast "
                        + $"({(s != null && s.SkillBasedLevel && ch * 100 < s.MinCastChancePercent ? "would be CAPPED to a lower level" : "castable")}).");
                }
                else Say("sample: (no KNOWN Creature self spell yet — spellbook empty or not synced)");
            }
            catch (Exception ex) { LogException(ex); }
        }

        /// <summary>/nbcat &lt;group#&gt; — list every spell in a stacking group that YOU KNOW (the live
        /// table filtered by your spellbook), strongest first, with id / power / aim / name, plus the
        /// spell selection would actually cast for a SELF buff of that group. This is the diagnostic
        /// for the weapon-aura question: run <c>/nbcat 154</c> and you see the Self aura, the Other
        /// aura (now aimed Other, so a self buff skips it), and the classic item spell side by side —
        /// exactly what the selector sees. '*' marks an off-ladder Self special that a self buff skips.</summary>
        private void PrintCategory(string arg)
        {
            int cat;
            if (string.IsNullOrEmpty(arg) || !int.TryParse(arg.Trim(), out cat) || cat <= 0)
            {
                Say("usage: /nbcat <stacking-group#>  e.g. /nbcat 154 (Blood Drinker), 37 (Invulnerability), 41 (Magic Resist).");
                return;
            }
            try
            {
                EnsurePlanner();
                if (_liveTable == null) { Say("spell table not ready yet (log in, then retry; see /nbdiag)."); return; }
                int knownCount = _state.RefreshSpellBook();

                var rows = new System.Collections.Generic.List<NB3.Core.Modern.SpellInfo>();
                foreach (var sp in _liveTable.All)
                    if (sp != null && sp.Category == cat && _state.SpellKnown(sp.Id))
                        rows.Add(sp);
                rows.Sort((a, b) => b.Level.CompareTo(a.Level));   // strongest first

                Say($"group {cat}: {rows.Count} spell(s) YOU KNOW (spellbook {knownCount}); '*' = off-ladder for a self buff:");
                foreach (var sp in rows)
                {
                    bool offLadder = sp.Target == NB3.Core.Modern.SpellTarget.Self
                        && sp.Level > 300
                        && (sp.Name == null || sp.Name.IndexOf("Incantation", StringComparison.OrdinalIgnoreCase) < 0);
                    Say($"  {(offLadder ? "*" : " ")}0x{sp.Id:X4} pow={sp.Level,3} {sp.Target,-5} \"{sp.Name}\"");
                }

                // The money line: what selection actually casts for a SELF buff of this group
                // (active enchantments ignored here, so you see the intended pick even when buffed).
                var picks = new NB3.Core.Modern.ModernBuffSelector(_liveTable).Select(
                    new[] { new NB3.Core.Modern.DesiredBuff(cat, NB3.Core.Modern.SpellTarget.Self) },
                    _state.SpellKnown, System.Array.Empty<int>());
                if (picks.Count > 0)
                {
                    var picked = _liveTable.ById(picks[0].SpellId);
                    Say($"  => self-buff pick: 0x{picks[0].SpellId:X4} pow={picks[0].Level} \"{(picked != null ? picked.Name : "")}\"");
                }
                else
                    Say("  => self-buff pick: (none castable on yourself in this group — a self entry here is skipped)");
            }
            catch (Exception ex) { LogException(ex); }
        }

        /// <summary>/nbreset — the only in-game fix for per-window state VVS persists
        /// outside the plugin (doc 03 §10.14): restore the known-good position AND size
        /// (UserW/UserH persist; a window once dragged to its title bar stays 26px tall
        /// forever), clear pin/click-through, restore alpha, and show the window.</summary>
        private void ResetWindows()
        {
            if (_view == null) { Say("no view to reset (see /nbdiag)."); return; }

            // Every NB3 window goes back to its XML-default geometry; the main view is shown,
            // the secondary views return to hidden (reopen via the buttons /nbed //nboptions).
            ResetOneWindow(_view, HomePosition, visible: true);
            ResetOneWindow(ViewByTitle(OptionsTitle), OptionsHome, visible: false);
            ResetOneWindow(ViewByTitle(EditorTitle), EditorHome, visible: false);
            ResetOneWindow(ViewByTitle(CastingTitle), CastingHome, visible: _cycle != null);

            _labelCache.Clear();                 // labels rewrite on the next poll
            var hud = UnderlyingHudView();
            Say($"windows reset: main {HomePosition.X},{HomePosition.Y} {HomePosition.Width}x{HomePosition.Height} visible; Options/Editor re-hidden at their defaults; unpinned, click-through off, alpha full."
                + (hud == null ? " (Decal backend: position/visibility only.)" : ""));
        }

        private void ResetOneWindow(IView v, System.Drawing.Rectangle home, bool visible)
        {
            if (v == null) return;
            try
            {
                v.Position = home;               // VVS backend: sets Location + ClientArea
                v.Visible = visible;
            }
            catch (Exception ex) { LogException(ex); }

            var hud = UnderlyingHudOf(v);
            if (hud != null)
            {
                // Numeric property types can vary by VVS build — Convert.ChangeType on write (§10.14).
                SetProp(hud, "Ghosted", false);
                SetProp(hud, "ClickThrough", false);
                SetProp(hud, "Alpha", 255);
                // Keep the Virindi bar entry in step with visibility: the main view stays in the
                // bar (visible:true), the secondary windows leave it when re-hidden (visible:false).
                SetProp(hud, "ShowInBar", visible);
            }
        }

        private object UnderlyingHudView() => UnderlyingHudOf(_view);

        private static object UnderlyingHudOf(IView v)
        {
            try
            {
                var p = v == null ? null : v.GetType().GetProperty("Underlying");
                return p == null ? null : p.GetValue(v, null);
            }
            catch { return null; }
        }

        private static string GetProp(object o, string name)
        {
            try
            {
                var p = o.GetType().GetProperty(name);
                return p == null ? "n/a" : Convert.ToString(p.GetValue(o, null));
            }
            catch { return "err"; }
        }

        private static void SetProp(object o, string name, object value)
        {
            try
            {
                var p = o.GetType().GetProperty(name);
                if (p != null && p.CanWrite) p.SetValue(o, Convert.ChangeType(value, p.PropertyType), null);
            }
            catch { }
        }

        /// <summary>Tiny join helper (avoids LINQ; LangVersion 7.3 / ns2.0-friendly).</summary>
        private sealed class StringBuilderLite
        {
            private readonly StringBuilder _sb = new StringBuilder();
            public bool Empty => _sb.Length == 0;
            public void Append(string s) { if (_sb.Length > 0) _sb.Append(", "); _sb.Append(s); }
            public override string ToString() => _sb.ToString();
        }

        // ---- helpers -------------------------------------------------------------------

        private void PrintHelp()
        {
            foreach (var line in new[]
            {
                "/nbnew [name] : create a starter self-buff profile (default name: 'default')",
                "/nbgen [name] : generate a full profile for THIS character from your trained/spec skills and select it (default name 'generated'). At login this runs automatically into a profile named after your character unless /nbset autogen 0.",
                "/nbskills : print the Specialized/Trained skills NB3 reads (sanity-check /nbgen)",
                "/nbuff <profile>[,<profile>...] [force] : load the profile(s) and buff ('force' recasts even active buffs)",
                "/nbpause /nbresume /nbabort : cycle control",
                "/nbed : open/close the Profile Editor view",
                "/nboptions : open the Options view",
                "/nbinclude <profile> : add an Include to the profile being edited",
                "/nbrefresh : rescan the profile folder into every dropdown",
                "/nbset : show/set Options from chat (regen mode, aggressiveness, kits...)",
                "/nbstatus : print current/max H/S/M",
                "/nbid : print target GUID + weapon/shield GUID",
                "/nbcovers : print GUID and cover masks of everything you're wearing",
                "/nbdiag : UI backend, wireup health, window state, control resolution",
                "/nbcat <group#> : list the spells YOU KNOW in a stacking group (id/power/aim) + the self-buff pick",
                "/nbreset : restore every NB3 window's position/size, unpin, clear click-through",
                "/nbdebug : toggle instrumentation logging (cast timings, status ids)",
                "/nbhelp : this list",
            }) Say(line);
        }

        private void PrintStatus() =>
            Say($"H {_state.CurrentHealth}/{_state.MaxHealth}  " +
                $"S {_state.CurrentStamina}/{_state.MaxStamina}  " +
                $"M {_state.CurrentMana}/{_state.MaxMana}");

        private void PrintTargetId()
        {
            int t = _state.SelectedTargetId;
            Say($"Target: 0x{t:X8}  Weapon: 0x{_state.WieldedWeapon(t):X8}  Shield: 0x{_state.WieldedShield(t):X8}");
        }

        private void PrintCovers()
        {
            foreach (var it in _state.WornItems)
                Say($"0x{it.Guid:X8}  cover=0x{it.CoverageMask:X8}  {it.Name}");
        }

        private static string ProfilePath(string name)
        {
            var dir = SIO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NerfusBuffus3");
            Directory.CreateDirectory(dir);
            if (!name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) name += ".xml";
            return SIO.Path.Combine(dir, name);
        }

        /// <summary>One-time data carry-over: NB3 uses %AppData%\NerfusBuffus3, but a user
        /// upgrading from the NB2-era revival has their profiles and per-character settings under
        /// %AppData%\NerfusBuffus2. If the NB3 folder doesn't exist yet and the NB2 one does, copy
        /// every .xml over (profiles + config_*.xml) so nothing is lost on the rename. Runs once
        /// (the NB3 folder exists afterwards); never overwrites, never deletes the old folder,
        /// and every failure is swallowed — a migration hiccup must not take the client down.</summary>
        private void MigrateLegacyData()
        {
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var newDir = SIO.Path.Combine(appData, "NerfusBuffus3");
                var oldDir = SIO.Path.Combine(appData, "NerfusBuffus2");
                if (Directory.Exists(newDir)) return;          // already migrated / fresh NB3 data
                if (!Directory.Exists(oldDir)) return;          // no NB2 data to carry over

                Directory.CreateDirectory(newDir);
                int copied = 0;
                foreach (var src in Directory.GetFiles(oldDir, "*.xml"))
                {
                    var dest = SIO.Path.Combine(newDir, SIO.Path.GetFileName(src));
                    if (!File.Exists(dest)) { File.Copy(src, dest); copied++; }
                }
                if (copied > 0 && LoggedIn())
                    Say($"Carried over {copied} file(s) from your Nerfus Buffus II data folder.");
            }
            catch (Exception ex) { LogException(ex); }
        }

        internal static string ReadResource(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            using (var r = new StreamReader(s)) return r.ReadToEnd();
        }

        private void Say(string msg) { try { Host.Actions.AddChatText("[NB3] " + msg, 5); } catch { } }

        /// <summary>Instrumentation sink (/nbdebug). File I/O, never Actions — safe from any
        /// callback. Failures are swallowed: logging must never take the client down.</summary>
        private void DebugLog(string msg)
        {
            if (!_debug) return;
            try
            {
                var dir = SIO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NerfusBuffus3");
                Directory.CreateDirectory(dir);
                File.AppendAllText(SIO.Path.Combine(dir, "nb3-debug.txt"),
                    $"{DateTime.Now:HH:mm:ss.fff} {msg}\r\n");
            }
            catch { }
        }

        private void LogException(Exception ex)
        {
            try
            {
                var dir = SIO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NerfusBuffus3");
                Directory.CreateDirectory(dir);
                File.AppendAllText(SIO.Path.Combine(dir, "nb3-errors.txt"),
                    $"{DateTime.Now:s} {ex}\r\n");
            }
            catch { }
        }
    }
}
