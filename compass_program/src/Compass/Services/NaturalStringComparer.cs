using System;
using System.Collections.Generic;

namespace Compass.Services;

public class NaturalStringComparer : IComparer<string>
{
    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        var indexX = 0;
        var indexY = 0;
        while (indexX < x.Length && indexY < y.Length)
        {
            var charX = x[indexX];
            var charY = y[indexY];
            var digitX = char.IsDigit(charX);
            var digitY = char.IsDigit(charY);

            if (digitX && digitY)
            {
                var valueX = 0L;
                while (indexX < x.Length && char.IsDigit(x[indexX]))
                {
                    valueX = valueX * 10 + (x[indexX] - '0');
                    indexX++;
                }

                var valueY = 0L;
                while (indexY < y.Length && char.IsDigit(y[indexY]))
                {
                    valueY = valueY * 10 + (y[indexY] - '0');
                    indexY++;
                }

                var comparison = valueX.CompareTo(valueY);
                if (comparison != 0)
                {
                    return comparison;
                }
            }
            else
            {
                var comparison = char.ToUpperInvariant(charX).CompareTo(char.ToUpperInvariant(charY));
                if (comparison != 0)
                {
                    return comparison;
                }

                indexX++;
                indexY++;
            }
        }

        if (indexX < x.Length)
        {
            return 1;
        }

        if (indexY < y.Length)
        {
            return -1;
        }

        return 0;
    }
}
