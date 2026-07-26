namespace ModernDevTools
{
    /// <summary>
    /// Public entry point for other mods that want to extend Modern Dev Tools from C# (as an
    /// alternative or complement to shipping ErrorModuleDef / KnownIssueDef XML).
    ///
    /// Example:
    ///   ModernDevTools.ModernDevToolsAPI.RegisterModule(new MyAnalyzer());
    ///
    /// where MyAnalyzer : ModernDevTools.ErrorModule overrides ContributeAttribution / Diagnose.
    /// </summary>
    public static class ModernDevToolsAPI
    {
        /// <summary>Register a module instance. It runs for every analysed error alongside the
        /// built-in modules. Safe to call from a StaticConstructorOnStartup or Mod ctor.</summary>
        public static void RegisterModule(ErrorModule module) => ErrorModuleRegistry.RegisterApiModule(module);

        /// <summary>Rebuild the module list and drop cached analyses (call after changing what is
        /// registered or enabled at runtime).</summary>
        public static void Invalidate()
        {
            ErrorModuleRegistry.Invalidate();
            LogAnalysisCache.Clear();
        }
    }
}
