using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using Verse;

namespace ModernDevTools
{
    /// <summary>
    /// XML side of the module API: one def per analysis module. Third-party mods add analysis or
    /// reporting by shipping an ErrorModuleDef pointing at their own ErrorModule subclass. Gate it
    /// on a packageId / DLC so a compat module only loads when the mod it understands is present.
    /// </summary>
    public class ErrorModuleDef : Def
    {
        public Type workerClass;
        public int order = 100;
        public bool enabledByDefault = true;
        /// <summary>Only load when this packageId is active.</summary>
        public string requiresPackageId;
        /// <summary>Only load when ANY of these packageIds is active.</summary>
        public List<string> requiresAnyPackageId;
        /// <summary>Only load when this DLC is active: royalty, ideology, biotech, anomaly, odyssey.</summary>
        public string requiresDlc;

        public bool Available
        {
            get
            {
                if (!requiresPackageId.NullOrEmpty() && !ModsConfig.IsActive(requiresPackageId)) return false;
                if (requiresAnyPackageId != null && requiresAnyPackageId.Count > 0
                    && !requiresAnyPackageId.Any(ModsConfig.IsActive)) return false;
                if (!requiresDlc.NullOrEmpty())
                {
                    switch (requiresDlc.ToLowerInvariant())
                    {
                        case "royalty": return ModsConfig.RoyaltyActive;
                        case "ideology": return ModsConfig.IdeologyActive;
                        case "biotech": return ModsConfig.BiotechActive;
                        case "anomaly": return ModsConfig.AnomalyActive;
                        case "odyssey": return ModsConfig.OdysseyActive;
                        default: return false;
                    }
                }
                return true;
            }
        }
    }

    /// <summary>
    /// C# side of the module API. Subclass and either point an ErrorModuleDef at it (XML) or register
    /// an instance via ModernDevToolsAPI.RegisterModule (code). For each selected error the engine
    /// calls ContributeAttribution then Diagnose; both are sandboxed so a broken module can never
    /// break the log. Optionally draw a custom inspector section by overriding HasSection + DrawSection.
    /// </summary>
    public abstract class ErrorModule
    {
        public ErrorModuleDef def;

        public virtual string Label => def?.label?.CapitalizeFirst() ?? GetType().Name;

        /// <summary>Add implicated mods to ctx via ctx.Attribute / ctx.AttributeNamed.</summary>
        public virtual void ContributeAttribution(ErrorContext ctx) { }

        /// <summary>Add plain-language diagnoses to ctx via ctx.AddDiagnosis.</summary>
        public virtual void Diagnose(ErrorContext ctx) { }

        /// <summary>Override to draw an extra titled section in the inspector (advanced). Draw into
        /// x=0..width starting at the given y; return the new y (top of the next section).</summary>
        public virtual bool HasSection => false;
        public virtual float DrawSection(float width, float y, ErrorContext ctx) => y;
    }

    /// <summary>
    /// Builds the active module list from defs (availability + user enable + order) plus any modules
    /// registered through the API. Cached; call Invalidate when the enable set changes.
    /// </summary>
    public static class ErrorModuleRegistry
    {
        private static List<ErrorModule> _modules;
        private static readonly List<ErrorModule> _apiModules = new List<ErrorModule>();

        public static List<ErrorModule> Modules
        {
            get { if (_modules == null) Build(); return _modules; }
        }

        public static void Invalidate() => _modules = null;

        internal static void RegisterApiModule(ErrorModule module)
        {
            if (module == null) return;
            _apiModules.Add(module);
            Invalidate();
        }

        private static void Build()
        {
            var list = new List<ErrorModule>();
            try
            {
                foreach (ErrorModuleDef def in DefDatabase<ErrorModuleDef>.AllDefsListForReading.OrderBy(d => d.order))
                {
                    if (!def.Available) continue;
                    if (!ModernDevToolsMod.IsModuleEnabled(def)) continue;
                    if (def.workerClass == null)
                    {
                        Log.Warning("[Advanced Dev Tools] ErrorModuleDef " + def.defName + " has no workerClass, skipping.");
                        continue;
                    }
                    try
                    {
                        var worker = (ErrorModule)Activator.CreateInstance(def.workerClass);
                        worker.def = def;
                        list.Add(worker);
                    }
                    catch (Exception e)
                    {
                        Log.Warning("[Advanced Dev Tools] Could not instantiate module " + def.defName + ": " + e.Message);
                    }
                }
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Advanced Dev Tools] Failed to build module list: " + e.Message, 0x2E19B10);
            }
            list.AddRange(_apiModules);
            _modules = list;
        }
    }
}
