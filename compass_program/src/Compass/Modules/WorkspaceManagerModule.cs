using System.Collections.Generic;

namespace Compass.Modules;

/// <summary>
/// Launches the Workspace Manager tooling by loading MyAutocadProgram.dll and invoking its command.
/// </summary>
public class WorkspaceManagerModule : ManagedPluginModuleBase
{
    private static readonly string[] CandidatePaths =
    {
        @"C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\WORKSPACE PROGRAM\MyAutocadProgram.DLL",
        @"C:\AUTOCAD-SETUP\Lisp_2000\COMPASS\WORKSPACE PROGRAM\MyAutocadProgram.DLL"
    };

    public override string Id => "workspace-manager";
    public override string DisplayName => "Workspace Builder";
    public override string Description => "Workspace Generator Tool";

    protected override IReadOnlyList<string> CandidateDllPaths => CandidatePaths;
    protected override string CommandName => "WSG";
}
