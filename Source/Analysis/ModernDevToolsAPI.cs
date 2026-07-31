using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// A source of curated known-issue entries supplied at runtime by another mod. Use this when your
    /// entries are not a static file - generated, fetched, or driven by your own settings. For a fixed
    /// list, dropping a known-issues.json in your About folder is simpler and needs no reference to us.
    /// </summary>
    public interface IKnownIssueSource
    {
        /// <summary>Shown as the provenance tag on any entry this source contributes.</summary>
        string SourceLabel { get; }

        /// <summary>
        /// Entries to score against errors. Called when the pool is rebuilt, not per error, so it is
        /// fine to do real work here. Return an empty list rather than null.
        /// </summary>
        IEnumerable<RemoteIssue> GetIssues();
    }

    /// <summary>
    /// ================================ PUBLIC API - FROZEN CONTRACT ================================
    ///
    /// Everything in this type is a stable contract for other mods. It is safe to bind by REFLECTION
    /// so you need no assembly reference and no dependency on Modern Dev Tools being installed:
    ///
    ///     var api = AccessTools.TypeByName("ModernDevTools.ModernDevToolsAPI");
    ///     api?.GetMethod("RegisterModule")?.Invoke(null, new object[] { myModule });
    ///
    /// Check ApiVersion before using anything added after v1. Members are only ever ADDED between
    /// versions; existing signatures do not change. If you need something that is not here, ask - the
    /// intent is that no mod should have to reflect into our internals.
    ///
    ///   v1: RegisterModule, Invalidate
    ///   v2: ApiVersion, RegisterKnowledgeSource, RegisterLogWidget, AnalysisCompleted,
    ///       ModernOwnsLogWindow, IsModernLogOpen, OpenModernLog, YieldLogWindow
    ///
    /// =============================================================================================
    /// </summary>
    public static class ModernDevToolsAPI
    {
        /// <summary>Incremented whenever members are added. See the contract block above.</summary>
        public const int ApiVersion = 2;

        // --- analysis modules (v1) ------------------------------------------------------------------

        /// <summary>Register a module instance. It runs for every analysed error alongside the
        /// built-in modules. Safe to call from a StaticConstructorOnStartup or Mod ctor.</summary>
        public static void RegisterModule(ErrorModule module) => ErrorModuleRegistry.RegisterApiModule(module);

        /// <summary>Rebuild the module list and drop cached analyses (call after changing what is
        /// registered or enabled at runtime).</summary>
        public static void Invalidate()
        {
            ErrorModuleRegistry.Invalidate();
            KnowledgeSources.Invalidate();
            LogAnalysisCache.Clear();
        }

        // --- knowledge sources (v2) -----------------------------------------------------------------

        /// <summary>
        /// Contribute known-issue entries from code. They are scored against errors with exactly the
        /// same matcher as the shipped library, mod-shipped files and the community database, and are
        /// marked as curated knowledge (so a match lights the "Known issue" badge).
        /// </summary>
        public static void RegisterKnowledgeSource(IKnownIssueSource source)
        {
            KnowledgeSources.Register(source);
            LogAnalysisCache.Clear();
        }

        // --- log window add-ons (v2) ----------------------------------------------------------------

        /// <summary>
        /// Add a control to the modern log window's add-on tray.
        ///
        /// The drawer signature is identical to HugsLib's LogWindowExtensions.WidgetDrawer, so the same
        /// method can be registered with either library. If you already support HugsLib you do NOT need
        /// to call this - we host HugsLib-registered widgets automatically.
        /// </summary>
        public static void RegisterLogWidget(string id, LogWidgetDrawer drawer, bool alignRight = false) =>
            LogWidgets.Register(id, drawer, alignRight ? LogWidgetAlign.Right : LogWidgetAlign.Left);

        // --- events (v2) ----------------------------------------------------------------------------

        /// <summary>
        /// Raised once per message, immediately after its analysis is built (never per frame). Use it
        /// for exporters, external reporting, or your own bookkeeping. Handlers are sandboxed: a throw
        /// is logged and the handler is left registered, so do not rely on exceptions propagating.
        /// </summary>
        public static event Action<LogAnalysis> AnalysisCompleted;

        internal static void NotifyAnalysisCompleted(LogAnalysis analysis)
        {
            Action<LogAnalysis> handlers = AnalysisCompleted;
            if (handlers == null) return;
            foreach (Delegate d in handlers.GetInvocationList())
            {
                try { ((Action<LogAnalysis>)d)(analysis); }
                catch (Exception e)
                {
                    Log.ErrorOnce("[Modern Dev Tools] an AnalysisCompleted handler threw: " + e,
                                  d.Method?.Name?.GetHashCode() ?? 0x2E19E20);
                }
            }
        }

        // --- log window ownership (v2) --------------------------------------------------------------
        //
        // Replacing the debug log is not a private decision: other mods decorate the vanilla window and
        // lose their UI when it never opens. These members let a mod see who owns it, reach ours, or ask
        // us to stand down - instead of having to guess.

        /// <summary>True while Modern Dev Tools answers the log hotkey, dev toolbar button and auto-open.
        /// When false the vanilla window behaves exactly as if this mod were not installed.</summary>
        public static bool ModernOwnsLogWindow => LogWindowCompat.ModernOwnsLog;

        public static bool IsModernLogOpen => Window_ModernLog.IsOpenNow;

        /// <summary>Open the modern log window (no-op if already open).</summary>
        public static void OpenModernLog() => Window_ModernLog.OpenIfNeeded();

        /// <summary>
        /// Ask Modern Dev Tools to hand the debug log back to vanilla, permanently (it is written to
        /// settings and the player can flip it back). Intended for a mod that must own the log window
        /// itself; prefer RegisterLogWidget if you only need to add controls.
        /// </summary>
        public static void YieldLogWindow()
        {
            var s = ModernDevToolsMod.Settings;
            if (s == null || s.yieldLogWindow) return;
            s.yieldLogWindow = true;
            ModernDevToolsMod.Instance?.WriteSettings();
            Log.Message("[Modern Dev Tools] another mod requested the log window; standing down to the vanilla log.");
        }
    }

    /// <summary>Holds code-registered knowledge sources and flattens them into one scoreable pool.</summary>
    public static class KnowledgeSources
    {
        private static readonly List<IKnownIssueSource> _sources = new List<IKnownIssueSource>();
        private static List<RemoteIssue> _pool;

        public static void Register(IKnownIssueSource source)
        {
            if (source == null || _sources.Contains(source)) return;
            _sources.Add(source);
            _pool = null;
        }

        public static void Invalidate() => _pool = null;

        public static List<RemoteIssue> Pool
        {
            get
            {
                if (_pool != null) return _pool;
                var list = new List<RemoteIssue>();
                foreach (IKnownIssueSource s in _sources)
                {
                    try
                    {
                        IEnumerable<RemoteIssue> issues = s.GetIssues();
                        if (issues == null) continue;
                        foreach (RemoteIssue ri in issues)
                        {
                            if (ri == null || ri.Title.NullOrEmpty()) continue;
                            if (ri.ReportedBy.NullOrEmpty()) ri.ReportedBy = s.SourceLabel;
                            list.Add(ri);
                        }
                    }
                    catch (Exception e)
                    {
                        Log.WarningOnce("[Modern Dev Tools] knowledge source '" + SafeLabel(s) + "' threw: " + e.Message,
                                        s.GetType().GetHashCode() ^ 0x2E19E21);
                    }
                }
                _pool = list;
                return _pool;
            }
        }

        private static string SafeLabel(IKnownIssueSource s)
        {
            try { return s.SourceLabel ?? s.GetType().Name; } catch { return s.GetType().Name; }
        }
    }
}
