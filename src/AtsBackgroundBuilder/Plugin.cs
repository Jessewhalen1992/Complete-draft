using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Geometry;

namespace AtsBackgroundBuilder
{
    public class Plugin : IExtensionApplication
    {
        public void Initialize()
        {
        }

        public void Terminate()
        {
        }

        [CommandMethod("ATSBUILD")]
        public void AtsBuild()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var editor = doc.Editor;
            var database = doc.Database;

            var logger = new Logger();
            var dllFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Environment.CurrentDirectory;
            logger.Initialize(Path.Combine(dllFolder, "AtsBackgroundBuilder.log"));

            var configPath = Path.Combine(dllFolder, "Config.json");
            var config = Config.Load(configPath, logger);

            var companyLookup = ExcelLookup.Load(Path.Combine(dllFolder, "CompanyLookup.xlsx"), logger);
            var purposeLookup = ExcelLookup.Load(Path.Combine(dllFolder, "PurposeLookup.xlsx"), logger);

            var sectionDrawResult = TryPromptAndBuildSections(editor, database, config, logger);
            var quarterPolylines = sectionDrawResult.QuarterPolylineIds;
            if (quarterPolylines.Count == 0)
            {
                editor.WriteMessage("\nNo quarter polylines selected.");
                return;
            }

            var dispositionPolylines = new List<ObjectId>();
            var importSummary = ShapefileImporter.ImportShapefiles(
                database,
                editor,
                logger,
                config,
                sectionDrawResult.SectionPolylineIds,
                dispositionPolylines);
            if (dispositionPolylines.Count == 0)
            {
                editor.WriteMessage("\nNo disposition polylines imported from shapefiles.");
                return;
            }

            var currentClient = PromptForClient(editor, companyLookup);
            if (string.IsNullOrWhiteSpace(currentClient))
            {
                editor.WriteMessage("\nCurrent client is required.");
                return;
            }

            var textHeight = PromptForDouble(editor, "Text height", config.TextHeight, 1.0, 100.0);
            var maxAttempts = PromptForInt(editor, "Max overlap attempts", config.MaxOverlapAttempts, 1, 200);
            config.TextHeight = textHeight;
            config.MaxOverlapAttempts = maxAttempts;

            var layerManager = new LayerManager(database);
            var dispositions = new List<DispositionInfo>();
            var result = new SummaryResult();

            using (var transaction = database.TransactionManager.StartTransaction())
            {
                foreach (var id in dispositionPolylines)
                {
                    var polyline = transaction.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (polyline == null || !polyline.Closed)
                    {
                        result.SkippedNotClosed++;
                        continue;
                    }

                    result.TotalDispositions++;
                    var od = OdHelpers.ReadObjectData(id, logger);
                    if (od == null)
                    {
                        result.SkippedNoOd++;
                        continue;
                    }

                    var dispNum = od.TryGetValue("DISP_NUM", out var dispRaw) ? dispRaw : string.Empty;
                    var company = od.TryGetValue("COMPANY", out var companyRaw) ? companyRaw : string.Empty;
                    var purpose = od.TryGetValue("PURPCD", out var purposeRaw) ? purposeRaw : string.Empty;

                    var mappedCompany = MapValue(companyLookup, company, company);
                    var mappedPurpose = MapValue(purposeLookup, purpose, purpose);
                    var purposeExtra = purposeLookup.Lookup(purpose)?.Extra ?? string.Empty;

                    var dispNumFormatted = FormatDispNum(dispNum);
                    var labelText = mappedCompany + "\\P" + mappedPurpose + "\\P" + dispNumFormatted;

                    var suffix = LayerManager.NormalizeSuffix(string.IsNullOrWhiteSpace(purposeExtra) ? purpose : purposeExtra);
                    string lineLayer;
                    string textLayer;
                    if (string.IsNullOrWhiteSpace(suffix))
                    {
                        lineLayer = polyline.Layer;
                        textLayer = polyline.Layer;
                        result.SkippedNoLayerMapping++;
                    }
                    else
                    {
                        var prefix = mappedCompany.Equals(currentClient, StringComparison.OrdinalIgnoreCase) ? "C" : "F";
                        lineLayer = prefix + "-" + suffix;
                        textLayer = prefix + "-" + suffix + "-T";
                        layerManager.EnsureLayer(lineLayer);
                        layerManager.EnsureLayer(textLayer);
                    }

                    if (!lineLayer.Equals(polyline.Layer, StringComparison.OrdinalIgnoreCase))
                    {
                        polyline.UpgradeOpen();
                        polyline.Layer = lineLayer;
                    }

                    var safePoint = GeometryUtils.GetSafeInteriorPoint(polyline);
                    var clone = (Polyline)polyline.Clone();
                    dispositions.Add(new DispositionInfo(id, clone, labelText, lineLayer, textLayer, safePoint));
                }

                transaction.Commit();
            }

            var quarters = new List<QuarterInfo>();
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                foreach (var id in quarterPolylines)
                {
                    var polyline = transaction.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (polyline == null || !polyline.Closed)
                    {
                        continue;
                    }

                    quarters.Add(new QuarterInfo((Polyline)polyline.Clone()));
                }
                transaction.Commit();
            }

            var placer = new LabelPlacer(database, editor, layerManager, config, logger);
            var placement = placer.PlaceLabels(quarters, dispositions, currentClient);

            result.LabelsPlaced = placement.LabelsPlaced;
            result.SkippedNoLayerMapping += placement.SkippedNoLayerMapping;
            result.OverlapForced = placement.OverlapForced;
            result.MultiQuarterProcessed = placement.MultiQuarterProcessed;
            result.ImportedDispositions = importSummary.ImportedDispositions;
            result.DedupedDispositions = importSummary.DedupedDispositions;
            result.FilteredDispositions = importSummary.FilteredDispositions;
            result.ImportFailures = importSummary.ImportFailures;

            editor.WriteMessage("\nATSBUILD complete.");
            editor.WriteMessage("\nTotal dispositions: " + result.TotalDispositions);
            editor.WriteMessage("\nLabels placed: " + result.LabelsPlaced);
            editor.WriteMessage("\nSkipped (no OD): " + result.SkippedNoOd);
            editor.WriteMessage("\nSkipped (not closed): " + result.SkippedNotClosed);
            editor.WriteMessage("\nNo layer mapping: " + result.SkippedNoLayerMapping);
            editor.WriteMessage("\nOverlap forced: " + result.OverlapForced);
            editor.WriteMessage("\nMulti-quarter processed: " + result.MultiQuarterProcessed);
            editor.WriteMessage("\nImported dispositions: " + result.ImportedDispositions);
            editor.WriteMessage("\nFiltered out: " + result.FilteredDispositions);
            editor.WriteMessage("\nDeduped: " + result.DedupedDispositions);
            editor.WriteMessage("\nImport failures: " + result.ImportFailures);

            logger.WriteLine("ATSBUILD summary");
            logger.WriteLine("Total dispositions: " + result.TotalDispositions);
            logger.WriteLine("Labels placed: " + result.LabelsPlaced);
            logger.WriteLine("Skipped (no OD): " + result.SkippedNoOd);
            logger.WriteLine("Skipped (not closed): " + result.SkippedNotClosed);
            logger.WriteLine("No layer mapping: " + result.SkippedNoLayerMapping);
            logger.WriteLine("Overlap forced: " + result.OverlapForced);
            logger.WriteLine("Multi-quarter processed: " + result.MultiQuarterProcessed);
            logger.WriteLine("Imported dispositions: " + result.ImportedDispositions);
            logger.WriteLine("Filtered out: " + result.FilteredDispositions);
            logger.WriteLine("Deduped: " + result.DedupedDispositions);
            logger.WriteLine("Import failures: " + result.ImportFailures);
            logger.Dispose();
        }

        private static SectionDrawResult TryPromptAndBuildSections(Editor editor, Database database, Config config, Logger logger)
        {
            if (config.UseSectionIndex)
            {
                var requests = PromptForSectionRequests(editor);
                if (requests.Count > 0)
                {
                    return DrawSectionsFromRequests(database, requests, config, logger);
                }
            }

            editor.WriteMessage("\nSection input required.");
            return new SectionDrawResult(new List<ObjectId>(), new List<ObjectId>(), false);
        }

        private static List<SectionRequest> PromptForSectionRequests(Editor editor)
        {
            var requests = new List<SectionRequest>();
            var zone = PromptForInt(editor, "Enter zone", 11, 1, 60);

            var addAnother = true;
            while (addAnother)
            {
                var quarter = PromptForQuarter(editor);
                if (quarter == QuarterSelection.None)
                {
                    break;
                }

                if (!TryPromptString(editor, "Enter section", out var section) ||
                    !TryPromptString(editor, "Enter township", out var township) ||
                    !TryPromptString(editor, "Enter range", out var range) ||
                    !TryPromptString(editor, "Enter meridian", out var meridian))
                {
                    break;
                }

                requests.Add(new SectionRequest(quarter, new SectionKey(zone, section, township, range, meridian)));

                var moreOptions = new PromptKeywordOptions("Add another section?")
                {
                    AllowNone = true
                };
                moreOptions.Keywords.Add("Yes");
                moreOptions.Keywords.Add("No");
                moreOptions.Keywords.Default = "No";

                var moreResult = editor.GetKeywords(moreOptions);
                addAnother = moreResult.Status == PromptStatus.OK &&
                             string.Equals(moreResult.StringResult, "Yes", StringComparison.OrdinalIgnoreCase);
            }

            return requests;
        }

        private static string PromptForClient(Editor editor, ExcelLookup lookup)
        {
            var values = lookup.GetAllValues();
            if (values.Count > 0 && values.Count <= 20)
            {
                var options = new PromptKeywordOptions("Select current client")
                {
                    AllowNone = false
                };

                foreach (var value in values)
                {
                    options.Keywords.Add(value);
                }

                var result = editor.GetKeywords(options);
                if (result.Status == PromptStatus.OK)
                {
                    return result.StringResult;
                }
            }

            if (values.Count > 0)
            {
                editor.WriteMessage("\nAvailable clients: " + string.Join(", ", values));
            }

            var prompt = new PromptStringOptions("Enter current client name: ")
            {
                AllowSpaces = true
            };
            var input = editor.GetString(prompt);
            return input.Status == PromptStatus.OK ? input.StringResult : string.Empty;
        }

        private static double PromptForDouble(Editor editor, string message, double defaultValue, double min, double max)
        {
            var options = new PromptDoubleOptions(message + " [" + defaultValue + "]: ")
            {
                DefaultValue = defaultValue,
                AllowNone = true
            };

            var result = editor.GetDouble(options);
            if (result.Status != PromptStatus.OK)
            {
                return defaultValue;
            }

            var value = result.Value;
            if (value < min || value > max)
            {
                editor.WriteMessage($"\nValue out of range. Using nearest allowed value ({min} - {max}).");
                return Math.Min(Math.Max(value, min), max);
            }

            return value;
        }

        private static int PromptForInt(Editor editor, string message, int defaultValue, int min, int max)
        {
            var options = new PromptIntegerOptions(message + " [" + defaultValue + "]: ")
            {
                DefaultValue = defaultValue,
                AllowNone = true,
                LowerLimit = min,
                UpperLimit = max
            };

            var result = editor.GetInteger(options);
            return result.Status == PromptStatus.OK ? result.Value : defaultValue;
        }

        private static string MapValue(ExcelLookup lookup, string key, string fallback)
        {
            var entry = lookup.Lookup(key);
            return entry == null || string.IsNullOrWhiteSpace(entry.Value) ? fallback : entry.Value;
        }

        private static string FormatDispNum(string dispNum)
        {
            var regex = new Regex("^([A-Z]{3})(\\d+)");
            var match = regex.Match(dispNum ?? string.Empty);
            if (!match.Success)
            {
                return dispNum;
            }

            return match.Groups[1].Value + " " + match.Groups[2].Value;
        }

        private static IEnumerable<Polyline> GenerateQuarters(Polyline section)
        {
            var extents = section.GeometricExtents;
            var minX = extents.MinPoint.X;
            var minY = extents.MinPoint.Y;
            var maxX = extents.MaxPoint.X;
            var maxY = extents.MaxPoint.Y;
            var midX = (minX + maxX) / 2.0;
            var midY = (minY + maxY) / 2.0;

            yield return CreateRectangle(minX, minY, midX, midY);
            yield return CreateRectangle(midX, minY, maxX, midY);
            yield return CreateRectangle(minX, midY, midX, maxY);
            yield return CreateRectangle(midX, midY, maxX, maxY);
        }

        private static Polyline CreateRectangle(double minX, double minY, double maxX, double maxY)
        {
            var polyline = new Polyline(4)
            {
                Closed = true
            };

            polyline.AddVertexAt(0, new Point2d(minX, minY), 0, 0, 0);
            polyline.AddVertexAt(1, new Point2d(maxX, minY), 0, 0, 0);
            polyline.AddVertexAt(2, new Point2d(maxX, maxY), 0, 0, 0);
            polyline.AddVertexAt(3, new Point2d(minX, maxY), 0, 0, 0);

            return polyline;
        }

        private static SectionDrawResult DrawSectionsFromRequests(Database database, List<SectionRequest> requests, Config config, Logger logger)
        {
            var quarterIds = new List<ObjectId>();
            var sectionIds = new List<ObjectId>();
            var createdSections = new Dictionary<string, SectionBuildResult>(StringComparer.OrdinalIgnoreCase);
            var baseFolder = string.IsNullOrWhiteSpace(config.SectionIndexFolder)
                ? (Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Environment.CurrentDirectory)
                : config.SectionIndexFolder;

            foreach (var request in requests)
            {
                var keyId = BuildSectionKeyId(request.Key);
                if (!createdSections.TryGetValue(keyId, out var buildResult))
                {
                    if (!SectionIndexReader.TryLoadSectionOutline(baseFolder, request.Key, logger, out var outline))
                    {
                        continue;
                    }

                    buildResult = DrawSectionFromIndex(database, outline);
                    createdSections[keyId] = buildResult;
                    sectionIds.Add(buildResult.SectionPolylineId);
                }

                if (request.Quarter == QuarterSelection.All)
                {
                    foreach (var quarterId in buildResult.QuarterPolylineIds.Values)
                    {
                        quarterIds.Add(quarterId);
                    }
                }
                else if (buildResult.QuarterPolylineIds.TryGetValue(request.Quarter, out var quarterId))
                {
                    quarterIds.Add(quarterId);
                }
            }

            return new SectionDrawResult(quarterIds, sectionIds, true);
        }

        private static SectionBuildResult DrawSectionFromIndex(Database database, SectionOutline outline)
        {
            var quarterIds = new Dictionary<QuarterSelection, ObjectId>();
            ObjectId sectionId;
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
                var sectionPolyline = new Polyline(outline.Vertices.Count)
                {
                    Closed = outline.Closed
                };

                for (var i = 0; i < outline.Vertices.Count; i++)
                {
                    var vertex = outline.Vertices[i];
                    sectionPolyline.AddVertexAt(i, vertex, 0, 0, 0);
                }

                sectionId = modelSpace.AppendEntity(sectionPolyline);
                transaction.AddNewlyCreatedDBObject(sectionPolyline, true);

                foreach (var quarter in GenerateQuarterMap(sectionPolyline))
                {
                    var quarterId = modelSpace.AppendEntity(quarter.Value);
                    transaction.AddNewlyCreatedDBObject(quarter.Value, true);
                    quarterIds[quarter.Key] = quarterId;
                }

                transaction.Commit();
            }

            return new SectionBuildResult(sectionId, quarterIds);
        }

        private static QuarterSelection PromptForQuarter(Editor editor)
        {
            var options = new PromptKeywordOptions("Select quarter")
            {
                AllowNone = false
            };
            options.Keywords.Add("NW");
            options.Keywords.Add("NE");
            options.Keywords.Add("SW");
            options.Keywords.Add("SE");
            options.Keywords.Add("ALL");

            var result = editor.GetKeywords(options);
            if (result.Status != PromptStatus.OK)
            {
                return QuarterSelection.None;
            }

            switch (result.StringResult.ToUpperInvariant())
            {
                case "NW":
                    return QuarterSelection.NorthWest;
                case "NE":
                    return QuarterSelection.NorthEast;
                case "SW":
                    return QuarterSelection.SouthWest;
                case "SE":
                    return QuarterSelection.SouthEast;
                case "ALL":
                    return QuarterSelection.All;
                default:
                    return QuarterSelection.None;
            }
        }

        private static bool TryPromptString(Editor editor, string message, out string value)
        {
            value = string.Empty;
            var options = new PromptStringOptions(message + ": ")
            {
                AllowSpaces = true
            };

            var result = editor.GetString(options);
            if (result.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(result.StringResult))
            {
                return false;
            }

            value = result.StringResult;
            return true;
        }

        private static bool TryPromptInt(Editor editor, string message, out int value)
        {
            value = 0;
            var options = new PromptIntegerOptions(message + ": ")
            {
                AllowNone = false
            };

            var result = editor.GetInteger(options);
            if (result.Status != PromptStatus.OK)
            {
                return false;
            }

            value = result.Value;
            return true;
        }

        private static Dictionary<QuarterSelection, Polyline> GenerateQuarterMap(Polyline section)
        {
            var extents = section.GeometricExtents;
            var minX = extents.MinPoint.X;
            var minY = extents.MinPoint.Y;
            var maxX = extents.MaxPoint.X;
            var maxY = extents.MaxPoint.Y;
            var midX = (minX + maxX) / 2.0;
            var midY = (minY + maxY) / 2.0;

            return new Dictionary<QuarterSelection, Polyline>
            {
                { QuarterSelection.SouthWest, CreateRectangle(minX, minY, midX, midY) },
                { QuarterSelection.SouthEast, CreateRectangle(midX, minY, maxX, midY) },
                { QuarterSelection.NorthWest, CreateRectangle(minX, midY, midX, maxY) },
                { QuarterSelection.NorthEast, CreateRectangle(midX, midY, maxX, maxY) }
            };
        }

        private static string BuildSectionKeyId(SectionKey key)
        {
            return $"Z{key.Zone}_SEC{key.Section}_TWP{key.Township}_RGE{key.Range}_MER{key.Meridian}";
        }
    }

    public sealed class SectionDrawResult
    {
        public SectionDrawResult(List<ObjectId> quarterPolylineIds, List<ObjectId> sectionPolylineIds, bool generatedFromIndex)
        {
            QuarterPolylineIds = quarterPolylineIds;
            SectionPolylineIds = sectionPolylineIds;
            GeneratedFromIndex = generatedFromIndex;
        }

        public List<ObjectId> QuarterPolylineIds { get; }
        public List<ObjectId> SectionPolylineIds { get; }
        public bool GeneratedFromIndex { get; }
    }

    public sealed class SectionBuildResult
    {
        public SectionBuildResult(ObjectId sectionPolylineId, Dictionary<QuarterSelection, ObjectId> quarterPolylineIds)
        {
            SectionPolylineId = sectionPolylineId;
            QuarterPolylineIds = quarterPolylineIds;
        }

        public ObjectId SectionPolylineId { get; }
        public Dictionary<QuarterSelection, ObjectId> QuarterPolylineIds { get; }
    }

    public sealed class SectionRequest
    {
        public SectionRequest(QuarterSelection quarter, SectionKey key)
        {
            Quarter = quarter;
            Key = key;
        }

        public QuarterSelection Quarter { get; }
        public SectionKey Key { get; }
    }

    public enum QuarterSelection
    {
        None,
        NorthWest,
        NorthEast,
        SouthWest,
        SouthEast,
        All
    }

    public sealed class SummaryResult
    {
        public int TotalDispositions { get; set; }
        public int LabelsPlaced { get; set; }
        public int SkippedNoOd { get; set; }
        public int SkippedNotClosed { get; set; }
        public int SkippedNoLayerMapping { get; set; }
        public int OverlapForced { get; set; }
        public int MultiQuarterProcessed { get; set; }
        public int ImportedDispositions { get; set; }
        public int FilteredDispositions { get; set; }
        public int DedupedDispositions { get; set; }
        public int ImportFailures { get; set; }
    }

    public sealed class Logger : IDisposable
    {
        private StreamWriter? _writer;

        public void Initialize(string path)
        {
            _writer = new StreamWriter(path, true) { AutoFlush = true };
            WriteLine("---- ATSBUILD " + DateTime.Now + " ----");
        }

        public void WriteLine(string message)
        {
            _writer?.WriteLine(message);
        }

        public void Dispose()
        {
            _writer?.Dispose();
        }
    }
}
