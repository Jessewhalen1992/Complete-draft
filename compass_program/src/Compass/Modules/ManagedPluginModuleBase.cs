using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;

namespace Compass.Modules;

/// <summary>
/// Base implementation for Compass modules that load a managed AutoCAD plug-in
/// from disk and invoke a command exposed via <see cref="CommandMethodAttribute"/>.
/// </summary>
public abstract class ManagedPluginModuleBase : ICompassModule
{
    /// <summary>
    /// A unique identifier for the module; used as the key when registering with the launcher.
    /// </summary>
    public abstract string Id { get; }

    /// <summary>
    /// The label displayed in the Compass UI.
    /// </summary>
    public abstract string DisplayName { get; }

    /// <summary>
    /// Short description displayed beneath the module title.
    /// </summary>
    public abstract string Description { get; }

    /// <summary>
    /// Candidate DLL paths to probe, in priority order.
    /// </summary>
    protected abstract IReadOnlyList<string> CandidateDllPaths { get; }

    /// <summary>
    /// The AutoCAD command exposed by the managed plug-in.
    /// </summary>
    protected abstract string CommandName { get; }

    public void Show()
    {
        try
        {
            void OnIdle(object? sender, EventArgs args)
            {
                Application.Idle -= OnIdle;
                LaunchSafe();
            }

            Application.Idle += OnIdle;
        }
        catch (Exception ex)
        {
            Application.ShowAlertDialog($"Failed to launch {DisplayName}: {ex.Message}");
        }
    }

    private void LaunchSafe()
    {
        try
        {
            Launch();
        }
        catch (Exception ex)
        {
            Application.ShowAlertDialog($"Failed to launch {DisplayName}: {ex.Message}");
        }
    }

    private void Launch()
    {
        var document = Application.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            Application.ShowAlertDialog("Open a drawing first.");
            return;
        }

        var dllPath = CandidateDllPaths.FirstOrDefault(File.Exists);
        if (dllPath == null)
        {
            Application.ShowAlertDialog(BuildMissingAssemblyMessage());
            return;
        }

        var folder = Path.GetDirectoryName(dllPath);
        if (!string.IsNullOrWhiteSpace(folder))
        {
            TrustPathIfNeeded(folder);
        }

        try
        {
            Assembly.LoadFrom(dllPath);
        }
        catch (Exception ex)
        {
            Application.ShowAlertDialog(BuildLoadFailureMessage(ex));
            return;
        }

        document.SendStringToExecute("\u001B\u001B", true, false, false);
        document.SendStringToExecute($"{CommandName}\n", true, false, false);
    }

    protected virtual string BuildMissingAssemblyMessage()
    {
        return $"{GetAssemblyDisplayName()} was not found in the expected locations.";
    }

    protected virtual string BuildLoadFailureMessage(Exception ex)
    {
        return $"Could not load {GetAssemblyDisplayName()}:\n{ex.Message}";
    }

    private string GetAssemblyDisplayName()
    {
        var fileName = CandidateDllPaths
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));

        return string.IsNullOrWhiteSpace(fileName)
            ? "the plug-in"
            : Path.GetFileName(fileName);
    }

    /// <summary>
    /// Adds a folder to TRUSTEDPATHS if SECURELOAD is enabled and the folder isn’t already present.
    /// </summary>
    private static void TrustPathIfNeeded(string folder)
    {
        short secureLoad = 0;

        try
        {
            object value = Application.GetSystemVariable("SECURELOAD");
            secureLoad = Convert.ToInt16(value);
        }
        catch
        {
            // Ignore SECURELOAD probing failures.
        }

        if (secureLoad == 0)
        {
            return;
        }

        try
        {
            var current = Convert.ToString(Application.GetSystemVariable("TRUSTEDPATHS")) ?? string.Empty;
            var normalized = folder.EndsWith("\\", StringComparison.Ordinal) ? folder : folder + "\\";
            var paths = current.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim());

            if (!paths.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                var updated = string.IsNullOrEmpty(current)
                    ? normalized
                    : $"{current};{normalized}";

                Application.SetSystemVariable("TRUSTEDPATHS", updated);
            }
        }
        catch
        {
            // Non-fatal. If TRUSTEDPATHS can't be updated the user can add it manually.
        }
    }
}
