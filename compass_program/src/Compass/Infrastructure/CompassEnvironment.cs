using System;
using System.Reflection;
using System.Text;
using Compass.Infrastructure.Logging;
using Compass.Models;
#if NET48
using Microsoft.Extensions.Configuration;
#else
using Newtonsoft.Json;
using System.IO;
#endif

namespace Compass.Infrastructure;

/// <summary>
/// Centralized bootstrapper for configuration, logging, and miscellaneous environment setup.
/// </summary>
public static class CompassEnvironment
{
    private static readonly object SyncRoot = new();
    private static bool _initialized;
#if NET48
    private static IConfigurationRoot? _configuration;
#endif
    private static AppSettings _appSettings = new();
    private static ILog _log = new NLogLogger();

    private static void RegisterCodePageEncodings()
    {
        try
        {
            // Look for Encoding.RegisterProvider via reflection; this method is only
            // present in .NET 4.6+. Skip registration on older runtimes.
            var registerProvider = typeof(Encoding).GetMethod(
                "RegisterProvider",
                BindingFlags.Public | BindingFlags.Static);

            if (registerProvider == null)
            {
                return;
            }

            // Best effort only. Some AutoCAD 2015 machines won't have this assembly.
            var providerType = Type.GetType(
                "System.Text.CodePagesEncodingProvider, System.Text.Encoding.CodePages",
                throwOnError: false);
            var instanceProp = providerType?.GetProperty(
                "Instance",
                BindingFlags.Public | BindingFlags.Static);
            var providerInstance = instanceProp?.GetValue(null);
            if (providerInstance == null)
            {
                CompassStartupDiagnostics.Log("System.Text.Encoding.CodePages not available; continuing without code page provider.");
                return;
            }

            registerProvider.Invoke(null, new[] { providerInstance });
            CompassStartupDiagnostics.Log("Registered code page encoding provider.");
        }
        catch (Exception ex)
        {
            CompassStartupDiagnostics.LogException("RegisterCodePageEncodings", ex);
        }
    }

    /// <summary>
    /// Ensures environment initialization occurs exactly once.
    /// </summary>
    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        lock (SyncRoot)
        {
            if (_initialized)
            {
                return;
            }

            RegisterCodePageEncodings();

            try
            {
#if NET48
                _configuration = new ConfigurationBuilder()
                    .SetBasePath(AppDomain.CurrentDomain.BaseDirectory)
                    .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                    .Build();

                var settings = new AppSettings();
                _configuration.Bind(settings);
                _appSettings = settings;

                _log = new NLogLogger(_configuration);
#else
                var appsettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
                var settings = new AppSettings();
                if (File.Exists(appsettingsPath))
                {
                    var json = File.ReadAllText(appsettingsPath);
                    var loaded = JsonConvert.DeserializeObject<AppSettings>(json);
                    if (loaded != null) settings = loaded;
                }
                _appSettings = settings;
                _log = new NLogLogger();
#endif
            }
            catch (Exception ex)
            {
                _log.Warn($"Failed to load configuration: {ex.Message}");
                _appSettings = new AppSettings();
            }

            _initialized = true;
        }
    }

#if NET48
    public static IConfiguration Configuration
        => (_configuration ?? new ConfigurationBuilder().Build());
#endif

    public static AppSettings AppSettings => _appSettings;

    public static ILog Log => _log;
}
