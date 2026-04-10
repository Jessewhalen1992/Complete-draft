using System.Collections.Generic;

namespace Compass.Models;

public class DrillGridState
{
    public List<string> DrillNames { get; set; } = new();
    public string Heading { get; set; } = string.Empty;

    public static DrillGridState CreateDefault(int count)
    {
        var state = new DrillGridState();
        for (var i = 0; i < count; i++)
        {
            state.DrillNames.Add($"DRILL_{i + 1}");
        }

        state.Heading = "ICP";
        return state;
    }
}
