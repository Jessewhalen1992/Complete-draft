using System;
using System.Globalization;

namespace WildlifeSweeps
{
    internal static class DmsFormatter
    {
        public static string ToDmsString(double decimalDegrees, bool isLat)
        {
            var abs = Math.Abs(decimalDegrees);
            var degrees = (int)Math.Floor(abs);
            var minutesFull = (abs - degrees) * 60.0;
            var minutes = (int)Math.Floor(minutesFull);
            var seconds = (minutesFull - minutes) * 60.0;

            seconds = Math.Round(seconds, 2, MidpointRounding.AwayFromZero);
            if (seconds >= 60.0)
            {
                seconds -= 60.0;
                minutes += 1;
                if (minutes >= 60)
                {
                    minutes -= 60;
                    degrees += 1;
                }
            }

            var hemi = isLat
                ? (decimalDegrees < 0.0 ? "S" : "N")
                : (decimalDegrees < 0.0 ? "W" : "E");

            var secondsText = seconds.ToString("00.00", CultureInfo.InvariantCulture);
            var minutesText = minutes.ToString("00", CultureInfo.InvariantCulture);
            return $"{degrees}°{minutesText}'{secondsText}\" {hemi}";
        }

        public static double ToSecondsDifference(double valueA, double valueB)
        {
            return Math.Abs(valueA - valueB) * 3600.0;
        }
    }
}
