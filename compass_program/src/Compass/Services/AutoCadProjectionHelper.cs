using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using WildlifeSweeps;

using AutoCADApplication = Autodesk.AutoCAD.ApplicationServices.Application;

namespace Compass.Services;

internal static class AutoCadProjectionHelper
{
    private static readonly object AdeProjectionSync = new();

    public static bool TryTransformUtm(string sourceCode, string destinationCode, double x, double y, out Point3d point, out string detail)
    {
        point = default;
        var failureDetails = new List<string>(2);

        if (Map3dCoordinateTransformer.TryCreate(sourceCode, destinationCode, out var transformer) && transformer != null)
        {
            // The shared transformer returns Y first and X second.
            if (transformer.TryProject(new Point3d(x, y, 0.0), out var transformedY, out var transformedX))
            {
                point = new Point3d(transformedX, transformedY, 0.0);
                detail = string.Empty;
                return true;
            }

            failureDetails.Add("managed Map transformer loaded but returned no projected point");
        }
        else
        {
            failureDetails.Add("managed Map transformer could not be created");
        }

        if (TryTransformViaAde(sourceCode, destinationCode, x, y, out point, out var adeDetail))
        {
            detail = string.Empty;
            return true;
        }

        failureDetails.Add(adeDetail);
        detail = string.Join("; ", failureDetails.Where(part => !string.IsNullOrWhiteSpace(part)));
        return false;
    }

    private static bool TryTransformViaAde(string sourceCode, string destinationCode, double x, double y, out Point3d point, out string detail)
    {
        point = default;
        detail = string.Empty;

        lock (AdeProjectionSync)
        {
            if (TryTransformViaAdeInvoke(sourceCode, destinationCode, x, y, out point, out detail))
            {
                return true;
            }

            var invokeDetail = detail;
            if (TryTransformViaSendCommand(sourceCode, destinationCode, x, y, out point, out var commandDetail))
            {
                return true;
            }

            detail = $"{invokeDetail}; command-line ADE returned {commandDetail}";
            return false;
        }
    }

    private static bool TryTransformViaAdeInvoke(string sourceCode, string destinationCode, double x, double y, out Point3d point, out string detail)
    {
        point = default;
        detail = string.Empty;

        try
        {
            InvokeLispFunction("ade_errclear");

            using var sourceResult = InvokeLispFunction("ade_projsetsrc", new TypedValue((int)LispDataType.Text, sourceCode));
            if (!HasTruthyLispResult(sourceResult))
            {
                detail = $"ADE ade_projsetsrc returned {DescribeResultBuffer(sourceResult)}";
                return false;
            }

            using var destinationResult = InvokeLispFunction("ade_projsetdest", new TypedValue((int)LispDataType.Text, destinationCode));
            if (!HasTruthyLispResult(destinationResult))
            {
                detail = $"ADE ade_projsetdest returned {DescribeResultBuffer(destinationResult)}";
                return false;
            }

            if (TryInvokeAdeProjectionVariant(
                    "Point3d",
                    out point,
                    out detail,
                    new TypedValue((int)LispDataType.Point3d, new Point3d(x, y, 0.0))))
            {
                return true;
            }

            if (TryInvokeAdeProjectionVariant(
                    "XYZ list",
                    out point,
                    out detail,
                    new TypedValue((int)LispDataType.ListBegin),
                    new TypedValue((int)LispDataType.Double, x),
                    new TypedValue((int)LispDataType.Double, y),
                    new TypedValue((int)LispDataType.Double, 0.0),
                    new TypedValue((int)LispDataType.ListEnd)))
            {
                return true;
            }

            if (TryInvokeAdeProjectionVariant(
                    "XY list",
                    out point,
                    out detail,
                    new TypedValue((int)LispDataType.ListBegin),
                    new TypedValue((int)LispDataType.Double, x),
                    new TypedValue((int)LispDataType.Double, y),
                    new TypedValue((int)LispDataType.ListEnd)))
            {
                return true;
            }

            return false;
        }
        catch (System.Exception ex)
        {
            detail = $"ADE invoke fallback threw {ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    private static bool TryInvokeAdeProjectionVariant(string variantName, out Point3d point, out string detail, params TypedValue[] args)
    {
        point = default;
        detail = string.Empty;

        using var projectionResult = InvokeLispFunction("ade_projptforward", args);
        if (TryReadPointResult(projectionResult, out point))
        {
            return true;
        }

        detail = $"ADE ade_projptforward {variantName} returned {DescribeResultBuffer(projectionResult)}";
        return false;
    }

    private static bool TryTransformViaSendCommand(string sourceCode, string destinationCode, double x, double y, out Point3d point, out string detail)
    {
        point = default;
        detail = string.Empty;

        var document = AutoCADApplication.DocumentManager.MdiActiveDocument;
        if (document == null)
        {
            detail = "no active document for command-line ADE fallback";
            return false;
        }

        var symbolName = $"COMPASS_ADE_{Guid.NewGuid():N}";
        var xText = ToLispRealLiteral(x);
        var yText = ToLispRealLiteral(y);
        var expression = BuildAdeCommandExpression(symbolName, sourceCode, destinationCode, xText, yText);

        try
        {
            InvokeComSendCommand(expression);
            var symbolValue = document.GetLispSymbol(symbolName);
            var raw = symbolValue?.ToString() ?? string.Empty;
            if (TryParseAdeCommandResult(raw, out point, out detail))
            {
                return true;
            }

            if (string.IsNullOrWhiteSpace(detail))
            {
                detail = string.IsNullOrWhiteSpace(raw) ? "empty symbol result" : raw;
            }

            return false;
        }
        catch (System.Exception ex)
        {
            detail = $"SendCommand ADE fallback threw {ex.GetType().Name}: {ex.Message}";
            return false;
        }
        finally
        {
            try
            {
                document.SetLispSymbol(symbolName, null);
            }
            catch
            {
                // Best-effort cleanup only.
            }
        }
    }

    private static string BuildAdeCommandExpression(string symbolName, string sourceCode, string destinationCode, string xText, string yText)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $@"(progn (setq {symbolName} (cond ((not (ade_projsetsrc ""{sourceCode}"")) ""ERR:ade_projsetsrc"") ((not (ade_projsetdest ""{destinationCode}"")) ""ERR:ade_projsetdest"") ((setq compassPt (ade_projptforward (list {xText} {yText}))) (strcat ""OK:"" (rtos (car compassPt) 2 16) "","" (rtos (cadr compassPt) 2 16))) (T ""ERR:ade_projptforward""))) (princ)){Environment.NewLine}");
    }

    private static string ToLispRealLiteral(double value)
    {
        var text = value.ToString("0.0###############", CultureInfo.InvariantCulture);
        return text.Contains('.') ? text : $"{text}.0";
    }

    private static void InvokeComSendCommand(string expression)
    {
        var acadApplication = AutoCADApplication.AcadApplication;
        if (acadApplication == null)
        {
            throw new InvalidOperationException("AutoCAD COM automation is not available.");
        }

        var acadDocument = acadApplication.GetType().InvokeMember(
            "ActiveDocument",
            BindingFlags.GetProperty,
            binder: null,
            target: acadApplication,
            args: null);
        if (acadDocument == null)
        {
            throw new InvalidOperationException("AutoCAD COM active document is not available.");
        }

        acadDocument.GetType().InvokeMember(
            "SendCommand",
            BindingFlags.InvokeMethod,
            binder: null,
            target: acadDocument,
            args: new object[] { expression });
    }

    private static bool TryParseAdeCommandResult(string raw, out Point3d point, out string detail)
    {
        point = default;
        detail = string.Empty;

        if (string.IsNullOrWhiteSpace(raw))
        {
            detail = "command-line ADE result symbol was blank";
            return false;
        }

        if (!raw.StartsWith("OK:", StringComparison.OrdinalIgnoreCase))
        {
            detail = raw;
            return false;
        }

        var values = raw.Substring(3).Split(',');
        if (values.Length < 2 ||
            !double.TryParse(values[0], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var convertedX) ||
            !double.TryParse(values[1], NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var convertedY))
        {
            detail = $"command-line ADE returned unparseable coordinates '{raw}'";
            return false;
        }

        point = new Point3d(convertedX, convertedY, 0.0);
        return true;
    }

    private static bool HasTruthyLispResult(ResultBuffer? result)
    {
        if (result == null)
        {
            return false;
        }

        foreach (var value in result.AsArray())
        {
            if (value.Value is string text && string.Equals(text, "nil", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (value.Value != null)
            {
                return true;
            }
        }

        return false;
    }

    private static ResultBuffer? InvokeLispFunction(string functionName, params TypedValue[] args)
    {
        using var request = new ResultBuffer();
        request.Add(new TypedValue((int)LispDataType.Text, functionName));
        foreach (var arg in args)
        {
            request.Add(arg);
        }

        return AutoCADApplication.Invoke(request);
    }

    private static bool TryReadPointResult(ResultBuffer? result, out Point3d point)
    {
        point = default;
        if (result == null)
        {
            return false;
        }

        var values = result.AsArray();
        var doubles = new List<double>(3);
        foreach (var value in values)
        {
            if (value.TypeCode == (int)LispDataType.Double)
            {
                doubles.Add(Convert.ToDouble(value.Value, CultureInfo.InvariantCulture));
                continue;
            }

            if (value.Value is Point3d point3d)
            {
                point = point3d;
                return true;
            }

            if (value.Value is Point2d point2d)
            {
                point = new Point3d(point2d.X, point2d.Y, 0.0);
                return true;
            }
        }

        if (doubles.Count >= 2)
        {
            point = new Point3d(doubles[0], doubles[1], doubles.Count >= 3 ? doubles[2] : 0.0);
            return true;
        }

        return false;
    }

    private static string DescribeResultBuffer(ResultBuffer? result)
    {
        if (result == null)
        {
            return "null";
        }

        var values = result.AsArray();
        if (values.Length == 0)
        {
            return "empty";
        }

        return string.Join(", ", values.Select(DescribeTypedValue));
    }

    private static string DescribeTypedValue(TypedValue value)
    {
        var renderedValue = value.Value switch
        {
            null => "<null>",
            string text => $"\"{text}\"",
            _ => value.Value.ToString() ?? "<unprintable>"
        };

        return $"{value.TypeCode}:{renderedValue}";
    }
}
