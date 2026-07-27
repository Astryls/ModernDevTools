using System.Collections.Generic;
using System.Text.RegularExpressions;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// A tiny plain-language glossary of the jargon that shows up in RimWorld errors and in this mod's
    /// own explanations (Harmony, def, xpath, TPS, load order, ...). It scans the selected error plus its
    /// diagnoses for these terms and returns short definitions, so a player who does not already know the
    /// vocabulary can still understand what they are reading. Data only; the UI lives in Module_Glossary.
    /// </summary>
    public static class Glossary
    {
        private class Term
        {
            public Regex Rx;
            public string LabelKey;
            public string DefKey;
        }

        private static List<Term> _terms;

        private static void Ensure()
        {
            if (_terms != null) return;
            var list = new List<Term>();
            void Add(string pattern, string label, string def) =>
                list.Add(new Term
                {
                    Rx = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled),
                    LabelKey = label,
                    DefKey = def
                });

            Add(@"\bharmony\b", "MDT_GlHarmony", "MDT_GlHarmonyDef");
            Add(@"\btranspiler\b", "MDT_GlTranspiler", "MDT_GlTranspilerDef");
            Add(@"\bcross[\s-]?reference\b", "MDT_GlCrossRef", "MDT_GlCrossRefDef");
            Add(@"\bdef(name)?s?\b", "MDT_GlDef", "MDT_GlDefDef");
            Add(@"\bxpath\b", "MDT_GlXpath", "MDT_GlXpathDef");
            Add(@"\bpatch operation\b", "MDT_GlPatchOp", "MDT_GlPatchOpDef");
            Add(@"\bstack trace\b", "MDT_GlStackTrace", "MDT_GlStackTraceDef");
            Add(@"\bnull ?reference\b|nullreferenceexception", "MDT_GlNullRef", "MDT_GlNullRefDef");
            Add(@"\bTPS\b", "MDT_GlTps", "MDT_GlTpsDef");
            Add(@"\bFPS\b", "MDT_GlFps", "MDT_GlFpsDef");
            Add(@"\bload[\s-]?order\b", "MDT_GlLoadOrder", "MDT_GlLoadOrderDef");
            Add(@"\bpackageid\b", "MDT_GlPackageId", "MDT_GlPackageIdDef");
            Add(@"\bhugslib\b", "MDT_GlHugsLib", "MDT_GlHugsLibDef");
            Add(@"typeloadexception|reflectiontypeloadexception", "MDT_GlTypeLoad", "MDT_GlTypeLoadDef");
            Add(@"\bthing ?comps?\b|\bcomps?\b", "MDT_GlComp", "MDT_GlCompDef");
            Add(@"\bhediffs?\b", "MDT_GlHediff", "MDT_GlHediffDef");
            Add(@"\bticks?\b|\bticking\b", "MDT_GlTick", "MDT_GlTickDef");
            Add(@"\bscribe\b", "MDT_GlScribe", "MDT_GlScribeDef");
            Add(@"\bload ?id\b", "MDT_GlLoadId", "MDT_GlLoadIdDef");
            Add(@"\bdefof\b", "MDT_GlDefOf", "MDT_GlDefOfDef");
            Add(@"\bgizmos?\b", "MDT_GlGizmo", "MDT_GlGizmoDef");
            Add(@"\bitab\b|\binspect tab\b", "MDT_GlITab", "MDT_GlITabDef");
            Add(@"\bmotes?\b", "MDT_GlMote", "MDT_GlMoteDef");
            Add(@"\bassembl(y|ies)\b|\.dll\b", "MDT_GlAssembly", "MDT_GlAssemblyDef");
            Add(@"\b(pre|post)fix\b", "MDT_GlPrePostfix", "MDT_GlPrePostfixDef");
            Add(@"\bdlc\b", "MDT_GlDlc", "MDT_GlDlcDef");

            _terms = list;
        }

        /// <summary>Returns (term label, definition) pairs whose term appears in the text, capped so the
        /// section stays short. Order follows the term list (roughly most-common first).</summary>
        public static List<KeyValuePair<string, string>> TermsIn(string text, int max = 8)
        {
            var found = new List<KeyValuePair<string, string>>();
            if (text.NullOrEmpty()) return found;
            Ensure();
            foreach (Term t in _terms)
            {
                if (!t.Rx.IsMatch(text)) continue;
                found.Add(new KeyValuePair<string, string>(t.LabelKey.Translate(), t.DefKey.Translate()));
                if (found.Count >= max) break;
            }
            return found;
        }

        /// <summary>Every glossary term as (label, definition), translated. Used by the knowledge browser.</summary>
        public static List<KeyValuePair<string, string>> AllTerms()
        {
            Ensure();
            var list = new List<KeyValuePair<string, string>>(_terms.Count);
            foreach (Term t in _terms)
                list.Add(new KeyValuePair<string, string>(t.LabelKey.Translate(), t.DefKey.Translate()));
            return list;
        }
    }
}
