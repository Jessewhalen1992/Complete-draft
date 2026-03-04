using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace WildlifeSweeps
{
    internal static class BoundarySamplingHelper
    {
        public static IReadOnlyList<Point3d> BuildBoundaryVertices(
            Polyline boundary,
            double samplingStepMeters,
            double sampleMergeTolerance)
        {
            var vertices = new List<Point3d>(Math.Max(16, boundary.NumberOfVertices * 3));
            var totalLength = boundary.Length;
            if (totalLength <= 0.0)
            {
                for (var index = 0; index < boundary.NumberOfVertices; index++)
                {
                    AddBoundaryVertex(vertices, boundary.GetPoint3dAt(index), sampleMergeTolerance);
                }

                return vertices;
            }

            for (var index = 0; index < boundary.NumberOfVertices; index++)
            {
                var segmentStartDistance = boundary.GetDistanceAtParameter(index);
                var segmentEndDistance = index == boundary.NumberOfVertices - 1
                    ? totalLength
                    : boundary.GetDistanceAtParameter(index + 1);

                AddBoundaryVertex(vertices, boundary.GetPoint3dAt(index), sampleMergeTolerance);
                if (segmentEndDistance <= segmentStartDistance)
                {
                    continue;
                }

                var distance = segmentStartDistance + samplingStepMeters;
                while (distance < segmentEndDistance - sampleMergeTolerance)
                {
                    AddBoundaryVertex(vertices, boundary.GetPointAtDist(distance), sampleMergeTolerance);
                    distance += samplingStepMeters;
                }
            }

            return vertices;
        }

        private static void AddBoundaryVertex(List<Point3d> vertices, Point3d candidate, double sampleMergeTolerance)
        {
            if (vertices.Count == 0 || vertices[vertices.Count - 1].DistanceTo(candidate) > sampleMergeTolerance)
            {
                vertices.Add(candidate);
            }
        }
    }
}
