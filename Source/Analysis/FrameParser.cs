using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Verse;

namespace ModernDevTools
{
    /// <summary>Shared, cached parsing of stack-trace lines and exception-type extraction.</summary>
    public static class FrameParser
    {
        // Unity ExtractStackTrace:   "Namespace.Type:Method (argTypes)"
        // Exception .StackTrace:     "  at Namespace.Type.Method (args) [0x..] in <..>:0"
        private static readonly Regex AtForm = new Regex(@"\bat\s+(.+?)\s*[\(\[]", RegexOptions.Compiled);
        private static readonly Regex ColonForm = new Regex(@"^\s*([A-Za-z_][\w.`+<>]*):[A-Za-z_.<>]+\s*\(", RegexOptions.Compiled);
        private static readonly Regex ExceptionForm = new Regex(@"([A-Za-z_][\w.]*?Exception)\b", RegexOptions.Compiled);

        private static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>();

        // Root namespace segments owned by the engine / bundled libraries. A mod that IL-merges one
        // of these (declaring e.g. System.* or Newtonsoft.* types in its own assembly) must NOT be
        // allowed to claim the root, or every vanilla/library frame would misattribute to it.
        private static readonly HashSet<string> FrameworkRoots = new HashSet<string>(StringComparer.Ordinal)
        {
            "System", "Microsoft", "Mono", "Unity", "UnityEngine", "UnityEditor", "UnityEngineInternal",
            "Verse", "RimWorld", "LudeonTK", "HarmonyLib", "MonoMod", "Cecil",
            "Newtonsoft", "Steamworks", "Ionic", "ICSharpCode", "NVorbis", "TMPro", "JetBrains", "NAudio"
        };

        public static string ExtractExceptionType(string message)
        {
            if (message.NullOrEmpty()) return null;
            Match m = ExceptionForm.Match(message);
            return m.Success ? ShortName(m.Groups[1].Value) : null;
        }

        /// <summary>Namespace.Type of a stack-trace line (method segment stripped), or null.</summary>
        public static string QualifiedTypeOf(string line)
        {
            if (line.NullOrEmpty()) return null;
            Match a = AtForm.Match(line);
            if (a.Success) return StripMethod(a.Groups[1].Value.Trim());
            Match c = ColonForm.Match(line);
            if (c.Success) return c.Groups[1].Value.Trim();
            return null;
        }

        public static Type ResolveType(string qualified)
        {
            if (qualified.NullOrEmpty()) return null;
            if (TypeCache.TryGetValue(qualified, out Type cached)) return cached;
            Type t = TryResolve(qualified);
            if (t == null)
            {
                int lastDot = qualified.LastIndexOf('.');
                if (lastDot > 0) t = TryResolve(qualified.Substring(0, lastDot));
            }
            TypeCache[qualified] = t;
            return t;
        }

        public static string NamespaceOf(string fullName)
        {
            if (fullName.NullOrEmpty()) return null;
            int lastDot = fullName.LastIndexOf('.');
            return lastDot > 0 ? fullName.Substring(0, lastDot) : null;
        }

        public static bool LooksLikeHarmony(string q) =>
            q.StartsWith("HarmonyLib", StringComparison.Ordinal) || q.IndexOf("wrapper dynamic-method", StringComparison.Ordinal) >= 0;

        public static bool LooksLikeVanilla(string q) =>
            q.StartsWith("Verse", StringComparison.Ordinal) || q.StartsWith("RimWorld", StringComparison.Ordinal) || q.StartsWith("LudeonTK", StringComparison.Ordinal);

        public static bool LooksLikeUnity(string q) =>
            q.StartsWith("UnityEngine", StringComparison.Ordinal) || q.StartsWith("System", StringComparison.Ordinal) || q.StartsWith("Mono", StringComparison.Ordinal);

        /// <summary>Top-level namespace segment of a qualified type name (e.g. "PerformanceOptimizer"
        /// from "PerformanceOptimizer.Optimizations.Foo"), with generic/nested mangling stripped.</summary>
        public static string RootNamespaceOf(string qualified)
        {
            if (qualified.NullOrEmpty()) return null;
            int dot = qualified.IndexOf('.');
            string root = dot > 0 ? qualified.Substring(0, dot) : qualified;
            int tick = root.IndexOf('`');
            if (tick >= 0) root = root.Substring(0, tick);
            int plus = root.IndexOf('+');
            if (plus >= 0) root = root.Substring(0, plus);
            return root.NullOrEmpty() ? null : root;
        }

        /// <summary>True when the root segment belongs to the engine or a bundled library, so it must
        /// never be attributed to a mod via namespace ownership.</summary>
        public static bool IsFrameworkRoot(string root) =>
            !root.NullOrEmpty() && FrameworkRoots.Contains(root);

        private static string StripMethod(string s)
        {
            int paren = s.IndexOf('(');
            if (paren >= 0) s = s.Substring(0, paren).Trim();
            int lastDot = s.LastIndexOf('.');
            return lastDot <= 0 ? s : s.Substring(0, lastDot);
        }

        private static Type TryResolve(string name)
        {
            try
            {
                int tick = name.IndexOf('`');
                string clean = tick >= 0 ? name.Substring(0, tick) : name;
                return GenTypes.GetTypeInAnyAssembly(clean) ?? GenTypes.GetTypeInAnyAssembly(name);
            }
            catch { return null; }
        }

        private static string ShortName(string typeName)
        {
            int lastDot = typeName.LastIndexOf('.');
            return lastDot >= 0 ? typeName.Substring(lastDot + 1) : typeName;
        }
    }
}
