using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.AutoCAD.Windows;

namespace WildlifeSweeps
{
    public class CompleteFromPhotosService
    {
        private const short OutsideBufferTableColor = 254;
        private const double BoundaryEdgeTolerance = 0.01;
        private const double BoundarySamplingStepMeters = 2.0;
        private const double BoundarySampleMergeTolerance = 0.01;
        private const string QuarterValidationLayerName = "L-QUARTER";
        private const string QuarterLegacySourceLayerName = "L-QUATER";
        private const string DefaultAtsSectionIndexFolder = @"C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\RES MANAGER";
        private const string AllowSimpleQuarterFallbackEnvVar = "WLS_ALLOW_SIMPLE_QUARTER_FALLBACK";
        private const short QuarterValidationLayerColor = 30;

        private enum BufferMode
        {
            None,
            IncludeBufferExcludeOutside,
            IncludeBufferAndAll
        }

        private enum BufferArea
        {
            Outside,
            HundredMeter,
            Proposed
        }

        public void Execute(Document doc, Editor editor, PluginSettings settings)
        {
            using (doc.LockDocument())
            {
                var includeQuarterLinework = settings.CompleteFromPhotosIncludeQuarterLinework;
                try
                {
                var bufferMode = ResolveBufferMode(settings);
                BufferBoundary? proposedBoundary = null;
                BufferBoundary? hundredMeterBoundary = null;
                if (UsesAreaSpecificBufferPrompts(bufferMode))
                {
                    proposedBoundary = PromptForBuffer(
                        editor,
                        doc.Database,
                        "\nSelect a closed polyline boundary for PROPOSED:");
                    if (proposedBoundary == null)
                    {
                        return;
                    }

                    hundredMeterBoundary = PromptForBuffer(
                        editor,
                        doc.Database,
                        "\nSelect a closed polyline boundary for the 100m buffer:");
                    if (hundredMeterBoundary == null)
                    {
                        return;
                    }
                }

                string? sampleBlockName = null;
                string? proposedBlockName = null;
                string? hundredMeterBlockName = null;
                string? outsideBufferBlockName = null;
                if (UsesAreaSpecificBufferPrompts(bufferMode))
                {
                    proposedBlockName = PromptForSamplePhotoBlock(
                        editor,
                        doc.Database,
                        "\nSelect ONE Photo_Location block for PROPOSED area: ");
                    if (string.IsNullOrWhiteSpace(proposedBlockName))
                    {
                        return;
                    }

                    hundredMeterBlockName = PromptForSamplePhotoBlock(
                        editor,
                        doc.Database,
                        "\nSelect ONE Photo_Location block for 100m-only area: ");
                    if (string.IsNullOrWhiteSpace(hundredMeterBlockName))
                    {
                        return;
                    }

                    if (bufferMode == BufferMode.IncludeBufferAndAll)
                    {
                        outsideBufferBlockName = PromptForSamplePhotoBlock(
                            editor,
                            doc.Database,
                            "\nSelect ONE Photo_Location block for OUTSIDE area: ");
                        if (string.IsNullOrWhiteSpace(outsideBufferBlockName))
                        {
                            return;
                        }
                    }
                }
                else
                {
                    sampleBlockName = PromptForSamplePhotoBlock(editor, doc.Database, "\nSelect ONE Photo_Location block (sample): ");
                    if (string.IsNullOrWhiteSpace(sampleBlockName))
                    {
                        return;
                    }
                }

                var utmZone = WildlifePromptHelper.PromptForUtmZone(editor, settings.UtmZone);
                settings.UtmZone = utmZone;

                var order = PromptForOrder(editor, settings.NumberOrder);
                settings.NumberOrder = order;

                var selectedTextFindings = PromptForFindingTextSelection(editor, doc.Database);
                if (selectedTextFindings.Count > 0)
                {
                    editor.WriteMessage($"\nSelected {selectedTextFindings.Count} finding text object(s).");
                }

                var jpgPath = WildlifePromptHelper.PromptForJpg("Pick ANY JPG in the folder (we auto-load all JPGs): ");
                var gpsPhotos = new List<PhotoGpsRecord>();
                string? photoFolder = null;
                if (!string.IsNullOrWhiteSpace(jpgPath))
                {
                    var folder = Path.GetDirectoryName(jpgPath);
                    if (string.IsNullOrWhiteSpace(folder))
                    {
                        editor.WriteMessage("\nUnable to determine folder from JPG.");
                        return;
                    }

                    photoFolder = folder;
                    gpsPhotos = LoadGpsPhotos(folder, editor);
                }
                else if (selectedTextFindings.Count == 0)
                {
                    return;
                }

                if (gpsPhotos.Count == 0 && selectedTextFindings.Count == 0)
                {
                    editor.WriteMessage("\nNo photo GPS records or text findings available.");
                    return;
                }

                if (!UtmCoordinateConverter.TryCreate(utmZone, out var utmConverter) || utmConverter == null)
                {
                    editor.WriteMessage("\n** Coordinate conversion failed - check UTM zone. **");
                    return;
                }

                AtsQuarterLocationResolver? locationResolver = null;
                var hasLocationZone = int.TryParse(utmZone, NumberStyles.Integer, CultureInfo.InvariantCulture, out var locationZone);
                if (hasLocationZone)
                {
                    AtsQuarterLocationResolver.TryCreate(locationZone, doc.Database.Filename, out locationResolver);
                }
                var quarterLayerSources = LoadQuarterLayerSources(doc.Database, locationResolver);
                if (quarterLayerSources.Count > 0)
                {
                    WriteQuarterLayerSourceSummary(editor, quarterLayerSources);
                }

                var tableStyleId = EnsureTableStyle(doc.Database, "Induction Bend");

                var projectedPhotos = new List<PhotoProjectedRecord>();
                foreach (var photo in gpsPhotos)
                {
                    if (!utmConverter.TryProjectLatLon(photo.Latitude, photo.Longitude, out var easting, out var northing))
                    {
                        editor.WriteMessage($"\nSkipped {photo.FileName}: unable to convert coordinates.");
                        continue;
                    }

                    projectedPhotos.Add(new PhotoProjectedRecord(photo.ImagePath, photo.FileName, photo.Latitude, photo.Longitude, northing, easting));
                }

                var projectedResult = BuildProjectedFindings(projectedPhotos, selectedTextFindings, utmConverter, editor, hundredMeterBoundary, bufferMode);
                var projectedFindings = ApplyBufferFiltering(projectedResult.Findings, hundredMeterBoundary, bufferMode);
                if (projectedFindings.Count == 0)
                {
                    editor.WriteMessage("\nNo findings available after 3m de-dup/filtering/buffer filtering.");
                    return;
                }

                var matchedTextCount = ColorAssociatedTextEntitiesGreen(doc.Database, projectedResult.MatchedTextIds);
                if (matchedTextCount > 0)
                {
                    editor.WriteMessage($"\nMarked {matchedTextCount} photo-associated text finding(s) in green.");
                }

                var ordered = SortRecordsForMode(
                    projectedFindings,
                    settings.NumberOrder,
                    bufferMode,
                    proposedBoundary,
                    hundredMeterBoundary);

                // Standardize descriptions before numbering so ignored findings never consume bubble/table numbers.
                var standardizer = new FindingsDescriptionStandardizer(
                    settings.FindingsLookupPath,
                    warning => editor.WriteMessage($"\n{warning}"));
                var logBuilder = FindingsStandardizationHelper.BuildLogHeader(doc);
                var curatedFindings = CurateFindings(ordered, standardizer, logBuilder);
                var ignoredTextCount = ColorIgnoredTextEntities(doc.Database, curatedFindings);
                if (ignoredTextCount > 0)
                {
                    editor.WriteMessage($"\nMarked {ignoredTextCount} skipped text finding(s) in red.");
                }

                var activeFindings = curatedFindings.Where(finding => !finding.Ignored).ToList();
                var ignoredCount = curatedFindings.Count(finding => finding.Ignored);
                var outsideCount = activeFindings.Count(finding => finding.IsOutsideBuffer);
                editor.WriteMessage(
                    $"\nCounts: text selected={projectedResult.TextSelectedCount}, photo findings loaded={projectedResult.ProjectedPhotoCount}, " +
                    $"photo findings used={projectedResult.PhotoMatchedToTextCount + projectedResult.PhotoOnlyAddedCount} " +
                    $"(matched to text={projectedResult.PhotoMatchedToTextCount}, photo-only={projectedResult.PhotoOnlyAddedCount}, dropped near-text name mismatch={projectedResult.PhotoDroppedNearTextNoNameMatchCount}), " +
                    $"text skipped near photo+name match (<=3m)={projectedResult.TextSkippedNearPhotoCount}, " +
                    $"text near photo but name mismatch (kept)={projectedResult.TextNearPhotoNameMismatchKeptCount}, " +
                    $"text skipped projection={projectedResult.TextSkippedProjectionCount}, " +
                    $"ignored={ignoredCount}, final findings={activeFindings.Count}." +
                    (bufferMode == BufferMode.None ? string.Empty : $" outside-buffer findings={outsideCount}."));
                var photoTextDebugReportPath = WritePhotoTextDebugReport(doc, photoFolder, projectedResult);
                if (!string.IsNullOrWhiteSpace(photoTextDebugReportPath))
                {
                    editor.WriteMessage($"\nPhoto/text debug report written to: {photoTextDebugReportPath}");
                }

                if (activeFindings.Count == 0)
                {
                    editor.WriteMessage("\nNo points inserted (all curated findings were ignored).");
                    return;
                }

                var atsQuarterGenerationAttempted = false;
                var atsQuarterGenerationSucceeded = false;
                var atsGenerationDetail = string.Empty;
                if (quarterLayerSources.Count == 0 &&
                    locationResolver != null &&
                    hasLocationZone)
                {
                    atsQuarterGenerationAttempted = true;
                    atsQuarterGenerationSucceeded = TryGenerateQuarterSourcesViaAtsBuild(
                        doc.Database,
                        editor,
                        locationResolver,
                        activeFindings,
                        locationZone,
                        out atsGenerationDetail);
                    if (atsQuarterGenerationSucceeded)
                    {
                        editor.WriteMessage($"\nQuarter boundary resolver: {atsGenerationDetail}");
                        quarterLayerSources = LoadQuarterLayerSources(doc.Database, locationResolver);
                        if (quarterLayerSources.Count > 0)
                        {
                            WriteQuarterLayerSourceSummary(editor, quarterLayerSources);
                        }
                    }
                }

                var allowSimpleQuarterFallback = IsAffirmativeToggle(Environment.GetEnvironmentVariable(AllowSimpleQuarterFallbackEnvVar));
                if (!allowSimpleQuarterFallback && quarterLayerSources.Count == 0)
                {
                    string reason;
                    if (atsQuarterGenerationAttempted)
                    {
                        reason = string.IsNullOrWhiteSpace(atsGenerationDetail)
                            ? "ATS section build fallback did not produce quarter polygons."
                            : atsGenerationDetail;
                    }
                    else if (!hasLocationZone)
                    {
                        reason = "ATS section build fallback skipped (UTM zone is invalid).";
                    }
                    else if (locationResolver == null)
                    {
                        reason = "ATS section build fallback skipped (section index resolver could not initialize).";
                    }
                    else
                    {
                        reason = "ATS section build fallback skipped (unknown precondition failure).";
                    }

                    var message =
                        "Quarter boundary resolver strict mode: missing quarter source polygons and ATS quarter generation was unavailable.\n" +
                        $"Details: {reason}\n" +
                        "Load AtsBackgroundBuilder.dll (or place it in a probe path), then run again.\n" +
                        $"To force legacy simple fallback, set {AllowSimpleQuarterFallbackEnvVar}=1.";
                    Application.ShowAlertDialog(message);
                    editor.WriteMessage($"\n{message}");
                    return;
                }

                var quarterLayerMatches = new List<QuarterLayerMatch>();
                var quarterLayerSource = string.Empty;
                if (TrySelectQuarterLayerSource(activeFindings, quarterLayerSources, out var selectedQuarterLayerSource, out var matchedFindingCount))
                {
                    quarterLayerMatches = selectedQuarterLayerSource.Matches;
                    quarterLayerSource = selectedQuarterLayerSource.LayerName;
                    editor.WriteMessage(
                        $"\nQuarter boundary resolver: using {quarterLayerMatches.Count} {quarterLayerSource} polygon(s), matched {matchedFindingCount}/{activeFindings.Count} finding(s).");
                }
                else if (TryGetPreferredQuarterLayerSource(quarterLayerSources, out selectedQuarterLayerSource))
                {
                    quarterLayerMatches = selectedQuarterLayerSource.Matches;
                    quarterLayerSource = selectedQuarterLayerSource.LayerName;
                    editor.WriteMessage(
                        $"\nQuarter boundary resolver: using fallback source {quarterLayerMatches.Count} {quarterLayerSource} polygon(s); direct containment matches were not found during source scoring.");
                }

                var duplicateBlockNames = new List<string>();
                if (UsesAreaSpecificBufferPrompts(bufferMode))
                {
                    if (!string.IsNullOrWhiteSpace(proposedBlockName))
                    {
                        duplicateBlockNames.Add(proposedBlockName);
                    }

                    if (!string.IsNullOrWhiteSpace(hundredMeterBlockName))
                    {
                        duplicateBlockNames.Add(hundredMeterBlockName);
                    }
                    
                    if (bufferMode == BufferMode.IncludeBufferAndAll && !string.IsNullOrWhiteSpace(outsideBufferBlockName))
                    {
                        duplicateBlockNames.Add(outsideBufferBlockName);
                    }
                }
                else if (!string.IsNullOrWhiteSpace(sampleBlockName))
                {
                    duplicateBlockNames.Add(sampleBlockName);
                }

                var duplicateNumbers = FindDuplicatePhotoNumbers(doc.Database, duplicateBlockNames, settings.PhotoStartNumber, activeFindings.Count);
                if (duplicateNumbers.Count > 0)
                {
                    var message = $"Duplicate block numbers found: {string.Join(", ", duplicateNumbers)}. Update the photo start # and try again.";
                    Application.ShowAlertDialog(message);
                    editor.WriteMessage($"\n{message}");
                    return;
                }

                var records = new List<PhotoPointRecord>();
                var standardizedRows = new List<PhotoStandardizationRecord>();
                var photoOnlyNumbers = new List<int>();
                var quarterValidationPolygonsByKey =
                    settings.CompleteFromPhotosIncludeQuarterLinework
                        ? new Dictionary<string, IReadOnlyList<Point2d>>(StringComparer.OrdinalIgnoreCase)
                        : null;
                var quarterLayerContainmentMatches = 0;
                var quarterLayerNearestDiagnosticAdds = 0;
                var quarterResolverFallbackAdds = 0;
                var number = settings.PhotoStartNumber;

                using var trInsert = doc.Database.TransactionManager.StartTransaction();
                var space = (BlockTableRecord)trInsert.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForWrite);
                PhotoLayoutHelper.EnsureLayer(doc.Database, settings.PhotoLayer, trInsert);

                foreach (var finding in activeFindings)
                {
                    var area = BufferArea.Outside;
                    string? blockName;
                    if (UsesAreaSpecificBufferPrompts(bufferMode))
                    {
                        area = ClassifyBufferArea(
                            new Point3d(finding.Easting, finding.Northing, 0.0),
                            proposedBoundary,
                            hundredMeterBoundary);
                        blockName = area switch
                        {
                            BufferArea.Proposed => proposedBlockName,
                            BufferArea.HundredMeter => hundredMeterBlockName,
                            BufferArea.Outside when bufferMode == BufferMode.IncludeBufferAndAll => outsideBufferBlockName,
                            _ => null
                        };
                    }
                    else
                    {
                        area = finding.IsOutsideBuffer
                            ? BufferArea.Outside
                            : BufferArea.HundredMeter;
                        blockName = finding.IsOutsideBuffer && bufferMode == BufferMode.IncludeBufferAndAll
                            ? outsideBufferBlockName
                            : sampleBlockName;
                    }

                    if (string.IsNullOrWhiteSpace(blockName))
                    {
                        continue;
                    }

                    var insertPoint = new Point3d(finding.Easting, finding.Northing, 0.0);
                    var blockId = InsertPhotoBlock(doc.Database, space, trInsert, blockName, insertPoint, number, settings.NumberOrder);
                    if (!blockId.IsNull)
                    {
                        var primary = finding.Standardizations.FirstOrDefault();
                        var wildlifeFinding = string.IsNullOrWhiteSpace(primary.StandardDescription)
                            ? finding.OriginalText
                            : primary.StandardDescription;
                        var location = string.Empty;
                        var findingPoint2d = new Point2d(finding.Easting, finding.Northing);
                        if (TryResolveFromQuarterLayer(findingPoint2d, quarterLayerMatches, out var quarterLayerMatch))
                        {
                            quarterLayerContainmentMatches++;
                            if (TryResolveLsdLocationFromQuarterLayer(findingPoint2d, quarterLayerMatch, locationResolver, out var quarterLayerLocation))
                            {
                                location = quarterLayerLocation;
                            }
                            else if (locationResolver != null &&
                                locationResolver.TryResolveLsdMatch(findingPoint2d, out var lsdMatchFromQuarterLayer))
                            {
                                location = lsdMatchFromQuarterLayer.Location;
                            }

                            if (quarterValidationPolygonsByKey != null)
                            {
                                quarterValidationPolygonsByKey[BuildPolygonKey(quarterLayerMatch.Vertices)] = quarterLayerMatch.Vertices;
                            }
                        }
                        else if (locationResolver != null &&
                            locationResolver.TryResolveLsdMatch(findingPoint2d, out var lsdMatch))
                        {
                            location = lsdMatch.Location;
                            if (quarterValidationPolygonsByKey != null &&
                                lsdMatch.QuarterVertices != null &&
                                lsdMatch.QuarterVertices.Count >= 3)
                            {
                                var resolverKey = BuildPolygonKey(lsdMatch.QuarterVertices);
                                if (!quarterValidationPolygonsByKey.ContainsKey(resolverKey))
                                {
                                    quarterValidationPolygonsByKey[resolverKey] = lsdMatch.QuarterVertices;
                                    quarterResolverFallbackAdds++;
                                }
                            }
                        }

                        if (quarterValidationPolygonsByKey != null &&
                            TryResolveNearestQuarterLayerForDiagnostics(findingPoint2d, quarterLayerMatches, 250.0, out var nearestQuarterLayerMatch))
                        {
                            var key = BuildPolygonKey(nearestQuarterLayerMatch.Vertices);
                            if (!quarterValidationPolygonsByKey.ContainsKey(key))
                            {
                                quarterValidationPolygonsByKey[key] = nearestQuarterLayerMatch.Vertices;
                                quarterLayerNearestDiagnosticAdds++;
                            }
                        }

                        var record = new PhotoPointRecord(
                            number,
                            finding.ImagePath,
                            finding.OriginalText,
                            finding.SourceImageName,
                            finding.Latitude,
                            finding.Longitude,
                            finding.Northing,
                            finding.Easting,
                            location,
                            wildlifeFinding,
                            finding.IsOutsideBuffer,
                            area);
                        records.Add(record);

                        if (finding.ImagePath != null && finding.SourceTextId.IsNull)
                        {
                            photoOnlyNumbers.Add(number);
                        }

                        foreach (var standardization in finding.Standardizations)
                        {
                            standardizedRows.Add(new PhotoStandardizationRecord(record, finding.OriginalText, standardization));
                        }

                        number++;
                    }
                }

                trInsert.Commit();

                if (records.Count == 0)
                {
                    editor.WriteMessage("\nNo points inserted.");
                    return;
                }

                if (locationResolver != null)
                {
                    var resolvedCount = records.Count(record => !string.IsNullOrWhiteSpace(record.Location));
                    editor.WriteMessage($"\nATS location match: {resolvedCount}/{records.Count} finding(s) resolved to LSD/section.");
                }

                if (settings.CompleteFromPhotosIncludeQuarterLinework)
                {
                    if (quarterValidationPolygonsByKey == null || quarterValidationPolygonsByKey.Count == 0)
                    {
                        if (quarterLayerMatches.Count == 0)
                        {
                            editor.WriteMessage(
                                "\nL-QUARTER validation linework requested, but no quarter source polygons were found on L-QUARTER or legacy L-QUATER.");
                        }
                        else
                        {
                            editor.WriteMessage("\nL-QUARTER validation linework requested, but no nearby quarter polygons could be associated to findings.");
                        }
                    }
                    else
                    {
                        var drawnQuarterCount = DrawQuarterValidationLinework(doc.Database, quarterValidationPolygonsByKey.Values);
                        editor.WriteMessage(
                            $"\nL-QUARTER validation linework: drew {drawnQuarterCount} quarter polygon(s) " +
                            $"(direct matches={quarterLayerContainmentMatches}, nearest diagnostic adds={quarterLayerNearestDiagnosticAdds}, resolver fallback adds={quarterResolverFallbackAdds}).");
                    }
                }

                var photoLayoutRecords = records
                    .Where(record => !string.IsNullOrWhiteSpace(record.ImagePath))
                    .Select(record => new PhotoLayoutRecord(record.Number, record.ImagePath, false, record.WildlifeFinding))
                    .ToList();
                var report = new List<string>();
                if (photoLayoutRecords.Count > 0
                    && !PhotoLayoutHelper.PlacePhotoGroups(doc.Database, editor, settings, photoLayoutRecords, out report))
                {
                    return;
                }

                var csvPath = PromptForCsvPath();
                if (!string.IsNullOrWhiteSpace(csvPath))
                {
                    WriteCsv(csvPath, standardizedRows);
                    editor.WriteMessage($"\nCSV written to: {csvPath}");
                }

                var tableInsertPoint = PromptForTableLocation(editor);
                if (tableInsertPoint.HasValue)
                {
                    using var trTable = doc.Database.TransactionManager.StartTransaction();
                    var tableSpace = (BlockTableRecord)trTable.GetObject(doc.Database.CurrentSpaceId, OpenMode.ForWrite);
                    PhotoLayoutHelper.EnsureLayer(doc.Database, "CG-NOTES", trTable);
                    CreateTable(
                        tableSpace,
                        trTable,
                        tableInsertPoint.Value,
                        records,
                        tableStyleId,
                        outsideLast: bufferMode == BufferMode.IncludeBufferAndAll,
                        insertProposedHundredSpacer: UsesAreaSpecificBufferPrompts(bufferMode));
                    trTable.Commit();
                }

                var hasNonAutomaticLogEntries = curatedFindings.Any(finding =>
                    finding.Standardizations.Any(standardization =>
                        FindingsStandardizationHelper.ShouldIncludeInLog(standardization.Source)));
                if (hasNonAutomaticLogEntries)
                {
                    var logPath = FindingsStandardizationHelper.WriteLogFile(doc, logBuilder.ToString());
                    if (!string.IsNullOrWhiteSpace(logPath))
                    {
                        editor.WriteMessage($"\nDescription log written to: {logPath}");
                    }
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

                if (photoOnlyNumbers.Count > 0)
                {
                    editor.WriteMessage($"\nPhoto-only finding #s (no matched text): {string.Join(", ", photoOnlyNumbers)}");
                }
                else if (projectedResult.PhotoOnlyAddedCount > 0)
                {
                    editor.WriteMessage(
                        $"\nPhoto-only findings with no matched text: {projectedResult.PhotoOnlyAddedCount}, but none received final #s (all were ignored during curation).");
                }

                editor.WriteMessage($"\nDone. Inserted {records.Count} finding point(s), with {photoLayoutRecords.Count} photo attachment(s).");
                }
                finally
                {
                    ApplyQuarterLineworkVisibilityPreference(doc.Database, includeQuarterLinework);
                }
            }
        }

        private static void ApplyQuarterLineworkVisibilityPreference(Database db, bool includeQuarterLinework)
        {
            if (db == null)
            {
                return;
            }

            try
            {
                using var tr = db.TransactionManager.StartTransaction();
                var layerTable = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);

                var updated = false;
                updated |= TrySetLayerVisibility(layerTable, tr, QuarterValidationLayerName, includeQuarterLinework);
                updated |= TrySetLayerVisibility(layerTable, tr, QuarterLegacySourceLayerName, includeQuarterLinework);

                if (updated)
                {
                    tr.Commit();
                }
            }
            catch
            {
                // Visibility enforcement should never block the command flow.
            }
        }

        private static bool TrySetLayerVisibility(
            LayerTable layerTable,
            Transaction tr,
            string layerName,
            bool isVisible)
        {
            if (layerTable == null || tr == null || string.IsNullOrWhiteSpace(layerName) || !layerTable.Has(layerName))
            {
                return false;
            }

            var layer = (LayerTableRecord)tr.GetObject(layerTable[layerName], OpenMode.ForWrite);
            var shouldBeOff = !isVisible;
            var changed = false;
            if (layer.IsOff != shouldBeOff)
            {
                layer.IsOff = shouldBeOff;
                changed = true;
            }

            if (isVisible && layer.IsFrozen)
            {
                layer.IsFrozen = false;
                changed = true;
            }

            return changed;
        }

        private static string PromptForOrder(Editor editor, string current)
        {
            var options = new PromptKeywordOptions("\nNumbering order [LeftToRight/RightToLeft/SouthToNorth/NorthToSouth/SEtoNW/SWtoNE/NEtoSW/NWtoSE] <LeftToRight>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("LeftToRight");
            options.Keywords.Add("RightToLeft");
            options.Keywords.Add("SouthToNorth");
            options.Keywords.Add("NorthToSouth");
            options.Keywords.Add("SEtoNW");
            options.Keywords.Add("SWtoNE");
            options.Keywords.Add("NEtoSW");
            options.Keywords.Add("NWtoSE");
            options.Keywords.Default = string.IsNullOrWhiteSpace(current) ? "LeftToRight" : current;

            var result = editor.GetKeywords(options);
            return result.Status == PromptStatus.OK ? result.StringResult : NormalizeOrder(current);
        }

        private static List<TextFindingRecord> PromptForFindingTextSelection(Editor editor, Database db)
        {
            var options = new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect finding text to include (TEXT/MTEXT), or Enter for none: "
            };
            var filter = new SelectionFilter(new[]
            {
                new TypedValue((int)DxfCode.Start, "TEXT,MTEXT")
            });
            var result = editor.GetSelection(options, filter);
            if (result.Status != PromptStatus.OK || result.Value == null)
            {
                return new List<TextFindingRecord>();
            }

            var findings = new List<TextFindingRecord>();
            using var tr = db.TransactionManager.StartTransaction();
            foreach (SelectedObject? selected in result.Value)
            {
                if (selected?.ObjectId == null)
                {
                    continue;
                }

                var entity = tr.GetObject(selected.ObjectId, OpenMode.ForRead) as Entity;
                if (entity == null)
                {
                    continue;
                }

                switch (entity)
                {
                    case DBText text when !string.IsNullOrWhiteSpace(text.TextString):
                        findings.Add(new TextFindingRecord(selected.ObjectId, text.TextString.Trim(), text.Position));
                        break;
                    case MText mtext when !string.IsNullOrWhiteSpace(mtext.Text):
                        findings.Add(new TextFindingRecord(selected.ObjectId, mtext.Text.Trim(), mtext.Location));
                        break;
                }
            }

            tr.Commit();
            return findings;
        }

        private static string? PromptForCsvPath()
        {
            var defaultPath = Path.Combine(Path.GetTempPath(), "photo_points.csv");
            var dialog = new SaveFileDialog("Save CSV file as", defaultPath, "csv", "csv", SaveFileDialog.SaveFileDialogFlags.DoNotTransferRemoteFiles);
            return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.Filename : null;
        }

        private static BufferMode ResolveBufferMode(PluginSettings settings)
        {
            if (settings.CompleteFromPhotosIncludeBufferIncludeAll)
            {
                return BufferMode.IncludeBufferAndAll;
            }

            return settings.CompleteFromPhotosIncludeBufferExcludeOutside
                ? BufferMode.IncludeBufferExcludeOutside
                : BufferMode.None;
        }

        private static bool UsesAreaSpecificBufferPrompts(BufferMode bufferMode)
        {
            return bufferMode == BufferMode.IncludeBufferExcludeOutside ||
                   bufferMode == BufferMode.IncludeBufferAndAll;
        }

        private static bool BlockExists(Database db, string blockName)
        {
            using var tr = db.TransactionManager.StartTransaction();
            var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            return blockTable.Has(blockName);
        }

        private static string? PromptForSamplePhotoBlock(Editor editor, Database db, string prompt)
        {
            var result = editor.GetEntity(prompt);
            if (result.Status != PromptStatus.OK)
            {
                return null;
            }

            using var tr = db.TransactionManager.StartTransaction();
            var sampleRef = tr.GetObject(result.ObjectId, OpenMode.ForRead) as BlockReference;
            if (sampleRef == null)
            {
                editor.WriteMessage("\nSelection is not a block reference.");
                return null;
            }

            return BlockSelectionHelper.GetEffectiveName(sampleRef, tr);
        }

        private static BufferBoundary? PromptForBuffer(Editor editor, Database db, string promptMessage)
        {
            var options = new PromptEntityOptions(promptMessage);
            options.SetRejectMessage("\nOnly closed LWPOLYLINE/POLYLINE boundaries are supported.");
            options.AddAllowedClass(typeof(Polyline), false);
            var result = editor.GetEntity(options);
            if (result.Status != PromptStatus.OK)
            {
                return null;
            }

            using var tr = db.TransactionManager.StartTransaction();
            if (tr.GetObject(result.ObjectId, OpenMode.ForRead) is not Polyline boundary)
            {
                editor.WriteMessage("\nSelection is not a polyline.");
                return null;
            }

            if (!boundary.Closed)
            {
                editor.WriteMessage("\nBoundary polyline must be closed.");
                return null;
            }

            if (boundary.NumberOfVertices < 3)
            {
                editor.WriteMessage("\nBoundary polyline must have at least 3 vertices.");
                return null;
            }

            var vertices = BoundarySamplingHelper.BuildBoundaryVertices(
                boundary,
                BoundarySamplingStepMeters,
                BoundarySampleMergeTolerance);
            if (vertices.Count < 3)
            {
                editor.WriteMessage("\nBoundary polyline could not be sampled.");
                return null;
            }

            return new BufferBoundary(vertices);
        }

        private static bool IsOutsideBuffer(Point3d point, BufferBoundary? boundary, BufferMode mode)
        {
            if (mode == BufferMode.None || boundary == null)
            {
                return false;
            }

            return !boundary.IsInside(point);
        }

        private static BufferArea ClassifyBufferArea(
            Point3d point,
            BufferBoundary? proposedBoundary,
            BufferBoundary? hundredMeterBoundary)
        {
            if (proposedBoundary != null && proposedBoundary.IsInside(point))
            {
                return BufferArea.Proposed;
            }

            if (hundredMeterBoundary != null && hundredMeterBoundary.IsInside(point))
            {
                return BufferArea.HundredMeter;
            }

            return BufferArea.Outside;
        }

        private static IReadOnlyList<FindingProjectedRecord> ApplyBufferFiltering(
            IReadOnlyList<FindingProjectedRecord> findings,
            BufferBoundary? boundary,
            BufferMode mode)
        {
            if (mode == BufferMode.None || boundary == null)
            {
                return findings;
            }

            if (mode != BufferMode.IncludeBufferExcludeOutside)
            {
                return findings;
            }

            return findings
                .Where(finding => !IsOutsideBuffer(new Point3d(finding.Easting, finding.Northing, 0.0), boundary, mode))
                .ToList();
        }

        private static Point3d? PromptForTableLocation(Editor editor)
        {
            var result = editor.GetPoint("\nPick insertion point for summary table: ");
            return result.Status == PromptStatus.OK ? result.Value : (Point3d?)null;
        }

        private static ObjectId EnsureTableStyle(Database db, string styleName)
        {
            using var tr = db.TransactionManager.StartTransaction();
            var tableStyleDict = (DBDictionary)tr.GetObject(db.TableStyleDictionaryId, OpenMode.ForRead);

            if (tableStyleDict.Contains(styleName))
            {
                var existingId = tableStyleDict.GetAt(styleName);
                var existingStyle = (TableStyle)tr.GetObject(existingId, OpenMode.ForWrite);

                existingStyle.IsTitleSuppressed = true;
                existingStyle.IsHeaderSuppressed = true;

                tr.Commit();
                return existingId;
            }

            tableStyleDict.UpgradeOpen();

            var tableStyle = new TableStyle
            {
                Name = styleName
            };

            tableStyle.IsTitleSuppressed = true;
            tableStyle.IsHeaderSuppressed = true;

            var styleId = tableStyleDict.SetAt(styleName, tableStyle);
            tr.AddNewlyCreatedDBObject(tableStyle, true);
            tr.Commit();

            return styleId;
        }

        private static void CreateTable(
            BlockTableRecord space,
            Transaction tr,
            Point3d insertPoint,
            IReadOnlyList<PhotoPointRecord> records,
            ObjectId tableStyleId,
            bool outsideLast,
            bool insertProposedHundredSpacer)
        {
            const int columnCount = 5;
            const int maxRowsPerSection = 40;

            var sectionSizes = BuildBalancedTableSectionSizes(records, maxRowsPerSection, outsideLast);
            var separatorRowCount = Math.Max(0, sectionSizes.Count - 1);
            var proposedHundredSpacerCount = insertProposedHundredSpacer
                ? CountProposedHundredTransitions(records)
                : 0;
            var totalRows = 1 + records.Count + separatorRowCount + proposedHundredSpacerCount;

            var table = new Table
            {
                TableStyle = tableStyleId,
                Position = insertPoint,
                Layer = "CG-NOTES"
            };

            table.SetSize(totalRows, columnCount);
            table.SetRowHeight(25.0);
            table.SetColumnWidth(150.0);
            table.Columns[0].Width = 150.0;
            table.Columns[1].Width = 250.0;
            table.Columns[2].Width = 150.0;
            table.Columns[3].Width = 125.0;
            table.Columns[4].Width = 125.0;

            table.Cells[0, 0].TextString = "#";
            table.Cells[0, 1].TextString = "WILDLIFE FINDINGS";
            table.Cells[0, 2].TextString = "LOCATION";
            table.Cells[0, 3].TextString = "LAT";
            table.Cells[0, 4].TextString = "LONG";

            for (var row = 0; row < totalRows; row++)
            {
                for (var col = 0; col < columnCount; col++)
                {
                    table.Cells[row, col].TextHeight = 10.0;
                }
            }

            var recordIndex = 0;
            var tableRow = 1;
            for (var sectionIndex = 0; sectionIndex < sectionSizes.Count; sectionIndex++)
            {
                var sectionSize = sectionSizes[sectionIndex];
                for (var i = 0; i < sectionSize; i++)
                {
                    var record = records[recordIndex++];
                    table.Cells[tableRow, 0].TextString = record.Number.ToString(CultureInfo.InvariantCulture);
                    table.Cells[tableRow, 1].TextString = record.WildlifeFinding ?? string.Empty;
                    table.Cells[tableRow, 2].TextString = record.Location ?? string.Empty;
                    table.Cells[tableRow, 3].TextString = record.Latitude.ToString("F6", CultureInfo.InvariantCulture);
                    table.Cells[tableRow, 4].TextString = record.Longitude.ToString("F6", CultureInfo.InvariantCulture);
                    if (record.IsOutsideBuffer)
                    {
                        ApplyOutsideBufferCellColor(table, tableRow, columnCount);
                    }

                    tableRow++;

                    if (insertProposedHundredSpacer &&
                        recordIndex < records.Count &&
                        ShouldInsertProposedHundredSpacer(records[recordIndex - 1], records[recordIndex]))
                    {
                        ConfigureGroupSpacerRow(table, tableRow, columnCount);
                        tableRow++;
                    }
                }

                if (sectionIndex < sectionSizes.Count - 1)
                {
                    ConfigureSeparatorRow(table, tableRow, columnCount);
                    tableRow++;
                }
            }

            table.GenerateLayout();
            space.AppendEntity(table);
            tr.AddNewlyCreatedDBObject(table, true);
        }

        private static int CountProposedHundredTransitions(IReadOnlyList<PhotoPointRecord> records)
        {
            if (records == null || records.Count < 2)
            {
                return 0;
            }

            var count = 0;
            for (var i = 1; i < records.Count; i++)
            {
                if (ShouldInsertProposedHundredSpacer(records[i - 1], records[i]))
                {
                    count++;
                }
            }

            return count;
        }

        private static bool ShouldInsertProposedHundredSpacer(PhotoPointRecord previous, PhotoPointRecord current)
        {
            return previous.Area == BufferArea.Proposed &&
                   current.Area == BufferArea.HundredMeter;
        }

        private static List<int> BuildBalancedTableSectionSizes(
            IReadOnlyList<PhotoPointRecord> records,
            int maxRowsPerSection,
            bool splitByBufferGroups)
        {
            if (records.Count <= 0)
            {
                return new List<int> { 0 };
            }

            if (!splitByBufferGroups || records.Count <= 1)
            {
                return BuildBalancedSectionSizes(records.Count, maxRowsPerSection);
            }

            var result = new List<int>();
            var start = 0;
            while (start < records.Count)
            {
                var groupOutside = records[start].IsOutsideBuffer;
                var end = start + 1;
                while (end < records.Count && records[end].IsOutsideBuffer == groupOutside)
                {
                    end++;
                }

                var groupSize = end - start;
                result.AddRange(BuildBalancedSectionSizes(groupSize, maxRowsPerSection));
                start = end;
            }

            if (result.Count > 0)
            {
                return result;
            }

            return BuildBalancedSectionSizes(records.Count, maxRowsPerSection);
        }

        private static List<int> BuildBalancedSectionSizes(int totalRecords, int maxRowsPerSection)
        {
            if (totalRecords <= 0)
            {
                return new List<int> { 0 };
            }

            if (totalRecords <= maxRowsPerSection)
            {
                return new List<int> { totalRecords };
            }

            var minSectionCount = (int)Math.Ceiling(totalRecords / (double)maxRowsPerSection);
            var minRowsPerSection = Math.Max(1, maxRowsPerSection / 2);
            var sectionCount = minSectionCount;

            // Prefer an exactly equal split when possible (e.g., 150 -> 30/30/30/30/30),
            // but keep sections reasonably sized and never over the max.
            for (var candidate = minSectionCount; candidate <= totalRecords; candidate++)
            {
                var averageRows = totalRecords / (double)candidate;
                if (averageRows < minRowsPerSection)
                {
                    break;
                }

                if (totalRecords % candidate == 0)
                {
                    sectionCount = candidate;
                    break;
                }
            }

            var baseRows = totalRecords / sectionCount;
            var remainder = totalRecords % sectionCount;
            var sections = new List<int>(sectionCount);
            for (var sectionIndex = 0; sectionIndex < sectionCount; sectionIndex++)
            {
                var rows = baseRows;
                if (remainder > 0 && sectionIndex >= sectionCount - remainder)
                {
                    rows++;
                }

                sections.Add(rows);
            }

            return sections;
        }

        private static void ConfigureSeparatorRow(Table table, int rowIndex, int columnCount)
        {
            const double separatorRowHeight = 150.0;
            table.Rows[rowIndex].Height = separatorRowHeight;

            for (var col = 0; col < columnCount; col++)
            {
                var cell = table.Cells[rowIndex, col];
                cell.TextString = string.Empty;
                cell.Borders.Left.IsVisible = false;
                cell.Borders.Right.IsVisible = false;
                cell.Borders.Vertical.IsVisible = false;
                cell.Borders.Top.IsVisible = true;
                cell.Borders.Bottom.IsVisible = true;
                cell.Borders.Horizontal.IsVisible = true;
            }
        }

        private static void ApplyOutsideBufferCellColor(Table table, int rowIndex, int columnCount)
        {
            var color = Color.FromColorIndex(ColorMethod.ByAci, OutsideBufferTableColor);
            for (var col = 0; col < columnCount; col++)
            {
                var cell = table.Cells[rowIndex, col];
                cell.BackgroundColor = color;
            }
        }

        private static void ConfigureGroupSpacerRow(Table table, int rowIndex, int columnCount)
        {
            table.Rows[rowIndex].Height = 125.0;
            for (var col = 0; col < columnCount; col++)
            {
                var cell = table.Cells[rowIndex, col];
                cell.TextString = string.Empty;
                cell.Borders.Left.IsVisible = false;
                cell.Borders.Right.IsVisible = false;
                cell.Borders.Vertical.IsVisible = false;
                cell.Borders.Top.IsVisible = true;
                cell.Borders.Bottom.IsVisible = true;
                cell.Borders.Horizontal.IsVisible = true;
            }
        }

        private static int DrawQuarterValidationLinework(
            Database db,
            IEnumerable<IReadOnlyList<Point2d>> quarterPolygons)
        {
            var polygons = quarterPolygons?
                .Where(polygon => polygon != null && polygon.Count >= 3)
                .ToList() ?? new List<IReadOnlyList<Point2d>>();
            if (polygons.Count == 0)
            {
                return 0;
            }

            using var tr = db.TransactionManager.StartTransaction();
            var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForWrite);
            PhotoLayoutHelper.EnsureLayer(
                db,
                QuarterValidationLayerName,
                Color.FromColorIndex(ColorMethod.ByAci, QuarterValidationLayerColor),
                tr);

            var drawn = 0;
            foreach (var polygon in polygons)
            {
                var polyline = new Polyline(polygon.Count)
                {
                    Closed = true,
                    Layer = QuarterValidationLayerName
                };

                for (var vertexIndex = 0; vertexIndex < polygon.Count; vertexIndex++)
                {
                    polyline.AddVertexAt(vertexIndex, polygon[vertexIndex], 0.0, 0.0, 0.0);
                }

                space.AppendEntity(polyline);
                tr.AddNewlyCreatedDBObject(polyline, true);
                drawn++;
            }

            tr.Commit();
            return drawn;
        }

        private static void WriteQuarterLayerSourceSummary(Editor editor, IReadOnlyList<QuarterLayerSource> quarterLayerSources)
        {
            if (editor == null || quarterLayerSources == null || quarterLayerSources.Count == 0)
            {
                return;
            }

            var summary = string.Join(
                ", ",
                quarterLayerSources.Select(source => $"{source.LayerName}={source.Matches.Count}"));
            editor.WriteMessage($"\nQuarter boundary resolver: found polygon sources [{summary}].");

            var hasCanonicalSource = quarterLayerSources.Any(source =>
                string.Equals(source.LayerName, QuarterValidationLayerName, StringComparison.OrdinalIgnoreCase));
            var hasLegacySource = quarterLayerSources.Any(source =>
                string.Equals(source.LayerName, QuarterLegacySourceLayerName, StringComparison.OrdinalIgnoreCase));
            if (!hasCanonicalSource && hasLegacySource)
            {
                editor.WriteMessage(
                    "\nQuarter boundary resolver: using legacy ATS source layer L-QUATER (no L-QUARTER polygons found).");
            }
        }

        private static bool TryGenerateQuarterSourcesViaAtsBuild(
            Database db,
            Editor editor,
            AtsQuarterLocationResolver locationResolver,
            IReadOnlyList<FindingCuratedRecord> activeFindings,
            int locationZone,
            out string detail)
        {
            detail = string.Empty;
            if (db == null || editor == null || locationResolver == null || activeFindings == null || activeFindings.Count == 0)
            {
                return false;
            }

            var sectionRequests = new List<(string Section, string Township, string Range, string Meridian)>();
            var sectionKeyIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < activeFindings.Count; i++)
            {
                var finding = activeFindings[i];
                var point = new Point2d(finding.Easting, finding.Northing);
                if (!locationResolver.TryResolveQuarterMatch(point, out var match))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(match.Section) ||
                    string.IsNullOrWhiteSpace(match.Township) ||
                    string.IsNullOrWhiteSpace(match.Range) ||
                    string.IsNullOrWhiteSpace(match.Meridian))
                {
                    continue;
                }

                var sectionKeyId = $"{match.Section}|{match.Township}|{match.Range}|{match.Meridian}";
                if (!sectionKeyIds.Add(sectionKeyId))
                {
                    continue;
                }

                sectionRequests.Add((match.Section, match.Township, match.Range, match.Meridian));
            }

            if (sectionRequests.Count == 0)
            {
                detail = "ATS section build fallback skipped (could not resolve section keys from active findings).";
                return false;
            }

            if (!TryLoadAtsBackgroundBuilderAssembly(db, out var atsAssembly, out var assemblySource))
            {
                detail = "ATS section build fallback skipped (AtsBackgroundBuilder.dll not found in loaded assemblies or probe paths).";
                return false;
            }

            var assemblyVersion = atsAssembly.GetName().Version?.ToString() ?? "unknown";
            editor.WriteMessage(
                $"\nATS section build fallback: using assembly \"{atsAssembly.GetName().Name}\" version {assemblyVersion} from {assemblySource}.");

            var pluginType = atsAssembly.GetType("AtsBackgroundBuilder.Plugin", throwOnError: false);
            var loggerType = atsAssembly.GetType("AtsBackgroundBuilder.Logger", throwOnError: false);
            var configType = atsAssembly.GetType("AtsBackgroundBuilder.Core.Config", throwOnError: false);
            var atsBuildInputType = atsAssembly.GetType("AtsBackgroundBuilder.Core.AtsBuildInput", throwOnError: false);
            var sectionRequestType = atsAssembly.GetType("AtsBackgroundBuilder.SectionRequest", throwOnError: false);
            var quarterSelectionType = atsAssembly.GetType("AtsBackgroundBuilder.QuarterSelection", throwOnError: false);
            var sectionKeyType = atsAssembly.GetType("AtsBackgroundBuilder.Sections.SectionKey", throwOnError: false)
                ?? atsAssembly.GetType("AtsBackgroundBuilder.SectionKey", throwOnError: false);
            if (pluginType == null ||
                loggerType == null ||
                configType == null ||
                atsBuildInputType == null ||
                sectionRequestType == null ||
                quarterSelectionType == null ||
                sectionKeyType == null)
            {
                detail = "ATS section build fallback skipped (required ATS types were not found).";
                return false;
            }

            var drawSectionsMethod = pluginType
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(method => string.Equals(method.Name, "DrawSectionsFromRequests", StringComparison.Ordinal) &&
                                          method.GetParameters().Length == 8);
            var cleanupMethod = pluginType
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(method => string.Equals(method.Name, "CleanupAfterBuild", StringComparison.Ordinal) &&
                                          method.GetParameters().Length == 5);
            if (drawSectionsMethod == null || cleanupMethod == null)
            {
                detail = "ATS section build fallback skipped (required ATS methods were not found).";
                return false;
            }

            object? logger = null;
            try
            {
                logger = Activator.CreateInstance(loggerType);
                var config = Activator.CreateInstance(configType);
                var input = Activator.CreateInstance(atsBuildInputType);
                if (logger == null || config == null || input == null)
                {
                    detail = "ATS section build fallback skipped (failed to instantiate ATS runtime objects).";
                    return false;
                }

                SetReflectionProperty(config, "UseSectionIndex", true);
                SetReflectionProperty(config, "SectionIndexFolder", ResolveSectionIndexFolderForAtsBuild(db));

                var quarterAll = Enum.Parse(quarterSelectionType, "All", ignoreCase: true);
                var requestListType = typeof(List<>).MakeGenericType(sectionRequestType);
                var requestList = Activator.CreateInstance(requestListType);
                var addRequest = requestListType.GetMethod("Add");
                if (requestList == null || addRequest == null)
                {
                    detail = "ATS section build fallback skipped (failed to create ATS section request list).";
                    return false;
                }

                var addedRequestCount = 0;
                for (var i = 0; i < sectionRequests.Count; i++)
                {
                    var section = sectionRequests[i];
                    var sectionKey = Activator.CreateInstance(
                        sectionKeyType,
                        locationZone,
                        section.Section,
                        section.Township,
                        section.Range,
                        section.Meridian);
                    if (sectionKey == null)
                    {
                        continue;
                    }

                    var request = Activator.CreateInstance(sectionRequestType, quarterAll, sectionKey, "AUTO");
                    if (request == null)
                    {
                        continue;
                    }

                    addRequest.Invoke(requestList, new[] { request });
                    addedRequestCount++;
                }

                if (addedRequestCount == 0)
                {
                    detail = "ATS section build fallback skipped (no ATS section requests were created).";
                    return false;
                }

                var sectionDrawResult = drawSectionsMethod.Invoke(
                    null,
                    new object[] { editor, db, requestList, config, logger, false, true, false });
                if (sectionDrawResult == null)
                {
                    detail = "ATS section build fallback skipped (ATS section build returned no result).";
                    return false;
                }

                SetReflectionProperty(input, "IncludeAtsFabric", false);
                SetReflectionProperty(input, "AllowMultiQuarterDispositions", true);
                SetReflectionProperty(input, "IncludeDispositionLinework", false);
                SetReflectionProperty(input, "IncludeDispositionLabels", false);
                SetReflectionProperty(input, "CurrentClient", "WLS");
                SetReflectionProperty(input, "Zone", locationZone);

                cleanupMethod.Invoke(null, new object[] { db, sectionDrawResult, new List<ObjectId>(), input, logger });
                detail =
                    $"ATS section build generated L-QUATER definitions for {addedRequestCount} section(s), then removed temporary build linework ({assemblySource}).";
                return true;
            }
            catch (TargetInvocationException ex)
            {
                detail = $"ATS section build fallback failed: {ex.InnerException?.Message ?? ex.Message}";
                return false;
            }
            catch (System.Exception ex)
            {
                detail = $"ATS section build fallback failed: {ex.Message}";
                return false;
            }
            finally
            {
                if (logger is IDisposable disposableLogger)
                {
                    disposableLogger.Dispose();
                }
            }
        }

        private static bool TryLoadAtsBackgroundBuilderAssembly(Database db, out Assembly atsAssembly, out string source)
        {
            atsAssembly = AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(assembly =>
                    string.Equals(assembly.GetName().Name, "AtsBackgroundBuilder", StringComparison.OrdinalIgnoreCase));
            if (atsAssembly != null)
            {
                source = string.IsNullOrWhiteSpace(atsAssembly.Location) ? "already loaded assembly" : atsAssembly.Location;
                return true;
            }

            var candidates = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            static void AddCandidate(
                List<string> target,
                HashSet<string> seenPaths,
                string? baseFolder,
                string fileName = "AtsBackgroundBuilder.dll")
            {
                if (string.IsNullOrWhiteSpace(baseFolder))
                {
                    return;
                }

                try
                {
                    var fullPath = Path.GetFullPath(Path.Combine(baseFolder, fileName));
                    if (seenPaths.Add(fullPath))
                    {
                        target.Add(fullPath);
                    }
                }
                catch
                {
                    // Ignore invalid probe paths.
                }
            }

            var assemblyDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            var probe = assemblyDir;
            for (var i = 0; i < 8 && !string.IsNullOrWhiteSpace(probe); i++)
            {
                AddCandidate(candidates, seen, probe);
                AddCandidate(candidates, seen, Path.Combine(probe, "build", "net8.0-windows"));
                probe = Path.GetDirectoryName(probe);
            }

            AddCandidate(candidates, seen, AppContext.BaseDirectory);
            AddCandidate(candidates, seen, Environment.CurrentDirectory);

            var drawingFolder = string.Empty;
            try
            {
                drawingFolder = Path.GetDirectoryName(db?.Filename ?? string.Empty) ?? string.Empty;
            }
            catch
            {
                drawingFolder = string.Empty;
            }

            AddCandidate(candidates, seen, drawingFolder);

            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                if (!File.Exists(candidate))
                {
                    continue;
                }

                try
                {
                    atsAssembly = Assembly.LoadFrom(candidate);
                    source = candidate;
                    return true;
                }
                catch
                {
                    // Keep probing.
                }
            }

            source = string.Empty;
            return false;
        }

        private static string ResolveSectionIndexFolderForAtsBuild(Database db)
        {
            var candidates = new[]
            {
                Environment.GetEnvironmentVariable("WLS_SECTION_INDEX_FOLDER"),
                Environment.GetEnvironmentVariable("ATSBUILD_SECTION_INDEX_FOLDER"),
                Environment.GetEnvironmentVariable("ATS_SECTION_INDEX_FOLDER"),
                SafeGetDirectoryName(db?.Filename),
                Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location),
                Environment.CurrentDirectory,
                DefaultAtsSectionIndexFolder
            };

            for (var i = 0; i < candidates.Length; i++)
            {
                var candidate = candidates[i];
                if (string.IsNullOrWhiteSpace(candidate))
                {
                    continue;
                }

                if (Directory.Exists(candidate))
                {
                    return candidate;
                }
            }

            return DefaultAtsSectionIndexFolder;
        }

        private static string SafeGetDirectoryName(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetDirectoryName(path) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool IsAffirmativeToggle(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                return false;
            }

            var normalized = raw.Trim();
            return string.Equals(normalized, "1", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "true", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "yes", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "on", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "y", StringComparison.OrdinalIgnoreCase);
        }

        private static bool SetReflectionProperty(object target, string propertyName, object value)
        {
            if (target == null || string.IsNullOrWhiteSpace(propertyName))
            {
                return false;
            }

            var property = target
                .GetType()
                .GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance);
            if (property == null || !property.CanWrite)
            {
                return false;
            }

            try
            {
                var valueType = value?.GetType();
                if (valueType == null || property.PropertyType.IsAssignableFrom(valueType))
                {
                    property.SetValue(target, value);
                    return true;
                }

                var converted = Convert.ChangeType(value, property.PropertyType, CultureInfo.InvariantCulture);
                property.SetValue(target, converted);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static List<QuarterLayerSource> LoadQuarterLayerSources(
            Database db,
            AtsQuarterLocationResolver? locationResolver)
        {
            var canonicalMatches = new List<QuarterLayerMatch>();
            var legacyMatches = new List<QuarterLayerMatch>();

            using var tr = db.TransactionManager.StartTransaction();
            var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (!blockTable.Has(BlockTableRecord.ModelSpace))
            {
                return new List<QuarterLayerSource>();
            }

            var modelSpace = (BlockTableRecord)tr.GetObject(blockTable[BlockTableRecord.ModelSpace], OpenMode.ForRead);
            foreach (ObjectId entityId in modelSpace)
            {
                if (tr.GetObject(entityId, OpenMode.ForRead, false) is not Entity entity)
                {
                    continue;
                }

                var layer = entity.Layer ?? string.Empty;
                var isCanonicalLayer = string.Equals(layer, QuarterValidationLayerName, StringComparison.OrdinalIgnoreCase);
                var isLegacyLayer = string.Equals(layer, QuarterLegacySourceLayerName, StringComparison.OrdinalIgnoreCase);
                if (!isCanonicalLayer && !isLegacyLayer)
                {
                    continue;
                }

                if (entity is not Polyline polyline || !polyline.Closed || polyline.NumberOfVertices < 3)
                {
                    continue;
                }

                var vertices = new List<Point2d>(polyline.NumberOfVertices);
                for (var vertexIndex = 0; vertexIndex < polyline.NumberOfVertices; vertexIndex++)
                {
                    vertices.Add(polyline.GetPoint2dAt(vertexIndex));
                }

                var representativePoint = BuildRepresentativePoint(vertices);
                var quarterToken = string.Empty;
                var section = string.Empty;
                var township = string.Empty;
                var range = string.Empty;
                var meridian = string.Empty;
                if (locationResolver != null &&
                    locationResolver.TryResolveQuarterMatch(representativePoint, out var quarterMatch))
                {
                    quarterToken = quarterMatch.QuarterToken;
                    section = quarterMatch.Section;
                    township = quarterMatch.Township;
                    range = quarterMatch.Range;
                    meridian = quarterMatch.Meridian;
                }

                var match = new QuarterLayerMatch(vertices, quarterToken, section, township, range, meridian);
                if (isCanonicalLayer)
                {
                    canonicalMatches.Add(match);
                }
                else
                {
                    legacyMatches.Add(match);
                }
            }

            tr.Commit();
            var sources = new List<QuarterLayerSource>();
            if (canonicalMatches.Count > 0)
            {
                sources.Add(new QuarterLayerSource(QuarterValidationLayerName, canonicalMatches));
            }

            if (legacyMatches.Count > 0)
            {
                sources.Add(new QuarterLayerSource(QuarterLegacySourceLayerName, legacyMatches));
            }

            return sources;
        }

        private static int GetQuarterSourcePriority(string layerName)
        {
            if (string.Equals(layerName, QuarterLegacySourceLayerName, StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }

            if (string.Equals(layerName, QuarterValidationLayerName, StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            return 0;
        }

        private static bool TrySelectQuarterLayerSource(
            IReadOnlyList<FindingCuratedRecord> findings,
            IReadOnlyList<QuarterLayerSource> sources,
            out QuarterLayerSource selected,
            out int matchedFindingCount)
        {
            selected = default;
            matchedFindingCount = 0;
            if (findings == null || findings.Count == 0 || sources == null || sources.Count == 0)
            {
                return false;
            }

            var found = false;
            var bestScore = int.MinValue;
            var bestPolygonCount = int.MinValue;
            var bestLayerPriority = int.MinValue;
            foreach (var source in sources)
            {
                if (source == null || source.Matches == null || source.Matches.Count == 0)
                {
                    continue;
                }

                var hitCount = 0;
                for (var i = 0; i < findings.Count; i++)
                {
                    var point = new Point2d(findings[i].Easting, findings[i].Northing);
                    if (TryResolveFromQuarterLayer(point, source.Matches, out _))
                    {
                        hitCount++;
                    }
                }

                var polygonCount = source.Matches.Count;
                var layerPriority = GetQuarterSourcePriority(source.LayerName);
                if (!found ||
                    hitCount > bestScore ||
                    (hitCount == bestScore && polygonCount > bestPolygonCount) ||
                    (hitCount == bestScore && polygonCount == bestPolygonCount && layerPriority > bestLayerPriority))
                {
                    found = true;
                    bestScore = hitCount;
                    bestPolygonCount = polygonCount;
                    bestLayerPriority = layerPriority;
                    selected = source;
                    matchedFindingCount = hitCount;
                }
            }

            return found;
        }

        private static bool TryGetPreferredQuarterLayerSource(
            IReadOnlyList<QuarterLayerSource> sources,
            out QuarterLayerSource selected)
        {
            selected = default;
            if (sources == null || sources.Count == 0)
            {
                return false;
            }

            QuarterLayerSource? best = null;
            var bestPriority = int.MinValue;
            var bestPolygonCount = int.MinValue;
            foreach (var source in sources)
            {
                if (source == null || source.Matches == null || source.Matches.Count == 0)
                {
                    continue;
                }

                var layerPriority = GetQuarterSourcePriority(source.LayerName);
                if (best == null ||
                    layerPriority > bestPriority ||
                    (layerPriority == bestPriority && source.Matches.Count > bestPolygonCount))
                {
                    best = source;
                    bestPriority = layerPriority;
                    bestPolygonCount = source.Matches.Count;
                }
            }

            if (best == null)
            {
                return false;
            }

            selected = best;
            return true;
        }

        private static Point2d BuildRepresentativePoint(IReadOnlyList<Point2d> vertices)
        {
            if (vertices == null || vertices.Count == 0)
            {
                return Point2d.Origin;
            }

            var xSum = 0.0;
            var ySum = 0.0;
            for (var i = 0; i < vertices.Count; i++)
            {
                xSum += vertices[i].X;
                ySum += vertices[i].Y;
            }

            return new Point2d(xSum / vertices.Count, ySum / vertices.Count);
        }

        private static bool TryResolveFromQuarterLayer(
            Point2d point,
            IReadOnlyList<QuarterLayerMatch> quarterLayerMatches,
            out QuarterLayerMatch match)
        {
            match = default;
            if (quarterLayerMatches == null || quarterLayerMatches.Count == 0)
            {
                return false;
            }

            QuarterLayerMatch? best = null;
            var bestScore = double.NegativeInfinity;
            foreach (var candidate in quarterLayerMatches)
            {
                if (!IsPointInsidePolygon2d(candidate.Vertices, point, BoundaryEdgeTolerance))
                {
                    continue;
                }

                var score = DistanceSqToBoundary2d(candidate.Vertices, point);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            if (best == null)
            {
                const double nearMissToleranceMeters = 2.0;
                var nearMissToleranceSq = nearMissToleranceMeters * nearMissToleranceMeters;
                var nearestDistanceSq = double.MaxValue;
                foreach (var candidate in quarterLayerMatches)
                {
                    var distanceSq = DistanceSqToBoundary2d(candidate.Vertices, point);
                    if (distanceSq < nearestDistanceSq)
                    {
                        nearestDistanceSq = distanceSq;
                        best = candidate;
                    }
                }

                if (best == null || nearestDistanceSq > nearMissToleranceSq)
                {
                    return false;
                }
            }

            match = best;
            return true;
        }

        private static bool TryResolveNearestQuarterLayerForDiagnostics(
            Point2d point,
            IReadOnlyList<QuarterLayerMatch> quarterLayerMatches,
            double maxDistanceMeters,
            out QuarterLayerMatch match)
        {
            match = default;
            if (quarterLayerMatches == null || quarterLayerMatches.Count == 0 || maxDistanceMeters <= 0.0)
            {
                return false;
            }

            var maxDistanceSq = maxDistanceMeters * maxDistanceMeters;
            QuarterLayerMatch? nearest = null;
            var nearestDistanceSq = double.MaxValue;
            foreach (var candidate in quarterLayerMatches)
            {
                var distanceSq = DistanceSqToBoundary2d(candidate.Vertices, point);
                if (distanceSq < nearestDistanceSq)
                {
                    nearestDistanceSq = distanceSq;
                    nearest = candidate;
                }
            }

            if (nearest == null || nearestDistanceSq > maxDistanceSq)
            {
                return false;
            }

            match = nearest;
            return true;
        }

        private static bool TryResolveLsdLocationFromQuarterLayer(
            Point2d point,
            QuarterLayerMatch match,
            AtsQuarterLocationResolver? locationResolver,
            out string location)
        {
            location = string.Empty;
            if (match == null)
            {
                return false;
            }

            var quarterToken = match.QuarterToken ?? string.Empty;
            var section = match.Section ?? string.Empty;
            var township = match.Township ?? string.Empty;
            var range = match.Range ?? string.Empty;
            var meridian = match.Meridian ?? string.Empty;
            if (locationResolver != null &&
                locationResolver.TryResolveQuarterMatch(point, out var quarterMatchAtPoint))
            {
                if (string.IsNullOrWhiteSpace(quarterToken))
                {
                    quarterToken = quarterMatchAtPoint.QuarterToken;
                }

                if (string.IsNullOrWhiteSpace(section))
                {
                    section = quarterMatchAtPoint.Section;
                }

                if (string.IsNullOrWhiteSpace(township))
                {
                    township = quarterMatchAtPoint.Township;
                }

                if (string.IsNullOrWhiteSpace(range))
                {
                    range = quarterMatchAtPoint.Range;
                }

                if (string.IsNullOrWhiteSpace(meridian))
                {
                    meridian = quarterMatchAtPoint.Meridian;
                }
            }

            if (string.IsNullOrWhiteSpace(quarterToken) ||
                string.IsNullOrWhiteSpace(section) ||
                string.IsNullOrWhiteSpace(township) ||
                string.IsNullOrWhiteSpace(range) ||
                string.IsNullOrWhiteSpace(meridian))
            {
                return false;
            }

            if (!TryResolveQuarterLsdRowCol(point, match.Vertices, quarterToken, out var row, out var col))
            {
                return false;
            }

            var lsd = LsdNumberingHelper.GetLsdNumber(row, col);
            location = $"{lsd}-{section}-{township}-{range}-W{meridian}";
            return true;
        }

        private static bool TryResolveQuarterLsdRowCol(
            Point2d point,
            IReadOnlyList<Point2d> quarterVertices,
            string quarterToken,
            out int row,
            out int col)
        {
            row = 0;
            col = 0;
            if (quarterVertices == null || quarterVertices.Count < 3)
            {
                return false;
            }

            if (!TryBuildPolygonFrame(quarterVertices, out var southWest, out var eastUnit, out var northUnit, out var width, out var height))
            {
                return false;
            }

            var vector = point - southWest;
            var u = vector.DotProduct(eastUnit) / width;
            var t = vector.DotProduct(northUnit) / height;
            u = Math.Max(0.0, Math.Min(0.999999, u));
            t = Math.Max(0.0, Math.Min(0.999999, t));

            var localCol = (int)Math.Floor(u * 2.0);
            var localRow = (int)Math.Floor(t * 2.0);
            localCol = Math.Max(0, Math.Min(1, localCol));
            localRow = Math.Max(0, Math.Min(1, localRow));

            if (!TryGetQuarterOffsets(quarterToken, out var rowOffset, out var colOffset))
            {
                return false;
            }

            row = rowOffset + localRow;
            col = colOffset + localCol;
            return true;
        }

        private static bool TryBuildPolygonFrame(
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

            var maxLen = 0.0;
            Vector2d? longest = null;
            for (var i = 0; i < vertices.Count; i++)
            {
                var a = vertices[i];
                var b = vertices[(i + 1) % vertices.Count];
                var edge = b - a;
                var length = edge.Length;
                if (length > maxLen)
                {
                    maxLen = length;
                    longest = edge / length;
                }
            }

            if (longest == null || maxLen <= 1e-9)
            {
                return false;
            }

            eastUnit = longest.Value;
            northUnit = new Vector2d(-eastUnit.Y, eastUnit.X);
            if (northUnit.Y < 0)
            {
                eastUnit = -eastUnit;
                northUnit = -northUnit;
            }

            if (!TryGetPolygonCorner(vertices, eastUnit, northUnit, CornerKey.SouthWest, out southWest) ||
                !TryGetPolygonCorner(vertices, eastUnit, northUnit, CornerKey.SouthEast, out var southEast) ||
                !TryGetPolygonCorner(vertices, eastUnit, northUnit, CornerKey.NorthWest, out var northWest))
            {
                return false;
            }

            width = Math.Abs((southEast - southWest).DotProduct(eastUnit));
            height = Math.Abs((northWest - southWest).DotProduct(northUnit));
            return width > 1e-6 && height > 1e-6;
        }

        private static bool TryGetPolygonCorner(
            IReadOnlyList<Point2d> vertices,
            Vector2d eastUnit,
            Vector2d northUnit,
            CornerKey corner,
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
                    CornerKey.NorthWest => n - e,
                    CornerKey.NorthEast => n + e,
                    CornerKey.SouthWest => -n - e,
                    CornerKey.SouthEast => -n + e,
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

        private static bool TryGetQuarterOffsets(string quarterToken, out int rowOffset, out int colOffset)
        {
            rowOffset = 0;
            colOffset = 0;
            switch (NormalizeQuarterToken(quarterToken))
            {
                case "SW":
                    rowOffset = 0;
                    colOffset = 0;
                    return true;
                case "SE":
                    rowOffset = 0;
                    colOffset = 2;
                    return true;
                case "NW":
                    rowOffset = 2;
                    colOffset = 0;
                    return true;
                case "NE":
                    rowOffset = 2;
                    colOffset = 2;
                    return true;
                default:
                    return false;
            }
        }

        private static string NormalizeQuarterToken(string? token)
        {
            if (string.IsNullOrWhiteSpace(token))
            {
                return string.Empty;
            }

            return token
                .Replace(".", string.Empty)
                .Trim()
                .ToUpperInvariant();
        }

        private static bool IsPointInsidePolygon2d(IReadOnlyList<Point2d> vertices, Point2d point, double tolerance)
        {
            if (vertices == null || vertices.Count < 3)
            {
                return false;
            }

            if (DistanceSqToBoundary2d(vertices, point) <= tolerance * tolerance)
            {
                return true;
            }

            var inside = false;
            var previous = vertices[vertices.Count - 1];
            for (var i = 0; i < vertices.Count; i++)
            {
                var current = vertices[i];
                if ((previous.Y > point.Y) != (current.Y > point.Y))
                {
                    var intersectX = ((current.X - previous.X) * (point.Y - previous.Y) / (current.Y - previous.Y)) + previous.X;
                    if (point.X < intersectX)
                    {
                        inside = !inside;
                    }
                }

                previous = current;
            }

            return inside;
        }

        private static double DistanceSqToBoundary2d(IReadOnlyList<Point2d> vertices, Point2d point)
        {
            var minDistanceSq = double.MaxValue;
            for (var i = 0; i < vertices.Count; i++)
            {
                var start = vertices[i];
                var end = vertices[(i + 1) % vertices.Count];
                minDistanceSq = Math.Min(minDistanceSq, DistanceSqToSegment2d(point, start, end));
            }

            return minDistanceSq;
        }

        private static double DistanceSqToSegment2d(Point2d point, Point2d start, Point2d end)
        {
            var edgeX = end.X - start.X;
            var edgeY = end.Y - start.Y;
            var lengthSq = (edgeX * edgeX) + (edgeY * edgeY);
            if (lengthSq <= 0.0)
            {
                var dx = point.X - start.X;
                var dy = point.Y - start.Y;
                return (dx * dx) + (dy * dy);
            }

            var dxp = point.X - start.X;
            var dyp = point.Y - start.Y;
            var projection = (dxp * edgeX) + (dyp * edgeY);
            var t = Math.Max(0.0, Math.Min(1.0, projection / lengthSq));
            var closestX = start.X + (t * edgeX);
            var closestY = start.Y + (t * edgeY);
            var dxc = point.X - closestX;
            var dyc = point.Y - closestY;
            return (dxc * dxc) + (dyc * dyc);
        }

        private static ObjectId InsertPhotoBlock(
            Database db,
            BlockTableRecord space,
            Transaction tr,
            string blockName,
            Point3d insertPoint,
            int number,
            string direction)
        {
            var blockTable = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            if (!blockTable.Has(blockName))
            {
                return ObjectId.Null;
            }

            var blockId = blockTable[blockName];
            var blockRef = new BlockReference(insertPoint, blockId)
            {
                Layer = "0"
            };

            space.AppendEntity(blockRef);
            tr.AddNewlyCreatedDBObject(blockRef, true);

            var blockDef = (BlockTableRecord)tr.GetObject(blockId, OpenMode.ForRead);
            if (!blockDef.HasAttributeDefinitions)
            {
                return blockRef.ObjectId;
            }

            foreach (ObjectId id in blockDef)
            {
                if (id.ObjectClass != RXObject.GetClass(typeof(AttributeDefinition)))
                {
                    continue;
                }

                var attDef = (AttributeDefinition)tr.GetObject(id, OpenMode.ForRead);
                if (attDef.Constant)
                {
                    continue;
                }

                var attRef = new AttributeReference();
                attRef.SetAttributeFromBlock(attDef, blockRef.BlockTransform);

                if (string.Equals(attDef.Tag, "#", StringComparison.OrdinalIgnoreCase))
                {
                    attRef.TextString = number.ToString(CultureInfo.InvariantCulture);
                }
                else if (string.Equals(attDef.Tag, "DIR", StringComparison.OrdinalIgnoreCase)
                         || string.Equals(attDef.Tag, "DIRECTION", StringComparison.OrdinalIgnoreCase))
                {
                    attRef.TextString = direction;
                }

                blockRef.AttributeCollection.AppendAttribute(attRef);
                tr.AddNewlyCreatedDBObject(attRef, true);
            }

            return blockRef.ObjectId;
        }

        private static IReadOnlyList<int> FindDuplicatePhotoNumbers(
            Database db,
            IReadOnlyList<string> blockNames,
            int startNumber,
            int count)
        {
            if (count <= 0)
            {
                return Array.Empty<int>();
            }

            var duplicates = new SortedSet<int>();
            var normalizedNames = new HashSet<string>(blockNames, StringComparer.OrdinalIgnoreCase);
            var endNumber = startNumber + count - 1;

            using var tr = db.TransactionManager.StartTransaction();
            var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);

            foreach (ObjectId id in space)
            {
                if (id.ObjectClass != RXObject.GetClass(typeof(BlockReference)))
                {
                    continue;
                }

                var blockRef = (BlockReference)tr.GetObject(id, OpenMode.ForRead);
                if (!normalizedNames.Contains(BlockSelectionHelper.GetEffectiveName(blockRef, tr)))
                {
                    continue;
                }

                var existingNumber = BlockSelectionHelper.TryGetAttribute(blockRef, "#", tr);
                if (existingNumber.HasValue && existingNumber.Value >= startNumber && existingNumber.Value <= endNumber)
                {
                    duplicates.Add(existingNumber.Value);
                }
            }

            tr.Commit();
            return duplicates.ToList();
        }

        private static void WriteCsv(string path, IReadOnlyList<PhotoStandardizationRecord> standardizedRows)
        {
            using var writer = new StreamWriter(path);
            writer.WriteLine("FindingRef,OriginalText,CleanedOriginal,Species,FindingType,StandardDescription,PhotoRef,Lat,Long,LatDDMMSS,LongDDMMSS,Northing,Easting,Image");
            foreach (var row in standardizedRows)
            {
                var record = row.Record;
                var standardization = row.Standardization;
                writer.WriteLine(string.Join(",",
                    record.Number,
                    EscapeCsv(row.OriginalText),
                    EscapeCsv(standardization.CleanedOriginal),
                    EscapeCsv(standardization.Species),
                    EscapeCsv(standardization.FindingType),
                    EscapeCsv(standardization.StandardDescription),
                    EscapeCsv(standardization.PhotoRef),
                    record.Latitude.ToString("F6", CultureInfo.InvariantCulture),
                    record.Longitude.ToString("F6", CultureInfo.InvariantCulture),
                    EscapeCsv(DmsFormatter.ToDmsString(record.Latitude, true)),
                    EscapeCsv(DmsFormatter.ToDmsString(record.Longitude, false)),
                    record.Northing.ToString("F3", CultureInfo.InvariantCulture),
                    record.Easting.ToString("F3", CultureInfo.InvariantCulture),
                    EscapeCsv(record.SourceImageName)));
            }
        }

        private static BuildProjectedFindingsResult BuildProjectedFindings(
            IReadOnlyList<PhotoProjectedRecord> photos,
            IReadOnlyList<TextFindingRecord> textFindings,
            UtmCoordinateConverter utmConverter,
            Editor editor,
            BufferBoundary? bufferBoundary,
            BufferMode bufferMode)
        {
            const double duplicateDistanceMeters = 3.0;
            const double duplicateDistanceSq = duplicateDistanceMeters * duplicateDistanceMeters;

            var nearbyPhotoForText = new bool[textFindings.Count];
            var nearbyTextForPhoto = new bool[photos.Count];
            var nearestPhotoIndexForText = Enumerable.Repeat(-1, textFindings.Count).ToArray();
            var nearestPhotoDistanceSqForText = Enumerable.Repeat(double.MaxValue, textFindings.Count).ToArray();
            var nearestTextIndexForPhoto = Enumerable.Repeat(-1, photos.Count).ToArray();
            var nearestTextDistanceSqForPhoto = Enumerable.Repeat(double.MaxValue, photos.Count).ToArray();
            var nameMatchCandidates = new List<(int TextIndex, int PhotoIndex, double DistanceSq)>();

            for (var textIndex = 0; textIndex < textFindings.Count; textIndex++)
            {
                var textFinding = textFindings[textIndex];
                for (var photoIndex = 0; photoIndex < photos.Count; photoIndex++)
                {
                    var photo = photos[photoIndex];
                    var dx = photo.Easting - textFinding.Location.X;
                    var dy = photo.Northing - textFinding.Location.Y;
                    var distanceSq = (dx * dx) + (dy * dy);
                    if (distanceSq > duplicateDistanceSq)
                    {
                        continue;
                    }

                    nearbyPhotoForText[textIndex] = true;
                    nearbyTextForPhoto[photoIndex] = true;

                    if (distanceSq < nearestPhotoDistanceSqForText[textIndex])
                    {
                        nearestPhotoDistanceSqForText[textIndex] = distanceSq;
                        nearestPhotoIndexForText[textIndex] = photoIndex;
                    }

                    if (distanceSq < nearestTextDistanceSqForPhoto[photoIndex])
                    {
                        nearestTextDistanceSqForPhoto[photoIndex] = distanceSq;
                        nearestTextIndexForPhoto[photoIndex] = textIndex;
                    }

                    if (PhotoTextMatcher.IsTextMatchingPhotoName(textFinding.Text, photo.FileName))
                    {
                        nameMatchCandidates.Add((textIndex, photoIndex, distanceSq));
                    }
                }
            }

            var assignedText = new bool[textFindings.Count];
            var assignedPhoto = new bool[photos.Count];
            var assignedPhotoToTextIndex = Enumerable.Repeat(-1, photos.Count).ToArray();
            foreach (var candidate in nameMatchCandidates.OrderBy(item => item.DistanceSq))
            {
                if (assignedText[candidate.TextIndex] || assignedPhoto[candidate.PhotoIndex])
                {
                    continue;
                }

                assignedText[candidate.TextIndex] = true;
                assignedPhoto[candidate.PhotoIndex] = true;
                assignedPhotoToTextIndex[candidate.PhotoIndex] = candidate.TextIndex;
            }

            var matchedTextIds = new List<ObjectId>();
            for (var textIndex = 0; textIndex < textFindings.Count; textIndex++)
            {
                if (assignedText[textIndex] && !textFindings[textIndex].SourceTextId.IsNull)
                {
                    matchedTextIds.Add(textFindings[textIndex].SourceTextId);
                }
            }

            var findings = new List<FindingProjectedRecord>(photos.Count + textFindings.Count);
            var photoMatchedToTextCount = 0;
            var photoOnlyAddedCount = 0;
            var photoDroppedNearTextNoNameMatchCount = 0;
            var photosWithoutMatchingText = new List<PhotoWithoutMatchingTextRecord>();
            for (var photoIndex = 0; photoIndex < photos.Count; photoIndex++)
            {
                var photo = photos[photoIndex];
                var shouldAddPhotoFinding = assignedPhoto[photoIndex] || !nearbyTextForPhoto[photoIndex];
                if (!shouldAddPhotoFinding)
                {
                    photoDroppedNearTextNoNameMatchCount++;
                    var nearestText = nearestTextIndexForPhoto[photoIndex] >= 0
                        ? textFindings[nearestTextIndexForPhoto[photoIndex]]
                        : null;
                var nearestDistanceMeters = nearestTextIndexForPhoto[photoIndex] >= 0
                        ? Math.Sqrt(nearestTextDistanceSqForPhoto[photoIndex])
                        : (double?)null;
                    photosWithoutMatchingText.Add(new PhotoWithoutMatchingTextRecord(
                        photo.FileName,
                        photo.Northing,
                        photo.Easting,
                        "Near text within 3m but name mismatch",
                        nearestText?.Text ?? string.Empty,
                        nearestDistanceMeters));
                    continue;
                }

                var isOutsideBuffer = IsOutsideBuffer(new Point3d(photo.Easting, photo.Northing, 0.0), bufferBoundary, bufferMode);
                if (bufferMode == BufferMode.IncludeBufferExcludeOutside && isOutsideBuffer)
                {
                    continue;
                }

                if (assignedPhoto[photoIndex])
                {
                    var matchedTextIndex = assignedPhotoToTextIndex[photoIndex];
                    var matchedText = matchedTextIndex >= 0
                        ? textFindings[matchedTextIndex]
                        : null;

                    findings.Add(new FindingProjectedRecord(
                        photo.ImagePath,
                        matchedText?.Text ?? photo.FileName,
                        photo.FileName,
                        photo.Latitude,
                        photo.Longitude,
                        photo.Northing,
                        photo.Easting,
                        matchedText?.SourceTextId ?? ObjectId.Null,
                        isOutsideBuffer));

                    photoMatchedToTextCount++;
                }
                else
                {
                    findings.Add(new FindingProjectedRecord(
                        photo.ImagePath,
                        photo.FileName,
                        photo.FileName,
                        photo.Latitude,
                        photo.Longitude,
                        photo.Northing,
                        photo.Easting,
                        ObjectId.Null,
                        isOutsideBuffer));

                    photosWithoutMatchingText.Add(new PhotoWithoutMatchingTextRecord(
                        photo.FileName,
                        photo.Northing,
                        photo.Easting,
                        "No text within 3m",
                        string.Empty,
                        null));
                    photoOnlyAddedCount++;
                }
            }

            var skippedNearPhoto = 0;
            var nearPhotoNameMismatchKept = 0;
            var skippedNoProjection = 0;
            var textNearPhotoNameMismatchDetails = new List<TextNearPhotoNameMismatchRecord>();
            for (var textIndex = 0; textIndex < textFindings.Count; textIndex++)
            {
                var textFinding = textFindings[textIndex];
                if (assignedText[textIndex])
                {
                    skippedNearPhoto++;
                    continue;
                }

                if (nearbyPhotoForText[textIndex])
                {
                    nearPhotoNameMismatchKept++;
                    var nearestPhoto = nearestPhotoIndexForText[textIndex] >= 0
                        ? photos[nearestPhotoIndexForText[textIndex]]
                        : null;
                    var nearestDistanceMeters = nearestPhotoIndexForText[textIndex] >= 0
                        ? Math.Sqrt(nearestPhotoDistanceSqForText[textIndex])
                        : (double?)null;
                    textNearPhotoNameMismatchDetails.Add(new TextNearPhotoNameMismatchRecord(
                        textFinding.Text,
                        textFinding.Location.Y,
                        textFinding.Location.X,
                        nearestPhoto?.FileName ?? string.Empty,
                        nearestDistanceMeters));
                }

                var isOutsideBuffer = IsOutsideBuffer(textFinding.Location, bufferBoundary, bufferMode);
                if (bufferMode == BufferMode.IncludeBufferExcludeOutside && isOutsideBuffer)
                {
                    continue;
                }

                if (!utmConverter.TryProject(textFinding.Location, out var lat, out var lon))
                {
                    skippedNoProjection++;
                    continue;
                }

                findings.Add(new FindingProjectedRecord(
                    null,
                    textFinding.Text,
                    string.Empty,
                    lat,
                    lon,
                    textFinding.Location.Y,
                    textFinding.Location.X,
                    textFinding.SourceTextId,
                    isOutsideBuffer));
            }

            if (skippedNearPhoto > 0)
            {
                editor.WriteMessage($"\nSkipped {skippedNearPhoto} text finding(s) within 3m of photo locations with matching photo name.");
            }

            if (nearPhotoNameMismatchKept > 0)
            {
                editor.WriteMessage($"\nKept {nearPhotoNameMismatchKept} text finding(s) near photos because text value did not match photo name.");
            }

            if (photoDroppedNearTextNoNameMatchCount > 0)
            {
                editor.WriteMessage($"\nDropped {photoDroppedNearTextNoNameMatchCount} photo finding(s) near text due to name mismatch (to avoid duplicates).");
            }

            if (skippedNoProjection > 0)
            {
                editor.WriteMessage($"\nSkipped {skippedNoProjection} text finding(s) due to coordinate projection failure.");
            }

            return new BuildProjectedFindingsResult(
                findings,
                textFindings.Count,
                photos.Count,
                skippedNearPhoto,
                nearPhotoNameMismatchKept,
                skippedNoProjection,
                photoMatchedToTextCount,
                photoOnlyAddedCount,
                photoDroppedNearTextNoNameMatchCount,
                matchedTextIds,
                photosWithoutMatchingText,
                textNearPhotoNameMismatchDetails);
        }

        private static List<FindingCuratedRecord> CurateFindings(
            IReadOnlyList<FindingProjectedRecord> findings,
            FindingsDescriptionStandardizer standardizer,
            StringBuilder logBuilder)
        {
            var curated = new List<FindingCuratedRecord>();

            foreach (var finding in findings)
            {
                var originalText = finding.OriginalText;
                // We pass the finding text through the standardizer so it cleans refs/parentheses like NUMPTS.
                var standardizations = standardizer.Standardize(
                    originalText,
                    context => FindingsStandardizationHelper.PromptForUnmappedFinding(context, standardizer));
                var ignored = standardizations.Any(standardization =>
                    standardization.Source == FindingsDescriptionStandardizer.StandardizationSource.Ignored);

                foreach (var standardization in standardizations)
                {
                    FindingsStandardizationHelper.TryAppendNonAutomaticLogEntry(logBuilder, originalText, standardization);
                }

                curated.Add(new FindingCuratedRecord(
                    finding.ImagePath,
                    finding.OriginalText,
                    finding.SourceImageName,
                    finding.Latitude,
                    finding.Longitude,
                    finding.Northing,
                    finding.Easting,
                    finding.SourceTextId,
                    finding.IsOutsideBuffer,
                    standardizations,
                    ignored));
            }

            return curated;
        }

        private static int ColorIgnoredTextEntities(Database db, IReadOnlyList<FindingCuratedRecord> curatedFindings)
        {
            var ignoredTextIds = curatedFindings
                .Where(finding => finding.Ignored && !finding.SourceTextId.IsNull)
                .Select(finding => finding.SourceTextId)
                .ToList();

            return ColorTextEntities(db, ignoredTextIds, 1);
        }

        private static int ColorAssociatedTextEntitiesGreen(Database db, IReadOnlyList<ObjectId> matchedTextIds)
        {
            return ColorTextEntities(db, matchedTextIds, 3);
        }

        private static int ColorTextEntities(Database db, IEnumerable<ObjectId> textIds, short colorIndex)
        {
            var distinctIds = textIds
                .Where(id => !id.IsNull)
                .Distinct()
                .ToList();

            if (distinctIds.Count == 0)
            {
                return 0;
            }

            var coloredCount = 0;
            using var tr = db.TransactionManager.StartTransaction();
            foreach (var textId in distinctIds)
            {
                try
                {
                    if (tr.GetObject(textId, OpenMode.ForWrite, false) is Entity entity)
                    {
                        entity.ColorIndex = colorIndex;
                        coloredCount++;
                    }
                }
                catch
                {
                    // Ignore stale/deleted text IDs and continue with the rest.
                }
            }

            tr.Commit();
            return coloredCount;
        }

        private static string? WritePhotoTextDebugReport(
            Document doc,
            string? fallbackDirectory,
            BuildProjectedFindingsResult projectedResult)
        {
            if (projectedResult.PhotosWithoutMatchingText.Count == 0
                && projectedResult.TextNearPhotoNameMismatchDetails.Count == 0)
            {
                return null;
            }

            try
            {
                var drawingPath = doc.Database.Filename;
                var targetDirectory = string.IsNullOrWhiteSpace(drawingPath)
                    ? (fallbackDirectory ?? Path.GetTempPath())
                    : (Path.GetDirectoryName(drawingPath) ?? fallbackDirectory ?? Path.GetTempPath());
                var baseName = string.IsNullOrWhiteSpace(drawingPath)
                    ? "unsaved_drawing"
                    : Path.GetFileNameWithoutExtension(drawingPath);
                var reportPath = Path.Combine(targetDirectory, $"{baseName}_photo_text_match_debug.txt");

                var lines = new List<string>
                {
                    "Photo/Text Match Debug Report",
                    $"Drawing: {doc.Name}",
                    $"Timestamp: {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                    "Threshold (m): 3.000",
                    "Match rule: distance <= 3m AND text value matches photo file name.",
                    string.Empty,
                    $"Photos loaded: {projectedResult.ProjectedPhotoCount}",
                    $"Text selected: {projectedResult.TextSelectedCount}",
                    $"Photos without matching text: {projectedResult.PhotosWithoutMatchingText.Count}",
                    $"Text near photo but name mismatch (kept): {projectedResult.TextNearPhotoNameMismatchDetails.Count}",
                    string.Empty
                };

                lines.Add("Photos without matching text:");
                lines.Add("PhotoFile\tNorthing\tEasting\tReason\tNearestText\tNearestTextDistanceMeters");
                lines.AddRange(projectedResult.PhotosWithoutMatchingText
                    .OrderBy(item => item.PhotoFileName, StringComparer.OrdinalIgnoreCase)
                    .Select(item => string.Join("\t",
                        item.PhotoFileName,
                        item.Northing.ToString("F3", CultureInfo.InvariantCulture),
                        item.Easting.ToString("F3", CultureInfo.InvariantCulture),
                        item.Reason,
                        item.NearestTextValue,
                        item.NearestTextDistanceMeters.HasValue
                            ? item.NearestTextDistanceMeters.Value.ToString("F3", CultureInfo.InvariantCulture)
                            : string.Empty)));

                lines.Add(string.Empty);
                lines.Add("Text near photo but name mismatch (kept):");
                lines.Add("TextValue\tNorthing\tEasting\tNearestPhoto\tNearestPhotoDistanceMeters");
                lines.AddRange(projectedResult.TextNearPhotoNameMismatchDetails
                    .OrderBy(item => item.TextValue, StringComparer.OrdinalIgnoreCase)
                    .Select(item => string.Join("\t",
                        item.TextValue,
                        item.Northing.ToString("F3", CultureInfo.InvariantCulture),
                        item.Easting.ToString("F3", CultureInfo.InvariantCulture),
                        item.NearestPhotoFileName,
                        item.NearestPhotoDistanceMeters.HasValue
                            ? item.NearestPhotoDistanceMeters.Value.ToString("F3", CultureInfo.InvariantCulture)
                            : string.Empty)));

                File.WriteAllLines(reportPath, lines, Encoding.UTF8);
                return reportPath;
            }
            catch
            {
                return null;
            }
        }

        private static string EscapeCsv(string? value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var escaped = value.Replace("\"", "\"\"");
            return $"\"{escaped}\"";
        }

        private static List<PhotoGpsRecord> LoadGpsPhotos(string folder, Editor editor)
        {
            var gpsPhotos = PhotoGpsMetadataReader.LoadGpsPhotos(
                folder,
                editor,
                deduplicateByCoordinate: true,
                missingGpsSummaryFormat: "\nSkipped {0} JPG(s) without GPS metadata.");
            return gpsPhotos
                .Select(photo => new PhotoGpsRecord(
                    photo.ImagePath,
                    photo.FileName,
                    photo.Latitude,
                    photo.Longitude))
                .ToList();
        }

        private static IEnumerable<FindingProjectedRecord> SortRecords(
            IEnumerable<FindingProjectedRecord> records,
            string order,
            bool outsideLast)
        {
            if (!outsideLast)
            {
                return SortRecordsByDirection(records, order);
            }

            var insideBuffer = records.Where(record => !record.IsOutsideBuffer);
            var outsideBuffer = records.Where(record => record.IsOutsideBuffer);

            return SortRecordsByDirection(insideBuffer, order)
                .Concat(SortRecordsByDirection(outsideBuffer, order));
        }

        private static IReadOnlyList<FindingProjectedRecord> SortRecordsForMode(
            IReadOnlyList<FindingProjectedRecord> records,
            string order,
            BufferMode bufferMode,
            BufferBoundary? proposedBoundary,
            BufferBoundary? hundredMeterBoundary)
        {
            if (records == null || records.Count == 0)
            {
                return Array.Empty<FindingProjectedRecord>();
            }

            if (!UsesAreaSpecificBufferPrompts(bufferMode))
            {
                return SortRecords(records, order, bufferMode == BufferMode.IncludeBufferAndAll).ToList();
            }

            var proposed = new List<FindingProjectedRecord>(records.Count);
            var hundredMeter = new List<FindingProjectedRecord>(records.Count);
            var outside = bufferMode == BufferMode.IncludeBufferAndAll
                ? new List<FindingProjectedRecord>(records.Count)
                : null;
            foreach (var record in records)
            {
                var area = ClassifyBufferArea(
                    new Point3d(record.Easting, record.Northing, 0.0),
                    proposedBoundary,
                    hundredMeterBoundary);
                if (area == BufferArea.Proposed)
                {
                    proposed.Add(record);
                }
                else if (area == BufferArea.HundredMeter)
                {
                    hundredMeter.Add(record);
                }
                else if (outside != null)
                {
                    outside.Add(record);
                }
            }

            var ordered = SortRecordsByDirection(proposed, order)
                .Concat(SortRecordsByDirection(hundredMeter, order));
            if (outside != null)
            {
                ordered = ordered.Concat(SortRecordsByDirection(outside, order));
            }

            return ordered.ToList();
        }

        private static IEnumerable<FindingProjectedRecord> SortRecordsByDirection(
            IEnumerable<FindingProjectedRecord> records,
            string order)
        {
            return order switch
            {
                "RightToLeft" => records.OrderByDescending(r => r.Easting),
                "SouthToNorth" => records.OrderBy(r => r.Northing).ThenBy(r => r.Easting),
                "NorthToSouth" => records.OrderByDescending(r => r.Northing).ThenBy(r => r.Easting),
                "SEtoNW" => records.OrderBy(r => r.Northing).ThenByDescending(r => r.Easting),
                "SWtoNE" => records.OrderBy(r => r.Northing).ThenBy(r => r.Easting),
                "NEtoSW" => records.OrderByDescending(r => r.Northing).ThenByDescending(r => r.Easting),
                "NWtoSE" => records.OrderByDescending(r => r.Northing).ThenBy(r => r.Easting),
                _ => records.OrderBy(r => r.Easting)
            };
        }

        private static string NormalizeOrder(string? value)
        {
            return value switch
            {
                "LeftToRight" => "LeftToRight",
                "RightToLeft" => "RightToLeft",
                "SouthToNorth" => "SouthToNorth",
                "NorthToSouth" => "NorthToSouth",
                "SEtoNW" => "SEtoNW",
                "SWtoNE" => "SWtoNE",
                "NEtoSW" => "NEtoSW",
                "NWtoSE" => "NWtoSE",
                _ => "LeftToRight"
            };
        }

        private static string BuildPolygonKey(IReadOnlyList<Point2d> vertices)
        {
            if (vertices == null || vertices.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(vertices.Count * 32);
            for (var index = 0; index < vertices.Count; index++)
            {
                var vertex = vertices[index];
                builder.Append(vertex.X.ToString("F3", CultureInfo.InvariantCulture));
                builder.Append(',');
                builder.Append(vertex.Y.ToString("F3", CultureInfo.InvariantCulture));
                builder.Append(';');
            }

            return builder.ToString();
        }

        private sealed record BufferBoundary(IReadOnlyList<Point3d> Vertices)
        {
            public bool IsWithinBuffer(Point3d point, double bufferDistanceMeters)
            {
                return IsInside(point);
            }

            public bool IsInside(Point3d point)
            {
                return BoundaryContainmentHelper.IsInside(
                    Vertices,
                    point,
                    BoundaryEdgeTolerance,
                    use3dDistanceForDegenerateSegments: true);
            }

            public double DistanceToBoundary(Point3d point)
            {
                return BoundaryContainmentHelper.DistanceToBoundary(
                    Vertices,
                    point,
                    use3dDistanceForDegenerateSegments: true);
            }
        }

        private sealed record PhotoGpsRecord(string ImagePath, string FileName, double Latitude, double Longitude);

        private sealed record PhotoProjectedRecord(
            string ImagePath,
            string FileName,
            double Latitude,
            double Longitude,
            double Northing,
            double Easting);

        private sealed record TextFindingRecord(
            ObjectId SourceTextId,
            string Text,
            Point3d Location);

        private sealed record FindingProjectedRecord(
            string? ImagePath,
            string OriginalText,
            string SourceImageName,
            double Latitude,
            double Longitude,
            double Northing,
            double Easting,
            ObjectId SourceTextId,
            bool IsOutsideBuffer);

        private sealed record FindingCuratedRecord(
            string? ImagePath,
            string OriginalText,
            string SourceImageName,
            double Latitude,
            double Longitude,
            double Northing,
            double Easting,
            ObjectId SourceTextId,
            bool IsOutsideBuffer,
            IReadOnlyList<FindingsDescriptionStandardizer.StandardizedFinding> Standardizations,
            bool Ignored);

        private sealed record BuildProjectedFindingsResult(
            IReadOnlyList<FindingProjectedRecord> Findings,
            int TextSelectedCount,
            int ProjectedPhotoCount,
            int TextSkippedNearPhotoCount,
            int TextNearPhotoNameMismatchKeptCount,
            int TextSkippedProjectionCount,
            int PhotoMatchedToTextCount,
            int PhotoOnlyAddedCount,
            int PhotoDroppedNearTextNoNameMatchCount,
            IReadOnlyList<ObjectId> MatchedTextIds,
            IReadOnlyList<PhotoWithoutMatchingTextRecord> PhotosWithoutMatchingText,
            IReadOnlyList<TextNearPhotoNameMismatchRecord> TextNearPhotoNameMismatchDetails);

        private sealed record PhotoWithoutMatchingTextRecord(
            string PhotoFileName,
            double Northing,
            double Easting,
            string Reason,
            string NearestTextValue,
            double? NearestTextDistanceMeters);

        private sealed record TextNearPhotoNameMismatchRecord(
            string TextValue,
            double Northing,
            double Easting,
            string NearestPhotoFileName,
            double? NearestPhotoDistanceMeters);

        private sealed record QuarterLayerMatch(
            IReadOnlyList<Point2d> Vertices,
            string QuarterToken,
            string Section,
            string Township,
            string Range,
            string Meridian);

        private sealed record QuarterLayerSource(
            string LayerName,
            List<QuarterLayerMatch> Matches);

        private enum CornerKey
        {
            NorthWest,
            NorthEast,
            SouthWest,
            SouthEast
        }

        private sealed record PhotoPointRecord(
            int Number,
            string? ImagePath,
            string OriginalText,
            string SourceImageName,
            double Latitude,
            double Longitude,
            double Northing,
            double Easting,
            string Location,
            string WildlifeFinding,
            bool IsOutsideBuffer,
            BufferArea Area);

        private sealed record PhotoStandardizationRecord(
            PhotoPointRecord Record,
            string OriginalText,
            FindingsDescriptionStandardizer.StandardizedFinding Standardization);
    }
}
