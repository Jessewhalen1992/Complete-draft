using System.Collections.Generic;

namespace Compass.Modules;

/// <summary>
/// Launches the Section Generator tooling by loading the SECTION GENERATOR plug-in
/// and invoking its Seclist command.
/// </summary>
public class SectionGeneratorModule : ManagedPluginModuleBase
{
    private static readonly string[] CandidatePaths =
    {
        @"C:\\AUTOCAD-SETUP CG\\CG_LISP\\COMPASS\\SECTION GENERATOR\\SECTIONGENERATOR.dll",
        @"C:\\AUTOCAD-SETUP\\Lisp_2000\\COMPASS\\SECTION GENERATOR\\SECTIONGENERATOR.dll"
    };

    public override string Id => "section-generator";
    public override string DisplayName => "Section Generator";
    public override string Description => "Generates Section List for Title Block";

    protected override IReadOnlyList<string> CandidateDllPaths => CandidatePaths;
    protected override string CommandName => "Seclist";
}
