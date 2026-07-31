using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Content fingerprints for installed mods, used by the "mods changed since this save" check.
    ///
    /// WHY NOT FOLDER TIMESTAMPS (the thing this replaces):
    /// The previous implementation used Directory.GetLastWriteTimeUtc(mod root) as an "updated" token.
    /// That is not a measure of content, in either direction:
    ///   * FALSE POSITIVE - a directory's write time changes whenever a DIRECT CHILD ENTRY is created,
    ///     deleted or renamed. Steam re-downloading identical bytes (verify-integrity, a resumed
    ///     partial download, moving the Steam library, re-subscribing), a backup/AV tool, or any mod
    ///     or manager that drops a file in the mod root all bump it. Steam also stamps files with the
    ///     LOCAL DOWNLOAD time, never the author's publish date - so a mod whose Workshop page says
    ///     "January" can easily carry a timestamp from last week. We then reported that as fact.
    ///   * FALSE NEGATIVE - editing Defs/Foo.xml three levels down does NOT touch the root directory's
    ///     write time at all, so a real update could go completely unreported.
    ///
    /// WHAT WE USE INSTEAD: a size-only content fingerprint - FNV-1a 64 over the sorted list of
    /// (relative path, file length) for every file under the mod's root(s). No timestamps appear in
    /// the hash at all, which makes it immune to every artifact above: re-downloads, copies, restores
    /// and library moves all reproduce the same fingerprint. It changes when a file is added, removed,
    /// renamed, or edited in a way that changes its length.
    ///
    /// KNOWN LIMIT (accepted deliberately): an edit that preserves every file's exact byte length is
    /// invisible. For a real mod update - which touches XML, DLLs or textures - that is vanishingly
    /// unlikely, and a missed report is far less harmful than a false accusation.
    ///
    /// FAIL CLOSED: any directory we cannot fully read, any mod over the file budget, and all official
    /// content are recorded as UNKNOWN rather than guessed. An unknown on either side of the diff means
    /// the mod is counted as unverified and never reported as changed.
    /// </summary>
    public static class ModFingerprint
    {
        /// <summary>Bumped when the hashing rule changes; snapshots from a different algorithm version
        /// are discarded rather than compared (comparing across versions would flag every mod).</summary>
        public const int AlgorithmVersion = 2;

        // Directories that are never loaded by RimWorld and churn constantly for anyone who develops
        // or version-controls a mod in place. Including them would report the user's own working tree
        // as a "mod update".
        private static readonly HashSet<string> ExcludedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".svn", ".hg", ".vs", ".idea", "obj", "bin", "node_modules", "__pycache__"
        };

        private const int MaxFilesPerMod = 40000;   // beyond this we report unknown rather than stall
        private const int MaxDepth = 24;            // guards against symlink/junction loops (Modmixer
                                                    // itself symlinks mod folders into RimWorld/Mods)

        private static Dictionary<string, string> _map;     // normalized packageId -> fingerprint
        private static volatile bool _ready;
        private static volatile bool _running;
        private static readonly object _lock = new object();

        /// <summary>True once a full scan has completed and fingerprints may be trusted.</summary>
        public static bool Ready => _ready;

        /// <summary>packageId with the engine's "_steam" duplicate postfix removed, lowercased.
        /// ModMetaData.PackageId gains that postfix only when the SAME mod also exists in the local
        /// Mods folder - so the raw id flips as unrelated copies come and go. Normalizing keeps a save
        /// comparable with itself.</summary>
        public static string NormalizeId(string packageId)
        {
            if (packageId.NullOrEmpty()) return null;
            string s = packageId.Trim().ToLowerInvariant();
            const string postfix = "_steam";
            if (s.Length > postfix.Length && s.EndsWith(postfix, StringComparison.Ordinal))
                s = s.Substring(0, s.Length - postfix.Length);
            return s.NullOrEmpty() ? null : s;
        }

        /// <summary>Start the scan on a low-priority background thread. Idempotent.</summary>
        public static void Begin()
        {
            if (_ready || _running) return;
            lock (_lock)
            {
                if (_ready || _running) return;
                _running = true;
            }
            try
            {
                var t = new Thread(Run) { IsBackground = true, Name = "MDT-ModFingerprint", Priority = ThreadPriority.Lowest };
                t.Start();
            }
            catch (Exception e)
            {
                _running = false;
                Log.WarningOnce("[Modern Dev Tools] fingerprint scan could not start: " + e.Message, 0x2E19E01);
            }
        }

        private static void Run()
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                // Group running mods by normalized packageId first: a local + Steam pair of the same
                // mod shares one identity, and hashing them together means moving a mod between
                // Mods/ and the Workshop folder does not read as a content change.
                var roots = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                var official = new HashSet<string>(StringComparer.Ordinal);
                foreach (ModContentPack mcp in LoadedModManager.RunningModsListForReading)
                {
                    if (mcp == null) continue;
                    string id = NormalizeId(mcp.PackageId);
                    if (id == null) continue;
                    if (IsOfficial(id)) { official.Add(id); continue; }   // tracked for presence only
                    string dir = mcp.RootDir;
                    if (dir.NullOrEmpty()) continue;
                    if (!roots.TryGetValue(id, out var list)) roots[id] = list = new List<string>();
                    list.Add(dir);
                }

                foreach (var kv in roots)
                {
                    string fp = Compute(kv.Value);
                    if (fp != null) map[kv.Key] = fp;   // null => unreadable/over budget => stays unknown
                }
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Modern Dev Tools] fingerprint scan failed: " + e.Message, 0x2E19E02);
            }
            finally
            {
                lock (_lock) { _map = map; _running = false; }
                _ready = true;
            }
        }

        /// <summary>Official Ludeon content is deliberately not fingerprinted: its files change with
        /// every game patch (which RimWorld already tells the player about) and Core alone is tens of
        /// thousands of files. Presence/absence is still tracked, so a DLC being turned off is
        /// reported - that being the change that actually breaks saves.</summary>
        public static bool IsOfficial(string normalizedId) =>
            !normalizedId.NullOrEmpty() && normalizedId.StartsWith("ludeon.", StringComparison.Ordinal);

        /// <summary>Fingerprint for a normalized packageId, or null when unknown/not yet scanned.</summary>
        public static string Get(string normalizedId)
        {
            if (normalizedId.NullOrEmpty() || !_ready) return null;
            lock (_lock)
                return _map != null && _map.TryGetValue(normalizedId, out string fp) ? fp : null;
        }

        /// <summary>Copy of the whole map, for writing into a save. Empty until the scan completes.</summary>
        public static Dictionary<string, string> Snapshot()
        {
            var copy = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!_ready) return copy;
            lock (_lock)
            {
                if (_map != null) foreach (var kv in _map) copy[kv.Key] = kv.Value;
            }
            return copy;
        }

        // ---- hashing ------------------------------------------------------------------------------

        /// <summary>Hash of (relative path, length) over every file under the given roots, or null if
        /// any part of the tree could not be read or the budget was exceeded.</summary>
        private static string Compute(List<string> rootDirs)
        {
            var entries = new List<string>(256);
            int budget = MaxFilesPerMod;
            rootDirs.Sort(StringComparer.Ordinal);
            foreach (string root in rootDirs)
            {
                DirectoryInfo di;
                try
                {
                    if (root.NullOrEmpty() || !Directory.Exists(root)) return null;
                    di = new DirectoryInfo(root);
                }
                catch { return null; }
                if (!Walk(di, "", entries, ref budget, 0)) return null;
            }
            if (entries.Count == 0) return null;   // nothing readable: say unknown, never "unchanged"

            entries.Sort(StringComparer.Ordinal);
            ulong h = 14695981039346656037UL;
            foreach (string e in entries)
            {
                for (int i = 0; i < e.Length; i++)
                {
                    char c = e[i];
                    h = (h ^ (byte)(c & 0xFF)) * 1099511628211UL;
                    h = (h ^ (byte)(c >> 8)) * 1099511628211UL;
                }
                h = (h ^ 0x0A) * 1099511628211UL;
            }
            return h.ToString("x16");
        }

        private static bool Walk(DirectoryInfo dir, string prefix, List<string> into, ref int budget, int depth)
        {
            if (depth > MaxDepth) return false;
            FileInfo[] files;
            DirectoryInfo[] subs;
            // One failed directory means a partial picture, and a partial picture would produce a
            // fingerprint that differs from a complete one for reasons that are not a mod update.
            try { files = dir.GetFiles(); subs = dir.GetDirectories(); }
            catch { return false; }

            foreach (FileInfo f in files)
            {
                if (--budget < 0) return false;
                long len;
                try { len = f.Length; } catch { return false; }
                into.Add(prefix + f.Name.ToLowerInvariant() + "\u001F" + len.ToString());
            }
            foreach (DirectoryInfo d in subs)
            {
                if (ExcludedDirs.Contains(d.Name)) continue;
                if (!Walk(d, prefix + d.Name.ToLowerInvariant() + "/", into, ref budget, depth + 1)) return false;
            }
            return true;
        }
    }
}
