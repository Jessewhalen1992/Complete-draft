using System.Collections.Generic;

namespace Compass.Modules
{
    /// <summary>
    /// Launches the Surface Impact Table tooling by loading the `GlimpsTable.dll` plug‑in
    /// and invoking its UI command.  The plug‑in parses GLIMPS XML reports and
    /// builds a formatted Surface Impact table inside AutoCAD.  Candidate DLL paths
    /// mirror other Compass modules: a primary CG_LISP location, a versioned 2015
    /// fallback, and a legacy Lisp_2000 location.  The first existing DLL will be
    /// loaded by <see cref="ManagedPluginModuleBase"/>.  When the module is invoked
    /// the AutoCAD command <c>GLIMPSUI</c> is sent to display the palette.
    /// </summary>
    public class SurfaceImpactTableModule : ManagedPluginModuleBase
    {
        private static readonly string[] CandidatePaths =
        {
            // Primary location: modern CG_LISP install
            @"C:\\AUTOCAD-SETUP CG\\CG_LISP\\COMPASS\\PLSR MANAGER\\GlimpsTable.dll",
            // Fallback: explicit 2015 build folder
            @"C:\\AUTOCAD-SETUP CG\\CG_LISP\\COMPASS\\PLSR MANAGER (2015)\\GlimpsTable.dll",
            // Legacy fallback: Lisp_2000 install
            @"C:\\AUTOCAD-SETUP\\Lisp_2000\\COMPASS\\PLSR MANAGER\\GlimpsTable.dll"
        };

        public override string Id => "surface-impact-table";
        public override string DisplayName => "Surface Impact Table";
        public override string Description => "Generate Surface Impact Tables with XML Files";
        protected override IReadOnlyList<string> CandidateDllPaths => CandidatePaths;
        protected override string CommandName => "GLIMPSUI";
    }
}
