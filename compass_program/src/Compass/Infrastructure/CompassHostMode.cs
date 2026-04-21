using System;
using System.Diagnostics;

namespace Compass.Infrastructure;

internal static class CompassHostMode
{
    private const string HeadlessEnvironmentVariable = "COMPASS_HEADLESS";

    public static bool IsHeadless
    {
        get
        {
            if (TryReadBooleanEnvironmentVariable(HeadlessEnvironmentVariable, out var enabled))
            {
                return enabled;
            }

            try
            {
                return string.Equals(Process.GetCurrentProcess().ProcessName, "accoreconsole", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    private static bool TryReadBooleanEnvironmentVariable(string variableName, out bool value)
    {
        value = false;
        var raw = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        raw = raw.Trim();
        if (string.Equals(raw, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "yes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "y", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }

        if (string.Equals(raw, "0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "no", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "n", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }

        return bool.TryParse(raw, out value);
    }
}
