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

            var quarterPolylines = PromptForQuarterPolylines(editor, database);
            if (quarterPolylines.Count == 0)
            {
                editor.WriteMessage("\nNo quarter polylines selected.");
                return;
            }

            var dispositionPolylines = PromptForDispositionPolylines(editor, database);
            if (dispositionPolylines.Count == 0)
            {
                editor.WriteMessage("\nNo disposition polylines selected.");
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

            editor.WriteMessage("\nATSBUILD complete.");
            editor.WriteMessage("\nTotal dispositions: " + result.TotalDispositions);
            editor.WriteMessage("\nLabels placed: " + result.LabelsPlaced);
            editor.WriteMessage("\nSkipped (no OD): " + result.SkippedNoOd);
            editor.WriteMessage("\nSkipped (not closed): " + result.SkippedNotClosed);
            editor.WriteMessage("\nNo layer mapping: " + result.SkippedNoLayerMapping);
            editor.WriteMessage("\nOverlap forced: " + result.OverlapForced);
            editor.WriteMessage("\nMulti-quarter processed: " + result.MultiQuarterProcessed);

            logger.WriteLine("ATSBUILD summary");
            logger.WriteLine("Total dispositions: " + result.TotalDispositions);
            logger.WriteLine("Labels placed: " + result.LabelsPlaced);
            logger.WriteLine("Skipped (no OD): " + result.SkippedNoOd);
            logger.WriteLine("Skipped (not closed): " + result.SkippedNotClosed);
            logger.WriteLine("No layer mapping: " + result.SkippedNoLayerMapping);
            logger.WriteLine("Overlap forced: " + result.OverlapForced);
            logger.WriteLine("Multi-quarter processed: " + result.MultiQuarterProcessed);
            logger.Dispose();
        }

        private static List<ObjectId> PromptForQuarterPolylines(Editor editor, Database database)
        {
            var filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "LWPOLYLINE")
            });

            var prompt = new PromptSelectionOptions
            {
                MessageForAdding = "Select quarter-section polylines (Enter to select sections): "
            };

            var selection = editor.GetSelection(prompt, filter);
            if (selection.Status == PromptStatus.OK)
            {
                return new List<ObjectId>(selection.Value.GetObjectIds());
            }

            editor.WriteMessage("\nSelect section polylines to generate quarters.");
            var sectionSelection = editor.GetSelection(prompt, filter);
            if (sectionSelection.Status != PromptStatus.OK)
            {
                return new List<ObjectId>();
            }

            var quarterIds = new List<ObjectId>();
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForWrite);

                foreach (var id in sectionSelection.Value.GetObjectIds())
                {
                    var section = transaction.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (section == null || !section.Closed)
                    {
                        continue;
                    }

                    foreach (var quarter in GenerateQuarters(section))
                    {
                        var quarterId = modelSpace.AppendEntity(quarter);
                        transaction.AddNewlyCreatedDBObject(quarter, true);
                        quarterIds.Add(quarterId);
                    }
                }

                transaction.Commit();
            }

            return quarterIds;
        }

        private static List<ObjectId> PromptForDispositionPolylines(Editor editor, Database database)
        {
            var filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "LWPOLYLINE")
            });

            var prompt = new PromptSelectionOptions
            {
                MessageForAdding = "Select disposition polylines (Enter for all on layer): "
            };

            var selection = editor.GetSelection(prompt, filter);
            if (selection.Status == PromptStatus.OK)
            {
                return new List<ObjectId>(selection.Value.GetObjectIds());
            }

            var layerPrompt = new PromptStringOptions("Enter disposition layer name (or * for all layers): ")
            {
                AllowSpaces = true
            };

            var layerResult = editor.GetString(layerPrompt);
            if (layerResult.Status != PromptStatus.OK)
            {
                return new List<ObjectId>();
            }

            var layerName = layerResult.StringResult.Trim();
            var objectIds = new List<ObjectId>();
            using (var transaction = database.TransactionManager.StartTransaction())
            {
                var blockTable = (BlockTable)transaction.GetObject(database.BlockTableId, OpenMode.ForRead);
                var modelSpace = (BlockTableRecord)transaction.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                foreach (ObjectId id in modelSpace)
                {
                    var polyline = transaction.GetObject(id, OpenMode.ForRead) as Polyline;
                    if (polyline == null || !polyline.Closed)
                    {
                        continue;
                    }

                    if (layerName == "*" || polyline.Layer.Equals(layerName, StringComparison.OrdinalIgnoreCase))
                    {
                        objectIds.Add(id);
                    }
                }

                transaction.Commit();
            }

            return objectIds;
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
                AllowNone = true,
                MinValue = min,
                MaxValue = max
            };

            var result = editor.GetDouble(options);
            return result.Status == PromptStatus.OK ? result.Value : defaultValue;
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
