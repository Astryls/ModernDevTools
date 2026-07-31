using System;
using System.Collections.Generic;
using LudeonTK;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Who owns the debug log window.
    ///
    /// This exists because replacing the vanilla log window is not a private decision - other mods
    /// decorate <see cref="EditWindow_Log"/> and lose their entire UI when it never opens. The two
    /// confirmed cases in the wild:
    ///
    ///   * HugsLib (UnlimitedHugs.HugsLib) - ships the community-standard extension point
    ///     HugsLib.Logs.LogWindowExtensions.AddLogWindowWidget(WidgetDrawer, WidgetAlignMode),
    ///     rendered by a prefix on EditWindow_Log.DoMessagesListing. It registers "Share logs"
    ///     (LogPublisher), "Files" and "Copy" itself, and ANY other mod may register more.
    ///   * Archotech Logs (kongkim.ArchotechLogs) - postfixes EditWindow_Log.DoWindowContents to add
    ///     its Diagnostics / Export / colour-mode buttons and a repeat-spam banner.
    ///
    /// Before this type existed, Modern Dev Tools' prefixes returned false unconditionally, so
    /// EditWindow_Log was never constructed and all of the above silently vanished with no error.
    /// Now the ownership is explicit, user-switchable, and always leaves an escape hatch.
    /// </summary>
    public enum LogWindowMode
    {
        /// <summary>Modern Dev Tools answers the hotkey, the dev toolbar and auto-open (default).
        /// The vanilla window stays reachable through the "Vanilla log" button.</summary>
        Modern,
        /// <summary>Stand down completely: every log redirect returns to vanilla, so the vanilla
        /// window and everything decorating it behave exactly as if this mod were not installed.
        /// The modern log is still reachable from mod settings and the dev actions window.</summary>
        Vanilla
    }

    /// <summary>
    /// Detects mods that decorate the vanilla log window and arbitrates ownership. Detection is by
    /// TYPE presence rather than packageId, because what actually matters is whether the decorating
    /// code is loaded - a renamed fork or a repackaged copy still counts, and a packageId that is
    /// active but failed to load its assembly does not.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class LogWindowCompat
    {
        /// <summary>One detected decorator: a display name plus the reason we believe it is there.</summary>
        public struct Decorator
        {
            public string Name;
            public string PackageId;
            public string Detail;   // localized "what you would lose" line
        }

        private static readonly List<Decorator> _decorators = new List<Decorator>();
        private static bool _scanned;

        /// <summary>Mods detected as decorating the vanilla log window. Empty on a clean modlist.</summary>
        public static List<Decorator> Decorators { get { EnsureScanned(); return _decorators; } }

        public static bool AnyDecorator { get { EnsureScanned(); return _decorators.Count > 0; } }

        /// <summary>True when HugsLib's log-window widget API is present and usable (Phase 5 hosts it).</summary>
        public static bool HugsLibWidgets { get; private set; }

        public static bool ArchotechLogs { get; private set; }

        static LogWindowCompat() { EnsureScanned(); }

        private static void EnsureScanned()
        {
            if (_scanned) return;
            _scanned = true;
            try
            {
                // HugsLib: the widget API type is the thing that matters, not the mod id.
                if (GenTypes.GetTypeInAnyAssembly("HugsLib.Logs.LogWindowExtensions") != null)
                {
                    HugsLibWidgets = true;
                    _decorators.Add(new Decorator
                    {
                        Name = NameFor("UnlimitedHugs.HugsLib", "HugsLib"),
                        PackageId = "UnlimitedHugs.HugsLib",
                        Detail = "MDT_CompatHugsLibDetail".Translate()
                    });
                }

                // Archotech Logs: its whole UI hangs off EditWindow_Log postfixes.
                if (GenTypes.GetTypeInAnyAssembly("KKArchotechLogs.UI.LogWindowPatch") != null)
                {
                    ArchotechLogs = true;
                    _decorators.Add(new Decorator
                    {
                        Name = NameFor("kongkim.ArchotechLogs", "Archotech Logs"),
                        PackageId = "kongkim.ArchotechLogs",
                        Detail = "MDT_CompatArchotechDetail".Translate()
                    });
                }
            }
            catch (Exception e)
            {
                Log.Warning("[Modern Dev Tools] log-window compat scan failed: " + e.Message);
            }
        }

        private static string NameFor(string packageId, string fallback)
        {
            try
            {
                ModMetaData meta = ModLister.GetModWithIdentifier(packageId, true);
                if (meta != null && !meta.Name.NullOrEmpty()) return meta.Name;
            }
            catch { }
            return fallback;
        }

        public static LogWindowMode Mode =>
            (ModernDevToolsMod.Settings != null && ModernDevToolsMod.Settings.yieldLogWindow)
                ? LogWindowMode.Vanilla : LogWindowMode.Modern;

        /// <summary>
        /// The single gate every log redirect consults. When false, all of our log patches fall through
        /// to vanilla, so the game behaves exactly as if this mod were not installed.
        /// </summary>
        public static bool ModernOwnsLog => Mode == LogWindowMode.Modern;

        /// <summary>Comma-joined decorator names, for the notice strip and settings copy.</summary>
        public static string DecoratorNames()
        {
            EnsureScanned();
            var names = new List<string>(_decorators.Count);
            foreach (Decorator d in _decorators) names.Add(d.Name);
            return names.ToCommaList(true);
        }

        /// <summary>
        /// Open (or close) the VANILLA log window, exactly the way UIRoot.CheckOpenLogWindow does.
        /// This is the escape hatch: it restores 100% of every decorating mod's UI with one click, and
        /// deliberately does NOT route through DebugWindowsOpener (which we patch).
        /// </summary>
        public static void ToggleVanillaLog()
        {
            try
            {
                var ws = Find.WindowStack;
                if (ws == null) return;
                var existing = ws.WindowOfType<EditWindow_Log>();
                if (existing != null) { ws.TryRemove(existing, false); return; }
                ws.Add(new EditWindow_Log());
                EditWindow_Log.wantsToOpen = false;   // consume any pending auto-open so it can't double-add
            }
            catch (Exception e)
            {
                Log.Warning("[Modern Dev Tools] could not open the vanilla log window: " + e.Message);
            }
        }

        public static bool VanillaLogOpen
        {
            get { try { return Find.WindowStack != null && Find.WindowStack.IsOpen(typeof(EditWindow_Log)); } catch { return false; } }
        }

        /// <summary>True while the one-time "these mods also use the log window" strip should show.</summary>
        public static bool ShowHint =>
            AnyDecorator && ModernOwnsLog
            && ModernDevToolsMod.Settings != null && !ModernDevToolsMod.Settings.dismissedLogCompatHint;

        public static void DismissHint()
        {
            var s = ModernDevToolsMod.Settings;
            if (s == null || s.dismissedLogCompatHint) return;
            s.dismissedLogCompatHint = true;
            ModernDevToolsMod.Instance?.WriteSettings();
        }
    }
}
