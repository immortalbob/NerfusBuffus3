///////////////////////////////////////////////////////////////////////////////
//File: Wrapper_WireupHelper.cs
//
//Description: A helper utility that emulates Decal.Adapter's automagic view
//  creation and control/event wireup with the MetaViewWrappers. A separate set
//  of attributes is used.
//
//References required:
//  Wrapper.cs
//
//This file is Copyright (c) 2010 VirindiPlugins
//
//MODIFIED (NB3 revival, 2026 — per research corpus doc 03 §10.11): binding is now
//  PER-BINDING TOLERANT instead of abort-on-first-failure. Each [MVControlReference]/
//  [MVControlEvent] binds independently; failures are RECORDED, never thrown (the stock
//  helper's first-bad-name throw abandons every later binding, and because reflection
//  enumeration order is unspecified the abort point varies by machine). Unresolved names
//  stay in a retry queue (controls on unopened notebook tabs realize lazily — doc 14 §4)
//  serviced via RetryPendingWireups(); GetWireupReport() surfaces health for the login
//  banner and /nbdiag. The MIT license above is unchanged.
//
//Permission is hereby granted, free of charge, to any person obtaining a copy
//  of this software and associated documentation files (the "Software"), to deal
//  in the Software without restriction, including without limitation the rights
//  to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
//  copies of the Software, and to permit persons to whom the Software is
//  furnished to do so, subject to the following conditions:
//
//The above copyright notice and this permission notice shall be included in
//  all copies or substantial portions of the Software.
//
//THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
//  IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//  FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//  AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
//  LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
//  OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
//  THE SOFTWARE.
///////////////////////////////////////////////////////////////////////////////

using System;
using System.Collections.Generic;
using System.Text;
using System.Reflection;

#if METAVIEW_PUBLIC_NS
namespace MetaViewWrappers
#else
namespace MyClasses.MetaViewWrappers
#endif
{
    #region Attribute Definitions

    [AttributeUsage(AttributeTargets.Class)]
#if VVS_WRAPPERS_PUBLIC
    public
#else
    internal
#endif
    sealed class MVWireUpControlEventsAttribute : Attribute
    {
        public MVWireUpControlEventsAttribute() { }
    }

    [AttributeUsage(AttributeTargets.Field)]
#if VVS_WRAPPERS_PUBLIC
    public
#else
    internal
#endif
    sealed class MVControlReferenceAttribute : Attribute
    {
        string ctrl;

        // Summary:
        //     Construct a new ControlReference
        //
        // Parameters:
        //   control:
        //     Control to reference
        public MVControlReferenceAttribute(string control)
        {
            ctrl = control;
        }

        // Summary:
        //     The Control Name
        public string Control
        {
            get
            {
                return ctrl;
            }
        }
    }

    [AttributeUsage(AttributeTargets.Field)]
#if VVS_WRAPPERS_PUBLIC
    public
#else
    internal
#endif
    sealed class MVControlReferenceArrayAttribute : Attribute
    {
        private System.Collections.ObjectModel.Collection<string> myControls;

        /// <summary>
        /// Constructs a new ControlReference array
        /// </summary>
        /// <param name="controls">Names of the controls to put in the array</param>
        public MVControlReferenceArrayAttribute(params string[] controls)
            : base()
        {
            this.myControls = new System.Collections.ObjectModel.Collection<string>(controls);
        }

        /// <summary>
        /// Control collection
        /// </summary>
        public System.Collections.ObjectModel.Collection<string> Controls
        {
            get
            {
                return this.myControls;
            }
        }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
#if VVS_WRAPPERS_PUBLIC
    public
#else
    internal
#endif
    sealed class MVViewAttribute : Attribute
    {
        string res;

        // Summary:
        //     Constructs a new view from the specified resource
        //
        // Parameters:
        //   Resource:
        //     Embedded resource path
        public MVViewAttribute(string resource)
        {
            res = resource;
        }

        // Summary:
        //     The resource to load
        public string Resource
        {
            get
            {
                return res;
            }
        }
    }

    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
#if VVS_WRAPPERS_PUBLIC
    public
#else
    internal
#endif
    sealed class MVControlEventAttribute : Attribute
    {
        string c;
        string e;
        // Summary:
        //     Constructs the ControlEvent
        //
        // Parameters:
        //   control:
        //     Control Name
        //
        //   controlEvent:
        //     Event to Wire
        public MVControlEventAttribute(string control, string eventName)
        {
            c = control;
            e = eventName;
        }

        // Summary:
        //     Control Name
        public string Control
        {
            get
            {
                return c;
            }
        }

        //
        // Summary:
        //     Event to Wire
        public string EventName
        {
            get
            {
                return e;
            }
        }
    }

    #endregion Attribute Definitions

#if VVS_WRAPPERS_PUBLIC
    public
#else
    internal
#endif
    static class MVWireupHelper
    {
        private class PendingRef
        {
            public FieldInfo Field;
            public string Control;
        }
        private class PendingEvent
        {
            public MethodInfo Method;
            public string Control;
            public string EventName;
        }
        private class ViewObjectInfo
        {
            public List<MyClasses.MetaViewWrappers.IView> Views = new List<IView>();
            public List<PendingRef> PendingRefs = new List<PendingRef>();
            public List<PendingEvent> PendingEvents = new List<PendingEvent>();
            public List<string> Failures = new List<string>();   // permanent (type/signature) failures
            public int RefsTotal, RefsBound, EventsTotal, EventsBound;
        }
        static Dictionary<object, ViewObjectInfo> VInfo = new Dictionary<object, ViewObjectInfo>();

        public static MyClasses.MetaViewWrappers.IView GetDefaultView(object ViewObj)
        {
            if (!VInfo.ContainsKey(ViewObj))
                return null;
            if (VInfo[ViewObj].Views.Count == 0)
                return null;
            return VInfo[ViewObj].Views[0];
        }

        /// <summary>All views created from the object's [MVView] attributes (NB3 addition:
        /// attribute enumeration order is unspecified, so callers matching a specific view
        /// should key on <c>IView.Title</c>, not on list position).</summary>
        public static List<MyClasses.MetaViewWrappers.IView> GetViews(object ViewObj)
        {
            if (!VInfo.ContainsKey(ViewObj))
                return new List<IView>();
            return new List<IView>(VInfo[ViewObj].Views);
        }

        /// <summary>Number of reference/event bindings still unresolved (controls on
        /// not-yet-shown tabs realize lazily — doc 14 §4). 0 = fully wired.</summary>
        public static int GetPendingWireupCount(object ViewObj)
        {
            if (!VInfo.ContainsKey(ViewObj)) return 0;
            ViewObjectInfo info = VInfo[ViewObj];
            return info.PendingRefs.Count + info.PendingEvents.Count;
        }

        /// <summary>Re-attempt every still-pending binding. Call from a throttled frame/timer
        /// loop; lazily-realized controls wire on the first retry after their tab is shown.
        /// Returns the number of bindings still pending afterwards.</summary>
        public static int RetryPendingWireups(object ViewObj)
        {
            if (!VInfo.ContainsKey(ViewObj)) return 0;
            ViewObjectInfo info = VInfo[ViewObj];
            TryBindPending(ViewObj, info);
            return info.PendingRefs.Count + info.PendingEvents.Count;
        }

        /// <summary>One-line wireup health summary for the login banner and /diag
        /// (doc 03 §10.11: report at login — a Startup-time chat line goes nowhere).</summary>
        public static string GetWireupReport(object ViewObj)
        {
            if (!VInfo.ContainsKey(ViewObj)) return "wireup: not started";
            ViewObjectInfo info = VInfo[ViewObj];
            StringBuilder sb = new StringBuilder();
            sb.Append("wireup: refs ").Append(info.RefsBound).Append("/").Append(info.RefsTotal)
              .Append(", events ").Append(info.EventsBound).Append("/").Append(info.EventsTotal);
            int pending = info.PendingRefs.Count + info.PendingEvents.Count;
            if (pending > 0)
            {
                sb.Append("; pending:");
                foreach (PendingRef p in info.PendingRefs) sb.Append(" ").Append(p.Control);
                foreach (PendingEvent p in info.PendingEvents) sb.Append(" ").Append(p.Control).Append(".").Append(p.EventName);
            }
            if (info.Failures.Count > 0)
            {
                sb.Append("; FAILED:");
                foreach (string f in info.Failures) sb.Append(" ").Append(f);
            }
            return sb.ToString();
        }

        public static void WireupStart(object ViewObj, Decal.Adapter.Wrappers.PluginHost Host)
        {
            if (VInfo.ContainsKey(ViewObj))
                WireupEnd(ViewObj);
            ViewObjectInfo info = new ViewObjectInfo();
            VInfo[ViewObj] = info;

            Type ObjType = ViewObj.GetType();

            //Start views — each independently; a bad resource must not kill its siblings.
            object[] viewattrs = ObjType.GetCustomAttributes(typeof(MVViewAttribute), true);
            foreach (MVViewAttribute a in viewattrs)
            {
                try
                {
                    IView v = MyClasses.MetaViewWrappers.ViewSystemSelector.CreateViewResource(Host, a.Resource);
                    if (v != null)
                        info.Views.Add(v);
                    else
                        info.Failures.Add("view(" + a.Resource + "): selector returned null");
                }
                catch (Exception ex)
                {
                    info.Failures.Add("view(" + a.Resource + "): " + ex.Message);
                }
            }

            //Collect control-reference bindings (bound tolerantly below)
            foreach (FieldInfo fi in ObjType.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy))
            {
                if (Attribute.IsDefined(fi, typeof(MVControlReferenceAttribute)))
                {
                    MVControlReferenceAttribute attr = (MVControlReferenceAttribute)Attribute.GetCustomAttribute(fi, typeof(MVControlReferenceAttribute));
                    info.RefsTotal++;
                    PendingRef p = new PendingRef();
                    p.Field = fi;
                    p.Control = attr.Control;
                    info.PendingRefs.Add(p);
                }
                else if (Attribute.IsDefined(fi, typeof(MVControlReferenceArrayAttribute)))
                {
                    //Array references bind element-wise, tolerantly, against the first view.
                    MVControlReferenceArrayAttribute attr = (MVControlReferenceArrayAttribute)Attribute.GetCustomAttribute(fi, typeof(MVControlReferenceArrayAttribute));
                    info.RefsTotal++;
                    if (info.Views.Count == 0)
                    {
                        info.Failures.Add(fi.Name + ": no views for reference array");
                        continue;
                    }
                    try
                    {
                        Array controls = Array.CreateInstance(fi.FieldType.GetElementType(), attr.Controls.Count);
                        IView view = info.Views[0];
                        for (int i = 0; i < attr.Controls.Count; ++i)
                        {
                            try { controls.SetValue(view[attr.Controls[i]], i); }
                            catch { info.Failures.Add(fi.Name + "[" + attr.Controls[i] + "]: not found"); }
                        }
                        fi.SetValue(ViewObj, controls);
                        info.RefsBound++;
                    }
                    catch (Exception ex)
                    {
                        info.Failures.Add(fi.Name + " (array): " + ex.Message);
                    }
                }
            }

            //Collect event bindings
            foreach (MethodInfo mi in ObjType.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy))
            {
                if (!Attribute.IsDefined(mi, typeof(MVControlEventAttribute)))
                    continue;
                Attribute[] attrs = Attribute.GetCustomAttributes(mi, typeof(MVControlEventAttribute));

                foreach (MVControlEventAttribute attr in attrs)
                {
                    info.EventsTotal++;
                    PendingEvent p = new PendingEvent();
                    p.Method = mi;
                    p.Control = attr.Control;
                    p.EventName = attr.EventName;
                    info.PendingEvents.Add(p);
                }
            }

            //First bind pass. Controls that don't resolve yet stay pending (retry loop);
            //type/signature mismatches are recorded as permanent failures. NOTHING throws.
            TryBindPending(ViewObj, info);
        }

        private static IControl ResolveControl(ViewObjectInfo info, string name)
        {
            foreach (MyClasses.MetaViewWrappers.IView v in info.Views)
            {
                if (v == null) continue;
                IControl c = null;
                try { c = v[name]; }
                catch { }
                if (c != null) return c;
            }
            return null;
        }

        private static void TryBindPending(object ViewObj, ViewObjectInfo info)
        {
            //References
            for (int i = info.PendingRefs.Count - 1; i >= 0; --i)
            {
                PendingRef p = info.PendingRefs[i];
                IControl c = ResolveControl(info, p.Control);
                if (c == null) continue;                              // not realized yet — retry later
                if (!p.Field.FieldType.IsAssignableFrom(c.GetType()))
                {
                    info.Failures.Add(p.Control + ": wrong type (" + c.GetType().Name + ")");
                    info.PendingRefs.RemoveAt(i);                     // permanent — do not retry
                    continue;
                }
                try
                {
                    p.Field.SetValue(ViewObj, c);
                    info.RefsBound++;
                }
                catch (Exception ex)
                {
                    info.Failures.Add(p.Control + ": " + ex.Message);
                }
                info.PendingRefs.RemoveAt(i);
            }

            //Events
            for (int i = info.PendingEvents.Count - 1; i >= 0; --i)
            {
                PendingEvent p = info.PendingEvents[i];
                IControl c = ResolveControl(info, p.Control);
                if (c == null) continue;                              // not realized yet — retry later
                try
                {
                    EventInfo ei = c.GetType().GetEvent(p.EventName);
                    if (ei == null)
                    {
                        info.Failures.Add(p.Control + "." + p.EventName + ": no such event on " + c.GetType().Name);
                    }
                    else
                    {
                        ei.AddEventHandler(c, Delegate.CreateDelegate(ei.EventHandlerType, ViewObj, p.Method.Name));
                        info.EventsBound++;
                    }
                }
                catch (Exception ex)
                {
                    info.Failures.Add(p.Control + "." + p.EventName + ": " + ex.Message);
                }
                info.PendingEvents.RemoveAt(i);                       // resolved control: bound or permanent
            }
        }

        public static void WireupEnd(object ViewObj)
        {
            if (!VInfo.ContainsKey(ViewObj))
                return;

            foreach (MyClasses.MetaViewWrappers.IView v in VInfo[ViewObj].Views)
                v.Dispose();

            VInfo.Remove(ViewObj);
        }
    }
}