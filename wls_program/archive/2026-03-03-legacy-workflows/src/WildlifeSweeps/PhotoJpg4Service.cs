using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;

namespace WildlifeSweeps
{
    public class PhotoJpg4Service
    {
        private static readonly Regex PicRegex = new Regex(@"PIC\s*(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PageRegex = new Regex(@"_PAGE_(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public void Execute(Document doc, Editor editor, PluginSettings settings)
        {
            using (doc.LockDocument())
            {
                var sampleResult = editor.GetEntity("\nSelect ONE Photo_Location block (sample): ");
                if (sampleResult.Status != PromptStatus.OK)
                {
                    return;
                }

                string? blockName;
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    var sampleRef = tr.GetObject(sampleResult.ObjectId, OpenMode.ForRead) as BlockReference;
                    if (sampleRef == null)
                    {
                        editor.WriteMessage("\nSelection is not a block reference.");
                        return;
                    }

                    blockName = BlockSelectionHelper.GetEffectiveName(sampleRef, tr);
                    tr.Commit();
                }

                var jpgPath = PromptForJpg(editor);
                if (string.IsNullOrWhiteSpace(jpgPath))
                {
                    return;
                }

                var folder = Path.GetDirectoryName(jpgPath);
                if (string.IsNullOrWhiteSpace(folder))
                {
                    editor.WriteMessage("\nUnable to determine folder from JPG.");
                    return;
                }

                var pageMap = BuildPageMap(folder);
                if (pageMap.Count == 0)
                {
                    editor.WriteMessage("\nNo JPGs with '_Page_XX' found in that folder.");
                    return;
                }

                var pageForPic1 = settings.PageForPic1 > 0
                    ? settings.PageForPic1
                    : PromptHelper.PromptForInt(editor, "\nJPEG page number for PIC 1 <2>: ", settings.PageForPic1);
                settings.PageForPic1 = pageForPic1;
                var pageOffset = pageForPic1 - 1;

                var candidatePics = GatherPicTexts(doc.Database, editor, settings);
                var picIndex = new PicSpatialIndex(candidatePics);

                var blocks = BlockSelectionHelper.PromptForBlocks(doc, editor, blockName);
                if (blocks.Count == 0)
                {
                    editor.WriteMessage($"\nNo INSERTs found for block: {blockName}.");
                    return;
                }

                var records = BuildPhotoRecords(doc.Database, blocks, picIndex, pageMap, settings, pageOffset, editor);
                if (records.Count == 0)
                {
                    editor.WriteMessage($"\nNo blocks matched effective name: {blockName}.");
                    return;
                }

                var ordered = records
                    .OrderBy(r => r.PhotoNumber)
                    .Select(r => new PhotoLayoutRecord(r.PhotoNumber, r.ImagePath))
                    .ToList();

                if (!PhotoLayoutHelper.PlacePhotoGroups(doc.Database, editor, settings, ordered, out var report))
                {
                    return;
                }

                if (report.Count > 0)
                {
                    editor.WriteMessage("\n--- Report ---");
                    foreach (var entry in report)
                    {
                        editor.WriteMessage($"\n{entry}");
                    }
                    editor.WriteMessage("\n-------------");
                }

                editor.WriteMessage($"\nDone. Processed {ordered.Count} photo locations.");
            }
        }

        private static string? PromptForJpg(Editor editor)
        {
            var dialog = new OpenFileDialog("Pick ANY JPG in the folder (we auto-load all _Page_XX): ", "", "jpg;jpeg", "jpg", OpenFileDialog.OpenFileDialogFlags.DoNotTransferRemoteFiles);
            return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.Filename : null;
        }

        private static Dictionary<int, string> BuildPageMap(string folder)
        {
            var map = new Dictionary<int, string>();
            var files = Directory.EnumerateFiles(folder, "*.*")
                .Where(path => path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                               || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase));

            foreach (var path in files)
            {
                var match = PageRegex.Match(Path.GetFileName(path) ?? string.Empty);
                if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var page))
                {
                    if (!map.ContainsKey(page))
                    {
                        map[page] = path;
                    }
                }
            }

            return map;
        }

        private static List<PicCandidate> GatherPicTexts(Database db, Editor editor, PluginSettings settings)
        {
            var candidates = new List<PicCandidate>();

            using var tr = db.TransactionManager.StartTransaction();
            var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

            foreach (ObjectId id in space)
            {
                if (id.ObjectClass == RXObject.GetClass(typeof(DBText)))
                {
                    var text = (DBText)tr.GetObject(id, OpenMode.ForRead);
                    var pic = ParsePicNumber(text.TextString);
                    if (pic.HasValue)
                    {
                        candidates.Add(new PicCandidate(pic.Value, text.Position));
                    }
                }
                else if (id.ObjectClass == RXObject.GetClass(typeof(MText)))
                {
                    var mtext = (MText)tr.GetObject(id, OpenMode.ForRead);
                    var pic = ParsePicNumber(mtext.Text);
                    if (pic.HasValue)
                    {
                        candidates.Add(new PicCandidate(pic.Value, mtext.Location));
                    }
                }
            }

            tr.Commit();

            if (candidates.Count == 0)
            {
                editor.WriteMessage("\nWARNING: No TEXT/MTEXT found containing 'PIC <number>'.");
            }

            return candidates;
        }

        private static List<PhotoRecord> BuildPhotoRecords(
            Database db,
            List<ObjectId> blocks,
            PicSpatialIndex picIndex,
            Dictionary<int, string> pageMap,
            PluginSettings settings,
            int pageOffset,
            Editor editor)
        {
            var records = new List<PhotoRecord>();

            using var tr = db.TransactionManager.StartTransaction();

            foreach (var blockId in blocks)
            {
                var block = tr.GetObject(blockId, OpenMode.ForRead) as BlockReference;
                if (block == null)
                {
                    continue;
                }

                var photoNumber = BlockSelectionHelper.TryGetAttribute(block, "#", tr);
                if (photoNumber == null || photoNumber <= 0)
                {
                    continue;
                }

                var nearest = picIndex.FindNearest(block.Position, settings.PicSearchRadius);
                var picNumber = nearest?.PicNumber;
                var pageNumber = picNumber.HasValue ? picNumber.Value + pageOffset : (int?)null;
                string? imagePath = null;

                if (pageNumber.HasValue && pageMap.TryGetValue(pageNumber.Value, out var path))
                {
                    imagePath = path;
                }

                if (!picNumber.HasValue)
                {
                    editor.WriteMessage($"\nPhoto {photoNumber}: no nearby 'PIC ##' text found.");
                }
                else if (!pageNumber.HasValue || imagePath == null)
                {
                    editor.WriteMessage($"\nPhoto {photoNumber} (PIC {picNumber}) => Page {pageNumber} : JPG not found (_Page_XX).");
                }

                records.Add(new PhotoRecord(photoNumber.Value, picNumber, pageNumber, imagePath));
            }

            tr.Commit();

            return records;
        }

        private static int? ParsePicNumber(string text)
        {
            var match = PicRegex.Match(text ?? string.Empty);
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            return null;
        }

        private record PicCandidate(int PicNumber, Point3d Position);

        private record PhotoRecord(int PhotoNumber, int? PicNumber, int? PageNumber, string? ImagePath);

        private sealed class PicSpatialIndex
        {
            private readonly KdNode? _root;

            public PicSpatialIndex(IReadOnlyList<PicCandidate> candidates)
            {
                _root = Build(candidates, 0);
            }

            public PicCandidate? FindNearest(Point3d point, double radius)
            {
                if (_root == null)
                {
                    return null;
                }

                var maxDistanceSquared = radius * radius;
                return FindNearest(_root, point, maxDistanceSquared, null, double.PositiveInfinity);
            }

            private static KdNode? Build(IReadOnlyList<PicCandidate> candidates, int depth)
            {
                if (candidates.Count == 0)
                {
                    return null;
                }

                var axis = depth % 2;
                var ordered = axis == 0
                    ? candidates.OrderBy(candidate => candidate.Position.X).ToList()
                    : candidates.OrderBy(candidate => candidate.Position.Y).ToList();
                var medianIndex = ordered.Count / 2;

                return new KdNode(
                    ordered[medianIndex],
                    Build(ordered.Take(medianIndex).ToList(), depth + 1),
                    Build(ordered.Skip(medianIndex + 1).ToList(), depth + 1),
                    axis);
            }

            private static PicCandidate? FindNearest(
                KdNode node,
                Point3d target,
                double maxDistanceSquared,
                PicCandidate? best,
                double bestDistanceSquared)
            {
                var distanceSquared = (target.X - node.Candidate.Position.X) * (target.X - node.Candidate.Position.X)
                                      + (target.Y - node.Candidate.Position.Y) * (target.Y - node.Candidate.Position.Y);

                if (distanceSquared < bestDistanceSquared)
                {
                    bestDistanceSquared = distanceSquared;
                    best = node.Candidate;
                }

                var axisDifference = node.Axis == 0
                    ? target.X - node.Candidate.Position.X
                    : target.Y - node.Candidate.Position.Y;

                var primary = axisDifference < 0 ? node.Left : node.Right;
                var secondary = axisDifference < 0 ? node.Right : node.Left;

                if (primary != null)
                {
                    best = FindNearest(primary, target, maxDistanceSquared, best, bestDistanceSquared);
                    if (best != null)
                    {
                        bestDistanceSquared = DistanceSquared(best.Position, target);
                    }
                }

                if (secondary != null && axisDifference * axisDifference < bestDistanceSquared)
                {
                    best = FindNearest(secondary, target, maxDistanceSquared, best, bestDistanceSquared);
                }

                if (bestDistanceSquared > maxDistanceSquared)
                {
                    return null;
                }

                return best;
            }

            private static double DistanceSquared(Point3d a, Point3d b)
            {
                var dx = a.X - b.X;
                var dy = a.Y - b.Y;
                return dx * dx + dy * dy;
            }

            private sealed record KdNode(PicCandidate Candidate, KdNode? Left, KdNode? Right, int Axis);
        }
    }
}
