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
        // Shown in every game state. Note: the flags are AND-combined in IsAllowedInCurrentGameState, so
        // Entry | Playing means "in Entry AND Playing at once" = never - use Invalid (no restriction) for
        // "anywhere". These actions were previously invisible in the list and only usable via the palette.
        private const AllowedGameStates Anywhere = AllowedGameStates.Invalid;

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

        [DebugAction(Category, "Log error with jargon (glossary demo)", allowedGameStates = Anywhere, displayPriority = 896)]
        private static void LogGlossaryDemo()
        {
            // Deliberately packed with modding jargon so the inspector's "Terms in this error" glossary
            // card renders (it scans the message text + exception type + diagnoses for known terms).
            Log.Error(Prefix + "glossary demo. A Harmony transpiler failed to apply its patch operation, "
                + "so a def cross-reference could not resolve through its xpath and a null reference followed. "
                + "Check your load order and the packageId; this can also surface as a TypeLoadException from "
                + "HugsLib, and errors like it can hurt both TPS and FPS. See the stack trace for the mod.");
        }

        [DebugAction(Category, "Log benign vanilla line (no-concern demo)", allowedGameStates = Anywhere, displayPriority = 894)]
        private static void LogBenignDemo()
        {
            // Deliberately NOT prefixed: this mimics vanilla's harmless "Initializing new game with mods:"
            // dump verbatim (a real Log.Message that lists packageIds) so the benign library recognizes it.
            // The inspector should tag it "No concern" and, crucially, leave "Likely source" empty - the
            // listed mods must NOT be implicated just because their packageIds appear in the text.
            Log.Message("Initializing new game with mods:\n  - brrainz.harmony\n  - Ludeon.RimWorld\n  - Ludeon.RimWorld.Royalty\n  - astryl.ModernDevTools\n  - modmixer.bridge");
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

        // --- log-spam throttle tests -------------------------------------------------------------
        // These exist because the throttle is otherwise untestable: "Log repeated error x5" hits the
        // threshold exactly and suppresses nothing. Both actions log 200 lines, which is deliberately
        // enough to be felt - with the throttle OFF they write 200 stack traces to Player.log.

        private const int SpamCount = 200;

        [DebugAction(Category, "Log spam x200 (same line)", allowedGameStates = Anywhere, displayPriority = 893)]
        private static void LogSpamSameLine()
        {
            // Vanilla DOES collapse this one: LogMessageQueue combines a message with lastMessage, and
            // every line here is identical and consecutive. Expect a single row reading x99 (vanilla's
            // repeat cap) or, with the throttle on, x5+195.
            for (int i = 0; i < SpamCount; i++)
                Log.Error(Prefix + "spam test - identical line repeated " + SpamCount + " times.");
        }

        [DebugAction(Category, "Log spam x200 (two interleaved lines)", allowedGameStates = Anywhere, displayPriority = 892)]
        private static void LogSpamInterleaved()
        {
            // THE CASE VANILLA CANNOT HANDLE, and the whole reason the throttle exists.
            // LogMessageQueue only ever compares against lastMessage, so dedupe is strictly
            // CONSECUTIVE. Alternating two lines means neither ever matches lastMessage: nothing
            // collapses, nothing reaches the 99-repeat cap, and all 400 entries extract a full stack
            // trace and write to disk. This is the real-world shape of one broken pawn raising two
            // errors per tick.
            //
            // Throttle OFF -> 400 separate rows, each x1.
            // Throttle ON  -> two rows, each x5 plus a suppressed count, and a periodic summary line.
            for (int i = 0; i < SpamCount; i++)
            {
                Log.Error(Prefix + "interleaved spam A - vanilla cannot collapse this because it never repeats consecutively.");
                Log.Error(Prefix + "interleaved spam B - the alternating partner of line A.");
            }
        }

        // --- nested calls so the thrown exception has a few mod-owned frames ---

        private static void ThrowInner(int depth)
        {
            if (depth < 2) { ThrowInner(depth + 1); return; }
            throw new InvalidOperationException(Prefix + "simulated failure from DebugActions_Testing.ThrowInner (this is intentional).");
        }
    }
}
