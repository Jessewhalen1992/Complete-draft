using System.Collections.Generic;

namespace Compass.Modules;

/// <summary>
/// Launches the Crossing Manager tooling by loading XingManager.dll and invoking its command.
/// </summary>
public class CrossingManagerModule : ManagedPluginModuleBase
{
    private static readonly string[] CandidatePaths =
    {
        @"C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\XING MANAGER\XingManager.DLL",
        @"C:\AUTOCAD-SETUP\Lisp_2000\COMPASS\XING MANAGER\XingManager.DLL"
    };

    public override string Id => "crossing-manager";
    public override string DisplayName => "Crossing Manager";
    public override string Description => "Manage Crossing bubbles and Tables";

    protected override IReadOnlyList<string> CandidateDllPaths => CandidatePaths;
    protected override string CommandName => "xingform";
}
