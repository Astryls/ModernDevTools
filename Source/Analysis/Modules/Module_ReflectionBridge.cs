using System;
using System.Reflection;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// Configuration for <see cref="Module_ReflectionBridge"/>: which static type to bind and which
    /// methods on it to call. Attach it to an ErrorModuleDef via modExtensions.
    ///
    /// Both methods must be PUBLIC STATIC and take (string messageText, string stackTrace):
    ///   describeMethod       -> string : a ready-to-show sentence, or null/empty for "nothing to say"
    ///   implicatedModMethod  -> string : a packageId or mod name to add to attribution, or null
    /// Either may be omitted.
    /// </summary>
    public class ReflectionBridgeExtension : DefModExtension
    {
        /// <summary>Fully-qualified type name to bind, e.g. "SomeMod.KnownIssues".</summary>
        public string typeName;

        public string describeMethod;
        public string implicatedModMethod;

        /// <summary>Keyed string for the diagnosis title. The bridged mod supplies the body text.</summary>
        public string titleKey = "MDT_BridgeTitle";

        /// <summary>
        /// Diagnosis score. Keep it BELOW 1 unless the bridged source is genuinely curated knowledge:
        /// the house scale reserves single digits for real fixes, and a context-only note that outranks
        /// an actual fix is worse than no note at all.
        /// </summary>
        public float score = 0.5f;

        /// <summary>Attribution weight for implicatedModMethod's answer. Deliberately below
        /// WeightMessageName (2.5) by default, so a bridged guess never outranks the message text.</summary>
        public float attributionWeight = 2f;

        /// <summary>Whether the player can mute this diagnosis.</summary>
        public bool ignorable = true;
    }

    /// <summary>
    /// A DECLARATIVE compat bridge: surfaces what another mod knows about the selected error, configured
    /// entirely from XML, with no compile-time reference to that mod and no new C# per integration.
    ///
    /// This generalises the pattern proven by Module_CircinusCorpus. Every previous integration meant a
    /// new class doing the same four things - probe a type once, bind two static methods, invoke them
    /// defensively, and warn loudly on contract drift - so it is now a def plus a modExtension.
    ///
    /// RULES THIS ENFORCES FOR YOU, because they were learned the hard way:
    ///   * The probe runs ONCE. If the type is absent (the mod is not installed) the module goes silent
    ///     forever after; that is the normal path, not an error, and it logs nothing.
    ///   * If the type IS present but its methods do not match the expected contract, that is reported
    ///     loudly and once. A reflection bridge that goes quietly dormant is precisely the failure mode
    ///     this mod exists to catch, and debugging "why is the card missing" from silence is miserable.
    ///   * A failed probe nulls EVERY binding, never just the one that failed - a half-bound bridge is
    ///     worse than no bridge.
    ///   * Results are never marked FromLibrary: a bridged opinion is not curated knowledge, and must
    ///     not light the "Known issue" badge.
    ///
    /// Gate the def with requiresPackageId so the module does not even appear in the module list for
    /// players without the bridged mod.
    /// </summary>
    public class Module_ReflectionBridge : ErrorModule
    {
        private bool _probed;
        private MethodInfo _describe;
        private MethodInfo _implicated;
        private ReflectionBridgeExtension _ext;

        private ReflectionBridgeExtension Ext =>
            _ext ?? (_ext = def?.GetModExtension<ReflectionBridgeExtension>());

        private void Probe()
        {
            if (_probed) return;
            _probed = true;

            ReflectionBridgeExtension ext = Ext;
            if (ext == null || ext.typeName.NullOrEmpty())
            {
                Log.WarningOnce("[Advanced Dev Tools] " + (def?.defName ?? "a reflection bridge") +
                                " has no ReflectionBridgeExtension (or no typeName); it will do nothing.",
                                (def?.defName ?? "bridge").GetHashCode() ^ 0x2E19E40);
                return;
            }

            try
            {
                Type t = GenTypes.GetTypeInAnyAssembly(ext.typeName);
                if (t == null) return;   // bridged mod absent: the normal quiet path.

                Type[] sig = { typeof(string), typeof(string) };
                if (!ext.describeMethod.NullOrEmpty())
                    _describe = t.GetMethod(ext.describeMethod, BindingFlags.Public | BindingFlags.Static, null, sig, null);
                if (!ext.implicatedModMethod.NullOrEmpty())
                    _implicated = t.GetMethod(ext.implicatedModMethod, BindingFlags.Public | BindingFlags.Static, null, sig, null);

                bool wantDescribe = !ext.describeMethod.NullOrEmpty();
                bool wantImplicated = !ext.implicatedModMethod.NullOrEmpty();
                if ((wantDescribe && _describe == null) || (wantImplicated && _implicated == null))
                {
                    Log.WarningOnce("[Advanced Dev Tools] " + ext.typeName + " was found but its methods did not match " +
                                    "the expected contract (string, string), so " + (def?.defName ?? "this bridge") +
                                    " is disabled. describe=" + (_describe != null) + " implicated=" + (_implicated != null),
                                    ext.typeName.GetHashCode() ^ 0x2E19E41);
                    _describe = null;
                    _implicated = null;
                }
            }
            catch (Exception e)
            {
                _describe = null;
                _implicated = null;
                Log.WarningOnce("[Advanced Dev Tools] reflection bridge probe failed for " +
                                (def?.defName ?? ext.typeName) + ": " + e.Message,
                                (def?.defName ?? "bridge").GetHashCode() ^ 0x2E19E42);
            }
        }

        public override void ContributeAttribution(ErrorContext ctx)
        {
            Probe();
            if (_implicated == null) return;

            string id;
            try { id = (string)_implicated.Invoke(null, new object[] { ctx.Text, ctx.StackTrace }); }
            catch { return; }
            if (id.NullOrEmpty()) return;

            ReflectionBridgeExtension ext = Ext;
            float weight = ext?.attributionWeight ?? 2f;
            string reason = "MDT_ReasonBridge".Translate(Label);

            ModMetaData meta = ctx.Mods?.PackageId(id) ?? ctx.Mods?.ExactName(id);
            if (meta != null) ctx.Attribute(meta, weight, reason);
            else ctx.AttributeNamed(id, null, weight, reason);
        }

        public override void Diagnose(ErrorContext ctx)
        {
            Probe();
            if (_describe == null) return;

            string line;
            try { line = (string)_describe.Invoke(null, new object[] { ctx.Text, ctx.StackTrace }); }
            catch { return; }
            if (line.NullOrEmpty()) return;

            ReflectionBridgeExtension ext = Ext;
            ctx.AddDiagnosis(new ErrorDiagnosis
            {
                Title = (ext?.titleKey ?? "MDT_BridgeTitle").Translate(Label),
                Explanation = line,          // the bridged mod writes its own sentence
                Source = "bridge:" + (def?.defName ?? "?"),
                Score = ext?.score ?? 0.5f,
                Ignorable = ext?.ignorable ?? true,
                FromLibrary = false          // a bridged opinion is not curated knowledge
            });
        }
    }
}
