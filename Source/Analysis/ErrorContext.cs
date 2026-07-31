using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace ModernDevTools
{
    public enum SourceKind { Mod, Vanilla, Harmony, Unity }

    /// <summary>One source (mod / vanilla / Harmony / Unity) implicated in an error.</summary>
    public class AttributedMod
    {
        public string Name;
        public string PackageId;
        public SourceKind Kind = SourceKind.Mod;
        public int Frames;
        public int FirstIndex = int.MaxValue;   // earliest stack frame index; lower = closer to the throw
        public float Weight;
        public bool Active = true;
        public bool Installed = true;
        public string Url;   // link to the mod's page (Workshop / git / site), if known
        public readonly List<string> Reasons = new List<string>();

        public string TopReason => Reasons.Count > 0 ? Reasons[0] : null;
    }

    /// <summary>A plain-language diagnosis produced by a module (library entry, heuristic, etc.).</summary>
    public class ErrorDiagnosis
    {
        public string Title;
        public string Explanation;
        public string Fix;
        public string Url;
        public string Source;   // which module produced it (also the ignore key when Ignorable)
        public float Score;
        public bool Ignorable;  // harmless class the player can mute
        public bool Benign;     // normal, no-fault engine output (rendered as "No concern", green strip)

        /// <summary>True only when this came from a CURATED knowledge source: the shipped/third-party
        /// KnownIssueDef library, a mod's own shipped known-issues file, or the community bug DB. It
        /// drives the "Known issue" badge, so heuristic modules (Harmony, dependencies, version) must
        /// leave it false - otherwise the banner claims the library recognized an error it has never
        /// seen, which reads as "we know this is bad" and sends people hunting a phantom.</summary>
        public bool FromLibrary;

        /// <summary>Curated ceiling on the impact banner. Critical = no opinion (the default), so an
        /// entry only downgrades when its author deliberately says so. Lets curated knowledge overrule
        /// the impact heuristic for self-healing engine lines that merely LOOK severe.</summary>
        public ImpactLevel MaxImpact = ImpactLevel.Critical;
    }

    /// <summary>
    /// The shared input+output passed to every analysis module for one error. Modules read Text /
    /// StackTrace / Frames / Mods and push results via Attribute(...) and AddDiagnosis(...).
    /// Attribution is merged by mod identity; ranking puts the most likely culprit first.
    /// </summary>
    public class ErrorContext
    {
        public LogMessage Message;
        public string Text;
        public string StackTrace;
        public string[] Frames;
        public string ExceptionType;
        public InstalledModIndex Mods;

        /// <summary>Set once, before the module pipeline runs, when the line is recognized as normal,
        /// no-fault engine output (see KnownIssueDef.benign). While true, all attribution is suppressed
        /// so a benign line that merely lists packageIds does not implicate those mods.</summary>
        public bool Benign;

        private readonly Dictionary<string, AttributedMod> _attr =
            new Dictionary<string, AttributedMod>(StringComparer.OrdinalIgnoreCase);

        // RankedMods() runs a five-key LINQ sort plus a ToList. Culprits is defined in terms of it and
        // four modules foreach over Culprits, so the sort was running four-plus times per analysed
        // message. Cached, and invalidated by Merge - which matters because attribution keeps arriving
        // while the pipeline runs (module N's Diagnose may read Culprits before module N+1 contributes).
        private List<AttributedMod> _ranked;
        public readonly List<ErrorDiagnosis> Diagnoses = new List<ErrorDiagnosis>();

        /// <summary>Namespace prefixes seen in the stack trace (filled by the stack-trace module,
        /// read by the knowledge-library module for namespace-scoped matches).</summary>
        public readonly HashSet<string> Namespaces = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Suggested weights (higher = more likely the real culprit).
        public const float WeightStackBase = 5f;
        public const float WeightMessagePrefix = 5f;  // stamped its own name as the log line's [prefix]
        public const float WeightMessageOwner = 6f;   // named as the mod with the problem
        public const float WeightMessagePath = 5f;    // named in a file path
        public const float WeightKnownAttr = 4f;      // captured by a known-issue pattern
        public const float WeightHarmonyPatch = 4f;   // patches a method that appears in the stack trace
        public const float WeightMessagePackage = 3f; // referenced by packageId
        public const float WeightMessageName = 2.5f;  // name mentioned in the text

        public void Attribute(ModContentPack mcp, float weight, string reason, int frameIndex = -1)
        {
            if (mcp == null) return;
            string url = Mods != null ? InstalledModIndex.UrlFor(Mods.PackageId(mcp.PackageId)) : null;
            Merge("pid:" + (mcp.PackageId ?? mcp.Name), mcp.Name, mcp.PackageId, SourceKind.Mod, weight, reason, frameIndex, true, true, url);
        }

        public void Attribute(ModMetaData meta, float weight, string reason, int frameIndex = -1)
        {
            if (meta == null) return;
            Merge("pid:" + (meta.PackageId ?? meta.Name), meta.Name, meta.PackageId, SourceKind.Mod, weight, reason, frameIndex, meta.Active, true, InstalledModIndex.UrlFor(meta));
        }

        public void AttributeSource(SourceKind kind, string name, float weight, string reason, int frameIndex = -1)
        {
            Merge(kind.ToString(), name, null, kind, weight, reason, frameIndex, true, true, null);
        }

        /// <summary>Attribute a mod named in the text that we could not resolve to an installed mod
        /// (e.g. it was uninstalled). Still worth showing since the message explicitly names it.</summary>
        public void AttributeNamed(string name, string packageId, float weight, string reason)
        {
            if (name.NullOrEmpty() && packageId.NullOrEmpty()) return;
            string key = !packageId.NullOrEmpty() ? "pid:" + packageId : "name:" + name.ToLowerInvariant();
            Merge(key, name ?? packageId, packageId, SourceKind.Mod, weight, reason, -1, false, false, null);
        }

        private void Merge(string key, string name, string packageId, SourceKind kind, float weight, string reason, int frameIndex, bool active, bool installed, string url)
        {
            if (Benign) return; // no-fault engine line: never implicate anyone
            if (!_attr.TryGetValue(key, out AttributedMod am))
            {
                am = new AttributedMod { Name = name, PackageId = packageId, Kind = kind, Active = active, Installed = installed };
                _attr[key] = am;
            }
            am.Weight = Math.Max(am.Weight, weight);
            am.Active |= active;
            am.Installed |= installed;
            if (am.Url.NullOrEmpty() && !url.NullOrEmpty()) am.Url = url;
            if (frameIndex >= 0)
            {
                am.Frames++;
                if (frameIndex < am.FirstIndex) am.FirstIndex = frameIndex;
            }
            if (!reason.NullOrEmpty() && !am.Reasons.Contains(reason)) am.Reasons.Add(reason);
            _ranked = null;   // attribution changed: the cached ranking is stale
        }

        public void AddDiagnosis(ErrorDiagnosis d)
        {
            if (d == null || d.Title.NullOrEmpty()) return;
            Diagnoses.Add(d);
        }

        public List<AttributedMod> RankedMods()
        {
            return _ranked ?? (_ranked = _attr.Values
                .OrderBy(a => a.Kind == SourceKind.Mod ? 0 : 1)
                .ThenByDescending(a => a.Weight)
                .ThenBy(a => a.FirstIndex)
                .ThenByDescending(a => a.Frames)
                .ThenBy(a => a.Name)
                .ToList());
        }

        public IEnumerable<AttributedMod> Culprits => RankedMods().Where(m => m.Kind == SourceKind.Mod);
        public bool AnyCulprit => _attr.Values.Any(m => m.Kind == SourceKind.Mod);

        public IEnumerable<string> ImplicatedPackageIds =>
            _attr.Values.Where(m => m.Kind == SourceKind.Mod && !m.PackageId.NullOrEmpty()).Select(m => m.PackageId);
    }
}
