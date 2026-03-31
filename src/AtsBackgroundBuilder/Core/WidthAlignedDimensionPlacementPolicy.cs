using System;
using System.Collections.Generic;

namespace AtsBackgroundBuilder.Core
{
    internal readonly struct WidthDimensionPoint
    {
        public WidthDimensionPoint(double x, double y)
        {
            X = x;
            Y = y;
        }

        public double X { get; }
        public double Y { get; }
    }

    internal readonly struct WidthAlignedDimensionPlacement
    {
        public WidthAlignedDimensionPlacement(
            WidthDimensionPoint textPoint,
            WidthDimensionPoint dimLinePoint,
            double textAlong,
            double dimLineAlong,
            double dimLineOffset,
            bool textOutsideArrowSpan)
        {
            TextPoint = textPoint;
            DimLinePoint = dimLinePoint;
            TextAlong = textAlong;
            DimLineAlong = dimLineAlong;
            DimLineOffset = dimLineOffset;
            TextOutsideArrowSpan = textOutsideArrowSpan;
        }

        public WidthDimensionPoint TextPoint { get; }
        public WidthDimensionPoint DimLinePoint { get; }
        public double TextAlong { get; }
        public double DimLineAlong { get; }
        public double DimLineOffset { get; }
        public bool TextOutsideArrowSpan { get; }
    }

    internal static class WidthAlignedDimensionPlacementPolicy
    {
        public static double GetPreferredOutsideAlongOffset(
            double spanLength,
            double halfTextAlong,
            double edgeMargin)
        {
            var safeHalfTextAlong = Math.Max(halfTextAlong, 0.0);
            var safeEdgeMargin = Math.Max(edgeMargin, 0.0);
            if (spanLength <= 1e-6)
            {
                return safeHalfTextAlong + safeEdgeMargin;
            }

            var preferredGap = Math.Max(spanLength, safeEdgeMargin);
            return (spanLength * 0.5) + safeHalfTextAlong + preferredGap;
        }

        public static WidthAlignedDimensionPlacement Resolve(
            WidthDimensionPoint spanStart,
            WidthDimensionPoint spanEnd,
            WidthDimensionPoint textPoint,
            double dimLineOffset)
        {
            var spanDx = spanEnd.X - spanStart.X;
            var spanDy = spanEnd.Y - spanStart.Y;
            var spanLength = Math.Sqrt((spanDx * spanDx) + (spanDy * spanDy));
            if (spanLength <= 1e-6)
            {
                var fallbackTextPoint = new WidthDimensionPoint(textPoint.X, textPoint.Y);
                var fallbackPoint = new WidthDimensionPoint(
                    (spanStart.X + spanEnd.X) * 0.5,
                    (spanStart.Y + spanEnd.Y) * 0.5);
                return new WidthAlignedDimensionPlacement(
                    fallbackTextPoint,
                    fallbackPoint,
                    textAlong: 0.0,
                    dimLineAlong: 0.0,
                    dimLineOffset: dimLineOffset,
                    textOutsideArrowSpan: false);
            }

            var spanUx = spanDx / spanLength;
            var spanUy = spanDy / spanLength;
            var normalX = -spanUy;
            var normalY = spanUx;
            var midX = (spanStart.X + spanEnd.X) * 0.5;
            var midY = (spanStart.Y + spanEnd.Y) * 0.5;
            var requestX = textPoint.X - midX;
            var requestY = textPoint.Y - midY;
            var textAlong = (requestX * spanUx) + (requestY * spanUy);
            var projectedTextPoint = new WidthDimensionPoint(
                midX + (spanUx * textAlong),
                midY + (spanUy * textAlong));
            var dimLinePoint = new WidthDimensionPoint(
                midX + (normalX * dimLineOffset),
                midY + (normalY * dimLineOffset));

            return new WidthAlignedDimensionPlacement(
                projectedTextPoint,
                dimLinePoint,
                textAlong,
                dimLineAlong: 0.0,
                dimLineOffset,
                textOutsideArrowSpan: Math.Abs(textAlong) > ((spanLength * 0.5) + 1e-6));
        }

        public static IReadOnlyList<double> BuildSameLineAlongOffsets(
            double spanLength,
            double preferredAlong,
            double halfTextAlong,
            double edgeMargin,
            double step,
            int expansionCount)
        {
            var results = new List<double>();
            var safeStep = Math.Max(step, 1e-6);
            var safeEdgeMargin = Math.Max(edgeMargin, 0.0);
            var safeHalfTextAlong = Math.Max(halfTextAlong, 0.0);
            var safeExpansionCount = Math.Max(1, expansionCount);
            var preferredSign = preferredAlong < 0.0 ? -1.0 : 1.0;
            var insideHalfSpan = Math.Max(0.0, (spanLength * 0.5) - safeHalfTextAlong - safeEdgeMargin);
            var canFitInsideArrowSpan = (safeHalfTextAlong + safeEdgeMargin) <= ((spanLength * 0.5) + 1e-6);

            void Add(double value)
            {
                for (var i = 0; i < results.Count; i++)
                {
                    if (Math.Abs(results[i] - value) <= 1e-6)
                    {
                        return;
                    }
                }

                results.Add(value);
            }

            if (canFitInsideArrowSpan && Math.Abs(preferredAlong) <= insideHalfSpan + 1e-6)
            {
                Add(preferredAlong);
                for (var i = 1; i <= safeExpansionCount; i++)
                {
                    var delta = safeStep * i;
                    Add(preferredAlong + delta);
                    Add(preferredAlong - delta);
                }
            }
            else
            {
                var outsideStart = (spanLength * 0.5) + safeHalfTextAlong + safeEdgeMargin;
                var preferredOutsideAlong = GetPreferredOutsideAlongOffset(
                    spanLength,
                    safeHalfTextAlong,
                    safeEdgeMargin);
                Add(preferredSign * preferredOutsideAlong);
                Add(-preferredSign * preferredOutsideAlong);
                Add(preferredSign * outsideStart);
                Add(-preferredSign * outsideStart);
                for (var i = 1; i <= safeExpansionCount; i++)
                {
                    var delta = safeStep * i;
                    Add(preferredSign * (preferredOutsideAlong + delta));
                    Add(-preferredSign * (preferredOutsideAlong + delta));

                    var tightenedOutside = preferredOutsideAlong - delta;
                    if (tightenedOutside > outsideStart + 1e-6)
                    {
                        Add(preferredSign * tightenedOutside);
                        Add(-preferredSign * tightenedOutside);
                    }
                }

                Add(preferredAlong);
                Add(0.0);
            }

            return results;
        }

        public static double EstimateTextHalfAlong(string text, double textHeight, double pad)
        {
            if (textHeight <= 0.0)
            {
                textHeight = 10.0;
            }

            var lines = SplitLabelLines(text);
            var maxChars = 1;
            for (var i = 0; i < lines.Count; i++)
            {
                maxChars = Math.Max(maxChars, Math.Max(1, lines[i].Length));
            }

            var width = Math.Max(textHeight * 2.0, maxChars * textHeight * 0.62) + (Math.Max(0.0, pad) * 2.0);
            return width * 0.5;
        }

        private static IReadOnlyList<string> SplitLabelLines(string text)
        {
            var normalized = string.IsNullOrEmpty(text)
                ? string.Empty
                : text.Replace("\\P", "\n");
            var rawLines = normalized.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            var lines = new List<string>();
            for (var i = 0; i < rawLines.Length; i++)
            {
                lines.Add(rawLines[i] ?? string.Empty);
            }

            if (lines.Count == 0)
            {
                lines.Add(string.Empty);
            }

            return lines;
        }
    }
}
