using System;
using WildlifeSweeps;

internal static class Program
{
    private static int Main()
    {
        try
        {
            RunAll();
            Console.WriteLine("Wildlife findings statement tests passed.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Wildlife findings statement tests failed: " + ex.Message);
            return 1;
        }
    }

    private static void RunAll()
    {
        TestBuildUsesPrimarilyAndOutsideIncluding();
        TestBuildUsesIncludedAndSimilarOutsideSentence();
        TestBuildCollapsesSpeciesSignAndHonorsPositiveFeatureFlags();
        TestBuildUsesNaturalMixedSpeciesWording();
    }

    private static void TestBuildUsesPrimarilyAndOutsideIncluding()
    {
        var result = WildlifeFindingsStatementBuilder.Build(
            new[]
            {
                new FindingRow(WildlifeFindingZone.Proposed, "Snowshoe Hare", "Tracks", "Snowshoe Hare Tracks"),
                new FindingRow(WildlifeFindingZone.Proposed, "Snowshoe Hare", "Tracks", "Snowshoe Hare Tracks"),
                new FindingRow(WildlifeFindingZone.Buffer100, "Snowshoe Hare", "Tracks", "Snowshoe Hare Tracks"),
                new FindingRow(WildlifeFindingZone.Buffer100, "Coyote", "Tracks", "Coyote Tracks"),
                new FindingRow(WildlifeFindingZone.Buffer100, "Red Squirrel", "Feeding Sign", "Red Squirrel Feeding Sign"),
                new FindingRow(WildlifeFindingZone.Buffer100, "Spruce Grouse", "Sighting", "Spruce Grouse Sighting"),
                new FindingRow(WildlifeFindingZone.Outside100, "Moose", "Tracks", "Moose Tracks")
            },
            new WildlifeFeatureFlags(false, false, false, false),
            findingsPageNumber: 3);

        const string expected =
            "No occupied nests, occupied dens, hibernacula, natural mineral licks, or other confirmed key wildlife features requiring a 100 m setback were identified during the sweep. " +
            "Wildlife sign and incidental observations documented within the proposed footprint and 100 m buffer consisted primarily of snowshoe hare tracks, with additional coyote tracks, red squirrel feeding sign, and spruce grouse sighting. " +
            "Additional wildlife sign and incidental observations were documented outside the 100 m buffer, including moose tracks. " +
            "The findings list can be found on page 3.";

        AssertEqual(expected, result, nameof(TestBuildUsesPrimarilyAndOutsideIncluding));
    }

    private static void TestBuildUsesIncludedAndSimilarOutsideSentence()
    {
        var result = WildlifeFindingsStatementBuilder.Build(
            new[]
            {
                new FindingRow(WildlifeFindingZone.Proposed, "Moose", "Tracks", "Moose Tracks"),
                new FindingRow(WildlifeFindingZone.Buffer100, "Coyote", "Tracks", "Coyote Tracks"),
                new FindingRow(WildlifeFindingZone.Buffer100, "Red Squirrel", "Feeding Sign", "Red Squirrel Feeding Sign"),
                new FindingRow(WildlifeFindingZone.Outside100, "Coyote", "Tracks", "Coyote Tracks")
            },
            new WildlifeFeatureFlags(false, false, false, false),
            findingsPageNumber: 4);

        const string expected =
            "No occupied nests, occupied dens, hibernacula, natural mineral licks, or other confirmed key wildlife features requiring a 100 m setback were identified during the sweep. " +
            "Wildlife sign documented within the proposed footprint and 100 m buffer included coyote tracks, moose tracks, and red squirrel feeding sign. " +
            "Additional wildlife sign of similar types was documented outside the 100 m buffer. " +
            "The findings list can be found on page 4.";

        AssertEqual(expected, result, nameof(TestBuildUsesIncludedAndSimilarOutsideSentence));
    }

    private static void TestBuildCollapsesSpeciesSignAndHonorsPositiveFeatureFlags()
    {
        var result = WildlifeFindingsStatementBuilder.Build(
            new[]
            {
                new FindingRow(WildlifeFindingZone.Proposed, "Red Squirrel", "Tracks", "Red Squirrel Tracks"),
                new FindingRow(WildlifeFindingZone.Buffer100, "Red Squirrel", "Feeding Sign", "Red Squirrel Feeding Sign"),
                new FindingRow(WildlifeFindingZone.Buffer100, "Moose", "Sighting", "Moose Sighting"),
                new FindingRow(WildlifeFindingZone.Outside100, "Red Squirrel", "Tracks", "Red Squirrel Tracks"),
                new FindingRow(WildlifeFindingZone.Outside100, "Black Bear", "Tracks", "Black Bear Tracks"),
                new FindingRow(WildlifeFindingZone.Outside100, "Black Bear", "Scat", "Black Bear Scat")
            },
            new WildlifeFeatureFlags(true, false, false, false),
            findingsPageNumber: 5);

        const string expected =
            "Occupied nests were identified during the sweep and require a 100 m setback. " +
            "Wildlife sign and incidental observations documented within the proposed footprint and 100 m buffer consisted primarily of signs of red squirrel, with additional moose sighting. " +
            "Additional wildlife sign and incidental observations of similar types were documented outside the 100 m buffer, including signs of black bear. " +
            "The findings list can be found on page 5.";

        AssertEqual(expected, result, nameof(TestBuildCollapsesSpeciesSignAndHonorsPositiveFeatureFlags));
    }

    private static void TestBuildUsesNaturalMixedSpeciesWording()
    {
        var result = WildlifeFindingsStatementBuilder.Build(
            new[]
            {
                new FindingRow(WildlifeFindingZone.Proposed, "Moose", "Tracks", "Moose Tracks"),
                new FindingRow(WildlifeFindingZone.Proposed, "Moose", "Sighting", "Moose Sighting"),
                new FindingRow(WildlifeFindingZone.Buffer100, "Red Squirrel", "Feeding Sign", "Red Squirrel Feeding Sign")
            },
            new WildlifeFeatureFlags(false, false, false, false),
            findingsPageNumber: 3);

        const string expected =
            "No occupied nests, occupied dens, hibernacula, natural mineral licks, or other confirmed key wildlife features requiring a 100 m setback were identified during the sweep. " +
            "Wildlife sign and incidental observations documented within the proposed footprint and 100 m buffer consisted primarily of signs and incidental observations of moose, with additional red squirrel feeding sign. " +
            "The findings list can be found on page 3.";

        AssertEqual(expected, result, nameof(TestBuildUsesNaturalMixedSpeciesWording));
    }

    private static void AssertEqual(string expected, string actual, string testName)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                testName +
                " expected:\n" +
                expected +
                "\nactual:\n" +
                actual);
        }
    }
}
