using System;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;

namespace WildlifeSweeps
{
    internal static class PluginLogger
    {
        public static string? TryLogException(Document doc, string context, Exception ex)
        {
            try
            {
                var drawingPath = doc.Database.Filename;
                var directory = !string.IsNullOrWhiteSpace(drawingPath)
                    ? Path.GetDirectoryName(drawingPath)
                    : null;
                var targetDirectory = string.IsNullOrWhiteSpace(directory)
                    ? Environment.CurrentDirectory
                    : directory;
                var logPath = Path.Combine(targetDirectory, "wildlife_sweeps_errors.log");

                var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}: {ex}\n";
                File.AppendAllText(logPath, entry);
                return logPath;
            }
            catch
            {
                return null;
            }
        }
    }
}
