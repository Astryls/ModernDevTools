using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Fast lookup from the fragments that show up in error text/stacks to the mod they belong to.
    /// Indexes ALL installed mods (ModLister.AllInstalledMods) by packageId, display name and folder
    /// - not just active ones - because most load-time warnings name inactive-but-installed mods.
    /// The assembly map only covers active mods (only they have loaded code in the stack).
    /// </summary>
    public class InstalledModIndex
    {
        public readonly Dictionary<string, ModMetaData> ByPackageId = new Dictionary<string, ModMetaData>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, ModMetaData> ByName = new Dictionary<string, ModMetaData>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, ModMetaData> ByFolder = new Dictionary<string, ModMetaData>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<Assembly, ModContentPack> ByAssembly = new Dictionary<Assembly, ModContentPack>();

        // Normalized identity keys ("giddyup", "mappreview", ...) -> the mods that own them, used to
        // resolve a bracket log-prefix tag ("[Giddy-Up]") back to a mod. _byNorm is the exact index;
        // _normList is the same pairs flattened for prefix/abbreviation scanning.
        private readonly Dictionary<string, List<ModMetaData>> _byNorm =
            new Dictionary<string, List<ModMetaData>>(StringComparer.Ordinal);
        private readonly List<KeyValuePair<string, ModMetaData>> _normList =
            new List<KeyValuePair<string, ModMetaData>>();

        // A trailing version stamp on a tag: "Map Preview v1.12.25" -> "Map Preview", "Giddy-Up 2" -> "Giddy-Up".
        private static readonly Regex TagVersionSuffix =
            new Regex(@"[\s._-]*v?\d+(?:[._]\d+)*\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Root namespace segment -> the single mod whose loaded assemblies declare it. Built lazily
        // (the first stack frame that fails assembly resolution) because it enumerates every type in
        // every mod assembly. Only unambiguous roots are kept - see BuildNamespaceRoots.
        private Dictionary<string, ModContentPack> _nsRoots;
        private readonly object _nsLock = new object();

        private static readonly object _buildLock = new object();
        private static InstalledModIndex _instance;
        public static InstalledModIndex Instance
        {
            get
            {
                if (_instance != null) return _instance;
                lock (_buildLock) { if (_instance == null) _instance = Build(); }
                return _instance;
            }
        }
        public static void Invalidate() { lock (_buildLock) _instance = null; }

        /// <summary>Build the whole index (installed mods + namespace-root map) ahead of time, off the
        /// interactive path. Called on a background thread at startup so the first error analysis - which
        /// on a launch-time auto-open of the log lands right as the user reads/closes it - does not block
        /// the main thread enumerating every mod assembly's types on a large modlist.</summary>
        public static void Prewarm()
        {
            try
            {
                InstalledModIndex idx = Instance;   // builds the installed-mod index (locked)
                idx.EnsureNamespaceRoots();         // builds the namespace-root map (locked)
                KnownIssueIndex.EnsureBuilt();      // cheap def scan; fold it into the prewarm too
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Advanced Dev Tools] index prewarm failed: " + e.Message, 0x2E19B04);
            }
        }

        private static InstalledModIndex Build()
        {
            var idx = new InstalledModIndex();
            try
            {
                foreach (ModMetaData meta in ModLister.AllInstalledMods)
                {
                    if (meta == null) continue;
                    if (!meta.PackageId.NullOrEmpty()) Put(idx.ByPackageId, meta.PackageId, meta);
                    if (!meta.Name.NullOrEmpty()) Put(idx.ByName, meta.Name, meta);
                    try
                    {
                        string folder = meta.RootDir?.Name;
                        if (!folder.NullOrEmpty()) Put(idx.ByFolder, folder, meta);
                    }
                    catch { }
                    idx.AddNormKeys(meta);
                }
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Advanced Dev Tools] Failed to index installed mods: " + e.Message, 0x2E19B01);
            }

            try
            {
                foreach (ModContentPack mcp in LoadedModManager.RunningModsListForReading)
                {
                    var asms = mcp.assemblies?.loadedAssemblies;
                    if (asms == null) continue;
                    foreach (Assembly a in asms)
                        if (a != null && !idx.ByAssembly.ContainsKey(a)) idx.ByAssembly[a] = mcp;
                }
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Advanced Dev Tools] Failed to map assemblies to mods: " + e.Message, 0x2E19B02);
            }

            return idx;
        }

        /// <summary>Index a mod under a key, PREFERRING AN ACTIVE MOD over an inactive one.
        ///
        /// Display names and folder names are not unique across installed mods - an old local copy of a
        /// mod sitting alongside the Workshop one is the everyday case. First-writer-wins meant a lookup
        /// could return the INACTIVE copy, and that copy then answers every question asked about the
        /// result: Module_VersionMismatch reads ITS About.xml, so a stale local folder could produce a
        /// confident "this mod does not support 1.6" about a mod that supports it fine. The running mod
        /// is the one the error came from, so it wins.</summary>
        private static void Put(Dictionary<string, ModMetaData> map, string key, ModMetaData meta)
        {
            if (!map.TryGetValue(key, out ModMetaData existing)) { map[key] = meta; return; }
            if (existing == null) { map[key] = meta; return; }
            bool existingActive;
            bool candidateActive;
            try { existingActive = existing.Active; candidateActive = meta.Active; } catch { return; }
            if (candidateActive && !existingActive) map[key] = meta;
        }

        public ModMetaData PackageId(string pid) =>
            !pid.NullOrEmpty() && ByPackageId.TryGetValue(pid, out var m) ? m : null;

        public ModMetaData ExactName(string name) =>
            !name.NullOrEmpty() && ByName.TryGetValue(name.Trim(), out var m) ? m : null;

        public ModMetaData Folder(string folder) =>
            !folder.NullOrEmpty() && ByFolder.TryGetValue(folder, out var m) ? m : null;

        public ModContentPack Assembly(Assembly a) =>
            a != null && ByAssembly.TryGetValue(a, out var m) ? m : null;

        // ---- Bracket log-prefix ("[Giddy-Up]") tag matching ----------------------------------------

        /// <summary>Lowercase, ASCII-alphanumeric-only form of a string ("Giddy-Up 2" -> "giddyup2").
        /// Non-ASCII characters are dropped, so a CJK display name folds away and the mod is matched on
        /// its ASCII packageId suffix instead. Null when nothing survives.</summary>
        public static string Normalize(string s)
        {
            if (s.NullOrEmpty()) return null;
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
            {
                if (c >= 'A' && c <= 'Z') sb.Append((char)(c + 32));
                else if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9')) sb.Append(c);
            }
            return sb.Length == 0 ? null : sb.ToString();
        }

        private void AddNormKeys(ModMetaData meta)
        {
            if (meta == null) return;
            AddNormKey(Normalize(meta.Name), meta);
            if (!meta.PackageId.NullOrEmpty())
            {
                AddNormKey(Normalize(meta.PackageId), meta);
                int dot = meta.PackageId.LastIndexOf('.');
                if (dot >= 0 && dot < meta.PackageId.Length - 1)
                    AddNormKey(Normalize(meta.PackageId.Substring(dot + 1)), meta);
            }
            try
            {
                string folder = meta.RootDir?.Name;
                // Workshop folders are numeric published-file ids; a normalized numeric key would only
                // ever match a numeric tag, so skip it.
                if (!folder.NullOrEmpty() && !AllDigits(folder))
                    AddNormKey(Normalize(folder), meta);
            }
            catch { }
        }

        private void AddNormKey(string key, ModMetaData meta)
        {
            if (key.NullOrEmpty() || key.Length < 3) return;
            if (!_byNorm.TryGetValue(key, out var list)) _byNorm[key] = list = new List<ModMetaData>();
            if (list.Contains(meta)) return;
            list.Add(meta);
            _normList.Add(new KeyValuePair<string, ModMetaData>(key, meta));
        }

        private static bool AllDigits(string s)
        {
            foreach (char c in s) if (c < '0' || c > '9') return false;
            return s.Length > 0;
        }

        /// <summary>Resolve a bracket log-prefix tag ("[Giddy-Up]", "[Map Preview v1.12.25]",
        /// "[MeleeAnim]") to the single ACTIVE mod that emits it, or null when the tag is not a clear,
        /// unambiguous mod name. Only active mods are considered - a running mod stamps the log line,
        /// so this both improves accuracy and rules out matching an inactive namesake. Ambiguous tags
        /// (a prefix shared by several mods) and non-mod tags ("[Ref ..]", "[0x..]") resolve to null.</summary>
        public ModMetaData MatchTag(string rawTag)
        {
            string nt = NormalizeTag(rawTag);
            if (nt == null) return null;

            // 1) Exact identity match (name / packageId / packageId suffix / folder), preferring the
            //    sole active owner.
            if (_byNorm.TryGetValue(nt, out var exact))
            {
                ModMetaData m = SoleActive(exact);
                if (m != null) return m;
            }

            // 2) Abbreviation: the tag is a prefix of exactly one active mod's identity. Require a
            //    reasonably specific tag and a UNIQUE shortest matching key, so "[MeleeAnim]" resolves
            //    to "Melee Animation" (key "meleeanimation") and not the longer "Melee Animation Vanilla",
            //    while a genuinely ambiguous prefix resolves to nothing.
            if (nt.Length >= 6)
            {
                ModMetaData best = null;
                int bestLen = int.MaxValue;
                bool tie = false;
                foreach (var kv in _normList)
                {
                    if (kv.Key.Length <= nt.Length) continue;      // exact handled above
                    if (!kv.Value.Active) continue;
                    if (!kv.Key.StartsWith(nt, StringComparison.Ordinal)) continue;
                    if (kv.Key.Length < bestLen) { bestLen = kv.Key.Length; best = kv.Value; tie = false; }
                    else if (kv.Key.Length == bestLen && kv.Value != best) tie = true;
                }
                if (best != null && !tie) return best;
            }
            return null;
        }

        /// <summary>Resolve a Harmony owner id (the string a mod passes to `new Harmony(id)`) back to the
        /// mod that owns it. Owner ids are free-form but overwhelmingly follow the mod's packageId or a
        /// reverse-DNS form, so we try: (1) exact packageId, (2) normalized-exact against name/packageId/
        /// suffix/folder keys, (3) a unique suffix match so "com.author.mymod" folds onto the mod key
        /// "mymod". Ambiguous or unrecognizable ids resolve to null (the raw id is still shown as text).</summary>
        public ModMetaData MatchOwnerId(string owner)
        {
            if (owner.NullOrEmpty()) return null;
            if (ByPackageId.TryGetValue(owner, out var direct) && direct != null) return direct;

            string n = Normalize(owner);
            if (n == null) return null;
            if (_byNorm.TryGetValue(n, out var exact))
            {
                ModMetaData m = SoleActive(exact) ?? (exact.Count == 1 ? exact[0] : null);
                if (m != null) return m;
            }

            ModMetaData best = null;
            int bestLen = 0;
            bool tie = false;
            foreach (var kv in _normList)
            {
                if (kv.Key.Length < 5) continue;
                if (kv.Value == null || !kv.Value.Active) continue;
                if (!n.EndsWith(kv.Key, StringComparison.Ordinal)) continue;
                if (kv.Key.Length > bestLen) { bestLen = kv.Key.Length; best = kv.Value; tie = false; }
                else if (kv.Key.Length == bestLen && kv.Value != best) tie = true;
            }
            return (best != null && !tie) ? best : null;
        }

        private static ModMetaData SoleActive(List<ModMetaData> owners)
        {
            ModMetaData found = null;
            foreach (ModMetaData m in owners)
            {
                if (m == null || !m.Active) continue;
                if (found != null && found != m) return null;   // ambiguous among active mods
                found = m;
            }
            return found;
        }

        private static string NormalizeTag(string rawTag)
        {
            if (rawTag.NullOrEmpty()) return null;
            string t = rawTag.Trim();
            // Drop obvious non-mod bracket tags before normalizing.
            if (t.StartsWith("Ref ", StringComparison.OrdinalIgnoreCase)) return null;  // RimWorld log ref markers
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return null;    // hex offsets
            t = TagVersionSuffix.Replace(t, "");
            string nt = Normalize(t);
            if (nt == null || nt.Length < 4) return null;   // too short to identify a mod
            bool hasLetter = false;
            foreach (char c in nt) if (c >= 'a' && c <= 'z') { hasLetter = true; break; }
            return hasLetter ? nt : null;                    // skip pure-number / hex-ish tags
        }

        /// <summary>The mod that OWNS a stack frame's root namespace (e.g. a Harmony patch class
        /// "SomeMod.Patches.Foo.Prefix" -> the mod that ships namespace "SomeMod"). High certainty:
        /// only returns a mod when exactly one loaded mod declares types under that root. Used as a
        /// fallback when the frame's type could not be resolved to a loaded assembly.</summary>
        public ModContentPack NamespaceRoot(string root)
        {
            if (root.NullOrEmpty() || FrameParser.IsFrameworkRoot(root)) return null;
            if (_nsRoots == null) EnsureNamespaceRoots();
            return _nsRoots.TryGetValue(root, out var m) ? m : null;
        }

        /// <summary>Build the namespace-root map once, thread-safely. This enumerates every type in every
        /// loaded mod assembly, so it is the expensive part of analysis - prewarmed at startup so the
        /// first error inspection does not freeze.</summary>
        public void EnsureNamespaceRoots()
        {
            if (_nsRoots != null) return;
            lock (_nsLock) { if (_nsRoots == null) _nsRoots = BuildNamespaceRoots(); }
        }

        private static Dictionary<string, ModContentPack> BuildNamespaceRoots()
        {
            var result = new Dictionary<string, ModContentPack>(StringComparer.Ordinal);
            var owners = new Dictionary<string, HashSet<ModContentPack>>(StringComparer.Ordinal);
            try
            {
                foreach (ModContentPack mcp in LoadedModManager.RunningModsListForReading)
                {
                    var asms = mcp.assemblies?.loadedAssemblies;
                    if (asms == null) continue;
                    foreach (Assembly a in asms)
                    {
                        if (a == null) continue;
                        Type[] types;
                        try { types = a.GetTypes(); }
                        catch (ReflectionTypeLoadException rtle) { types = rtle.Types; }
                        catch { continue; }
                        if (types == null) continue;
                        foreach (Type t in types)
                        {
                            string ns = t?.Namespace;
                            if (ns.NullOrEmpty()) continue;
                            int dot = ns.IndexOf('.');
                            string root = dot > 0 ? ns.Substring(0, dot) : ns;
                            if (FrameParser.IsFrameworkRoot(root)) continue;
                            if (!owners.TryGetValue(root, out var set)) owners[root] = set = new HashSet<ModContentPack>();
                            set.Add(mcp);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Advanced Dev Tools] Failed to index namespace roots: " + e.Message, 0x2E19B03);
            }
            // Keep only roots owned by a single mod - a root shared by two mods (forks, shared library
            // namespace) is ambiguous and would give false certainty, so we drop it.
            foreach (var kv in owners)
                if (kv.Value.Count == 1)
                    foreach (var m in kv.Value) { result[kv.Key] = m; break; }
            return result;
        }

        /// <summary>Best outbound link for a mod: its About.xml url (usually git/website), else its
        /// Steam Workshop page when it came from the Workshop. Null when neither is known.</summary>
        public static string UrlFor(ModMetaData meta)
        {
            if (meta == null) return null;
            try
            {
                if (!meta.Url.NullOrEmpty()) return meta.Url;
                if (meta.OnSteamWorkshop)
                {
                    // For a Workshop mod the folder name IS the published file id
                    // (Steam stores it under .../294100/<id>). Avoids a Steamworks reference.
                    string id = meta.RootDir?.Name;
                    if (!id.NullOrEmpty() && ulong.TryParse(id, out _))
                        return "https://steamcommunity.com/sharedfiles/filedetails/?id=" + id;
                }
            }
            catch { }
            return null;
        }
    }
}
