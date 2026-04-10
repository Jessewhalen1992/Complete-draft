using System.Collections.Generic;

namespace Compass.Modules;

/// <summary>
/// Launches the Profile‑Xing‑Gen tooling from the Compass palette.
/// This implementation programmatically loads the managed ProfileCrossings.dll
/// (replicating NETLOAD) and then posts the "profilemanager" command to AutoCAD.
/// </summary>
public class ProfileManagerModule : ManagedPluginModuleBaseB
{
    private static readonly string[] CandidatePaths =
    {
        @"C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\PROFILE PROGRAM\ProfileCrossings.dll",
        @"C:\AUTOCAD-SETUP\Lisp_2000\COMPASS\PROFILE PROGRAM\ProfileCrossings.dll"
    };

    public override string Id => "profile-manager";
    public override string DisplayName => "3D Profile Manager";
    public override string Description => "Generate Profiles and Cross-sections";

    protected override IReadOnlyList<string> CandidateDllPaths => CandidatePaths;
    protected override string CommandName => "profilemanager";
}
