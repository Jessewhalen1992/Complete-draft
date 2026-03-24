using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using AtsBackgroundBuilder;
using AtsBackgroundBuilder.Sections;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using FontDescriptor = Autodesk.AutoCAD.GraphicsInterface.FontDescriptor;

namespace WildlifeSweeps
{
    public sealed class TitleBlockControlInput
    {
        public string? SweepType { get; set; }
        public string? Purpose { get; set; }
        public string? Location { get; set; }
        public List<DateTime>? SurveyDates { get; set; }
        public string? SubRegion { get; set; }
        public List<string>? ExistingLinearInfrastructure { get; set; }
        public List<string>? ExistingLeases { get; set; }
        public List<string>? ExistingLandOther { get; set; }
        public string? MethodologySpacingMeters { get; set; }
    }

    public sealed class TitleBlockControlService
    {
        private const double BoundaryToleranceMeters = 0.25;
        private const string LegacyLayoutPrefix = "Layout 1";
        private const string DefaultSectionIndexFolder = @"C:\AUTOCAD-SETUP CG\CG_LISP\COMPASS\RES MANAGER";
        private const string EnvironmentalConditionsLayerName = "WLS-ENV-COND";
        private const string EnvironmentalConditionsHeading = "ENVIRONMENTAL CONDITIONS";
        private const string StartOfSweepLabel = "START OF SWEEP";
        private const string EndOfSweepLabel = "END OF SWEEP";
        private const short BlueColorIndex = 5;
        private const short GreenColorIndex = 3;
        private const double EnvironmentalConditionsRowHeight = 20.0;
        private const double EnvironmentalConditionsHeadingTextHeight = 16.0;
        private const double EnvironmentalConditionsValueTextHeight = 10.0;
        private const double EnvironmentalConditionsVerticalGap = 0.0;
        private const double EnvironmentalConditionsAnchorX = 67.256;
        private const double EnvironmentalConditionsAnchorY = 753.705;
        private const string EnvironmentalConditionsNonBoldStyleSuffix = "_WLS_NB";
        private static readonly double[] EnvironmentalConditionsColumnWidths = { 185.0, 135.0, 135.0 };
        private static readonly string[] EnvironmentalConditionsDetailLabels =
        {
            "TEMPERATURE (C\u00B0)",
            "WIND SPEED (km/h)",
            "PRECIPITATION (mm)",
            "CLOUD COVER (%)"
        };
        private static readonly Color EnvironmentalConditionsHeadingColor = Color.FromColorIndex(ColorMethod.ByAci, 14);
        private static readonly Color EnvironmentalConditionsValueColor = Color.FromColorIndex(ColorMethod.ByAci, 1);
        private static readonly Color EnvironmentalConditionsDetailColor = Color.FromColorIndex(ColorMethod.ByAci, 7);
        private static readonly Color EnvironmentalConditionsBackgroundColor = Color.FromColorIndex(ColorMethod.ByAci, 254);
        private static readonly Color EnvironmentalConditionsBorderColor = Color.FromColorIndex(ColorMethod.ByAci, 7);

        public bool TryCollectLocationText(
            Document doc,
            Editor editor,
            string utmZone,
            IntPtr hostWindowHandle,
            out string locationText,
            out string message)
        {
            locationText = string.Empty;
            message = string.Empty;

            if (doc == null)
            {
                message = "Document is required.";
                return false;
            }

            if (editor == null)
            {
                message = "Editor is required.";
                return false;
            }

            if (!TryParseUtmZone(utmZone, out var zone))
            {
                message = "UTM zone must be 11 or 12.";
                return false;
            }

            using (doc.LockDocument())
            {
                if (!TryCollectClosedBoundaryLoops(editor, doc.Database, hostWindowHandle, out var boundaryLoops, out message))
                {
                    return false;
                }

                if (!TryLoadSectionFrames(doc.Database.Filename, zone, out var frames, out message))
                {
                    return false;
                }

                var entries = CollectQuarterEntries(frames, boundaryLoops);
                if (entries.Count == 0)
                {
                    message = "No ATS quarter sections were found inside the selected boundary or boundaries.";
                    return false;
                }

                locationText = FormatLocationEntries(entries);
                if (string.IsNullOrWhiteSpace(locationText))
                {
                    message = "Unable to format the collected location text.";
                    return false;
                }

                message = $"Collected location text from {entries.Count} quarter section(s).";
                return true;
            }
        }

        public bool Apply(
            Document doc,
            Editor editor,
            TitleBlockControlInput input,
            out string message)
        {
            message = string.Empty;

            if (doc == null)
            {
                message = "Document is required.";
                return false;
            }

            if (editor == null)
            {
                message = "Editor is required.";
                return false;
            }

            if (input == null)
            {
                message = "Title block input is required.";
                return false;
            }

            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var layout = FindTargetLayout(tr, doc.Database);
                if (layout == null)
                {
                    message = "Could not find the first paper-space layout. Expected a tab like '1 of 5' or 'Layout 1'.";
                    return false;
                }

                var replacements = BuildReplacementValues(input);
                var orderedSurveyDates = GetOrderedSurveyDates(input.SurveyDates);
                var environmentalAnchor = new Point3d(EnvironmentalConditionsAnchorX, EnvironmentalConditionsAnchorY, 0.0);
                var hasEnvironmentalAnchor = orderedSurveyDates.Count > 0;

                RemoveEnvironmentalConditionsTables(tr, layout);
                var updatedCount = ApplyReplacementsToLayout(tr, layout, replacements);
                var environmentalTableCount = 0;
                if (orderedSurveyDates.Count > 0 && hasEnvironmentalAnchor)
                {
                    environmentalTableCount = RebuildEnvironmentalConditionsTables(
                        tr,
                        doc.Database,
                        layout,
                        environmentalAnchor,
                        orderedSurveyDates);
                }

                tr.Commit();

                if (orderedSurveyDates.Count > 0 && !hasEnvironmentalAnchor)
                {
                    message = $"Applied title block values to layout '{layout.LayoutName}', but no survey-date anchor was found for the Environmental Conditions tables.";
                }
                else if (updatedCount > 0 || environmentalTableCount > 0)
                {
                    message = $"Applied title block values to layout '{layout.LayoutName}' ({updatedCount} text object(s) updated, {environmentalTableCount} Environmental Conditions table(s) rebuilt).";
                }
                else
                {
                    message = $"Layout '{layout.LayoutName}' was found, but no placeholder tokens or Environmental Conditions tables were updated.";
                }

                return true;
            }
        }

        public static string FormatSurveyDates(IEnumerable<DateTime>? dates)
        {
            if (dates == null)
            {
                return string.Empty;
            }

            var ordered = dates
                .Select(date => date.Date)
                .Distinct()
                .OrderBy(date => date)
                .ToList();

            if (ordered.Count == 0)
            {
                return string.Empty;
            }

            return JoinReadableList(ordered.Select(FormatSurveyDate));
        }

        public static string BuildExistingLandSummary(
            IEnumerable<string>? existingLinearInfrastructure,
            IEnumerable<string>? existingLeases,
            IEnumerable<string>? existingLandOther)
        {
            var categories = new List<string>();

            var linear = BuildExistingLandCategory("existing linear infrastructure", existingLinearInfrastructure);
            if (!string.IsNullOrWhiteSpace(linear))
            {
                categories.Add(linear);
            }

            var leases = BuildExistingLandCategory("existing leases", existingLeases);
            if (!string.IsNullOrWhiteSpace(leases))
            {
                categories.Add(leases);
            }

            var other = BuildExistingLandCategory("other", existingLandOther);
            if (!string.IsNullOrWhiteSpace(other))
            {
                categories.Add(other);
            }

            if (categories.Count == 0)
            {
                return string.Empty;
            }

            return $"rural mix of {JoinReadableList(categories)}";
        }

        public static string FormatMethodologySpacing(string? spacingMeters)
        {
            if (string.IsNullOrWhiteSpace(spacingMeters))
            {
                return string.Empty;
            }

            var trimmed = spacingMeters.Trim();
            var normalized = NormalizeTrailingMeterSuffix(trimmed);
            if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var meters) ||
                double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out meters))
            {
                return $"{meters.ToString("0.###", CultureInfo.InvariantCulture)}m";
            }

            return normalized.EndsWith("m", StringComparison.OrdinalIgnoreCase)
                ? normalized
                : $"{normalized}m";
        }

        private static IReadOnlyDictionary<int, string> BuildReplacementValues(TitleBlockControlInput input)
        {
            return new Dictionary<int, string>
            {
                [1] = FormatSweepTypeReplacementText(input.SweepType),
                [2] = FormatUppercaseReplacementText(input.Purpose),
                [3] = NormalizeReplacementText(input.Location),
                [4] = FormatSurveyDates(input.SurveyDates),
                [5] = NormalizeReplacementText(input.SubRegion),
                [6] = BuildExistingLandSummary(
                    input.ExistingLinearInfrastructure,
                    input.ExistingLeases,
                    input.ExistingLandOther),
                [7] = FormatMethodologySpacing(input.MethodologySpacingMeters)
            };
        }

        private static List<DateTime> GetOrderedSurveyDates(IEnumerable<DateTime>? dates)
        {
            return dates == null
                ? new List<DateTime>()
                : dates.Select(date => date.Date)
                    .Distinct()
                    .OrderBy(date => date)
                    .ToList();
        }

        private static bool TryResolveEnvironmentalConditionsAnchor(
            Transaction tr,
            Layout layout,
            IReadOnlyList<DateTime> surveyDates,
            string? surveyDatesText,
            out Point3d anchor)
        {
            anchor = Point3d.Origin;

            if (TryFindEnvironmentalTableAnchor(tr, layout, out anchor))
            {
                return true;
            }

            return TryFindSurveyDateAnchor(tr, layout, surveyDates, surveyDatesText, out anchor);
        }

        private static bool TryFindEnvironmentalTableAnchor(Transaction tr, Layout layout, out Point3d anchor)
        {
            anchor = Point3d.Origin;
            var blockTableRecord = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);

            Table? bestTable = null;
            foreach (ObjectId entityId in blockTableRecord)
            {
                if (tr.GetObject(entityId, OpenMode.ForRead, false) is not Table table || table.IsErased)
                {
                    continue;
                }

                if (!IsEnvironmentalConditionsTable(table))
                {
                    continue;
                }

                if (bestTable == null || table.Position.Y > bestTable.Position.Y)
                {
                    bestTable = table;
                }
            }

            if (bestTable == null)
            {
                return false;
            }

            anchor = bestTable.Position;
            return true;
        }

        private static bool TryFindSurveyDateAnchor(
            Transaction tr,
            Layout layout,
            IReadOnlyList<DateTime> surveyDates,
            string? surveyDatesText,
            out Point3d anchor)
        {
            anchor = Point3d.Origin;
            var candidates = new List<string> { "4." };
            if (!string.IsNullOrWhiteSpace(surveyDatesText))
            {
                candidates.Add(surveyDatesText!);
            }

            foreach (var surveyDate in surveyDates)
            {
                var formatted = FormatSurveyDate(surveyDate);
                if (!candidates.Contains(formatted, StringComparer.OrdinalIgnoreCase))
                {
                    candidates.Add(formatted);
                }
            }

            var blockTableRecord = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForRead);
            foreach (ObjectId entityId in blockTableRecord)
            {
                if (tr.GetObject(entityId, OpenMode.ForRead, false) is not Entity entity || entity.IsErased)
                {
                    continue;
                }

                if (TryGetEnvironmentalAnchorFromEntity(tr, entity, candidates, out anchor))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryGetEnvironmentalAnchorFromEntity(
            Transaction tr,
            Entity entity,
            IReadOnlyList<string> textCandidates,
            out Point3d anchor)
        {
            anchor = Point3d.Origin;

            switch (entity)
            {
                case MText mtext when ContainsAnyToken(mtext.Contents, textCandidates):
                    return TryBuildAnchorFromExtents(mtext, mtext.Location, out anchor);

                case DBText dbText when ContainsAnyToken(dbText.TextString, textCandidates):
                    return TryBuildAnchorFromExtents(dbText, dbText.Position, out anchor);

                case BlockReference blockReference:
                    foreach (ObjectId attributeId in blockReference.AttributeCollection)
                    {
                        if (tr.GetObject(attributeId, OpenMode.ForRead, false) is not AttributeReference attributeReference || attributeReference.IsErased)
                        {
                            continue;
                        }

                        if (!ContainsAnyToken(attributeReference.TextString, textCandidates))
                        {
                            continue;
                        }

                        return TryBuildAnchorFromExtents(attributeReference, attributeReference.Position, out anchor);
                    }

                    break;
            }

            return false;
        }

        private static bool TryBuildAnchorFromExtents(Entity entity, Point3d fallbackPoint, out Point3d anchor)
        {
            try
            {
                var extents = entity.GeometricExtents;
                anchor = new Point3d(
                    extents.MinPoint.X,
                    extents.MinPoint.Y - EnvironmentalConditionsVerticalGap,
                    extents.MinPoint.Z);
                return true;
            }
            catch
            {
                anchor = new Point3d(fallbackPoint.X, fallbackPoint.Y - EnvironmentalConditionsVerticalGap, fallbackPoint.Z);
                return true;
            }
        }

        private static bool ContainsAnyToken(string? text, IReadOnlyList<string> candidates)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return false;
            }

            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate) &&
                    text.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static void RemoveEnvironmentalConditionsTables(Transaction tr, Layout layout)
        {
            var blockTableRecord = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);
            foreach (ObjectId entityId in blockTableRecord)
            {
                if (tr.GetObject(entityId, OpenMode.ForWrite, false) is not Table table || table.IsErased)
                {
                    continue;
                }

                if (IsEnvironmentalConditionsTable(table))
                {
                    table.Erase(true);
                }
            }
        }

        private static bool IsEnvironmentalConditionsTable(Table table)
        {
            if (table == null || table.IsErased || table.Columns.Count != 3 || table.Rows.Count < 1)
            {
                return false;
            }

            if (table.Rows.Count >= 2 &&
                ContainsNormalizedCellText(table.Cells[0, 0], EnvironmentalConditionsHeading) &&
                ContainsNormalizedCellText(table.Cells[1, 1], StartOfSweepLabel) &&
                ContainsNormalizedCellText(table.Cells[1, 2], EndOfSweepLabel))
            {
                return true;
            }

            return table.Rows.Count == 1 &&
                   ContainsNormalizedCellText(table.Cells[0, 1], StartOfSweepLabel) &&
                   ContainsNormalizedCellText(table.Cells[0, 2], EndOfSweepLabel);
        }

        private static bool ContainsNormalizedCellText(Cell cell, string expected)
        {
            var actual = (cell?.TextString ?? string.Empty).Replace("\\P", " ", StringComparison.OrdinalIgnoreCase).Trim();
            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
        }

        private static int RebuildEnvironmentalConditionsTables(
            Transaction tr,
            Database db,
            Layout layout,
            Point3d anchor,
            IReadOnlyList<DateTime> surveyDates)
        {
            if (surveyDates == null || surveyDates.Count == 0)
            {
                return 0;
            }

            PhotoLayoutHelper.EnsureLayer(db, EnvironmentalConditionsLayerName, tr);
            var blockTableRecord = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);
            var insertPoint = anchor;
            var builtCount = 0;

            for (var index = 0; index < surveyDates.Count; index++)
            {
                var table = BuildEnvironmentalConditionsTable(
                    tr,
                    db,
                    insertPoint,
                    surveyDates[index],
                    includeHeading: index == 0);

                blockTableRecord.AppendEntity(table);
                tr.AddNewlyCreatedDBObject(table, true);
                table.GenerateLayout();
                builtCount++;

                var nextY = insertPoint.Y - GetEnvironmentalTableHeight(table) - EnvironmentalConditionsVerticalGap;
                insertPoint = new Point3d(insertPoint.X, nextY, insertPoint.Z);
            }

            return builtCount;
        }

        private static Table BuildEnvironmentalConditionsTable(
            Transaction tr,
            Database db,
            Point3d insertPoint,
            DateTime surveyDate,
            bool includeHeading)
        {
            var rowCount = (includeHeading ? 2 : 1) + EnvironmentalConditionsDetailLabels.Length;
            var headerRowIndex = includeHeading ? 1 : 0;
            var detailRowStartIndex = headerRowIndex + 1;

            var table = new Table
            {
                TableStyle = db.Tablestyle,
                Position = insertPoint,
                Layer = EnvironmentalConditionsLayerName
            };

#pragma warning disable CS0618
            table.IsTitleSuppressed = true;
            table.IsHeaderSuppressed = true;
#pragma warning restore CS0618
            table.SetSize(rowCount, 3);
            for (var columnIndex = 0; columnIndex < EnvironmentalConditionsColumnWidths.Length; columnIndex++)
            {
                table.Columns[columnIndex].Width = EnvironmentalConditionsColumnWidths[columnIndex];
            }

            for (var rowIndex = 0; rowIndex < rowCount; rowIndex++)
            {
                table.Rows[rowIndex].Height = EnvironmentalConditionsRowHeight;
            }

            if (includeHeading)
            {
                ConfigureEnvironmentalConditionsHeadingRow(tr, db, table);
            }

            ConfigureEnvironmentalConditionsValueRow(tr, db, table, headerRowIndex, surveyDate);
            ConfigureEnvironmentalConditionsDetailRows(tr, db, table, detailRowStartIndex);
            return table;
        }

        private static void ConfigureEnvironmentalConditionsHeadingRow(Transaction tr, Database db, Table table)
        {
            for (var columnIndex = 0; columnIndex < 3; columnIndex++)
            {
                var cell = table.Cells[0, columnIndex];
                cell.TextString = columnIndex == 0 ? EnvironmentalConditionsHeading : string.Empty;
                cell.TextHeight = EnvironmentalConditionsHeadingTextHeight;
                cell.Alignment = CellAlignment.MiddleCenter;
                cell.ContentColor = EnvironmentalConditionsHeadingColor;
                ApplyEnvironmentalConditionsTextStyle(tr, db, cell);
                SetCellBackgroundNone(cell);
                HideCellBorders(cell);
            }

            table.MergeCells(CellRange.Create(table, 0, 0, 0, 2));
            table.Cells[0, 0].Alignment = CellAlignment.MiddleCenter;
        }

        private static void ConfigureEnvironmentalConditionsValueRow(
            Transaction tr,
            Database db,
            Table table,
            int rowIndex,
            DateTime surveyDate)
        {
            ConfigureEnvironmentalConditionsValueCell(
                tr,
                db,
                table.Cells[rowIndex, 0],
                FormatEnvironmentalConditionsSurveyDate(surveyDate),
                CellAlignment.MiddleCenter);
            ConfigureEnvironmentalConditionsValueCell(
                tr,
                db,
                table.Cells[rowIndex, 1],
                StartOfSweepLabel,
                CellAlignment.MiddleCenter);
            ConfigureEnvironmentalConditionsValueCell(
                tr,
                db,
                table.Cells[rowIndex, 2],
                EndOfSweepLabel,
                CellAlignment.MiddleCenter);
        }

        private static void ConfigureEnvironmentalConditionsDetailRows(
            Transaction tr,
            Database db,
            Table table,
            int startRowIndex)
        {
            for (var labelIndex = 0; labelIndex < EnvironmentalConditionsDetailLabels.Length; labelIndex++)
            {
                var rowIndex = startRowIndex + labelIndex;
                ConfigureEnvironmentalConditionsDetailCell(
                    tr,
                    db,
                    table.Cells[rowIndex, 0],
                    EnvironmentalConditionsDetailLabels[labelIndex]);
                ConfigureEnvironmentalConditionsDetailCell(
                    tr,
                    db,
                    table.Cells[rowIndex, 1],
                    string.Empty);
                ConfigureEnvironmentalConditionsDetailCell(
                    tr,
                    db,
                    table.Cells[rowIndex, 2],
                    string.Empty);
            }
        }

        private static void ConfigureEnvironmentalConditionsDetailCell(
            Transaction tr,
            Database db,
            Cell cell,
            string text)
        {
            cell.TextString = text;
            cell.TextHeight = EnvironmentalConditionsValueTextHeight;
            cell.Alignment = CellAlignment.MiddleCenter;
            cell.ContentColor = EnvironmentalConditionsDetailColor;
            ApplyEnvironmentalConditionsTextStyle(tr, db, cell);
            SetCellBackgroundNone(cell);
            SetAllBorders(cell, true);
        }

        private static void ConfigureEnvironmentalConditionsValueCell(
            Transaction tr,
            Database db,
            Cell cell,
            string text,
            CellAlignment alignment)
        {
            cell.TextString = text;
            cell.TextHeight = EnvironmentalConditionsValueTextHeight;
            cell.Alignment = alignment;
            cell.ContentColor = EnvironmentalConditionsValueColor;
            ApplyEnvironmentalConditionsTextStyle(tr, db, cell);
            SetCellBackground(cell, EnvironmentalConditionsBackgroundColor);
            SetAllBorders(cell, true);
        }

        private static void ApplyEnvironmentalConditionsTextStyle(Transaction tr, Database db, Cell cell)
        {
            if (cell == null)
            {
                return;
            }

            cell.TextStyleId = EnsureNonBoldTextStyleId(tr, db, cell.TextStyleId ?? ObjectId.Null);
        }

        private static ObjectId EnsureNonBoldTextStyleId(Transaction tr, Database db, ObjectId baseStyleId)
        {
            var resolvedBaseStyleId = baseStyleId.IsNull ? db.Textstyle : baseStyleId;
            if (resolvedBaseStyleId.IsNull)
            {
                return resolvedBaseStyleId;
            }

            if (tr.GetObject(resolvedBaseStyleId, OpenMode.ForRead, false) is not TextStyleTableRecord baseStyleRecord || baseStyleRecord.IsErased)
            {
                return resolvedBaseStyleId;
            }

            var baseFont = baseStyleRecord.Font;
            if (!baseFont.Bold)
            {
                return resolvedBaseStyleId;
            }

            var nonBoldStyleName = $"{baseStyleRecord.Name}{EnvironmentalConditionsNonBoldStyleSuffix}";
            var textStyleTable = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            if (textStyleTable.Has(nonBoldStyleName))
            {
                return textStyleTable[nonBoldStyleName];
            }

            textStyleTable.UpgradeOpen();
            using var nonBoldStyleRecord = new TextStyleTableRecord();
            nonBoldStyleRecord.CopyFrom(baseStyleRecord);
            nonBoldStyleRecord.Name = nonBoldStyleName;
            nonBoldStyleRecord.Font = new FontDescriptor(
                baseFont.TypeFace,
                false,
                baseFont.Italic,
                baseFont.CharacterSet,
                baseFont.PitchAndFamily);

            var createdStyleId = textStyleTable.Add(nonBoldStyleRecord);
            tr.AddNewlyCreatedDBObject(nonBoldStyleRecord, true);
            return createdStyleId;
        }

        private static double GetEnvironmentalTableHeight(Table table)
        {
            var totalHeight = 0.0;
            for (var rowIndex = 0; rowIndex < table.Rows.Count; rowIndex++)
            {
                totalHeight += table.Rows[rowIndex].Height;
            }

            return totalHeight;
        }

        private static void HideCellBorders(Cell cell)
        {
            SetAllBorders(cell, false);
        }

        private static void SetAllBorders(Cell cell, bool visible)
        {
            cell.Borders.Top.IsVisible = visible;
            cell.Borders.Bottom.IsVisible = visible;
            cell.Borders.Left.IsVisible = visible;
            cell.Borders.Right.IsVisible = visible;
            cell.Borders.Horizontal.IsVisible = visible;
            cell.Borders.Vertical.IsVisible = visible;

            cell.Borders.Top.Color = EnvironmentalConditionsBorderColor;
            cell.Borders.Bottom.Color = EnvironmentalConditionsBorderColor;
            cell.Borders.Left.Color = EnvironmentalConditionsBorderColor;
            cell.Borders.Right.Color = EnvironmentalConditionsBorderColor;
            cell.Borders.Horizontal.Color = EnvironmentalConditionsBorderColor;
            cell.Borders.Vertical.Color = EnvironmentalConditionsBorderColor;
        }

        private static void SetCellBackground(Cell cell, Color color)
        {
            cell.IsBackgroundColorNone = false;
            cell.BackgroundColor = color;
        }

        private static void SetCellBackgroundNone(Cell cell)
        {
            cell.IsBackgroundColorNone = true;
        }

        private static int ApplyReplacementsToLayout(
            Transaction tr,
            Layout layout,
            IReadOnlyDictionary<int, string> replacements)
        {
            var updatedCount = 0;
            var blockTableRecord = (BlockTableRecord)tr.GetObject(layout.BlockTableRecordId, OpenMode.ForWrite);

            foreach (ObjectId entityId in blockTableRecord)
            {
                if (!(tr.GetObject(entityId, OpenMode.ForWrite, false) is Entity entity) || entity.IsErased)
                {
                    continue;
                }

                updatedCount += ApplyReplacementsToEntity(tr, entity, replacements);
            }

            return updatedCount;
        }

        private static int ApplyReplacementsToEntity(
            Transaction tr,
            Entity entity,
            IReadOnlyDictionary<int, string> replacements)
        {
            var updatedCount = 0;
            switch (entity)
            {
                case MText mtext:
                    updatedCount += ApplyReplacementText(
                        mtext.Contents,
                        replacements,
                        updatedText => mtext.Contents = updatedText,
                        (tokenNumber, text, tokenIndex, replacement) => ResolveReplacementForMTextToken(
                            tr,
                            mtext,
                            text,
                            tokenIndex,
                            tokenNumber,
                            replacement));
                    break;
                case DBText dbText:
                    if (TryResolveAllowedEntityColor(tr, dbText, null, out var dbTextColor))
                    {
                        updatedCount += ApplyReplacementText(
                            dbText.TextString,
                            replacements,
                            updatedText => dbText.TextString = updatedText,
                            (tokenNumber, _, _, replacement) => ResolveReplacementForColor(tokenNumber, replacement, dbTextColor));
                    }

                    break;
                case BlockReference blockReference:
                    foreach (ObjectId attributeId in blockReference.AttributeCollection)
                    {
                        if (tr.GetObject(attributeId, OpenMode.ForWrite, false) is AttributeReference attributeReference && !attributeReference.IsErased)
                        {
                            if (TryResolveAllowedEntityColor(tr, attributeReference, blockReference, out var attributeColor))
                            {
                                updatedCount += ApplyReplacementText(
                                    attributeReference.TextString,
                                    replacements,
                                    updatedText => attributeReference.TextString = updatedText,
                                    (tokenNumber, _, _, replacement) => ResolveReplacementForColor(tokenNumber, replacement, attributeColor));
                            }
                        }
                    }

                    break;
            }

            return updatedCount;
        }

        private static int ApplyReplacementText(
            string? currentText,
            IReadOnlyDictionary<int, string> replacements,
            Action<string> assignText,
            Func<int, string, int, string, string?>? resolveReplacementAtIndex = null)
        {
            if (string.IsNullOrEmpty(currentText))
            {
                return 0;
            }

            var updatedText = currentText;
            var changed = false;
            foreach (var pair in replacements.OrderByDescending(pair => pair.Key))
            {
                var token = $"{pair.Key}.";
                if (!updatedText.Contains(token, StringComparison.Ordinal))
                {
                    continue;
                }

                updatedText = ReplaceAllowedOccurrences(
                    updatedText,
                    pair.Key,
                    token,
                    pair.Value ?? string.Empty,
                    resolveReplacementAtIndex,
                    ref changed);
            }

            if (!changed || string.Equals(updatedText, currentText, StringComparison.Ordinal))
            {
                return 0;
            }

            assignText(updatedText);
            return 1;
        }

        private static string ReplaceAllowedOccurrences(
            string source,
            int tokenNumber,
            string token,
            string replacement,
            Func<int, string, int, string, string?>? resolveReplacementAtIndex,
            ref bool changed)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(token))
            {
                return source;
            }

            var firstIndex = source.IndexOf(token, StringComparison.Ordinal);
            if (firstIndex < 0)
            {
                return source;
            }

            if (resolveReplacementAtIndex == null)
            {
                var replaced = source.Replace(token, replacement, StringComparison.Ordinal);
                changed |= !string.Equals(replaced, source, StringComparison.Ordinal);
                return replaced;
            }

            var builder = new StringBuilder(source.Length);
            var searchIndex = 0;
            while (searchIndex < source.Length)
            {
                var tokenIndex = source.IndexOf(token, searchIndex, StringComparison.Ordinal);
                if (tokenIndex < 0)
                {
                    builder.Append(source, searchIndex, source.Length - searchIndex);
                    break;
                }

                builder.Append(source, searchIndex, tokenIndex - searchIndex);
                var resolvedReplacement = resolveReplacementAtIndex(tokenNumber, source, tokenIndex, replacement);
                if (resolvedReplacement != null)
                {
                    builder.Append(resolvedReplacement);
                    changed = true;
                }
                else
                {
                    builder.Append(token);
                }

                searchIndex = tokenIndex + token.Length;
            }

            return builder.ToString();
        }

        private static string? ResolveReplacementForMTextToken(
            Transaction tr,
            MText mtext,
            string mtextContents,
            int tokenIndex,
            int tokenNumber,
            string replacement)
        {
            if (!TryResolveMTextTokenColor(tr, mtext, mtextContents, tokenIndex, out var color))
            {
                return null;
            }

            return ResolveReplacementForColor(tokenNumber, replacement, color);
        }

        private static string ResolveReplacementForColor(int tokenNumber, string replacement, Color color)
        {
            if (tokenNumber != 2)
            {
                return replacement;
            }

            return IsBlueTextColor(color)
                ? FormatPurposeReplacementText(replacement)
                : FormatUppercaseReplacementText(replacement);
        }

        private static Layout? FindTargetLayout(Transaction tr, Database database)
        {
            if (database == null)
            {
                return null;
            }

            var layoutDictionary = (DBDictionary)tr.GetObject(database.LayoutDictionaryId, OpenMode.ForRead);
            Layout? best = null;
            var bestRank = int.MaxValue;

            foreach (DBDictionaryEntry entry in layoutDictionary)
            {
                if (!(tr.GetObject(entry.Value, OpenMode.ForRead) is Layout layout))
                {
                    continue;
                }

                var layoutName = (layout.LayoutName ?? string.Empty).Trim();
                if (layoutName.Equals("Model", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var rank = GetTargetLayoutRank(layoutName);
                if (rank == int.MaxValue)
                {
                    continue;
                }

                if (best == null ||
                    rank < bestRank ||
                    (rank == bestRank && string.Compare(layoutName, best.LayoutName, StringComparison.OrdinalIgnoreCase) < 0))
                {
                    best = layout;
                    bestRank = rank;
                }
            }

            return best;
        }

        private static bool ShouldUpdateMTextToken(
            Transaction tr,
            MText mtext,
            string mtextContents,
            int tokenIndex)
        {
            if (TryGetInlineMTextColor(mtextContents, tokenIndex, out var inlineColor))
            {
                return IsAllowedTextColor(inlineColor);
            }

            return ShouldUpdateTextEntity(tr, mtext);
        }

        private static bool ShouldUpdateTextEntity(
            Transaction tr,
            Entity entity,
            BlockReference? ownerBlockReference = null)
        {
            return TryResolveEffectiveEntityColor(tr, entity, ownerBlockReference, out var color)
                   && IsAllowedTextColor(color);
        }

        private static bool TryResolveMTextTokenColor(
            Transaction tr,
            MText mtext,
            string mtextContents,
            int tokenIndex,
            out Color color)
        {
            if (TryGetInlineMTextColor(mtextContents, tokenIndex, out color))
            {
                return IsAllowedTextColor(color);
            }

            return TryResolveAllowedEntityColor(tr, mtext, null, out color);
        }

        private static bool TryResolveAllowedEntityColor(
            Transaction tr,
            Entity entity,
            BlockReference? ownerBlockReference,
            out Color color)
        {
            if (!TryResolveEffectiveEntityColor(tr, entity, ownerBlockReference, out color))
            {
                return false;
            }

            return IsAllowedTextColor(color);
        }

        private static bool TryResolveEffectiveEntityColor(
            Transaction tr,
            Entity entity,
            BlockReference? ownerBlockReference,
            out Color color)
        {
            color = entity.Color;

            if (IsConcreteEntityColor(color))
            {
                return true;
            }

            if (color.ColorMethod == ColorMethod.ByLayer)
            {
                return TryResolveLayerColor(tr, entity.LayerId, out color);
            }

            if (color.ColorMethod == ColorMethod.ByBlock && ownerBlockReference != null)
            {
                return TryResolveEffectiveEntityColor(tr, ownerBlockReference, null, out color);
            }

            return false;
        }

        private static bool TryResolveLayerColor(Transaction tr, ObjectId layerId, out Color color)
        {
            color = default!;
            if (layerId.IsNull)
            {
                return false;
            }

            if (tr.GetObject(layerId, OpenMode.ForRead, false) is not LayerTableRecord layer || layer.IsErased)
            {
                return false;
            }

            color = layer.Color;
            return IsConcreteEntityColor(color);
        }

        private static bool IsConcreteEntityColor(Color color)
        {
            return color != null &&
                   color.ColorMethod != ColorMethod.ByLayer &&
                   color.ColorMethod != ColorMethod.ByBlock &&
                   color.ColorMethod != ColorMethod.None;
        }

        private static bool IsAllowedTextColor(Color color)
        {
            if (color == null)
            {
                return false;
            }

            if (color.ColorIndex == BlueColorIndex || color.ColorIndex == GreenColorIndex)
            {
                return true;
            }

            return IsAllowedRgbColor(color.Red, color.Green, color.Blue);
        }

        private static bool IsBlueTextColor(Color color)
        {
            if (color == null)
            {
                return false;
            }

            return color.ColorIndex == BlueColorIndex || (color.Red == 0 && color.Green == 0 && color.Blue == 255);
        }

        private static bool IsAllowedRgbColor(int red, int green, int blue)
        {
            return (red == 0 && green == 0 && blue == 255) ||
                   (red == 0 && green == 255 && blue == 0);
        }

        private static bool TryGetInlineMTextColor(string contents, int tokenIndex, out Color color)
        {
            color = default!;
            if (string.IsNullOrEmpty(contents) || tokenIndex < 0 || tokenIndex > contents.Length)
            {
                return false;
            }

            var stateStack = new Stack<InlineColorState>();
            var currentState = InlineColorState.None;

            for (var index = 0; index < contents.Length && index < tokenIndex; index++)
            {
                var current = contents[index];
                if (current == '{')
                {
                    stateStack.Push(currentState);
                    continue;
                }

                if (current == '}')
                {
                    currentState = stateStack.Count > 0 ? stateStack.Pop() : InlineColorState.None;
                    continue;
                }

                if (current != '\\' || index + 1 >= contents.Length)
                {
                    continue;
                }

                var code = contents[index + 1];
                if (code is '\\' or '{' or '}')
                {
                    index++;
                    continue;
                }

                if (code == 'C')
                {
                    if (TryReadMTextFormatValue(contents, index + 2, out var colorText, out var endIndex) &&
                        int.TryParse(colorText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var aciValue))
                    {
                        currentState = InlineColorState.FromAci(aciValue);
                        index = endIndex;
                        continue;
                    }
                }
                else if (code == 'c')
                {
                    if (TryReadMTextFormatValue(contents, index + 2, out var colorText, out var endIndex) &&
                        int.TryParse(colorText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rgbValue))
                    {
                        currentState = InlineColorState.FromRgb(rgbValue);
                        index = endIndex;
                        continue;
                    }
                }
            }

            if (!currentState.HasColor)
            {
                return false;
            }

            color = currentState.ToColor();
            return true;
        }

        private static bool TryReadMTextFormatValue(
            string contents,
            int startIndex,
            out string value,
            out int endIndex)
        {
            value = string.Empty;
            endIndex = startIndex;
            if (startIndex < 0 || startIndex >= contents.Length)
            {
                return false;
            }

            var semicolonIndex = contents.IndexOf(';', startIndex);
            if (semicolonIndex < 0)
            {
                return false;
            }

            value = contents.Substring(startIndex, semicolonIndex - startIndex).Trim();
            endIndex = semicolonIndex;
            return !string.IsNullOrWhiteSpace(value);
        }

        private static string NormalizeTrailingMeterSuffix(string value)
        {
            if (!value.EndsWith("m", StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }

            return $"{value[..^1].TrimEnd()}{value[^1]}";
        }

        private readonly struct InlineColorState
        {
            private readonly Color? _color;

            private InlineColorState(Color? color)
            {
                _color = color;
            }

            public static InlineColorState None => default;

            public bool HasColor => _color != null;

            public static InlineColorState FromAci(int colorIndex)
            {
                if (colorIndex <= 0 || colorIndex >= 256)
                {
                    return None;
                }

                return new InlineColorState(Color.FromColorIndex(ColorMethod.ByAci, (short)colorIndex));
            }

            public static InlineColorState FromRgb(int rgbValue)
            {
                if (rgbValue < 0)
                {
                    return None;
                }

                var red = (byte)((rgbValue >> 16) & 0xFF);
                var green = (byte)((rgbValue >> 8) & 0xFF);
                var blue = (byte)(rgbValue & 0xFF);
                return new InlineColorState(Color.FromRgb(red, green, blue));
            }

            public Color ToColor()
            {
                return _color ?? Color.FromColorIndex(ColorMethod.None, 0);
            }
        }

        private static int GetTargetLayoutRank(string layoutName)
        {
            if (IsFirstSheetLayoutName(layoutName))
            {
                return 0;
            }

            if (layoutName.StartsWith(LegacyLayoutPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return 1;
            }

            if (layoutName.Equals("1", StringComparison.OrdinalIgnoreCase))
            {
                return 2;
            }

            return int.MaxValue;
        }

        private static bool IsFirstSheetLayoutName(string layoutName)
        {
            if (string.IsNullOrWhiteSpace(layoutName))
            {
                return false;
            }

            var parts = layoutName
                .Trim()
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);

            return parts.Length >= 3 &&
                   parts[0].Equals("1", StringComparison.OrdinalIgnoreCase) &&
                   parts[1].Equals("of", StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryCollectClosedBoundaryLoops(
            Editor editor,
            Database database,
            IntPtr hostWindowHandle,
            out List<IReadOnlyList<Point2d>> boundaryLoops,
            out string message)
        {
            _ = hostWindowHandle;
            boundaryLoops = new List<IReadOnlyList<Point2d>>();
            message = string.Empty;

            var options = new PromptSelectionOptions
            {
                MessageForAdding = "\nSelect one or more closed boundary polylines: "
            };

            RefreshEditorPrompt(editor);
            var selectionResult = editor.GetSelection(options);
            if (selectionResult.Status != PromptStatus.OK)
            {
                message = "Boundary selection was canceled.";
                return false;
            }

            using var tr = database.TransactionManager.StartTransaction();
            foreach (SelectedObject selected in selectionResult.Value)
            {
                if (selected?.ObjectId.IsNull != false)
                {
                    continue;
                }

                if (!(tr.GetObject(selected.ObjectId, OpenMode.ForRead, false) is Entity entity) || entity.IsErased)
                {
                    continue;
                }

                if (TryGetClosedBoundaryVertices(tr, entity, out var vertices))
                {
                    boundaryLoops.Add(vertices);
                }
            }

            tr.Commit();

            if (boundaryLoops.Count == 0)
            {
                message = "No closed boundary polylines were selected.";
                return false;
            }

            return true;
        }

        private static bool TryGetClosedBoundaryVertices(
            Transaction tr,
            Entity entity,
            out List<Point2d> vertices)
        {
            vertices = new List<Point2d>();
            switch (entity)
            {
                case Polyline polyline when polyline.Closed && polyline.NumberOfVertices >= 3:
                    for (var index = 0; index < polyline.NumberOfVertices; index++)
                    {
                        vertices.Add(polyline.GetPoint2dAt(index));
                    }

                    return vertices.Count >= 3;
                case Polyline2d polyline2d when polyline2d.Closed:
                    foreach (ObjectId vertexId in polyline2d)
                    {
                        if (tr.GetObject(vertexId, OpenMode.ForRead, false) is Vertex2d vertex)
                        {
                            vertices.Add(new Point2d(vertex.Position.X, vertex.Position.Y));
                        }
                    }

                    return vertices.Count >= 3;
                case Polyline3d polyline3d when polyline3d.Closed:
                    foreach (ObjectId vertexId in polyline3d)
                    {
                        if (tr.GetObject(vertexId, OpenMode.ForRead, false) is PolylineVertex3d vertex)
                        {
                            vertices.Add(new Point2d(vertex.Position.X, vertex.Position.Y));
                        }
                    }

                    return vertices.Count >= 3;
                default:
                    return false;
            }
        }

        private static void RefreshEditorPrompt(Editor editor)
        {
            if (editor == null)
            {
                return;
            }

            try
            {
                editor.WriteMessage("\n");
                editor.PostCommandPrompt();
            }
            catch
            {
                // best-effort refresh only
            }
        }

        private static bool TryLoadSectionFrames(
            string drawingPath,
            int zone,
            out List<SectionFrame> frames,
            out string message)
        {
            frames = new List<SectionFrame>();
            message = string.Empty;

            var logger = new Logger();
            foreach (var folder in BuildSearchFolders(drawingPath))
            {
                if (!Directory.Exists(folder))
                {
                    continue;
                }

                if (!SectionIndexReader.TryLoadSectionOutlinesForZone(folder, zone, logger, out var outlines) ||
                    outlines.Count == 0)
                {
                    continue;
                }

                frames = BuildFrames(outlines);
                if (frames.Count > 0)
                {
                    return true;
                }
            }

            message = $"Unable to load section index data for UTM zone {zone}.";
            return false;
        }

        private static List<SectionFrame> BuildFrames(IReadOnlyList<SectionIndexReader.SectionOutlineEntry> outlines)
        {
            var frames = new List<SectionFrame>(outlines.Count);
            foreach (var outline in outlines)
            {
                if (outline?.Outline?.Vertices == null || outline.Outline.Vertices.Count < 3)
                {
                    continue;
                }

                if (!TryCreateFrame(outline, out var frame))
                {
                    continue;
                }

                frames.Add(frame);
            }

            return frames;
        }

        private static bool TryCreateFrame(
            SectionIndexReader.SectionOutlineEntry entry,
            [NotNullWhen(true)] out SectionFrame frame)
        {
            frame = default;
            var vertices = entry.Outline.Vertices;
            if (!TryGetExtents(vertices, out var extents))
            {
                return false;
            }

            if (!AtsPolygonFrameBuilder.TryBuildFrame(
                    vertices,
                    out var southWest,
                    out var eastUnit,
                    out var northUnit,
                    out var width,
                    out var height))
            {
                southWest = new Point2d(extents.MinX, extents.MinY);
                eastUnit = new Vector2d(1.0, 0.0);
                northUnit = new Vector2d(0.0, 1.0);
                width = Math.Max(1e-6, extents.MaxX - extents.MinX);
                height = Math.Max(1e-6, extents.MaxY - extents.MinY);
            }

            frame = new SectionFrame(
                NormalizeToken(entry.Key.Section),
                NormalizeToken(entry.Key.Township),
                NormalizeToken(entry.Key.Range),
                NormalizeToken(entry.Key.Meridian),
                vertices,
                extents,
                southWest,
                eastUnit,
                northUnit,
                width,
                height);
            return true;
        }

        private static bool TryGetExtents(IReadOnlyList<Point2d> vertices, out SectionExtents extents)
        {
            extents = default;
            if (vertices == null || vertices.Count == 0)
            {
                return false;
            }

            var minX = double.PositiveInfinity;
            var minY = double.PositiveInfinity;
            var maxX = double.NegativeInfinity;
            var maxY = double.NegativeInfinity;
            foreach (var vertex in vertices)
            {
                minX = Math.Min(minX, vertex.X);
                minY = Math.Min(minY, vertex.Y);
                maxX = Math.Max(maxX, vertex.X);
                maxY = Math.Max(maxY, vertex.Y);
            }

            extents = new SectionExtents(minX, minY, maxX, maxY);
            return true;
        }

        private static List<QuarterEntry> CollectQuarterEntries(
            IReadOnlyList<SectionFrame> frames,
            IReadOnlyList<IReadOnlyList<Point2d>> boundaryLoops)
        {
            var entries = new Dictionary<string, QuarterEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var frame in frames)
            {
                foreach (var quarter in BuildQuarterEntries(frame))
                {
                    if (!IsQuarterInsideAnyBoundary(quarter.Vertices, boundaryLoops))
                    {
                        continue;
                    }

                    var key = BuildQuarterKey(frame, quarter.Token);
                    if (!entries.ContainsKey(key))
                    {
                        entries[key] = quarter;
                    }
                }
            }

            return entries.Values
                .OrderBy(entry => entry.MeridianSortKey)
                .ThenBy(entry => entry.RangeSortKey)
                .ThenBy(entry => entry.TownshipSortKey)
                .ThenBy(entry => entry.SectionSortKey)
                .ThenBy(entry => entry.QuarterSortKey)
                .ToList();
        }

        private static IEnumerable<QuarterEntry> BuildQuarterEntries(SectionFrame frame)
        {
            yield return BuildQuarterEntry(frame, "NW", 0.0, 0.5, 0.5, 1.0, 0);
            yield return BuildQuarterEntry(frame, "NE", 0.5, 1.0, 0.5, 1.0, 1);
            yield return BuildQuarterEntry(frame, "SW", 0.0, 0.5, 0.0, 0.5, 2);
            yield return BuildQuarterEntry(frame, "SE", 0.5, 1.0, 0.0, 0.5, 3);
        }

        private static QuarterEntry BuildQuarterEntry(
            SectionFrame frame,
            string token,
            double uMin,
            double uMax,
            double tMin,
            double tMax,
            int sortIndex)
        {
            var vertices = new[]
            {
                QuarterLocalToWorld(frame, uMin, tMin),
                QuarterLocalToWorld(frame, uMax, tMin),
                QuarterLocalToWorld(frame, uMax, tMax),
                QuarterLocalToWorld(frame, uMin, tMax)
            };

            return new QuarterEntry(
                frame.Section,
                frame.Township,
                frame.Range,
                frame.Meridian,
                token,
                sortIndex,
                frame.SectionSortKey,
                frame.TownshipSortKey,
                frame.RangeSortKey,
                frame.MeridianSortKey,
                vertices);
        }

        private static Point2d QuarterLocalToWorld(SectionFrame frame, double u, double t)
        {
            var localOffset = (frame.EastUnit * (u * frame.Width)) + (frame.NorthUnit * (t * frame.Height));
            return frame.SouthWest + localOffset;
        }

        private static bool IsQuarterInsideAnyBoundary(
            IReadOnlyList<Point2d> quarterVertices,
            IReadOnlyList<IReadOnlyList<Point2d>> boundaryLoops)
        {
            foreach (var boundary in boundaryLoops)
            {
                if (IsPolygonInsidePolygon(quarterVertices, boundary))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool IsPolygonInsidePolygon(
            IReadOnlyList<Point2d> innerVertices,
            IReadOnlyList<Point2d> outerVertices)
        {
            if (innerVertices == null || innerVertices.Count == 0 || outerVertices == null || outerVertices.Count < 3)
            {
                return false;
            }

            foreach (var vertex in innerVertices)
            {
                if (!IsPointInsidePolygon(outerVertices, vertex, BoundaryToleranceMeters))
                {
                    return false;
                }
            }

            return true;
        }

        private static bool IsPointInsidePolygon(
            IReadOnlyList<Point2d> vertices,
            Point2d point,
            double tolerance)
        {
            if (DistanceSqToBoundary(vertices, point) <= tolerance * tolerance)
            {
                return true;
            }

            var inside = false;
            var previous = vertices[vertices.Count - 1];
            for (var index = 0; index < vertices.Count; index++)
            {
                var current = vertices[index];
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

        private static double DistanceSqToBoundary(IReadOnlyList<Point2d> vertices, Point2d point)
        {
            var minDistanceSq = double.MaxValue;
            for (var index = 0; index < vertices.Count; index++)
            {
                var start = vertices[index];
                var end = vertices[(index + 1) % vertices.Count];
                minDistanceSq = Math.Min(minDistanceSq, DistanceSqToSegment(point, start, end));
            }

            return minDistanceSq;
        }

        private static double DistanceSqToSegment(Point2d point, Point2d start, Point2d end)
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

        private static string FormatLocationEntries(IReadOnlyList<QuarterEntry> entries)
        {
            if (entries == null || entries.Count == 0)
            {
                return string.Empty;
            }

            var grouped = entries
                .GroupBy(entry => new GroupKey(entry.Meridian, entry.Range, entry.Township),
                    GroupKeyComparer.Instance)
                .OrderBy(group => group.Key.MeridianSortKey)
                .ThenBy(group => group.Key.RangeSortKey)
                .ThenBy(group => group.Key.TownshipSortKey)
                .ToList();

            var groupTexts = new List<string>(grouped.Count);
            foreach (var group in grouped)
            {
                var sectionTexts = group
                    .GroupBy(entry => entry.Section, StringComparer.OrdinalIgnoreCase)
                    .Select(sectionGroup => new
                    {
                        SortKey = GetSectionSortKey(sectionGroup.Key),
                        Text = FormatSectionEntry(sectionGroup.Key, sectionGroup.Select(item => item.Token).ToList())
                    })
                    .Where(item => !string.IsNullOrWhiteSpace(item.Text))
                    .OrderBy(item => item.SortKey)
                    .ThenBy(item => item.Text, StringComparer.OrdinalIgnoreCase)
                    .Select(item => item.Text)
                    .ToList();

                if (sectionTexts.Count == 0)
                {
                    continue;
                }

                var suffix = $"Twp. {group.Key.Township} Rge. {group.Key.Range} W.{group.Key.Meridian}M";
                groupTexts.Add($"{string.Join(", ", sectionTexts)}, {suffix}");
            }

            return string.Join(" & ", groupTexts);
        }

        private static string FormatSectionEntry(string section, IReadOnlyCollection<string> quarterTokens)
        {
            var tokens = new HashSet<string>(quarterTokens.Select(token => (token ?? string.Empty).Trim().ToUpperInvariant()), StringComparer.OrdinalIgnoreCase);
            if (tokens.Count == 4)
            {
                return $"Sec. {section}";
            }

            var parts = new List<string>();
            if (tokens.Contains("NW") && tokens.Contains("NE"))
            {
                parts.Add("N.1/2");
                tokens.Remove("NW");
                tokens.Remove("NE");
            }
            else if (tokens.Contains("SW") && tokens.Contains("SE"))
            {
                parts.Add("S.1/2");
                tokens.Remove("SW");
                tokens.Remove("SE");
            }
            else if (tokens.Contains("NE") && tokens.Contains("SE"))
            {
                parts.Add("E.1/2");
                tokens.Remove("NE");
                tokens.Remove("SE");
            }
            else if (tokens.Contains("NW") && tokens.Contains("SW"))
            {
                parts.Add("W.1/2");
                tokens.Remove("NW");
                tokens.Remove("SW");
            }

            foreach (var token in new[] { "NW", "NE", "SW", "SE" })
            {
                if (tokens.Contains(token))
                {
                    parts.Add(FormatQuarterToken(token));
                }
            }

            if (parts.Count == 0)
            {
                return string.Empty;
            }

            return $"{string.Join(", ", parts)} Sec. {section}";
        }

        private static string FormatQuarterToken(string token)
        {
            return (token ?? string.Empty).Trim().ToUpperInvariant() switch
            {
                "NW" => "N.W.1/4",
                "NE" => "N.E.1/4",
                "SW" => "S.W.1/4",
                "SE" => "S.E.1/4",
                _ => token
            };
        }

        private static string BuildQuarterKey(SectionFrame frame, string token)
        {
            return string.Join("|", frame.Section, frame.Township, frame.Range, frame.Meridian, token);
        }

        private static IEnumerable<string> BuildSearchFolders(string? drawingPath)
        {
            var folders = new List<string>();
            AddFolder(folders, Environment.GetEnvironmentVariable("WLS_SECTION_INDEX_FOLDER"));
            AddFolder(folders, Environment.GetEnvironmentVariable("ATSBUILD_SECTION_INDEX_FOLDER"));
            AddFolder(folders, Environment.GetEnvironmentVariable("ATS_SECTION_INDEX_FOLDER"));
            AddFolder(folders, TryGetDrawingFolder(drawingPath));
            AddFolder(folders, Path.GetDirectoryName(typeof(TitleBlockControlService).Assembly.Location));
            AddFolder(folders, Environment.CurrentDirectory);
            AddFolder(folders, DefaultSectionIndexFolder);
            return folders;
        }

        private static void AddFolder(List<string> folders, string? folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return;
            }

            var trimmed = folder.Trim();
            if (!folders.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                folders.Add(trimmed);
            }
        }

        private static string? TryGetDrawingFolder(string? drawingPath)
        {
            if (string.IsNullOrWhiteSpace(drawingPath))
            {
                return null;
            }

            try
            {
                return Path.GetDirectoryName(drawingPath);
            }
            catch
            {
                return null;
            }
        }

        private static bool TryParseUtmZone(string? utmZone, out int zone)
        {
            zone = 0;
            var normalized = (utmZone ?? string.Empty).Trim();
            if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out zone) &&
                (zone == 11 || zone == 12))
            {
                return true;
            }

            return false;
        }

        private static string NormalizeToken(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var digits = new string(value.Where(char.IsDigit).ToArray());
            if (digits.Length > 0 &&
                int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            {
                return number.ToString(CultureInfo.InvariantCulture);
            }

            return value.Trim().TrimStart('0');
        }

        private static string NormalizeReplacementText(string? value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private static string FormatSweepTypeReplacementText(string? value)
        {
            return FormatUppercaseReplacementText(value);
        }

        private static string FormatUppercaseReplacementText(string? value)
        {
            var normalized = NormalizeWhitespace(value);
            return string.IsNullOrEmpty(normalized) ? string.Empty : normalized.ToUpperInvariant();
        }

        private static string FormatPurposeReplacementText(string? value)
        {
            var normalized = NormalizeWhitespace(value);
            return normalized switch
            {
                "PIPELINE RIGHT-OF-WAY" => "Pipeline Right-of-Way",
                "PIPELINE RIGHT-OF-WAY & TEMPORARY AREAS" => "Pipeline Right-of-Way & Temporary Areas",
                "PAD SITE" => "Pad Site",
                "PAD SITE & ACCESS ROAD" => "Pad Site & Access Road",
                "PAD SITE & TEMPORARY AREAS" => "Pad Site & Temporary Areas",
                "PAD SITE, ACCESS ROAD & TEMPORARY AREAS" => "Pad Site, Access Road & Temporary Areas",
                "TEMPORARY AREAS" => "Temporary Areas",
                _ => normalized
            };
        }

        private static string NormalizeWhitespace(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return string.Join(" ", value
                .Trim()
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        }

        private static string FormatSurveyDate(DateTime date)
        {
            return $"{date:MMMM} {date.Day}{GetOrdinalSuffix(date.Day)}, {date:yyyy}";
        }

        private static string FormatEnvironmentalConditionsSurveyDate(DateTime date)
        {
            return $"{date.ToString("MMMM", CultureInfo.InvariantCulture).ToUpperInvariant()} {date.Day}{GetOrdinalSuffix(date.Day)}, {date:yyyy}";
        }

        private static string BuildExistingLandCategory(string categoryLabel, IEnumerable<string>? items)
        {
            var values = NormalizeDisplayValues(items);
            if (values.Count == 0)
            {
                return string.Empty;
            }

            return $"{categoryLabel} ({JoinReadableList(values)})";
        }

        private static List<string> NormalizeDisplayValues(IEnumerable<string>? values)
        {
            if (values == null)
            {
                return new List<string>();
            }

            var items = new List<string>();
            foreach (var value in values)
            {
                var trimmed = (value ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    continue;
                }

                if (!items.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                {
                    items.Add(trimmed);
                }
            }

            return items;
        }

        private static string JoinReadableList(IReadOnlyList<string> values)
        {
            if (values.Count == 0)
            {
                return string.Empty;
            }

            if (values.Count == 1)
            {
                return values[0];
            }

            if (values.Count == 2)
            {
                return $"{values[0]} and {values[1]}";
            }

            return string.Join(", ", values.Take(values.Count - 1)) + $", and {values[^1]}";
        }

        private static string JoinReadableList(IEnumerable<string> values)
        {
            return JoinReadableList(values.ToList());
        }

        private static string GetOrdinalSuffix(int day)
        {
            if ((day % 100) is 11 or 12 or 13)
            {
                return "th";
            }

            return (day % 10) switch
            {
                1 => "st",
                2 => "nd",
                3 => "rd",
                _ => "th"
            };
        }

        private static int GetSectionSortKey(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return int.MaxValue;
            }

            var digits = new string(text.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            return int.MaxValue;
        }

        private static int GetNumericSortKey(string value)
        {
            var digits = new string((value ?? string.Empty).Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return parsed;
            }

            return int.MaxValue;
        }

        private sealed class GroupKeyComparer : IEqualityComparer<GroupKey>
        {
            public static readonly GroupKeyComparer Instance = new GroupKeyComparer();

            public bool Equals(GroupKey x, GroupKey y)
            {
                return string.Equals(x.Meridian, y.Meridian, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(x.Range, y.Range, StringComparison.OrdinalIgnoreCase) &&
                       string.Equals(x.Township, y.Township, StringComparison.OrdinalIgnoreCase);
            }

            public int GetHashCode(GroupKey obj)
            {
                return HashCode.Combine(
                    obj.Meridian?.ToUpperInvariant(),
                    obj.Range?.ToUpperInvariant(),
                    obj.Township?.ToUpperInvariant());
            }
        }

        private readonly struct GroupKey
        {
            public GroupKey(string meridian, string range, string township)
            {
                Meridian = meridian ?? string.Empty;
                Range = range ?? string.Empty;
                Township = township ?? string.Empty;
                MeridianSortKey = GetNumericSortKey(Meridian);
                RangeSortKey = GetNumericSortKey(Range);
                TownshipSortKey = GetNumericSortKey(Township);
            }

            public string Meridian { get; }
            public string Range { get; }
            public string Township { get; }
            public int MeridianSortKey { get; }
            public int RangeSortKey { get; }
            public int TownshipSortKey { get; }
        }

        private readonly struct QuarterEntry
        {
            public QuarterEntry(
                string section,
                string township,
                string range,
                string meridian,
                string token,
                int quarterSortKey,
                int sectionSortKey,
                int townshipSortKey,
                int rangeSortKey,
                int meridianSortKey,
                IReadOnlyList<Point2d> vertices)
            {
                Section = section ?? string.Empty;
                Township = township ?? string.Empty;
                Range = range ?? string.Empty;
                Meridian = meridian ?? string.Empty;
                Token = token ?? string.Empty;
                QuarterSortKey = quarterSortKey;
                SectionSortKey = sectionSortKey;
                TownshipSortKey = townshipSortKey;
                RangeSortKey = rangeSortKey;
                MeridianSortKey = meridianSortKey;
                Vertices = vertices ?? Array.Empty<Point2d>();
            }

            public string Section { get; }
            public string Township { get; }
            public string Range { get; }
            public string Meridian { get; }
            public string Token { get; }
            public int QuarterSortKey { get; }
            public int SectionSortKey { get; }
            public int TownshipSortKey { get; }
            public int RangeSortKey { get; }
            public int MeridianSortKey { get; }
            public IReadOnlyList<Point2d> Vertices { get; }
        }

        private readonly struct SectionFrame
        {
            public SectionFrame(
                string section,
                string township,
                string range,
                string meridian,
                IReadOnlyList<Point2d> vertices,
                SectionExtents extents,
                Point2d southWest,
                Vector2d eastUnit,
                Vector2d northUnit,
                double width,
                double height)
            {
                Section = section ?? string.Empty;
                Township = township ?? string.Empty;
                Range = range ?? string.Empty;
                Meridian = meridian ?? string.Empty;
                Vertices = vertices ?? Array.Empty<Point2d>();
                Extents = extents;
                SouthWest = southWest;
                EastUnit = eastUnit;
                NorthUnit = northUnit;
                Width = width;
                Height = height;
                SectionSortKey = GetNumericSortKey(Section);
                TownshipSortKey = GetNumericSortKey(Township);
                RangeSortKey = GetNumericSortKey(Range);
                MeridianSortKey = GetNumericSortKey(Meridian);
            }

            public string Section { get; }
            public string Township { get; }
            public string Range { get; }
            public string Meridian { get; }
            public IReadOnlyList<Point2d> Vertices { get; }
            public SectionExtents Extents { get; }
            public Point2d SouthWest { get; }
            public Vector2d EastUnit { get; }
            public Vector2d NorthUnit { get; }
            public double Width { get; }
            public double Height { get; }
            public int SectionSortKey { get; }
            public int TownshipSortKey { get; }
            public int RangeSortKey { get; }
            public int MeridianSortKey { get; }
        }

        private readonly struct SectionExtents
        {
            public SectionExtents(double minX, double minY, double maxX, double maxY)
            {
                MinX = minX;
                MinY = minY;
                MaxX = maxX;
                MaxY = maxY;
            }

            public double MinX { get; }
            public double MinY { get; }
            public double MaxX { get; }
            public double MaxY { get; }
        }

    }
}
