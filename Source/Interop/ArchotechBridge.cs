using System;
using System.Reflection;
using HarmonyLib;
using LudeonTK;
using UnityEngine;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Hosts Archotech Logs' two analysis windows from the modern log.
    ///
    /// Archotech Logs (kongkim.ArchotechLogs) is a direct functional neighbour: it adds a diagnostics
    /// view and a log exporter, and its entire UI hangs off postfixes on EditWindow_Log.DoWindowContents
    /// - which never runs while we own the log window. Rather than compete, we open its own windows.
    ///
    /// TWO THINGS THE DECOMPILE SETTLED, both of which make this safe:
    ///
    /// 1. Dialog_ArchotechInfoView's constructor takes an EditWindow_Log, but it is used ONLY for window
    ///    positioning, and BOTH consumers explicitly null-check it:
    ///        PreOpen()       -> if (vanillaLogHost == null) { centre on screen; return; }
    ///        WindowUpdate()  -> if (vanillaLogHost == null || !IsOpen) return;
    ///    So passing null is a supported path: the dialog simply centres instead of snapping to the
    ///    vanilla window. No detached dummy EditWindow_Log is needed (which would have been a real risk).
    ///
    /// 2. Its DoWindowContents reads the selected message from EditWindow_Log's private STATIC
    ///    `selectedMessage` field. It does not ask its host window. So the dialog only shows anything
    ///    if that static reflects the message the player actually picked - which is why we mirror our
    ///    selection into it. That mirroring is good citizenship generally: any other mod reading the
    ///    same field now sees a correct selection instead of a stale or null one.
    ///
    /// Everything here is reflection and null-guarded; with Archotech absent, nothing runs.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class ArchotechBridge
    {
        private const string InfoViewType = "KKArchotechLogs.UI.Dialog_ArchotechInfoView";
        private const string ExportType = "KKArchotechLogs.UI.ArchotechExportWindow";

        private static bool _probed;
        private static ConstructorInfo _infoCtor;      // (EditWindow_Log) - we pass null
        private static ConstructorInfo _exportCtor;    // (ExportPreset preset = Default) - all-optional
        private static object[] _infoArgs, _exportArgs;
        private static Type _infoType, _exportType;

        // EditWindow_Log.selectedMessage: private STATIC LogMessage.
        private static FieldInfo _vanillaSelected;
        private static bool _selectedProbed;

        public static bool Available { get { Probe(); return _infoCtor != null || _exportCtor != null; } }

        private static void Probe()
        {
            if (_probed) return;
            _probed = true;
            try
            {
                _infoType = GenTypes.GetTypeInAnyAssembly(InfoViewType);
                _exportType = GenTypes.GetTypeInAnyAssembly(ExportType);
                if (_infoType == null && _exportType == null) return;   // not installed: quiet path.

                if (_infoType != null) _infoCtor = FindCtor(_infoType, new[] { typeof(EditWindow_Log) }, out _infoArgs);
                if (_exportType != null) _exportCtor = FindCtor(_exportType, Type.EmptyTypes, out _exportArgs);

                // Report per window, and only for the one that actually failed - the previous wording
                // claimed "its buttons are not hosted" even when one of the two bound fine.
                if (_infoType != null && _infoCtor == null)
                    Log.WarningOnce("[Advanced Dev Tools] Archotech Logs' diagnostics window constructor did not match the " +
                                    "expected shape, so that button is not hosted. Use \"Vanilla log\" to reach it.", 0x2E19E50);
                if (_exportType != null && _exportCtor == null)
                    Log.WarningOnce("[Advanced Dev Tools] Archotech Logs' export window constructor did not match the " +
                                    "expected shape, so that button is not hosted. Use \"Vanilla log\" to reach it.", 0x2E19E53);
            }
            catch (Exception e)
            {
                _infoCtor = null;
                _exportCtor = null;
                Log.WarningOnce("[Advanced Dev Tools] Archotech Logs bridge probe failed: " + e.Message, 0x2E19E51);
            }
        }

        /// <summary>
        /// Find a usable constructor and the argument array to invoke it with.
        ///
        /// Tries the preferred signature first, then falls back to any constructor whose parameters are
        /// ALL OPTIONAL. That fallback is not academic: ArchotechExportWindow is declared
        /// `ArchotechExportWindow(ExportPreset preset = ExportPreset.Default)`, so `new X()` compiles in
        /// C# but there is no parameterless constructor in the IL - optional parameters are compile-time
        /// sugar and reflection sees the real arity. GetConstructor(Type.EmptyTypes) therefore returns
        /// null, which is exactly what our contract-drift warning caught in-game.
        ///
        /// Requiring EVERY parameter to be optional is deliberate: it means the author intended the
        /// no-argument call to be valid. A constructor that grows a REQUIRED parameter is genuine drift
        /// and should still be reported rather than guessed at.
        /// </summary>
        private static ConstructorInfo FindCtor(Type type, Type[] preferred, out object[] args)
        {
            args = null;
            try
            {
                ConstructorInfo exact = type.GetConstructor(preferred);
                if (exact != null)
                {
                    args = new object[preferred.Length];   // callers pass null for each preferred arg
                    return exact;
                }

                ConstructorInfo best = null;
                foreach (ConstructorInfo ci in type.GetConstructors())
                {
                    ParameterInfo[] ps = ci.GetParameters();
                    bool allOptional = ps.Length > 0;
                    foreach (ParameterInfo p in ps) if (!p.IsOptional) { allOptional = false; break; }
                    if (!allOptional) continue;
                    if (best == null || ps.Length < best.GetParameters().Length) best = ci;
                }
                if (best == null) return null;

                ParameterInfo[] bps = best.GetParameters();
                var built = new object[bps.Length];
                for (int i = 0; i < bps.Length; i++)
                {
                    Type pt = bps[i].ParameterType;
                    object v = bps[i].DefaultValue;
                    if (v == DBNull.Value || v == Missing.Value)
                        v = pt.IsValueType ? Activator.CreateInstance(pt) : null;
                    // Mono can hand back an optional enum default as its underlying integral type;
                    // Invoke needs the exact enum type for a value-type parameter.
                    else if (pt.IsEnum && v != null && v.GetType() != pt) v = Enum.ToObject(pt, v);
                    built[i] = v;
                }
                args = built;
                return best;
            }
            catch { args = null; return null; }
        }

        /// <summary>
        /// Mirror the modern log's selection into EditWindow_Log's private static selectedMessage.
        /// Archotech's diagnostics view reads that static directly, so without this it would always
        /// report "no log selected" no matter what the player clicked in our list.
        /// </summary>
        public static void MirrorSelection(LogMessage msg)
        {
            if (!_selectedProbed)
            {
                _selectedProbed = true;
                try { _vanillaSelected = AccessTools.Field(typeof(EditWindow_Log), "selectedMessage"); }
                catch { _vanillaSelected = null; }
            }
            if (_vanillaSelected == null) return;
            try { _vanillaSelected.SetValue(null, msg); } catch { }
        }

        private static void Toggle(Type type, ConstructorInfo ctor, object[] args)
        {
            var ws = Find.WindowStack;
            if (ws == null || ctor == null || type == null) return;
            try
            {
                Window existing = null;
                foreach (Window w in ws.Windows)
                    if (w != null && type.IsInstanceOfType(w)) { existing = w; break; }

                if (existing != null) { ws.TryRemove(existing, false); return; }
                if (ctor.Invoke(args) is Window win) ws.Add(win);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[Advanced Dev Tools] could not open an Archotech Logs window: " + e, 0x2E19E52);
            }
        }

        /// <summary>Register our tray buttons. Called once at startup; no-op when Archotech is absent.</summary>
        static ArchotechBridge()
        {
            try
            {
                Probe();
                if (!Available) return;

                // Keep EditWindow_Log.selectedMessage in step with our inspector from now on, so the
                // diagnostics view reflects what the player actually clicked.
                LogState.SelectionChanged += MirrorSelection;

                if (_infoCtor != null)
                LogWidgets.Register("archotech.diagnostics", (window, area, selected, row) =>
                {
                    // Mirror again here as a belt-and-braces: the dialog can be opened before the player
                    // has changed selection even once, so the event may not have fired yet.
                    MirrorSelection(selected);
                    if (row.ButtonText("MDT_ArchotechDiagnostics".Translate(), "MDT_ArchotechDiagnosticsTip".Translate()))
                        Toggle(_infoType, _infoCtor, _infoArgs);
                });

                // Only register a widget we can actually honour - a registered no-op would sit in the
                // tray drawing nothing.
                if (_exportCtor != null)
                    LogWidgets.Register("archotech.export", (window, area, selected, row) =>
                    {
                        if (row.ButtonText("MDT_ArchotechExport".Translate(), "MDT_ArchotechExportTip".Translate()))
                            Toggle(_exportType, _exportCtor, _exportArgs);
                    });
            }
            catch (Exception e)
            {
                Log.Warning("[Advanced Dev Tools] Archotech Logs bridge registration failed: " + e.Message);
            }
        }
    }
}
