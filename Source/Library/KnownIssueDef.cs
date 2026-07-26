using System.Collections.Generic;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// A curated, data-driven knowledge-base entry. Shipped with the mod and extendable by any
    /// other mod (it is just a Def). When a selected error matches an entry's signature, the
    /// inspector shows the entry's plain-language explanation (Def.description) and remedy (fix).
    ///
    /// Matching signals (any that are present are tested; more specific = higher score):
    ///   exceptionTypes  short exception names found in the message, e.g. "NullReferenceException"
    ///   regexes         .NET regex patterns tested against the message text
    ///   keywords        case-insensitive substrings of the message text
    ///   namespaces      namespace prefixes appearing in the stack trace
    ///   packageIds      packageIds of mods implicated by the stack trace
    /// </summary>
    public class KnownIssueDef : Def
    {
        // Def.label = short title. Def.description = the plain-language explanation.
        public string fix;                 // suggested remedy (plain language, sentence case)
        public string url;                 // optional help link shown as a copyable line
        public int priority = 0;           // ties broken toward higher priority
        public bool ignorable = false;     // a harmless class the player can mute from the inspector

        /// <summary>Normal, no-fault engine output (the version banner, the "Initializing new game with
        /// mods" list, etc.). A benign match is presented as "No concern": the inspector shows the calm
        /// explanation, suppresses mod attribution (so merely listing packageIds does not implicate them),
        /// and hides the report card. Reserve this for genuine vanilla lines that blame nobody.</summary>
        public bool benign = false;

        /// <summary>Optional: a regex whose first capture group is a mod name or packageId to blame.
        /// When this entry matches, that mod is added to the error's attribution, letting a data entry
        /// both explain the problem AND point at the culprit.</summary>
        public string attributeRegex;

        public List<string> exceptionTypes = new List<string>();
        public List<string> keywords = new List<string>();
        public List<string> regexes = new List<string>();
        public List<string> namespaces = new List<string>();
        public List<string> packageIds = new List<string>();
    }
}
