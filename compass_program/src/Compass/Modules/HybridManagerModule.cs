using System.Collections.Generic;

namespace Compass.Modules;

/// <summary>
/// Launches the Hybrid Manager tooling by loading HybridProgram_One.dll and invoking its command.
/// </summary>
public class HybridManagerModule : ManagedPluginModuleBase
{
    private static readonly string[] CandidatePaths =
    {
        @"C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\HYBRID PROGRAM\HybridProgram_One.DLL",
        @"C:\AUTOCAD-SETUP\Lisp_2000\COMPASS\HYBRID PROGRAM\HybridProgram_One.DLL"
    };

    public override string Id => "hybrid-manager";
    public override string DisplayName => "Hybrid Manager";
    public override string Description => "Generate and Manage Hybrid Points";

    protected override IReadOnlyList<string> CandidateDllPaths => CandidatePaths;
    protected override string CommandName => "hybridplan";
}
