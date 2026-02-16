using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AtsBackgroundBuilder
{
    public partial class Plugin
    {
        private static void NormalizeShortRoadAllowanceLayersByNeighborhood(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            IReadOnlyCollection<ObjectId> generatedRoadAllowanceIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 102.0);
            if (clipWindows.Count == 0)
            {
                return;
            }

            bool IsPointInAnyWindow(Point2d p)
            {
                for (var i = 0; i < clipWindows.Count; i++)
                {
                    var w = clipWindows[i];
                    if (p.X >= w.MinPoint.X && p.X <= w.MaxPoint.X &&
                        p.Y >= w.MinPoint.Y && p.Y <= w.MaxPoint.Y)
                    {
                        return true;
                    }
                }

                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b)
            {
                if (IsPointInAnyWindow(a) || IsPointInAnyWindow(b))
                {
                    return true;
                }

                for (var i = 0; i < clipWindows.Count; i++)
                {
                    if (TryClipSegmentToWindow(a, b, clipWindows[i], out _, out _))
                    {
                        return true;
                    }
                }

                return false;
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                bool TryReadOpenSegment(Entity ent, out Point2d a, out Point2d b)
                {
                    a = default;
                    b = default;
                    if (ent == null)
                    {
                        return false;
                    }

                    if (ent is Line ln)
                    {
                        a = new Point2d(ln.StartPoint.X, ln.StartPoint.Y);
                        b = new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                        return a.GetDistanceTo(b) > 1e-4;
                    }

                    if (ent is Polyline pl)
                    {
                        if (pl.Closed || pl.NumberOfVertices != 2)
                        {
                            return false;
                        }

                        a = pl.GetPoint2dAt(0);
                        b = pl.GetPoint2dAt(1);
                        return a.GetDistanceTo(b) > 1e-4;
                    }

                    return false;
                }

                bool IsHorizontalLike(Point2d a, Point2d b)
                {
                    var d = b - a;
                    return Math.Abs(d.X) >= Math.Abs(d.Y);
                }

                bool IsVerticalLike(Point2d a, Point2d b)
                {
                    var d = b - a;
                    return Math.Abs(d.Y) > Math.Abs(d.X);
                }

                var generatedSet = generatedRoadAllowanceIds == null
                    ? new HashSet<ObjectId>()
                    : new HashSet<ObjectId>(generatedRoadAllowanceIds.Where(id => !id.IsNull));
                var segments = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, bool Horizontal, bool Vertical, double Length)>();
                var generatedSegments = new List<(ObjectId Id, Point2d A, Point2d B, bool Horizontal, bool Vertical, double Length)>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    if (!string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b))
                    {
                        continue;
                    }

                    if (!DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    var len = a.GetDistanceTo(b);
                    var horizontal = IsHorizontalLike(a, b);
                    var vertical = IsVerticalLike(a, b);
                    if (!horizontal && !vertical)
                    {
                        continue;
                    }

                    segments.Add((id, ent.Layer, a, b, horizontal, vertical, len));
                    if (generatedSet.Contains(id))
                    {
                        generatedSegments.Add((id, a, b, horizontal, vertical, len));
                    }
                }

                if (segments.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                const double candidateMinLen = 2.0;
                const double candidateMaxLen = 120.0;
                const double neighborAxisTol = 1.50;
                const double neighborEndTol = 2.50;
                const double neighborMinLen = 6.0;
                const double referenceDistanceTol = 1.20;
                const double corridorAxisTol = 31.0;
                const double corridorOverlapMin = 20.0;

                bool IsNearGeneratedCorridor((ObjectId Id, string Layer, Point2d A, Point2d B, bool Horizontal, bool Vertical, double Length) candidate)
                {
                    if (generatedSegments.Count == 0)
                    {
                        return false;
                    }

                    for (var gi = 0; gi < generatedSegments.Count; gi++)
                    {
                        var g = generatedSegments[gi];
                        if ((candidate.Horizontal != g.Horizontal) || (candidate.Vertical != g.Vertical))
                        {
                            continue;
                        }

                        if (candidate.Horizontal)
                        {
                            var yCandidate = 0.5 * (candidate.A.Y + candidate.B.Y);
                            var yGenerated = 0.5 * (g.A.Y + g.B.Y);
                            if (Math.Abs(yCandidate - yGenerated) > corridorAxisTol)
                            {
                                continue;
                            }

                            var cMin = Math.Min(candidate.A.X, candidate.B.X);
                            var cMax = Math.Max(candidate.A.X, candidate.B.X);
                            var gMin = Math.Min(g.A.X, g.B.X);
                            var gMax = Math.Max(g.A.X, g.B.X);
                            var overlap = Math.Min(cMax, gMax) - Math.Max(cMin, gMin);
                            if (overlap < Math.Max(corridorOverlapMin, Math.Min(candidate.Length, g.Length) * 0.35))
                            {
                                continue;
                            }
                        }
                        else
                        {
                            var xCandidate = 0.5 * (candidate.A.X + candidate.B.X);
                            var xGenerated = 0.5 * (g.A.X + g.B.X);
                            if (Math.Abs(xCandidate - xGenerated) > corridorAxisTol)
                            {
                                continue;
                            }

                            var cMin = Math.Min(candidate.A.Y, candidate.B.Y);
                            var cMax = Math.Max(candidate.A.Y, candidate.B.Y);
                            var gMin = Math.Min(g.A.Y, g.B.Y);
                            var gMax = Math.Max(g.A.Y, g.B.Y);
                            var overlap = Math.Min(cMax, gMax) - Math.Max(cMin, gMin);
                            if (overlap < Math.Max(corridorOverlapMin, Math.Min(candidate.Length, g.Length) * 0.35))
                            {
                                continue;
                            }
                        }

                        return true;
                    }

                    return false;
                }

                var inspected = 0;
                var normalized = 0;
                for (var i = 0; i < segments.Count; i++)
                {
                    var s = segments[i];
                    if (s.Length < candidateMinLen || s.Length > candidateMaxLen)
                    {
                        continue;
                    }

                    if (!s.Horizontal && !s.Vertical)
                    {
                        continue;
                    }

                    // Keep 30.16/20.11 generated RA corridors stable; they are normalized separately.
                    if (IsNearGeneratedCorridor(s))
                    {
                        continue;
                    }

                    inspected++;
                    var yS = 0.5 * (s.A.Y + s.B.Y);
                    var xS = 0.5 * (s.A.X + s.B.X);

                    double secVotesA = 0.0;
                    double usecVotesA = 0.0;
                    double secVotesB = 0.0;
                    double usecVotesB = 0.0;
                    var secCountA = 0;
                    var usecCountA = 0;
                    var secCountB = 0;
                    var usecCountB = 0;
                    var bestSecScore = double.MaxValue;
                    var bestUsecScore = double.MaxValue;

                    for (var j = 0; j < segments.Count; j++)
                    {
                        if (i == j)
                        {
                            continue;
                        }

                        var o = segments[j];
                        if (o.Length < neighborMinLen)
                        {
                            continue;
                        }

                        if (s.Horizontal != o.Horizontal || s.Vertical != o.Vertical)
                        {
                            continue;
                        }

                        if (s.Horizontal)
                        {
                            var yO = 0.5 * (o.A.Y + o.B.Y);
                            if (Math.Abs(yS - yO) > neighborAxisTol)
                            {
                                continue;
                            }
                        }
                        else
                        {
                            var xO = 0.5 * (o.A.X + o.B.X);
                            if (Math.Abs(xS - xO) > neighborAxisTol)
                            {
                                continue;
                            }
                        }

                        var dA = Math.Min(s.A.GetDistanceTo(o.A), s.A.GetDistanceTo(o.B));
                        var dB = Math.Min(s.B.GetDistanceTo(o.A), s.B.GetDistanceTo(o.B));
                        var isSec = string.Equals(o.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase);
                        var isUsec = string.Equals(o.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
                        if (!isSec && !isUsec)
                        {
                            continue;
                        }

                        var daRef = DistancePointToSegment(s.A, o.A, o.B);
                        var dbRef = DistancePointToSegment(s.B, o.A, o.B);
                        if (daRef <= referenceDistanceTol || dbRef <= referenceDistanceTol)
                        {
                            var score = daRef + dbRef;
                            if (isSec)
                            {
                                if (score < bestSecScore)
                                {
                                    bestSecScore = score;
                                }
                            }
                            else if (score < bestUsecScore)
                            {
                                bestUsecScore = score;
                            }
                        }

                        if (dA <= neighborEndTol)
                        {
                            var wA = 1.0 + (neighborEndTol - dA);
                            if (isSec)
                            {
                                secVotesA += wA;
                                secCountA++;
                            }
                            else
                            {
                                usecVotesA += wA;
                                usecCountA++;
                            }
                        }

                        if (dB <= neighborEndTol)
                        {
                            var wB = 1.0 + (neighborEndTol - dB);
                            if (isSec)
                            {
                                secVotesB += wB;
                                secCountB++;
                            }
                            else
                            {
                                usecVotesB += wB;
                                usecCountB++;
                            }
                        }
                    }

                    string? bestEndA = null;
                    if (secVotesA > usecVotesA + 0.25)
                    {
                        bestEndA = "L-SEC";
                    }
                    else if (usecVotesA > secVotesA + 0.25)
                    {
                        bestEndA = "L-USEC";
                    }

                    string? bestEndB = null;
                    if (secVotesB > usecVotesB + 0.25)
                    {
                        bestEndB = "L-SEC";
                    }
                    else if (usecVotesB > secVotesB + 0.25)
                    {
                        bestEndB = "L-USEC";
                    }

                    string? targetLayer = null;
                    if (!string.IsNullOrWhiteSpace(bestEndA) &&
                        !string.IsNullOrWhiteSpace(bestEndB) &&
                        string.Equals(bestEndA, bestEndB, StringComparison.OrdinalIgnoreCase))
                    {
                        targetLayer = bestEndA;
                    }
                    else
                    {
                        var secVotes = secVotesA + secVotesB;
                        var usecVotes = usecVotesA + usecVotesB;
                        var secCount = secCountA + secCountB;
                        var usecCount = usecCountA + usecCountB;
                        if (secCount >= 2 || usecCount >= 2)
                        {
                            if (secVotes > usecVotes + 1.00)
                            {
                                targetLayer = "L-SEC";
                            }
                            else if (usecVotes > secVotes + 1.00)
                            {
                                targetLayer = "L-USEC";
                            }
                        }
                    }

                    if (string.IsNullOrWhiteSpace(targetLayer) && s.Length <= 80.0)
                    {
                        if (bestSecScore < double.MaxValue || bestUsecScore < double.MaxValue)
                        {
                            if (bestSecScore == double.MaxValue)
                            {
                                targetLayer = "L-USEC";
                            }
                            else if (bestUsecScore == double.MaxValue)
                            {
                                targetLayer = "L-SEC";
                            }
                            else
                            {
                                var diff = Math.Abs(bestSecScore - bestUsecScore);
                                if (diff >= 0.35)
                                {
                                    targetLayer = bestSecScore < bestUsecScore
                                        ? "L-SEC"
                                        : "L-USEC";
                                }
                            }
                        }
                    }

                    if (string.IsNullOrWhiteSpace(targetLayer) ||
                        string.Equals(s.Layer, targetLayer, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Entity? writable = null;
                    try
                    {
                        writable = tr.GetObject(s.Id, OpenMode.ForWrite, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (writable == null || writable.IsErased)
                    {
                        continue;
                    }

                    writable.Layer = targetLayer;
                    writable.ColorIndex = 256;
                    normalized++;
                }

                tr.Commit();
                logger?.WriteLine($"Cleanup: neighborhood-normalized {normalized} boundary segment(s) layer (inspected {inspected}).");
            }
        }

        private static void NormalizeHorizontalSecRoadAllowanceLayers(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            IReadOnlyCollection<ObjectId> generatedRoadAllowanceIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null || generatedRoadAllowanceIds == null || generatedRoadAllowanceIds.Count == 0)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 102.0);
            if (clipWindows.Count == 0)
            {
                return;
            }

            bool IsPointInAnyWindow(Point2d p)
            {
                for (var i = 0; i < clipWindows.Count; i++)
                {
                    var w = clipWindows[i];
                    if (p.X >= w.MinPoint.X && p.X <= w.MaxPoint.X &&
                        p.Y >= w.MinPoint.Y && p.Y <= w.MaxPoint.Y)
                    {
                        return true;
                    }
                }

                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b)
            {
                if (IsPointInAnyWindow(a) || IsPointInAnyWindow(b))
                {
                    return true;
                }

                for (var i = 0; i < clipWindows.Count; i++)
                {
                    if (TryClipSegmentToWindow(a, b, clipWindows[i], out _, out _))
                    {
                        return true;
                    }
                }

                return false;
            }

            bool TryReadOpenSegment(Entity ent, out Point2d a, out Point2d b)
            {
                a = default;
                b = default;
                if (ent == null)
                {
                    return false;
                }

                if (ent is Line ln)
                {
                    a = new Point2d(ln.StartPoint.X, ln.StartPoint.Y);
                    b = new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                    return a.GetDistanceTo(b) > 1e-4;
                }

                if (ent is Polyline pl)
                {
                    if (pl.Closed || pl.NumberOfVertices < 2)
                    {
                        return false;
                    }

                    a = pl.GetPoint2dAt(0);
                    b = pl.GetPoint2dAt(pl.NumberOfVertices - 1);
                    if (a.GetDistanceTo(b) <= 1e-4)
                    {
                        return false;
                    }

                    // Accept multi-vertex open polylines only when they are effectively collinear.
                    if (pl.NumberOfVertices > 2)
                    {
                        const double collinearTol = 0.35;
                        for (var vi = 1; vi < pl.NumberOfVertices - 1; vi++)
                        {
                            var p = pl.GetPoint2dAt(vi);
                            if (DistancePointToInfiniteLine(p, a, b) > collinearTol)
                            {
                                return false;
                            }
                        }
                    }

                    return true;
                }

                return false;
            }

            bool IsHorizontalLike(Point2d a, Point2d b)
            {
                var d = b - a;
                return Math.Abs(d.X) >= Math.Abs(d.Y);
            }

            bool HorizontalOverlaps((Point2d A, Point2d B) a, (Point2d A, Point2d B) b, double minOverlap)
            {
                var aMin = Math.Min(a.A.X, a.B.X);
                var aMax = Math.Max(a.A.X, a.B.X);
                var bMin = Math.Min(b.A.X, b.B.X);
                var bMax = Math.Max(b.A.X, b.B.X);
                var overlap = Math.Min(aMax, bMax) - Math.Max(aMin, bMin);
                return overlap >= minOverlap;
            }

            bool IsSeamLayer(string layer)
            {
                if (string.IsNullOrWhiteSpace(layer))
                {
                    return false;
                }

                return string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-3018", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-SEC-0", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase);
            }

            var generatedSet = new HashSet<ObjectId>(generatedRoadAllowanceIds.Where(id => !id.IsNull));
            using (var tr = database.TransactionManager.StartTransaction())
            {
                var generatedHorizontals = new List<(ObjectId Id, Point2d A, Point2d B, double Y, double Length)>();
                var secCandidates = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, double Y, double Length)>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    var layerName = ent.Layer ?? string.Empty;
                    if (!IsSeamLayer(layerName))
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b))
                    {
                        continue;
                    }

                    if (!DoesSegmentIntersectAnyWindow(a, b) || !IsHorizontalLike(a, b))
                    {
                        continue;
                    }

                    var len = a.GetDistanceTo(b);
                    if (len < 8.0)
                    {
                        continue;
                    }

                    var y = 0.5 * (a.Y + b.Y);
                    if (generatedSet.Contains(id))
                    {
                        generatedHorizontals.Add((id, a, b, y, len));
                    }
                    else
                    {
                        secCandidates.Add((id, ent.Layer ?? string.Empty, a, b, y, len));
                    }
                }

                if (generatedHorizontals.Count == 0 || secCandidates.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                const double usecBandTol = 34.5;
                const double secTargetMin = 15.5;
                const double secTargetMax = 24.5;
                const double usecCompanionMin = 7.0;
                const double usecCompanionMax = 13.8;
                const double candidateMinLen = 10.0;
                const double candidateMaxLen = 140.0;
                const double neighborAxisTol = 0.80;
                const double neighborOverlapMin = 8.0;

                (bool SecLeft, bool SecRight, bool UsecLeft, bool UsecRight) AnalyzeNeighbors(int index)
                {
                    var s = secCandidates[index];
                    var sCenterX = 0.5 * (s.A.X + s.B.X);
                    var secLeft = false;
                    var secRight = false;
                    var usecLeft = false;
                    var usecRight = false;
                    for (var j = 0; j < secCandidates.Count; j++)
                    {
                        if (index == j)
                        {
                            continue;
                        }

                        var o = secCandidates[j];
                        if (Math.Abs(s.Y - o.Y) > neighborAxisTol)
                        {
                            continue;
                        }

                        if (!HorizontalOverlaps((s.A, s.B), (o.A, o.B), minOverlap: neighborOverlapMin))
                        {
                            continue;
                        }

                        var oCenterX = 0.5 * (o.A.X + o.B.X);
                        var left = oCenterX < (sCenterX - 0.25);
                        var right = oCenterX > (sCenterX + 0.25);
                        if (!left && !right)
                        {
                            left = true;
                            right = true;
                        }

                        if (string.Equals(o.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase))
                        {
                            if (left)
                            {
                                secLeft = true;
                            }

                            if (right)
                            {
                                secRight = true;
                            }
                        }
                        else if (string.Equals(o.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase))
                        {
                            if (left)
                            {
                                usecLeft = true;
                            }

                            if (right)
                            {
                                usecRight = true;
                            }
                        }
                    }

                    return (secLeft, secRight, usecLeft, usecRight);
                }

                bool HasThirtyEighteenCompanion(int generatedIndex, int candidateIndex)
                {
                    var g = generatedHorizontals[generatedIndex];
                    var s = secCandidates[candidateIndex];
                    for (var j = 0; j < secCandidates.Count; j++)
                    {
                        if (j == candidateIndex)
                        {
                            continue;
                        }

                        var o = secCandidates[j];
                        if (o.Length < candidateMinLen || o.Length > candidateMaxLen)
                        {
                            continue;
                        }

                        if (!HorizontalOverlaps((o.A, o.B), (g.A, g.B), minOverlap: 20.0))
                        {
                            continue;
                        }

                        if (!HorizontalOverlaps((o.A, o.B), (s.A, s.B), minOverlap: neighborOverlapMin))
                        {
                            continue;
                        }

                        var dy = Math.Abs(o.Y - g.Y);
                        if (dy >= usecCompanionMin && dy <= usecCompanionMax)
                        {
                            return true;
                        }
                    }

                    return false;
                }

                var normalized = 0;
                var skippedThirtyEighteen = 0;
                for (var i = 0; i < secCandidates.Count; i++)
                {
                    var s = secCandidates[i];
                    if (!string.Equals(s.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (s.Length < candidateMinLen || s.Length > candidateMaxLen)
                    {
                        continue;
                    }

                    var bestDy = double.MaxValue;
                    var bestGeneratedIndex = -1;
                    for (var gi = 0; gi < generatedHorizontals.Count; gi++)
                    {
                        var g = generatedHorizontals[gi];
                        var dy = Math.Abs(s.Y - g.Y);
                        if (dy < 1.0 || dy > usecBandTol)
                        {
                            continue;
                        }

                        if (!HorizontalOverlaps((s.A, s.B), (g.A, g.B), minOverlap: 20.0))
                        {
                            continue;
                        }

                        if (dy < bestDy)
                        {
                            bestDy = dy;
                            bestGeneratedIndex = gi;
                        }
                    }

                    if (bestDy == double.MaxValue)
                    {
                        continue;
                    }

                    if (bestDy < secTargetMin || bestDy > secTargetMax)
                    {
                        continue;
                    }

                    // Guard: when a 10.06 companion exists for the same generated corridor, this is a 30.16
                    // RA boundary and should remain L-USEC (do not misclassify as 20.11 L-SEC).
                    if (bestGeneratedIndex >= 0 && HasThirtyEighteenCompanion(bestGeneratedIndex, i))
                    {
                        skippedThirtyEighteen++;
                        continue;
                    }

                    var neighbors = AnalyzeNeighbors(i);
                    var hasSecSupport =
                        (neighbors.SecLeft && neighbors.SecRight) ||
                        ((neighbors.SecLeft || neighbors.SecRight) && !(neighbors.UsecLeft || neighbors.UsecRight));
                    if (!hasSecSupport)
                    {
                        continue;
                    }

                    Entity? writable = null;
                    try
                    {
                        writable = tr.GetObject(s.Id, OpenMode.ForWrite, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (writable == null || writable.IsErased ||
                        string.Equals(writable.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    writable.Layer = "L-SEC";
                    writable.ColorIndex = 256;
                    normalized++;
                }

                tr.Commit();
                if (normalized > 0)
                {
                    logger?.WriteLine($"Cleanup: normalized {normalized} horizontal 20.11 road allowance segment(s) to L-SEC.");
                }
                if (skippedThirtyEighteen > 0)
                {
                    logger?.WriteLine($"Cleanup: skipped {skippedThirtyEighteen} horizontal candidate(s) that matched 30.16 corridor companion pattern.");
                }
            }
        }

        private static void NormalizeBottomTownshipBoundaryLayers(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            IReadOnlyCollection<ObjectId> generatedRoadAllowanceIds,
            IEnumerable<QuarterLabelInfo> sectionInfos,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null || generatedRoadAllowanceIds == null || generatedRoadAllowanceIds.Count == 0)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 102.0);
            if (clipWindows.Count == 0)
            {
                return;
            }

            bool IsPointInAnyWindow(Point2d p)
            {
                for (var i = 0; i < clipWindows.Count; i++)
                {
                    var w = clipWindows[i];
                    if (p.X >= w.MinPoint.X && p.X <= w.MaxPoint.X &&
                        p.Y >= w.MinPoint.Y && p.Y <= w.MaxPoint.Y)
                    {
                        return true;
                    }
                }

                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b)
            {
                if (IsPointInAnyWindow(a) || IsPointInAnyWindow(b))
                {
                    return true;
                }

                for (var i = 0; i < clipWindows.Count; i++)
                {
                    if (TryClipSegmentToWindow(a, b, clipWindows[i], out _, out _))
                    {
                        return true;
                    }
                }

                return false;
            }

            bool TryReadOpenSegment(Entity ent, out Point2d a, out Point2d b)
            {
                a = default;
                b = default;
                if (ent == null)
                {
                    return false;
                }

                if (ent is Line ln)
                {
                    a = new Point2d(ln.StartPoint.X, ln.StartPoint.Y);
                    b = new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                    return a.GetDistanceTo(b) > 1e-4;
                }

                if (ent is Polyline pl)
                {
                    if (pl.Closed || pl.NumberOfVertices < 2)
                    {
                        return false;
                    }

                    a = pl.GetPoint2dAt(0);
                    b = pl.GetPoint2dAt(pl.NumberOfVertices - 1);
                    return a.GetDistanceTo(b) > 1e-4;
                }

                return false;
            }

            bool IsHorizontalLike(Point2d a, Point2d b)
            {
                var d = b - a;
                return Math.Abs(d.X) >= Math.Abs(d.Y);
            }

            bool HorizontalOverlaps((Point2d A, Point2d B) a, (Point2d A, Point2d B) b, double minOverlap)
            {
                var aMin = Math.Min(a.A.X, a.B.X);
                var aMax = Math.Max(a.A.X, a.B.X);
                var bMin = Math.Min(b.A.X, b.B.X);
                var bMax = Math.Max(b.A.X, b.B.X);
                var overlap = Math.Min(aMax, bMax) - Math.Max(aMin, bMin);
                return overlap >= minOverlap;
            }

            bool IsSeamLayer(string layer)
            {
                if (string.IsNullOrWhiteSpace(layer))
                {
                    return false;
                }

                return string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecBase, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecZero, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, LayerUsecThirty, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-2012", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-USEC-3018", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-SEC-0", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(layer, "L-SEC-2012", StringComparison.OrdinalIgnoreCase);
            }

            var generatedSet = new HashSet<ObjectId>(generatedRoadAllowanceIds.Where(id => !id.IsNull));
            using (var tr = database.TransactionManager.StartTransaction())
            {
                var generatedBottomY = double.MaxValue;
                var generatedHorizontals = new List<(Point2d A, Point2d B, double Y)>();
                var candidates = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, double Y, double Length)>();

                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    var layerName = ent.Layer ?? string.Empty;
                    if (!IsSeamLayer(layerName))
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b) ||
                        !DoesSegmentIntersectAnyWindow(a, b) ||
                        !IsHorizontalLike(a, b))
                    {
                        continue;
                    }

                    var len = a.GetDistanceTo(b);
                    if (len < 4.0)
                    {
                        continue;
                    }

                    var y = 0.5 * (a.Y + b.Y);
                    if (generatedSet.Contains(id))
                    {
                        generatedHorizontals.Add((a, b, y));
                        if (y < generatedBottomY)
                        {
                            generatedBottomY = y;
                        }
                    }

                    candidates.Add((id, layerName, a, b, y, len));
                }

                var baselineHints = new List<(double Y, double MinX, double MaxX)>();
                var seenBaselineHintKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (sectionInfos != null)
                {
                    foreach (var info in sectionInfos)
                    {
                        if (info == null || info.SectionPolylineId.IsNull)
                        {
                            continue;
                        }

                        if (!TryParsePositiveToken(info.SectionKey.Township, out var township))
                        {
                            continue;
                        }

                        var includeTop = (township % 4) == 0;
                        var includeBottom = (township % 4) == 1;
                        if (!includeTop && !includeBottom)
                        {
                            continue;
                        }

                        if (!(tr.GetObject(info.SectionPolylineId, OpenMode.ForRead, false) is Polyline section) || section.IsErased)
                        {
                            continue;
                        }

                        QuarterAnchors anchors;
                        if (!TryGetQuarterAnchors(section, out anchors))
                        {
                            anchors = GetFallbackAnchors(section);
                        }

                        Extents3d ext;
                        try
                        {
                            ext = section.GeometricExtents;
                        }
                        catch
                        {
                            continue;
                        }

                        var xMin = ext.MinPoint.X;
                        var xMax = ext.MaxPoint.X;
                        if ((xMax - xMin) < 4.0)
                        {
                            continue;
                        }

                        if (includeTop)
                        {
                            var y = anchors.Top.Y;
                            var key = string.Format(
                                CultureInfo.InvariantCulture,
                                "T|{0:0.###}|{1:0.###}|{2:0.###}",
                                y,
                                xMin,
                                xMax);
                            if (seenBaselineHintKeys.Add(key))
                            {
                                baselineHints.Add((y, xMin, xMax));
                            }
                        }

                        if (includeBottom)
                        {
                            var y = anchors.Bottom.Y;
                            var key = string.Format(
                                CultureInfo.InvariantCulture,
                                "B|{0:0.###}|{1:0.###}|{2:0.###}",
                                y,
                                xMin,
                                xMax);
                            if (seenBaselineHintKeys.Add(key))
                            {
                                baselineHints.Add((y, xMin, xMax));
                            }
                        }
                    }
                }

                if (candidates.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                var candidateBottomY = candidates.Min(c => c.Y);
                var seamBaseY = generatedBottomY == double.MaxValue
                    ? candidateBottomY
                    : Math.Min(candidateBottomY, generatedBottomY);

                // Only fix around the bottom township seam where layering drifts.
                const double seamBandBelow = 20.0;
                const double seamBandAbove = 80.0;
                const double secTargetMin = 15.5;
                const double secTargetMax = 24.5;
                const double usecTargetMin = 25.5;
                const double usecTargetMax = 34.5;
                const double dyOverlapMin = 5.0;
                const double expectedSecDy = 20.11;
                const double expectedUsecDy = RoadAllowanceUsecWidthMeters;
                const double expectedDyTol = 6.0;
                const double expectedDyDecisionMargin = 0.45;
                const double baselineBandTol = 36.0;
                const double baselineMinOverlap = 20.0;
                const double baselineYForceTol = 1.60;

                double NormalizeBaselineY(double y)
                {
                    return Math.Round(y, 2);
                }

                var forcedBaselineYs = new HashSet<double>();
                if (baselineHints.Count > 0)
                {
                    for (var bi = 0; bi < baselineHints.Count; bi++)
                    {
                        var hint = baselineHints[bi];
                        var nearestDy = double.MaxValue;
                        var nearestY = double.NaN;
                        for (var ci = 0; ci < candidates.Count; ci++)
                        {
                            var c = candidates[ci];
                            var dy = Math.Abs(c.Y - hint.Y);
                            if (dy > baselineBandTol || dy >= nearestDy)
                            {
                                continue;
                            }

                            nearestDy = dy;
                            nearestY = c.Y;
                        }

                        if (nearestDy == double.MaxValue || double.IsNaN(nearestY))
                        {
                            continue;
                        }

                        forcedBaselineYs.Add(NormalizeBaselineY(nearestY));
                    }
                }

                double ComputeBestExpectedScore(int index, double expectedDy)
                {
                    var s = candidates[index];
                    var bestScore = double.MaxValue;
                    for (var oi = 0; oi < candidates.Count; oi++)
                    {
                        if (oi == index)
                        {
                            continue;
                        }

                        var o = candidates[oi];
                        if (o.Y < (seamBaseY - seamBandBelow) ||
                            o.Y > (seamBaseY + seamBandAbove))
                        {
                            continue;
                        }

                        if (!HorizontalOverlaps((s.A, s.B), (o.A, o.B), minOverlap: dyOverlapMin))
                        {
                            continue;
                        }

                        var dy = Math.Abs(s.Y - o.Y);
                        if (dy < 1.0)
                        {
                            continue;
                        }

                        var score = Math.Abs(dy - expectedDy);
                        if (score < bestScore)
                        {
                            bestScore = score;
                        }
                    }

                    return bestScore;
                }

                bool IsBaselineBoundaryCandidate(int index)
                {
                    if (forcedBaselineYs.Count == 0)
                    {
                        return false;
                    }

                    var s = candidates[index];
                    var normalizedY = NormalizeBaselineY(s.Y);
                    if (forcedBaselineYs.Contains(normalizedY))
                    {
                        return true;
                    }

                    // Keep a small tolerance around matched baseline Y values to absorb tiny
                    // endpoint numeric drift between neighboring segments.
                    foreach (var by in forcedBaselineYs)
                    {
                        if (Math.Abs(s.Y - by) <= baselineYForceTol)
                        {
                            return true;
                        }
                    }

                    var sMinX = Math.Min(s.A.X, s.B.X);
                    var sMaxX = Math.Max(s.A.X, s.B.X);
                    for (var bi = 0; bi < baselineHints.Count; bi++)
                    {
                        var hint = baselineHints[bi];
                        if (Math.Abs(s.Y - hint.Y) > baselineBandTol)
                        {
                            continue;
                        }

                        var overlap = Math.Min(sMaxX, hint.MaxX) - Math.Max(sMinX, hint.MinX);
                        if (overlap < baselineMinOverlap)
                        {
                            continue;
                        }

                        return true;
                    }

                    return false;
                }

                var normalized = 0;
                var baselineForcedToSec = 0;
                for (var i = 0; i < candidates.Count; i++)
                {
                    var c = candidates[i];
                    if (c.Y < (seamBaseY - seamBandBelow) ||
                        c.Y > (seamBaseY + seamBandAbove))
                    {
                        continue;
                    }

                    var canonicalSecScore = ComputeBestExpectedScore(i, expectedSecDy);
                    var canonicalUsecScore = ComputeBestExpectedScore(i, expectedUsecDy);
                    var canonicalDecision = false;
                    var bestDy = double.MaxValue;
                    string? targetLayer = null;
                    if (IsBaselineBoundaryCandidate(i))
                    {
                        targetLayer = "L-SEC";
                        canonicalDecision = true;
                        baselineForcedToSec++;
                    }
                    else if (canonicalSecScore <= expectedDyTol &&
                             (canonicalSecScore + expectedDyDecisionMargin) < canonicalUsecScore)
                    {
                        targetLayer = "L-SEC";
                        canonicalDecision = true;
                    }
                    else if (canonicalUsecScore <= expectedDyTol &&
                             (canonicalUsecScore + expectedDyDecisionMargin) < canonicalSecScore)
                    {
                        targetLayer = "L-USEC";
                        canonicalDecision = true;
                    }

                    if (!canonicalDecision)
                    {
                        for (var gi = 0; gi < generatedHorizontals.Count; gi++)
                        {
                            var g = generatedHorizontals[gi];
                            if (!HorizontalOverlaps((c.A, c.B), (g.A, g.B), minOverlap: dyOverlapMin))
                            {
                                continue;
                            }

                            var dy = Math.Abs(c.Y - g.Y);
                            if (dy < 1.0)
                            {
                                continue;
                            }

                            if (dy < bestDy)
                            {
                                bestDy = dy;
                            }
                        }

                        if (bestDy == double.MaxValue)
                        {
                            for (var oi = 0; oi < candidates.Count; oi++)
                            {
                                if (oi == i)
                                {
                                    continue;
                                }

                                var o = candidates[oi];
                                if (o.Y < (seamBaseY - seamBandBelow) ||
                                    o.Y > (seamBaseY + seamBandAbove))
                                {
                                    continue;
                                }

                                if (!HorizontalOverlaps((c.A, c.B), (o.A, o.B), minOverlap: dyOverlapMin))
                                {
                                    continue;
                                }

                                var dy = Math.Abs(c.Y - o.Y);
                                if (dy < 1.0)
                                {
                                    continue;
                                }

                                if (dy < bestDy)
                                {
                                    bestDy = dy;
                                }
                            }
                        }

                        if (bestDy < usecTargetMin || bestDy > usecTargetMax)
                        {
                            if (bestDy < secTargetMin || bestDy > secTargetMax)
                            {
                                continue;
                            }
                        }

                        targetLayer = bestDy >= usecTargetMin && bestDy <= usecTargetMax
                            ? "L-USEC"
                            : "L-SEC";
                    }

                    if (targetLayer == null)
                    {
                        continue;
                    }

                    if (string.Equals(c.Layer, targetLayer, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Entity? writable = null;
                    try
                    {
                        writable = tr.GetObject(c.Id, OpenMode.ForWrite, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (writable == null || writable.IsErased ||
                        string.Equals(writable.Layer, targetLayer, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    writable.Layer = targetLayer;
                    writable.ColorIndex = 256;
                    normalized++;
                }

                tr.Commit();
                if (normalized > 0)
                {
                    logger?.WriteLine($"Cleanup: normalized {normalized} bottom-township seam segment(s) to expected L-SEC/L-USEC layer.");
                }
                if (baselineForcedToSec > 0)
                {
                    logger?.WriteLine($"Cleanup: baseline-township rule forced {baselineForcedToSec} bottom-township seam segment(s) to L-SEC.");
                }
            }
        }

        private static void NormalizeRangeEdgeHorizontalRoadAllowanceLayers(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            IReadOnlyCollection<ObjectId> generatedRoadAllowanceIds,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null || generatedRoadAllowanceIds == null || generatedRoadAllowanceIds.Count == 0)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 102.0);
            if (clipWindows.Count == 0)
            {
                return;
            }

            bool IsPointInAnyWindow(Point2d p)
            {
                for (var i = 0; i < clipWindows.Count; i++)
                {
                    var w = clipWindows[i];
                    if (p.X >= w.MinPoint.X && p.X <= w.MaxPoint.X &&
                        p.Y >= w.MinPoint.Y && p.Y <= w.MaxPoint.Y)
                    {
                        return true;
                    }
                }

                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b)
            {
                if (IsPointInAnyWindow(a) || IsPointInAnyWindow(b))
                {
                    return true;
                }

                for (var i = 0; i < clipWindows.Count; i++)
                {
                    if (TryClipSegmentToWindow(a, b, clipWindows[i], out _, out _))
                    {
                        return true;
                    }
                }

                return false;
            }

            bool TryReadOpenSegment(Entity ent, out Point2d a, out Point2d b)
            {
                a = default;
                b = default;
                if (ent == null)
                {
                    return false;
                }

                if (ent is Line ln)
                {
                    a = new Point2d(ln.StartPoint.X, ln.StartPoint.Y);
                    b = new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                    return a.GetDistanceTo(b) > 1e-4;
                }

                if (ent is Polyline pl)
                {
                    if (pl.Closed || pl.NumberOfVertices < 2)
                    {
                        return false;
                    }

                    a = pl.GetPoint2dAt(0);
                    b = pl.GetPoint2dAt(pl.NumberOfVertices - 1);
                    if (a.GetDistanceTo(b) <= 1e-4)
                    {
                        return false;
                    }

                    if (pl.NumberOfVertices > 2)
                    {
                        const double collinearTol = 0.35;
                        for (var vi = 1; vi < pl.NumberOfVertices - 1; vi++)
                        {
                            var p = pl.GetPoint2dAt(vi);
                            if (DistancePointToInfiniteLine(p, a, b) > collinearTol)
                            {
                                return false;
                            }
                        }
                    }

                    return true;
                }

                return false;
            }

            bool IsHorizontalLike(Point2d a, Point2d b)
            {
                var d = b - a;
                return Math.Abs(d.X) >= Math.Abs(d.Y);
            }

            bool HorizontalOverlaps((Point2d A, Point2d B) a, (Point2d A, Point2d B) b, double minOverlap)
            {
                var aMin = Math.Min(a.A.X, a.B.X);
                var aMax = Math.Max(a.A.X, a.B.X);
                var bMin = Math.Min(b.A.X, b.B.X);
                var bMax = Math.Max(b.A.X, b.B.X);
                var overlap = Math.Min(aMax, bMax) - Math.Max(aMin, bMin);
                return overlap >= minOverlap;
            }

            var clipMinX = clipWindows.Min(w => w.MinPoint.X);
            var clipMaxX = clipWindows.Max(w => w.MaxPoint.X);
            const double edgeBand = 145.0;
            const double minSegmentLength = 4.0;
            const double overlapMin = 8.0;
            const double usecNearMin = 7.0;    // 10.06 +- tolerance
            const double usecNearMax = 13.8;
            const double usecFarMin = 16.6;    // 20.11 +- tolerance
            const double usecFarMax = 23.8;

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var generatedSet = new HashSet<ObjectId>(generatedRoadAllowanceIds.Where(id => !id.IsNull));
                var candidates = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, double Y, bool Generated)>();
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    var isUsec = string.Equals(ent.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase);
                    var isSec = string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase);
                    if (!isUsec && !isSec)
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b) ||
                        !DoesSegmentIntersectAnyWindow(a, b) ||
                        !IsHorizontalLike(a, b))
                    {
                        continue;
                    }

                    if (a.GetDistanceTo(b) < minSegmentLength)
                    {
                        continue;
                    }

                    var y = 0.5 * (a.Y + b.Y);
                    var generated = generatedSet.Contains(id);
                    candidates.Add((id, ent.Layer ?? string.Empty, a, b, y, generated));
                }

                if (candidates.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                bool IsRangeEdgeCandidate((ObjectId Id, string Layer, Point2d A, Point2d B, double Y, bool Generated) c)
                {
                    var segMinX = Math.Min(c.A.X, c.B.X);
                    var segMaxX = Math.Max(c.A.X, c.B.X);
                    var touchesWestEdge = segMinX <= (clipMinX + edgeBand);
                    var touchesEastEdge = segMaxX >= (clipMaxX - edgeBand);
                    return touchesWestEdge || touchesEastEdge;
                }

                var targets = new Dictionary<ObjectId, string>();
                var inspectedGenerated = 0;
                for (var i = 0; i < candidates.Count; i++)
                {
                    var g = candidates[i];
                    if (!g.Generated || !IsRangeEdgeCandidate(g))
                    {
                        continue;
                    }

                    inspectedGenerated++;

                    // Generated RA offsets represent the 20.11 interior corridor line.
                    if (!string.Equals(g.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase))
                    {
                        targets[g.Id] = "L-SEC";
                    }

                    for (var j = 0; j < candidates.Count; j++)
                    {
                        if (i == j)
                        {
                            continue;
                        }

                        var c = candidates[j];
                        if (c.Generated || !IsRangeEdgeCandidate(c))
                        {
                            continue;
                        }

                        if (!HorizontalOverlaps((g.A, g.B), (c.A, c.B), minOverlap: overlapMin))
                        {
                            continue;
                        }

                        var dy = Math.Abs(c.Y - g.Y);
                        var shouldUsec =
                            (dy >= usecNearMin && dy <= usecNearMax) ||
                            (dy >= usecFarMin && dy <= usecFarMax);
                        if (!shouldUsec)
                        {
                            continue;
                        }

                        if (!string.Equals(c.Layer, "L-USEC", StringComparison.OrdinalIgnoreCase))
                        {
                            targets[c.Id] = "L-USEC";
                        }
                    }
                }

                if (targets.Count == 0)
                {
                    tr.Commit();
                    return;
                }

                var normalizedGenerated = 0;
                var normalizedBoundary = 0;
                foreach (var kvp in targets)
                {
                    Entity? writable = null;
                    try
                    {
                        writable = tr.GetObject(kvp.Key, OpenMode.ForWrite, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (writable == null || writable.IsErased ||
                        string.Equals(writable.Layer, kvp.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    writable.Layer = kvp.Value;
                    writable.ColorIndex = 256;
                    if (string.Equals(kvp.Value, "L-SEC", StringComparison.OrdinalIgnoreCase))
                    {
                        normalizedGenerated++;
                    }
                    else
                    {
                        normalizedBoundary++;
                    }
                }

                tr.Commit();
                if (normalizedGenerated > 0 || normalizedBoundary > 0)
                {
                    logger?.WriteLine(
                        $"Cleanup: normalized {normalizedGenerated} range-edge generated 20.11 segment(s) to L-SEC and {normalizedBoundary} adjoining range-edge segment(s) to L-USEC (inspected generated={inspectedGenerated}).");
                }
            }
        }

        private static List<(bool Horizontal, double Axis, double SpanMin, double SpanMax)> SnapshotOriginalRangeEdgeSecRoadAllowanceAnchors(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            IReadOnlyCollection<ObjectId> generatedRoadAllowanceIds,
            Logger? logger)
        {
            var anchors = new List<(bool Horizontal, double Axis, double SpanMin, double SpanMax)>();
            if (database == null || requestedQuarterIds == null)
            {
                return anchors;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 102.0);
            if (clipWindows.Count == 0)
            {
                return anchors;
            }

            var clipMinX = clipWindows.Min(w => w.MinPoint.X);
            var clipMaxX = clipWindows.Max(w => w.MaxPoint.X);
            const double edgeBand = 145.0;
            const double minSegmentLength = 4.0;
            var generatedSet = generatedRoadAllowanceIds == null
                ? new HashSet<ObjectId>()
                : new HashSet<ObjectId>(generatedRoadAllowanceIds.Where(id => !id.IsNull));

            bool IsPointInAnyWindow(Point2d p)
            {
                for (var i = 0; i < clipWindows.Count; i++)
                {
                    var w = clipWindows[i];
                    if (p.X >= w.MinPoint.X && p.X <= w.MaxPoint.X &&
                        p.Y >= w.MinPoint.Y && p.Y <= w.MaxPoint.Y)
                    {
                        return true;
                    }
                }

                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b)
            {
                if (IsPointInAnyWindow(a) || IsPointInAnyWindow(b))
                {
                    return true;
                }

                for (var i = 0; i < clipWindows.Count; i++)
                {
                    if (TryClipSegmentToWindow(a, b, clipWindows[i], out _, out _))
                    {
                        return true;
                    }
                }

                return false;
            }

            bool TryReadOpenSegment(Entity ent, out Point2d a, out Point2d b)
            {
                a = default;
                b = default;
                if (ent == null)
                {
                    return false;
                }

                if (ent is Line ln)
                {
                    a = new Point2d(ln.StartPoint.X, ln.StartPoint.Y);
                    b = new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                    return a.GetDistanceTo(b) > 1e-4;
                }

                if (ent is Polyline pl)
                {
                    if (pl.Closed || pl.NumberOfVertices < 2)
                    {
                        return false;
                    }

                    a = pl.GetPoint2dAt(0);
                    b = pl.GetPoint2dAt(pl.NumberOfVertices - 1);
                    return a.GetDistanceTo(b) > 1e-4;
                }

                return false;
            }

            bool IsHorizontalLike(Point2d a, Point2d b)
            {
                var d = b - a;
                return Math.Abs(d.X) >= Math.Abs(d.Y);
            }

            bool IsVerticalLike(Point2d a, Point2d b)
            {
                var d = b - a;
                return Math.Abs(d.Y) > Math.Abs(d.X);
            }

            bool IsRangeEdgeCandidate(Point2d a, Point2d b)
            {
                var segMinX = Math.Min(a.X, b.X);
                var segMaxX = Math.Max(a.X, b.X);
                return segMinX <= (clipMinX + edgeBand) ||
                       segMaxX >= (clipMaxX - edgeBand);
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    var isGenerated = generatedSet.Contains(id);

                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased ||
                        !string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b) ||
                        !DoesSegmentIntersectAnyWindow(a, b) ||
                        !IsRangeEdgeCandidate(a, b))
                    {
                        continue;
                    }

                    if (a.GetDistanceTo(b) < minSegmentLength)
                    {
                        continue;
                    }

                    var horizontal = IsHorizontalLike(a, b);
                    var vertical = IsVerticalLike(a, b);
                    if (!horizontal && !vertical)
                    {
                        continue;
                    }

                    var axis = horizontal
                        ? 0.5 * (a.Y + b.Y)
                        : 0.5 * (a.X + b.X);
                    var spanMin = horizontal
                        ? Math.Min(a.X, b.X)
                        : Math.Min(a.Y, b.Y);
                    var spanMax = horizontal
                        ? Math.Max(a.X, b.X)
                        : Math.Max(a.Y, b.Y);
                    anchors.Add((horizontal, axis, spanMin, spanMax));
                }

                tr.Commit();
            }

            logger?.WriteLine(
                $"Cleanup: snapshot captured {anchors.Count} original range-edge L-SEC anchor(s) for final relayer protection.");
            return anchors;
        }

        private static void ReapplyOriginalRangeEdgeSecRoadAllowanceLayers(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            IReadOnlyCollection<ObjectId> generatedRoadAllowanceIds,
            IReadOnlyCollection<(bool Horizontal, double Axis, double SpanMin, double SpanMax)> originalRangeEdgeSecAnchors,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 102.0);
            if (clipWindows.Count == 0)
            {
                return;
            }

            const double minSegmentLength = 4.0;
            const double axisTolerance = 1.25;
            const double minSpanOverlap = 0.20;
            const double maxSpanGap = 0.80;
            const double companionAxisTol = 1.20;
            const double companionMinOverlap = 220.0;
            const double edgeBand = 145.0;
            var generatedSet = generatedRoadAllowanceIds == null
                ? new HashSet<ObjectId>()
                : new HashSet<ObjectId>(generatedRoadAllowanceIds.Where(id => !id.IsNull));
            var secAnchorSet = originalRangeEdgeSecAnchors ?? Array.Empty<(bool Horizontal, double Axis, double SpanMin, double SpanMax)>();
            var clipMinX = clipWindows.Min(w => w.MinPoint.X);
            var clipMaxX = clipWindows.Max(w => w.MaxPoint.X);

            bool IsPointInAnyWindow(Point2d p)
            {
                for (var i = 0; i < clipWindows.Count; i++)
                {
                    var w = clipWindows[i];
                    if (p.X >= w.MinPoint.X && p.X <= w.MaxPoint.X &&
                        p.Y >= w.MinPoint.Y && p.Y <= w.MaxPoint.Y)
                    {
                        return true;
                    }
                }

                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b)
            {
                if (IsPointInAnyWindow(a) || IsPointInAnyWindow(b))
                {
                    return true;
                }

                for (var i = 0; i < clipWindows.Count; i++)
                {
                    if (TryClipSegmentToWindow(a, b, clipWindows[i], out _, out _))
                    {
                        return true;
                    }
                }

                return false;
            }

            static bool HasSpanOverlap(double aMin, double aMax, double bMin, double bMax, double minOverlap)
            {
                var overlap = Math.Min(aMax, bMax) - Math.Max(aMin, bMin);
                return overlap >= minOverlap;
            }

            static double IntervalGap(double aMin, double aMax, double bMin, double bMax)
            {
                if (aMax < bMin)
                {
                    return bMin - aMax;
                }

                if (bMax < aMin)
                {
                    return aMin - bMax;
                }

                return 0.0;
            }

            bool TryReadOpenSegment(Entity ent, out Point2d a, out Point2d b)
            {
                a = default;
                b = default;
                if (ent == null)
                {
                    return false;
                }

                if (ent is Line ln)
                {
                    a = new Point2d(ln.StartPoint.X, ln.StartPoint.Y);
                    b = new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                    return a.GetDistanceTo(b) > 1e-4;
                }

                if (ent is Polyline pl)
                {
                    if (pl.Closed || pl.NumberOfVertices < 2)
                    {
                        return false;
                    }

                    a = pl.GetPoint2dAt(0);
                    b = pl.GetPoint2dAt(pl.NumberOfVertices - 1);
                    return a.GetDistanceTo(b) > 1e-4;
                }

                return false;
            }

            bool IsHorizontalLike(Point2d a, Point2d b)
            {
                var d = b - a;
                return Math.Abs(d.X) >= Math.Abs(d.Y);
            }

            bool IsVerticalLike(Point2d a, Point2d b)
            {
                var d = b - a;
                return Math.Abs(d.Y) > Math.Abs(d.X);
            }

            bool IsRangeEdgeCandidate(Point2d a, Point2d b)
            {
                var segMinX = Math.Min(a.X, b.X);
                var segMaxX = Math.Max(a.X, b.X);
                return segMinX <= (clipMinX + edgeBand) ||
                       segMaxX >= (clipMaxX - edgeBand);
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var adjusted = 0;
                var adjustedByAnchor = 0;
                var adjustedByCompanion = 0;
                var adjustedByRangeEdgeTwenty = 0;
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                var secCompanionAnchors = new List<(bool Horizontal, double Axis, double SpanMin, double SpanMax)>();
                foreach (ObjectId id in ms)
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased ||
                        !string.Equals(ent.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b) ||
                        !DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    if (a.GetDistanceTo(b) < minSegmentLength)
                    {
                        continue;
                    }

                    var horizontal = IsHorizontalLike(a, b);
                    var vertical = IsVerticalLike(a, b);
                    if (!horizontal && !vertical)
                    {
                        continue;
                    }

                    var axis = horizontal
                        ? 0.5 * (a.Y + b.Y)
                        : 0.5 * (a.X + b.X);
                    var spanMin = horizontal
                        ? Math.Min(a.X, b.X)
                        : Math.Min(a.Y, b.Y);
                    var spanMax = horizontal
                        ? Math.Max(a.X, b.X)
                        : Math.Max(a.Y, b.Y);
                    secCompanionAnchors.Add((horizontal, axis, spanMin, spanMax));
                }

                foreach (ObjectId id in ms)
                {
                    var isGenerated = generatedSet.Contains(id);

                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
                    if (!IsUsecLayer(layer) &&
                        !string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b) ||
                        !DoesSegmentIntersectAnyWindow(a, b))
                    {
                        continue;
                    }

                    if (a.GetDistanceTo(b) < minSegmentLength)
                    {
                        continue;
                    }

                    var horizontal = IsHorizontalLike(a, b);
                    var vertical = IsVerticalLike(a, b);
                    if (!horizontal && !vertical)
                    {
                        continue;
                    }

                    var axis = horizontal
                        ? 0.5 * (a.Y + b.Y)
                        : 0.5 * (a.X + b.X);
                    var spanMin = horizontal
                        ? Math.Min(a.X, b.X)
                        : Math.Min(a.Y, b.Y);
                    var spanMax = horizontal
                        ? Math.Max(a.X, b.X)
                        : Math.Max(a.Y, b.Y);
                    var promoteRangeEdgeTwentyToSec =
                        IsRangeEdgeCandidate(a, b) &&
                        string.Equals(layer, LayerUsecTwenty, StringComparison.OrdinalIgnoreCase);
                    if (isGenerated && !promoteRangeEdgeTwentyToSec)
                    {
                        continue;
                    }

                    var matchesAnchor = false;
                    foreach (var anchor in secAnchorSet)
                    {
                        if (anchor.Horizontal != horizontal)
                        {
                            continue;
                        }

                        if (Math.Abs(anchor.Axis - axis) > axisTolerance)
                        {
                            continue;
                        }

                        if (!HasSpanOverlap(anchor.SpanMin, anchor.SpanMax, spanMin, spanMax, minSpanOverlap) &&
                            IntervalGap(anchor.SpanMin, anchor.SpanMax, spanMin, spanMax) > maxSpanGap)
                        {
                            continue;
                        }

                        matchesAnchor = true;
                        break;
                    }

                    var hasSecCompanion = false;
                    if (!matchesAnchor)
                    {
                        for (var i = 0; i < secCompanionAnchors.Count; i++)
                        {
                            var companion = secCompanionAnchors[i];
                            if (companion.Horizontal != horizontal)
                            {
                                continue;
                            }

                            var axisDelta = Math.Abs(companion.Axis - axis);
                            if (Math.Abs(axisDelta - RoadAllowanceSecWidthMeters) > companionAxisTol)
                            {
                                continue;
                            }

                            if (!HasSpanOverlap(companion.SpanMin, companion.SpanMax, spanMin, spanMax, companionMinOverlap))
                            {
                                continue;
                            }

                            hasSecCompanion = true;
                            break;
                        }
                    }

                    if ((!matchesAnchor && !hasSecCompanion && !promoteRangeEdgeTwentyToSec) ||
                        string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Entity? writable = null;
                    try
                    {
                        writable = tr.GetObject(id, OpenMode.ForWrite, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (writable == null || writable.IsErased ||
                        string.Equals(writable.Layer, "L-SEC", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    writable.Layer = "L-SEC";
                    writable.ColorIndex = 256;
                    adjusted++;
                    if (matchesAnchor)
                    {
                        adjustedByAnchor++;
                    }
                    else if (promoteRangeEdgeTwentyToSec)
                    {
                        adjustedByRangeEdgeTwenty++;
                    }
                    else if (hasSecCompanion)
                    {
                        adjustedByCompanion++;
                    }
                }

                tr.Commit();
                if (adjusted > 0)
                {
                    logger?.WriteLine(
                        $"Cleanup: reapplied original range-edge L-SEC classification to {adjusted} segment(s) after deterministic relayer (anchor={adjustedByAnchor}, rangeEdge2012={adjustedByRangeEdgeTwenty}, companion20.11={adjustedByCompanion}).");
                }
            }
        }

        private static void TraceTargetLayerSegmentState(
            Database database,
            IEnumerable<ObjectId> requestedQuarterIds,
            string passTag,
            Logger? logger)
        {
            if (database == null || requestedQuarterIds == null || logger == null)
            {
                return;
            }

            var clipWindows = BuildBufferedQuarterWindows(database, requestedQuarterIds, 102.0);
            if (clipWindows.Count == 0)
            {
                return;
            }

            bool IsPointInAnyWindow(Point2d p)
            {
                for (var i = 0; i < clipWindows.Count; i++)
                {
                    var w = clipWindows[i];
                    if (p.X >= w.MinPoint.X && p.X <= w.MaxPoint.X &&
                        p.Y >= w.MinPoint.Y && p.Y <= w.MaxPoint.Y)
                    {
                        return true;
                    }
                }

                return false;
            }

            bool DoesSegmentIntersectAnyWindow(Point2d a, Point2d b)
            {
                if (IsPointInAnyWindow(a) || IsPointInAnyWindow(b))
                {
                    return true;
                }

                for (var i = 0; i < clipWindows.Count; i++)
                {
                    if (TryClipSegmentToWindow(a, b, clipWindows[i], out _, out _))
                    {
                        return true;
                    }
                }

                return false;
            }

            bool TryReadOpenSegment(Entity ent, out Point2d a, out Point2d b)
            {
                a = default;
                b = default;
                if (ent == null)
                {
                    return false;
                }

                if (ent is Line ln)
                {
                    a = new Point2d(ln.StartPoint.X, ln.StartPoint.Y);
                    b = new Point2d(ln.EndPoint.X, ln.EndPoint.Y);
                    return a.GetDistanceTo(b) > 1e-4;
                }

                if (ent is Polyline pl)
                {
                    if (pl.Closed || pl.NumberOfVertices < 2)
                    {
                        return false;
                    }

                    a = pl.GetPoint2dAt(0);
                    b = pl.GetPoint2dAt(pl.NumberOfVertices - 1);
                    return a.GetDistanceTo(b) > 1e-4;
                }

                return false;
            }

            using (var tr = database.TransactionManager.StartTransaction())
            {
                var matches = new List<(ObjectId Id, string Layer, Point2d A, Point2d B, double MidDist)>();
                var targetMid = Midpoint(LayerTraceSegmentStart, LayerTraceSegmentEnd);
                var bt = (BlockTable)tr.GetObject(database.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in ms)
                {
                    Entity? ent = null;
                    try
                    {
                        ent = tr.GetObject(id, OpenMode.ForRead, false) as Entity;
                    }
                    catch (Autodesk.AutoCAD.Runtime.Exception)
                    {
                        continue;
                    }

                    if (ent == null || ent.IsErased)
                    {
                        continue;
                    }

                    var layer = ent.Layer ?? string.Empty;
                    if (!string.Equals(layer, "L-SEC", StringComparison.OrdinalIgnoreCase) &&
                        !IsUsecLayer(layer))
                    {
                        continue;
                    }

                    if (!TryReadOpenSegment(ent, out var a, out var b) ||
                        !DoesSegmentIntersectAnyWindow(a, b) ||
                        !IsTargetLayerTraceSegment(a, b))
                    {
                        continue;
                    }

                    var mid = Midpoint(a, b);
                    matches.Add((id, layer, a, b, mid.GetDistanceTo(targetMid)));
                }

                matches = matches
                    .OrderBy(m => m.MidDist)
                    .ThenBy(m => m.Id.Handle.ToString())
                    .ToList();

                if (matches.Count == 0)
                {
                    logger.WriteLine($"LAYER-TARGET pass={passTag} matches=0");
                    tr.Commit();
                    return;
                }

                logger.WriteLine($"LAYER-TARGET pass={passTag} matches={matches.Count}");
                for (var i = 0; i < matches.Count; i++)
                {
                    var m = matches[i];
                    logger.WriteLine(
                        $"LAYER-TARGET pass={passTag} idx={i + 1} id={m.Id.Handle} layer={m.Layer} " +
                        $"a=({m.A.X:0.###},{m.A.Y:0.###}) b=({m.B.X:0.###},{m.B.Y:0.###}) len={m.A.GetDistanceTo(m.B):0.###}");
                }

                tr.Commit();
            }
        }
    }
}
