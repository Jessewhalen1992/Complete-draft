using System.Collections.Generic;

namespace Compass.Modules;

/// <summary>
/// Launches the Surface Development tooling by loading ResidenceSync.dll and invoking its command.
/// </summary>
public class SurfaceDevelopmentModule : ManagedPluginModuleBase
{
    private static readonly string[] CandidatePaths =
    {
        @"C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\RES MANAGER\ResidenceSync.DLL",
        @"C:\AUTOCAD-SETUP\Lisp_2000\COMPASS\RES MANAGER\ResidenceSync.DLL"
    };

    public override string Id => "surface-development";
    public override string DisplayName => "Surface Development";
    public override string Description => "Generate Surf Developments, Manage Residences";

    protected override IReadOnlyList<string> CandidateDllPaths => CandidatePaths;
    protected override string CommandName => "RSUI";
}
