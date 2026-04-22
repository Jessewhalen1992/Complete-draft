using System;
using System.IO;
using System.Text;

namespace Compass.Infrastructure;

internal static class CompassStartupDiagnostics
{
    private static readonly object SyncRoot = new();
    private static readonly string LogFilePath = Path.Combine(Path.GetTempPath(), "Compass-netload.log");

    public static string LogPath => LogFilePath;

    public static void Log(string message)
    {
        try
        {
            var line = string.Format(
                "{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}{3}",
                DateTime.Now,
                AppDomain.CurrentDomain.FriendlyName,
                message,
                Environment.NewLine);

            lock (SyncRoot)
            {
                File.AppendAllText(LogFilePath, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics should never interfere with plugin startup.
        }
    }

    public static void LogException(string context, Exception ex)
    {
        if (ex == null)
        {
            return;
        }

        Log(context + " failed: " + ex.GetType().FullName + ": " + ex.Message);

        var current = ex.InnerException;
        var depth = 1;
        while (current != null)
        {
            Log(
                context
                + " inner[" + depth + "]: "
                + current.GetType().FullName
                + ": "
                + current.Message);

            current = current.InnerException;
            depth++;
        }
    }
}
