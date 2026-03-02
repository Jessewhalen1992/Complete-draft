using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AtsBackgroundBuilder.Core
{
    internal sealed class ShapeUpdatePlan
    {
        public ShapeUpdatePlan(
            string shapeType,
            string confirmationMessage,
            string sourceDisplayPath,
            string sourceCopyPath,
            string destinationPath,
            IReadOnlyList<string>? shapeBaseNames)
        {
            ShapeType = shapeType ?? string.Empty;
            ConfirmationMessage = confirmationMessage ?? string.Empty;
            SourceDisplayPath = sourceDisplayPath ?? string.Empty;
            SourceCopyPath = sourceCopyPath ?? string.Empty;
            DestinationPath = destinationPath ?? string.Empty;
            ShapeBaseNames = shapeBaseNames?.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).ToArray()
                ?? Array.Empty<string>();
        }

        public string ShapeType { get; }
        public string ConfirmationMessage { get; }
        public string SourceDisplayPath { get; }
        public string SourceCopyPath { get; }
        public string DestinationPath { get; }
        public IReadOnlyList<string> ShapeBaseNames { get; }
        public bool UseShapeSetFilter => ShapeBaseNames.Count > 0;
    }

    internal static class ShapeUpdateService
    {
        public const string DispositionShapeType = "Disposition";
        public const string CompassMappingShapeType = "Compass Mapping";
        public const string CrownReservationsShapeType = "Crown Reservations";

        private static readonly string[] DispositionShapeUpdateSourceRoots =
        {
            @"N:\Mapping\FTP Updates\AltaLIS",
            @"O:\Mapping\FTP Updates\AltaLIS",
        };
        private static readonly string[] CompassMappingShapeUpdateSourceRoots =
        {
            @"N:\Mapping\Mapping\COMPASS_SURVEYED\SHP",
            @"O:\Mapping\Mapping\COMPASS_SURVEYED\SHP",
        };
        private static readonly string[] CrownReservationsShapeUpdateSourceRoots =
        {
            @"N:\Mapping\FTP Updates\GoA",
            @"O:\Mapping\FTP Updates\GoA",
        };
        private static readonly string[] CompassMappingShapeBaseNames =
        {
            "SURVEYED_POLYGON_N83UTMZ11",
            "SURVEYED_POLYGON_N83UTMZ12",
        };
        private static readonly string[] CrownReservationsShapeBaseNames =
        {
            "CrownLandReservations",
        };

        private const string DispositionShapeDestinationFolder = @"C:\AUTOCAD-SETUP CG\SHAPE FILES\DISPOS";
        private const string CompassMappingShapeDestinationFolder = @"C:\AUTOCAD-SETUP CG\SHAPE FILES\COMPASS MAPPING";
        private const string CrownReservationsShapeDestinationFolder = @"C:\AUTOCAD-SETUP CG\SHAPE FILES\CLR";

        private static readonly IReadOnlyList<string> SupportedShapeTypesInternal = new[]
        {
            DispositionShapeType,
            CompassMappingShapeType,
            CrownReservationsShapeType,
        };

        public static IReadOnlyList<string> SupportedShapeTypes => SupportedShapeTypesInternal;

        public static bool TryPreparePlan(string shapeType, out ShapeUpdatePlan? plan, out string error)
        {
            plan = null;
            error = string.Empty;
            var normalizedType = shapeType?.Trim() ?? string.Empty;

            if (string.Equals(normalizedType, DispositionShapeType, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryResolveNewestDidsFolderAcrossRoots(
                        out var sourceRoot,
                        out var newestFolder,
                        out var newestDate,
                        out var newestFolderError))
                {
                    error = newestFolderError;
                    return false;
                }

                plan = new ShapeUpdatePlan(
                    DispositionShapeType,
                    "Copy latest Disposition shape files?\n\n" +
                    $"Source root: {sourceRoot}\n" +
                    $"Latest folder: {newestFolder}\n" +
                    $"Detected date: {newestDate:yyyy-MM-dd}\n\n" +
                    $"Destination: {DispositionShapeDestinationFolder}\n\n" +
                    "This will replace current destination contents.",
                    newestFolder,
                    newestFolder,
                    DispositionShapeDestinationFolder,
                    Array.Empty<string>());
                return true;
            }

            if (string.Equals(normalizedType, CompassMappingShapeType, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryResolveFirstExistingRootAcrossRoots(
                        CompassMappingShapeUpdateSourceRoots,
                        "COMPASS MAPPING update folder",
                        out var sourceRoot,
                        out var rootError))
                {
                    error = rootError;
                    return false;
                }

                plan = new ShapeUpdatePlan(
                    CompassMappingShapeType,
                    "Copy COMPASS MAPPING shape files?\n\n" +
                    $"Source: {sourceRoot}\n\n" +
                    $"Shape sets: {string.Join(", ", CompassMappingShapeBaseNames)}\n\n" +
                    $"Destination: {CompassMappingShapeDestinationFolder}\n\n" +
                    "This will replace current destination contents.",
                    sourceRoot,
                    sourceRoot,
                    CompassMappingShapeDestinationFolder,
                    CompassMappingShapeBaseNames);
                return true;
            }

            if (string.Equals(normalizedType, CrownReservationsShapeType, StringComparison.OrdinalIgnoreCase))
            {
                if (!TryResolveNewestDatedFolderAcrossRoots(
                        CrownReservationsShapeUpdateSourceRoots,
                        "Crown Reservations update folder",
                        out var sourceRoot,
                        out var newestFolder,
                        out var newestDate,
                        out var rootError))
                {
                    error = rootError;
                    return false;
                }

                plan = new ShapeUpdatePlan(
                    CrownReservationsShapeType,
                    "Copy Crown Reservations shape files?\n\n" +
                    $"Source root: {sourceRoot}\n" +
                    $"Latest folder: {newestFolder}\n" +
                    $"Detected date: {newestDate:yyyy-MM-dd}\n\n" +
                    $"Shape sets: {string.Join(", ", CrownReservationsShapeBaseNames)}\n\n" +
                    $"Destination: {CrownReservationsShapeDestinationFolder}\n\n" +
                    "This will replace current destination contents.",
                    newestFolder,
                    newestFolder,
                    CrownReservationsShapeDestinationFolder,
                    CrownReservationsShapeBaseNames);
                return true;
            }

            error = $"Unsupported shape type: {normalizedType}";
            return false;
        }

        public static int ExecutePlan(ShapeUpdatePlan plan)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            return plan.UseShapeSetFilter
                ? ReplaceDirectoryContentsWithSelectedShapeSets(plan.SourceCopyPath, plan.DestinationPath, plan.ShapeBaseNames)
                : ReplaceDirectoryContents(plan.SourceCopyPath, plan.DestinationPath);
        }

        private static bool TryFindNewestDidsFolder(string sourceRoot, out string newestFolder, out DateTime newestDate, out string error)
        {
            newestFolder = string.Empty;
            newestDate = default;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
            {
                error = $"Source root not found: {sourceRoot}";
                return false;
            }

            var candidates = new List<(string FolderPath, DateTime Date)>();
            foreach (var folder in Directory.GetDirectories(sourceRoot, "dids_*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(folder) ?? string.Empty;
                if (TryParseDateFromFolderName(name, out var parsedDate))
                {
                    candidates.Add((folder, parsedDate));
                }
            }

            if (candidates.Count == 0)
            {
                error = $"No dated dids_* folders found under:\n{sourceRoot}";
                return false;
            }

            var selected = candidates
                .OrderByDescending(c => c.Date)
                .ThenByDescending(c => Path.GetFileName(c.FolderPath), StringComparer.OrdinalIgnoreCase)
                .First();

            newestFolder = selected.FolderPath;
            newestDate = selected.Date;
            return true;
        }

        private static bool TryResolveNewestDidsFolderAcrossRoots(
            out string selectedSourceRoot,
            out string newestFolder,
            out DateTime newestDate,
            out string error)
        {
            selectedSourceRoot = string.Empty;
            newestFolder = string.Empty;
            newestDate = default;
            error = string.Empty;

            var existingRoots = DispositionShapeUpdateSourceRoots
                .Where(Directory.Exists)
                .ToList();
            if (existingRoots.Count == 0)
            {
                error = "Unable to find AltaLIS FTP update folder.\nChecked:\n" + string.Join("\n", DispositionShapeUpdateSourceRoots);
                return false;
            }

            var foundAny = false;
            var bestDate = DateTime.MinValue;
            var bestFolder = string.Empty;
            var bestRoot = string.Empty;
            var diagnostics = new List<string>();

            for (var i = 0; i < existingRoots.Count; i++)
            {
                var root = existingRoots[i];
                if (!TryFindNewestDidsFolder(root, out var candidateFolder, out var candidateDate, out var rootError))
                {
                    diagnostics.Add(rootError);
                    continue;
                }

                if (!foundAny || candidateDate > bestDate)
                {
                    foundAny = true;
                    bestDate = candidateDate;
                    bestFolder = candidateFolder;
                    bestRoot = root;
                }
            }

            if (!foundAny)
            {
                error = "No dated dids_* folders were found in available AltaLIS roots.\n" + string.Join("\n", diagnostics);
                return false;
            }

            selectedSourceRoot = bestRoot;
            newestFolder = bestFolder;
            newestDate = bestDate;
            return true;
        }

        private static bool TryResolveFirstExistingRootAcrossRoots(
            IReadOnlyList<string> roots,
            string sourceDescription,
            out string selectedRoot,
            out string error)
        {
            selectedRoot = string.Empty;
            error = string.Empty;
            if (roots == null || roots.Count == 0)
            {
                error = $"No candidate roots configured for {sourceDescription}.";
                return false;
            }

            for (var i = 0; i < roots.Count; i++)
            {
                var root = roots[i];
                if (!string.IsNullOrWhiteSpace(root) && Directory.Exists(root))
                {
                    selectedRoot = root;
                    return true;
                }
            }

            error = $"Unable to find {sourceDescription}.\nChecked:\n" + string.Join("\n", roots);
            return false;
        }

        private static bool TryResolveNewestDatedFolderAcrossRoots(
            IReadOnlyList<string> roots,
            string sourceDescription,
            out string selectedSourceRoot,
            out string newestFolder,
            out DateTime newestDate,
            out string error)
        {
            selectedSourceRoot = string.Empty;
            newestFolder = string.Empty;
            newestDate = default;
            error = string.Empty;

            var existingRoots = roots
                .Where(Directory.Exists)
                .ToList();
            if (existingRoots.Count == 0)
            {
                error = $"Unable to find {sourceDescription}.\nChecked:\n" + string.Join("\n", roots);
                return false;
            }

            var foundAny = false;
            var bestDate = DateTime.MinValue;
            var bestFolder = string.Empty;
            var bestRoot = string.Empty;
            var diagnostics = new List<string>();
            for (var i = 0; i < existingRoots.Count; i++)
            {
                var root = existingRoots[i];
                if (!TryFindNewestDatedSubfolder(root, out var candidateFolder, out var candidateDate, out var rootError))
                {
                    diagnostics.Add(rootError);
                    continue;
                }

                if (!foundAny || candidateDate > bestDate)
                {
                    foundAny = true;
                    bestDate = candidateDate;
                    bestFolder = candidateFolder;
                    bestRoot = root;
                }
            }

            if (!foundAny)
            {
                error = $"No dated folders were found in available roots for {sourceDescription}.\n" + string.Join("\n", diagnostics);
                return false;
            }

            selectedSourceRoot = bestRoot;
            newestFolder = bestFolder;
            newestDate = bestDate;
            return true;
        }

        private static bool TryFindNewestDatedSubfolder(string sourceRoot, out string newestFolder, out DateTime newestDate, out string error)
        {
            newestFolder = string.Empty;
            newestDate = default;
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
            {
                error = $"Source root not found: {sourceRoot}";
                return false;
            }

            var candidates = new List<(string FolderPath, DateTime Date)>();
            foreach (var folder in Directory.GetDirectories(sourceRoot, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(folder) ?? string.Empty;
                if (TryParseDateFromFolderName(name, out var parsedDate))
                {
                    candidates.Add((folder, parsedDate));
                }
            }

            if (candidates.Count == 0)
            {
                error = $"No dated folders found under:\n{sourceRoot}";
                return false;
            }

            var selected = candidates
                .OrderByDescending(c => c.Date)
                .ThenByDescending(c => Path.GetFileName(c.FolderPath), StringComparer.OrdinalIgnoreCase)
                .First();

            newestFolder = selected.FolderPath;
            newestDate = selected.Date;
            return true;
        }

        private static bool TryParseDateFromFolderName(string folderName, out DateTime date)
        {
            date = default;
            if (string.IsNullOrWhiteSpace(folderName))
            {
                return false;
            }

            var match = Regex.Match(folderName, @"(?<a>\d{1,2})-(?<b>\d{1,2})-(?<y>\d{2,4})");
            if (!match.Success)
            {
                return false;
            }

            if (!int.TryParse(match.Groups["a"].Value, out var first) ||
                !int.TryParse(match.Groups["b"].Value, out var second) ||
                !int.TryParse(match.Groups["y"].Value, out var year))
            {
                return false;
            }

            if (year < 100)
            {
                year += 2000;
            }

            int month;
            int day;
            if (first > 12 && second <= 12)
            {
                // Unambiguous: dd-MM-yyyy
                day = first;
                month = second;
            }
            else if (second > 12 && first <= 12)
            {
                // Unambiguous: MM-dd-yyyy
                month = first;
                day = second;
            }
            else
            {
                // Ambiguous (both <= 12): default to dd-MM-yyyy to match FTP folder naming.
                day = first;
                month = second;
            }

            if (month < 1 || month > 12 || day < 1)
            {
                return false;
            }

            var maxDay = DateTime.DaysInMonth(year, month);
            if (day > maxDay)
            {
                return false;
            }

            date = new DateTime(year, month, day);
            return true;
        }

        private static int ReplaceDirectoryContents(string sourceDirectory, string destinationDirectory)
        {
            if (!Directory.Exists(sourceDirectory))
            {
                throw new DirectoryNotFoundException("Source folder not found: " + sourceDirectory);
            }

            Directory.CreateDirectory(destinationDirectory);
            ClearDirectoryContents(destinationDirectory);

            var copiedCount = 0;
            foreach (var sourcePath in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
                var destinationPath = Path.Combine(destinationDirectory, relativePath);
                var destinationFolder = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(destinationFolder))
                {
                    Directory.CreateDirectory(destinationFolder);
                }

                File.Copy(sourcePath, destinationPath, overwrite: true);
                copiedCount++;
            }

            return copiedCount;
        }

        private static int ReplaceDirectoryContentsWithSelectedShapeSets(
            string sourceDirectory,
            string destinationDirectory,
            IReadOnlyList<string> shapeBaseNames)
        {
            if (!Directory.Exists(sourceDirectory))
            {
                throw new DirectoryNotFoundException("Source folder not found: " + sourceDirectory);
            }

            Directory.CreateDirectory(destinationDirectory);
            ClearDirectoryContents(destinationDirectory);

            var selectedBaseNames = new HashSet<string>(
                shapeBaseNames?.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()) ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            var copiedCount = 0;
            foreach (var sourcePath in Directory.GetFiles(sourceDirectory, "*", SearchOption.AllDirectories))
            {
                var baseName = Path.GetFileNameWithoutExtension(sourcePath);
                if (string.IsNullOrWhiteSpace(baseName) || !selectedBaseNames.Contains(baseName))
                {
                    continue;
                }

                var relativePath = Path.GetRelativePath(sourceDirectory, sourcePath);
                var destinationPath = Path.Combine(destinationDirectory, relativePath);
                var destinationFolder = Path.GetDirectoryName(destinationPath);
                if (!string.IsNullOrWhiteSpace(destinationFolder))
                {
                    Directory.CreateDirectory(destinationFolder);
                }

                File.Copy(sourcePath, destinationPath, overwrite: true);
                copiedCount++;
            }

            return copiedCount;
        }

        private static void ClearDirectoryContents(string directoryPath)
        {
            if (!Directory.Exists(directoryPath))
            {
                return;
            }

            var directory = new DirectoryInfo(directoryPath);
            foreach (var file in directory.GetFiles("*", SearchOption.TopDirectoryOnly))
            {
                file.IsReadOnly = false;
                file.Delete();
            }

            foreach (var childDirectory in directory.GetDirectories("*", SearchOption.TopDirectoryOnly))
            {
                childDirectory.Delete(recursive: true);
            }
        }
    }
}
