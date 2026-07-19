// ---------------------------------------------------------------------------------------------
// Faithful compile-check shims (doc 15) — signatures copied from the confirmed members in the
// research corpus (docs 01, 10, 13) and from the vendored MetaViewWrappers source. NOT shipped;
// compiled ONLY by tools/shimcheck/run-shimcheck.sh to type-check the plugin glue on a machine
// with neither Decal nor VVS installed. The harness is only as truthful as these signatures:
// update this file whenever the glue uses a new external member (it doubles as a precise
// inventory of the plugin's Decal/VVS surface).
//
// Enum values are transcribed from the doc-01 appendix dump — never from memory (doc 13 §9
// records a shim whose invented enum values type-checked while being wrong).
//
// 2026-07 update: every signature below has been VERIFIED against the real assemblies'
// metadata dumps (corpus vendor/metadata/*_dump.txt, doc 18). Notable exact shapes:
// CoreManager.FileService returns Decal.Adapter.FilterBase (cast to Decal.Filters.FileService);
// ChatParserInterceptEventArgs derives from EatableEventArgs (Eat lives on the base);
// HooksWrapper.CombatMode returns the CombatState enum; Vital is HookIndexer<VitalType>.
// ---------------------------------------------------------------------------------------------

// ================= MyClasses.MetaViewWrappers (vendored wrapper — doc 10) ====================
namespace MyClasses.MetaViewWrappers
{
    [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true)]
    internal sealed class MVViewAttribute : System.Attribute
    { public MVViewAttribute(string resource) { } }

    [System.AttributeUsage(System.AttributeTargets.Class)]
    internal sealed class MVWireUpControlEventsAttribute : System.Attribute
    { public MVWireUpControlEventsAttribute() { } }

    [System.AttributeUsage(System.AttributeTargets.Method, AllowMultiple = true)]
    internal sealed class MVControlEventAttribute : System.Attribute
    { public MVControlEventAttribute(string control, string eventName) { } }

    internal class MVControlEventArgs : System.EventArgs
    { public int Id { get { return 0; } } }

    internal static class MVWireupHelper
    {
        public static void WireupStart(object viewObj, Decal.Adapter.Wrappers.PluginHost host) { }
        public static void WireupEnd(object viewObj) { }
        public static IView GetDefaultView(object viewObj) { return null; }
        // NB3-modified helper surface (doc 03 §10.11 — per-binding tolerant wireup):
        public static int GetPendingWireupCount(object viewObj) { return 0; }
        public static int RetryPendingWireups(object viewObj) { return 0; }
        public static string GetWireupReport(object viewObj) { return ""; }
        // NB3 addition: all [MVView]-created views (matched by Title, order unspecified).
        public static System.Collections.Generic.List<IView> GetViews(object viewObj)
        { return new System.Collections.Generic.List<IView>(); }
    }

    // List Click delegate (vendored Wrapper.cs) — the editor/casting list handlers bind it.
    internal delegate void dClickedList(object sender, int row, int col);

    internal class MVListSelectEventArgs : MVControlEventArgs
    { public int Row { get { return 0; } } public int Column { get { return 0; } } }

    internal class MVIndexChangeEventArgs : MVControlEventArgs
    { public int Index { get { return 0; } } }

    internal class MVCheckBoxChangeEventArgs : MVControlEventArgs
    { public bool Checked { get { return false; } } }

    internal class MVTextBoxChangeEventArgs : MVControlEventArgs
    { public string Text { get { return ""; } } }

    // Backend selector (vendored ViewSystemSelector.cs): the glue reads IView.ViewType
    // against this enum for the doc-03 §10.13 backend classification.
    internal static class ViewSystemSelector
    {
        public enum eViewSystem { DecalInject, VirindiViewService }
    }

    // ---- wrapper interfaces (vendored Wrapper.cs, doc 10 §3–§4) — members the glue binds.
    internal interface IView : System.IDisposable
    {
        string Title { get; set; }
        bool Visible { get; set; }
        ViewSystemSelector.eViewSystem ViewType { get; }
        System.Drawing.Point Location { get; set; }
        System.Drawing.Rectangle Position { get; set; }
        System.Drawing.Size Size { get; }
        IControl this[string id] { get; }   // throws on a missing/not-yet-realized control (doc 10 §8)
    }

    internal interface IControl : System.IDisposable
    {
        string Name { get; }
        bool Visible { get; set; }
        string TooltipText { get; set; }
        int Id { get; }
        System.Drawing.Rectangle LayoutPosition { get; set; }
    }

    internal interface IStaticText : IControl
    {
        string Text { get; set; }
        event System.EventHandler<MVControlEventArgs> Click;
    }

    internal interface IComboIndexer
    {
        string this[int index] { get; set; }
    }

    internal interface ICombo : IControl
    {
        IComboIndexer Text { get; }
        int Count { get; }
        int Selected { get; set; }
        void Add(string text);
        void Add(string text, object obj);
        void Insert(int index, string text);
        void RemoveAt(int index);
        void Remove(int index);
        void Clear();
    }

    internal interface ICheckBox : IControl
    {
        string Text { get; set; }
        bool Checked { get; set; }
        event System.EventHandler<MVCheckBoxChangeEventArgs> Change;
    }

    internal interface ITextBox : IControl
    {
        string Text { get; set; }
        int Caret { get; set; }
        event System.EventHandler<MVTextBoxChangeEventArgs> Change;
    }

    internal interface IList : IControl
    {
        event System.EventHandler<MVListSelectEventArgs> Selected;
        event dClickedList Click;
        void Clear();
        IListRow this[int row] { get; }
        IListRow AddRow();
        IListRow Add();
        IListRow InsertRow(int pos);
        IListRow Insert(int pos);
        int RowCount { get; }
        void RemoveRow(int index);
        void Delete(int index);
        int ColCount { get; }
        int ScrollPosition { get; set; }
    }

    internal interface IListRow { IListCell this[int col] { get; } }

    internal interface IListCell
    {
        System.Drawing.Color Color { get; set; }
        int Width { get; set; }
        object this[int subval] { get; set; }
        void ResetColor();
    }

    internal interface INotebook : IControl
    {
        event System.EventHandler<MVIndexChangeEventArgs> Change;
        int ActiveTab { get; set; }
    }

    // The doc-13 §5 hazard, replicated so the shim gate CATCHES the Path shadow instead of
    // hiding it: a non-static member named Path on a type the wrapper namespace exposes.
    internal class Extension { public string Path = ""; }
}

// ============================ Decal.Adapter (docs 01, 13) ====================================
namespace Decal.Adapter
{
    [System.AttributeUsage(System.AttributeTargets.Class)]
    public sealed class FriendlyNameAttribute : System.Attribute
    { public FriendlyNameAttribute(string name) { } }

    public abstract class PluginBase
    {
        protected abstract void Startup();
        protected abstract void Shutdown();
        protected Decal.Adapter.Wrappers.PluginHost Host { get { return null; } }
    }

    public class EatableEventArgs : System.EventArgs
    {
        public bool Eat { get { return false; } set { } }
    }

    public class ChatParserInterceptEventArgs : EatableEventArgs
    {
        public string Text { get { return ""; } }
    }

    // adapter_dump.txt: "ChatTextInterceptEventArgs : Decal.Adapter.EatableEventArgs —
    // prop string Text {get;} / prop int Color {get;} / prop int Target {get;}"
    public class ChatTextInterceptEventArgs : EatableEventArgs
    {
        public string Text { get { return ""; } }
        public int Color { get { return 0; } }
        public int Target { get { return 0; } }
    }

    public class CoreManager
    {
        public static CoreManager Current { get { return null; } }
        public Decal.Adapter.Wrappers.CharacterFilter CharacterFilter { get { return null; } }
        public Decal.Adapter.Wrappers.WorldFilter WorldFilter { get { return null; } }
        // Returns the BASE type; the concrete Decal.Filters.FileService is reached by cast
        // (doc 13 §3 — the load-bearing shape).
        public FilterBase FileService { get { return null; } }
        public event System.EventHandler<ChatParserInterceptEventArgs> CommandLineText { add { } remove { } }
        // adapter_dump.txt (CoreManager): "event EventHandler`1<ChatTextInterceptEventArgs> ChatBoxMessage"
        public event System.EventHandler<ChatTextInterceptEventArgs> ChatBoxMessage { add { } remove { } }
        // adapter_dump.txt (CoreManager): "event EventHandler`1<System.EventArgs> RenderFrame"
        public event System.EventHandler<System.EventArgs> RenderFrame { add { } remove { } }
    }

    public class FilterBase { }
}

namespace Decal.Adapter.Wrappers
{
    public class PluginHost
    {
        public HooksWrapper Actions { get { return null; } }
    }

    // doc-01 appendix: 1 Peace, 2 Melee, 4 Missile, 8 Magic
    public enum CombatState { Peace = 1, Melee = 2, Missile = 4, Magic = 8 }

    // doc-01 appendix: VitalType — 9 members
    public enum VitalType
    {
        MaximumHealth = 1, CurrentHealth = 2, MaximumStamina = 3, CurrentStamina = 4,
        MaximumMana = 5, CurrentMana = 6, BaseHealth = 7, BaseStamina = 8, BaseMana = 9
    }

    // doc-01 appendix / doc 13 §10.3: EnterPortal = 0, ExitPortal = 1
    public enum PortalEventType { EnterPortal = 0, ExitPortal = 1 }

    // adapter_dump.txt: "AddRemoveEventType : System.Enum — Add = 0 / Delete = 1"
    public enum AddRemoveEventType { Add = 0, Delete = 1 }

    // adapter_dump.txt: "ChangeEnchantmentsEventArgs : System.EventArgs —
    // prop AddRemoveEventType Type {get;} / prop EnchantmentWrapper Enchantment {get;}"
    public class ChangeEnchantmentsEventArgs : System.EventArgs
    {
        public AddRemoveEventType Type { get { return AddRemoveEventType.Add; } }
        public EnchantmentWrapper Enchantment { get { return null; } }
    }

    // adapter_dump.txt: "StatusMessageEventArgs : System.EventArgs —
    // prop int Type {get;} / prop string Text {get;}"
    public class StatusMessageEventArgs : System.EventArgs
    {
        public int Type { get { return 0; } }
        public string Text { get { return ""; } }
    }

    public class ChangePortalModeEventArgs : System.EventArgs
    { public PortalEventType Type { get { return PortalEventType.EnterPortal; } } }

    // Synthetic keys per doc 13 §6; real property ids are read via an int cast, which any
    // enum accepts — the named members here are only those the glue binds by name.
    public enum LongValueKey { Icon = 218103809, SpellCount = 218103838 }

    // Double-valued property keys (adapter_dump.txt: "double Values(DoubleValueKey index)" +
    // the (key, default) overload, Decal.Adapter.xml:1245). The consumable auto-scan reads
    // HealkitMod (float property 100, ace-world-property-ids.tsv) via an int cast.
    public enum DoubleValueKey { HealkitMod = 100 }

    // Object classes (adapter_dump.txt "Decal.Adapter.Wrappers.ObjectClass : System.Enum"):
    // Food = 6, HealingKit = 29 — used by FindBestPotion / FindBestHealingKit to tell a drink
    // from a kit before reading BoosterEnum/BoostValue.
    public enum ObjectClass { Food = 6, HealingKit = 29 }

    public class VitalIndexer { public int this[VitalType v] { get { return 0; } } }

    public class HooksWrapper
    {
        public void AddChatText(string text, int color) { }
        public void CastSpell(int spellId, int objectId) { }
        public void RequestId(int objectId) { }
        public void UseItem(int guid, int type) { }
        public void SetCombatMode(CombatState state) { }
        public CombatState CombatMode { get { return CombatState.Peace; } }
        // adapter_dump.txt: "prop int CurrentSelection {get;set;}" / "void SelectItem(int objectId)"
        public int CurrentSelection { get { return 0; } set { } }
        public void SelectItem(int objectId) { }
        // adapter_dump.txt: "void AutoWield(int item)" (+ slot/explicit overloads not shimmed)
        public void AutoWield(int item) { }
        public int BusyState { get { return 0; } }
        public VitalIndexer Vital { get { return null; } }
    }

    public class EnchantmentWrapper
    {
        public int SpellId { get { return 0; } }
        public int Family { get { return 0; } }
        public int Layer { get { return 0; } }
        public double Duration { get { return 0; } }
        public int TimeRemaining { get { return 0; } }
        public System.DateTime Expires { get { return System.DateTime.MinValue; } }
        public double Adjustment { get { return 0; } }
        public int Affected { get { return 0; } }
        public int AffectedMask { get { return 0; } }
    }

    public class EnchantmentCollection : System.Collections.Generic.IEnumerable<EnchantmentWrapper>
    {
        public System.Collections.Generic.IEnumerator<EnchantmentWrapper> GetEnumerator() { yield break; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { yield break; }
    }

    public class CharacterFilter
    {
        public int Id { get { return 0; } }
        public string Name { get { return ""; } }
        public bool IsSpellKnown(int spellId) { return false; }
        // adapter_dump.txt (CharacterFilter): "prop ReadOnlyCollection`1<int> SpellBook {get;}"
        public System.Collections.ObjectModel.ReadOnlyCollection<int> SpellBook { get { return null; } }
        public EnchantmentCollection Enchantments { get { return null; } }
        public event System.EventHandler<ChangePortalModeEventArgs> ChangePortalMode { add { } remove { } }
        public event System.EventHandler LoginComplete { add { } remove { } }
        // adapter_dump.txt (CharacterFilter): "event EventHandler`1<ChangeEnchantmentsEventArgs> ChangeEnchantments"
        public event System.EventHandler<ChangeEnchantmentsEventArgs> ChangeEnchantments { add { } remove { } }
        // adapter_dump.txt (CharacterFilter): "event EventHandler`1<StatusMessageEventArgs> StatusMessage"
        public event System.EventHandler<StatusMessageEventArgs> StatusMessage { add { } remove { } }
    }

    public class WorldObject
    {
        public int Id { get { return 0; } }
        public string Name { get { return ""; } }
        // adapter_dump.txt (WorldObject): "prop ObjectClass ObjectClass {get;}"
        public ObjectClass ObjectClass { get { return ObjectClass.Food; } }
        public int Values(LongValueKey key) { return 0; }
        public int Values(LongValueKey key, int defaultValue) { return defaultValue; }
        // adapter_dump.txt: "double Values(DoubleValueKey index)" + the (key, default) overload.
        public double Values(DoubleValueKey key) { return 0.0; }
        public double Values(DoubleValueKey key, double defaultValue) { return defaultValue; }
    }

    public class WorldObjectCollection : System.Collections.Generic.IEnumerable<WorldObject>
    {
        public System.Collections.Generic.IEnumerator<WorldObject> GetEnumerator() { yield break; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() { yield break; }
    }

    public class WorldFilter
    {
        public WorldObjectCollection GetInventory() { return null; }
        public WorldObjectCollection GetByContainer(int containerId) { return null; }
        // adapter_dump.txt (WorldFilter): "WorldObjectCollection GetByName(string name)"
        public WorldObjectCollection GetByName(string name) { return null; }
        public WorldObject this[int id] { get { return null; } }
    }
}

// ============================ Decal.Filters (docs 01 §4.8, 13 §3) ============================
namespace Decal.Filters
{
    public class FileService : Decal.Adapter.FilterBase
    {
        public SpellTable SpellTable { get { return null; } }
    }

    // Only Id/Name are bound-stable (doc 13 §3); everything else the glue reads reflectively,
    // so the shim deliberately declares nothing more.
    public class Spell
    {
        public int Id { get { return 0; } }
        public string Name { get { return ""; } }
    }

    public class SpellTable
    {
        public Spell GetById(int spellId) { return null; }
    }
}

// ================= Desktop-only BCL types absent from the netcore ref pack ===================
namespace System.Windows.Forms
{
    public class Timer : System.IDisposable
    {
        public int Interval { get { return 0; } set { } }
        public event System.EventHandler Tick { add { } remove { } }
        public void Start() { }
        public void Stop() { }
        public void Dispose() { }
    }
}

namespace System.Media
{
    // net48 System.dll — the doc-06 zero-dependency WAV player (BuffComplete.wav).
    public class SoundPlayer : System.IDisposable
    {
        public SoundPlayer(System.IO.Stream stream) { }
        public SoundPlayer(string path) { }
        public void Load() { }
        public void Play() { }      // async; PlaySync is deliberately NOT shimmed (doc 06: never from a callback)
        public void Stop() { }
        public void Dispose() { }
    }
}
