using System;
using LudeonTK;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Dev-only debug actions that emit test log entries, so the modern debug log (attribution, the
    /// impact banner, the community report card, filters and culling) can be exercised on demand without
    /// waiting for a real error. They appear under the "Modern Dev Tools" category in the Actions tab of
    /// the dev-actions window (and can be pinned to the palette), from the main menu and in-game.
    ///
    /// The static constructor registers the category's sort order. RimWorld's DebugActionCategories only
    /// orders categories it knows about; every other (mod) category falls back to int.MaxValue and is
    /// dumped at the very bottom of the Actions list. Registering a low order pulls ours to the top so
    /// it is easy to find.
    ///
    /// These are developer-facing tools that never reach a player, so their names and payloads are plain
    /// literal strings (same convention as vanilla debug actions) rather than translated keys.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class DebugActions_Testing
    {
        private const string Category = "Modern Dev Tools";
        private const string Prefix = "[Modern Dev Tools test] ";
        private const AllowedGameStates Anywhere = AllowedGameStates.Entry | AllowedGameStates.Playing;

        static DebugActions_Testing()
        {
            try
            {
                // Give our category a defined order (top of the list) instead of int.MaxValue (bottom).
                if (DebugActionCategories.categoryOrders != null && !DebugActionCategories.categoryOrders.ContainsKey(Category))
                    DebugActionCategories.categoryOrders[Category] = 50;
            }
            catch (Exception e) { Log.Warning("[Modern Dev Tools] could not register debug category order: " + e.Message); }
        }

        [DebugAction(Category, "Log test message", allowedGameStates = Anywhere, displayPriority = 900)]
        private static void LogTestMessage()
        {
            Log.Message(Prefix + "informational message. This is a benign Message-level entry used to check the log's message filter and list rendering.");
        }

        [DebugAction(Category, "Log test warning", allowedGameStates = Anywhere, displayPriority = 899)]
        private static void LogTestWarning()
        {
            Log.Warning(Prefix + "warning. Warning-level entries show the report card and the impact banner, and can be muted with Ignore.");
        }

        [DebugAction(Category, "Log test error", allowedGameStates = Anywhere, displayPriority = 898)]
        private static void LogTestError()
        {
            Log.Error(Prefix + "error. A plain Error-level entry with no exception - use it to check the impact banner and the 'Report this error' flow.");
        }

        [DebugAction(Category, "Throw test exception", allowedGameStates = Anywhere, displayPriority = 897)]
        private static void ThrowTestException()
        {
            // A real exception with a stack trace that runs through this mod's own types, so the stack-trace
            // attribution should name Modern Dev Tools and the report signature is derived from real frames.
            try { ThrowInner(0); }
            catch (Exception e) { Log.Error(Prefix + "caught test exception:\n" + e); }
        }

        [DebugAction(Category, "Log burst (mixed)", allowedGameStates = Anywhere, displayPriority = 896)]
        private static void LogBurst()
        {
            for (int i = 1; i <= 3; i++) Log.Message(Prefix + "burst message " + i + " of 3.");
            for (int i = 1; i <= 2; i++) Log.Warning(Prefix + "burst warning " + i + " of 2.");
            Log.Error(Prefix + "burst error. Use this to fill the log quickly and test filters, counts and culling.");
        }

        [DebugAction(Category, "Log repeated error x5", allowedGameStates = Anywhere, displayPriority = 895)]
        private static void LogRepeatedError()
        {
            // Same text five times, so the log collapses them into one row with a repeat count (x5).
            for (int i = 0; i < 5; i++) Log.Error(Prefix + "repeated error - this line is logged five times to test repeat collapsing.");
        }

        // --- nested calls so the thrown exception has a few mod-owned frames ---

        private static void ThrowInner(int depth)
        {
            if (depth < 2) { ThrowInner(depth + 1); return; }
            throw new InvalidOperationException(Prefix + "simulated failure from DebugActions_Testing.ThrowInner (this is intentional).");
        }
    }
}
