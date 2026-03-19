using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.Geometry;

namespace WildlifeSweeps
{
    internal static class AtsPolygonFrameBuilder
    {
        public static bool TryBuildFrame(
            IReadOnlyList<Point2d> vertices,
            out Point2d southWest,
            out Vector2d eastUnit,
            out Vector2d northUnit,
            out double width,
            out double height)
        {
            southWest = Point2d.Origin;
            eastUnit = new Vector2d(1.0, 0.0);
            northUnit = new Vector2d(0.0, 1.0);
            width = 0.0;
            height = 0.0;

            if (vertices == null || vertices.Count < 3)
            {
                return false;
            }

            if (!TrySelectEastUnit(vertices, out eastUnit))
            {
                return false;
            }

            northUnit = new Vector2d(-eastUnit.Y, eastUnit.X);
            if (northUnit.Y < 0.0 || (Math.Abs(northUnit.Y) <= 1e-9 && northUnit.X < 0.0))
            {
                eastUnit = -eastUnit;
                northUnit = -northUnit;
            }

            if (!TryGetCorner(vertices, eastUnit, northUnit, Corner.SouthWest, out southWest) ||
                !TryGetCorner(vertices, eastUnit, northUnit, Corner.SouthEast, out var southEast) ||
                !TryGetCorner(vertices, eastUnit, northUnit, Corner.NorthWest, out var northWest))
            {
                return false;
            }

            width = Math.Abs((southEast - southWest).DotProduct(eastUnit));
            height = Math.Abs((northWest - southWest).DotProduct(northUnit));
            return width > 1e-6 && height > 1e-6;
        }

        private static bool TrySelectEastUnit(IReadOnlyList<Point2d> vertices, out Vector2d eastUnit)
        {
            eastUnit = new Vector2d(1.0, 0.0);

            var found = false;
            var bestScore = double.NegativeInfinity;
            var bestLength = double.NegativeInfinity;
            var bestUnit = new Vector2d(1.0, 0.0);
            for (var i = 0; i < vertices.Count; i++)
            {
                var a = vertices[i];
                var b = vertices[(i + 1) % vertices.Count];
                var edge = b - a;
                var length = edge.Length;
                if (length <= 1e-9)
                {
                    continue;
                }

                var unit = edge / length;
                var horizontalScore = Math.Abs(unit.X) - Math.Abs(unit.Y);
                if (!found ||
                    horizontalScore > bestScore + 1e-9 ||
                    (Math.Abs(horizontalScore - bestScore) <= 1e-9 && length > bestLength + 1e-9))
                {
                    found = true;
                    bestScore = horizontalScore;
                    bestLength = length;
                    bestUnit = unit;
                }
            }

            if (!found)
            {
                return false;
            }

            // Quarter/section polylines can start on a vertical edge, so "longest edge"
            // tie-breaking flips east/west on otherwise valid square cells. Prefer the
            // most east-west edge, and rotate a vertical winner back onto the east axis.
            if (Math.Abs(bestUnit.X) < Math.Abs(bestUnit.Y))
            {
                bestUnit = new Vector2d(-bestUnit.Y, bestUnit.X);
            }

            if (bestUnit.X < 0.0 || (Math.Abs(bestUnit.X) <= 1e-9 && bestUnit.Y < 0.0))
            {
                bestUnit = -bestUnit;
            }

            eastUnit = bestUnit / bestUnit.Length;
            return true;
        }

        private static bool TryGetCorner(
            IReadOnlyList<Point2d> vertices,
            Vector2d eastUnit,
            Vector2d northUnit,
            Corner corner,
            out Point2d point)
        {
            point = default;
            var found = false;
            var bestScore = double.MinValue;
            for (var i = 0; i < vertices.Count; i++)
            {
                var vertex = vertices[i];
                var e = (vertex.X * eastUnit.X) + (vertex.Y * eastUnit.Y);
                var n = (vertex.X * northUnit.X) + (vertex.Y * northUnit.Y);
                var score = corner switch
                {
                    Corner.NorthWest => n - e,
                    Corner.NorthEast => n + e,
                    Corner.SouthWest => -n - e,
                    Corner.SouthEast => -n + e,
                    _ => double.MinValue
                };

                if (!found || score > bestScore)
                {
                    bestScore = score;
                    point = vertex;
                    found = true;
                }
            }

            return found;
        }

        private enum Corner
        {
            NorthWest,
            NorthEast,
            SouthWest,
            SouthEast
        }
    }
}
