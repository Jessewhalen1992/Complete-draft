using System.Collections.Generic;

namespace Compass.Modules;

/// <summary>
/// Launches Area Manager by loading AreaManager.DLL and invoking the AMUI command.
/// </summary>
public class AreaManagerModule : ManagedPluginModuleBase
{
    private static readonly string[] CandidatePaths =
    {
        @"C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\AREA MANAGER\AreaManager.DLL",
        @"C:\AUTOCAD-SETUP\Lisp_2000\COMPASS\AREA MANAGER\AreaManager.DLL"
    };

    public override string Id => "area-manager";
    public override string DisplayName => "Area Manager";
    public override string Description => "Generate Crown area and ID tables";

    protected override IReadOnlyList<string> CandidateDllPaths => CandidatePaths;
    protected override string CommandName => "AMUI";
}
