using System.Collections.Generic;

namespace Compass.Modules;

/// <summary>
/// Launches the COGO UI tooling by loading SurveyCalculator.dll and invoking its command.
/// </summary>
public class CogoProgramModule : ManagedPluginModuleBase
{
    private static readonly string[] CandidatePaths =
    {
        @"C:\\AUTOCAD-SETUP CG\\CG_LISP\\COMPASS\\COGO PROGRAM\\SurveyCalculator.DLL",
        @"C:\\AUTOCAD-SETUP CG\\CG_LISP\\COMPASS\\COGO PROGRAM (2015)\\SurveyCalculator.DLL",
        @"C:\\AUTOCAD-SETUP\\Lisp_2000\\COMPASS\\COGO PROGRAM (2015)\\SurveyCalculator.DLL"
    };

    public override string Id => "cogo-ui";
    public override string DisplayName => "COGO UI";
    public override string Description => "COGO PROGRAM WITH UI";

    protected override IReadOnlyList<string> CandidateDllPaths => CandidatePaths;
    protected override string CommandName => "ALSWIZARD";
}
