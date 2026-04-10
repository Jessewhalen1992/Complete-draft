using System;
using System.Reflection;
using NLog;
using OfficeOpenXml;

namespace Compass.Services;

public static class EpplusCompat
{
    private static readonly Logger Log = LogManager.GetCurrentClassLogger();

    public static void EnsureLicense()
    {
        try
        {
            var packageType = typeof(ExcelPackage);
            var assembly = packageType.Assembly;

            var licenseProperty = packageType.GetProperty("License", BindingFlags.Public | BindingFlags.Static);
            if (licenseProperty != null)
            {
                var licenseObject = licenseProperty.GetValue(null);
                if (licenseObject != null)
                {
                    var organizationMethod = licenseObject.GetType().GetMethod("SetNonCommercialOrganization", new[] { typeof(string) });
                    if (organizationMethod != null)
                    {
                        organizationMethod.Invoke(licenseObject, new object[] { Environment.UserName });
                        return;
                    }

                    var personalMethod = licenseObject.GetType().GetMethod("SetNonCommercialPersonal", new[] { typeof(string) });
                    if (personalMethod != null)
                    {
                        personalMethod.Invoke(licenseObject, new object[] { Environment.UserName });
                        return;
                    }
                }
            }

            var legacyEnumType = assembly.GetType("OfficeOpenXml.LicenseContext") ?? assembly.GetType("OfficeOpenXml.ExcelPackageLicenseContext");
            if (legacyEnumType != null)
            {
                var legacyLicenseProperty = packageType.GetProperty("License", BindingFlags.Public | BindingFlags.Static);
                if (legacyLicenseProperty != null)
                {
                    var container = legacyLicenseProperty.GetValue(null);
                    var setMethod = container?.GetType().GetMethod("SetLicense", new[] { legacyEnumType });
                    if (setMethod != null)
                    {
                        var nonCommercial = Enum.Parse(legacyEnumType, "NonCommercial");
                        setMethod.Invoke(container, new[] { nonCommercial });
                        return;
                    }
                }
            }

            var contextProperty = packageType.GetProperty("LicenseContext", BindingFlags.Public | BindingFlags.Static);
            if (contextProperty != null)
            {
                var nonCommercial = Enum.Parse(contextProperty.PropertyType, "NonCommercial");
                contextProperty.SetValue(null, nonCommercial);
                return;
            }
        }
        catch (Exception ex)
        {
            Log.Warn(ex, "Failed to set EPPlus license context");
        }
    }
}
