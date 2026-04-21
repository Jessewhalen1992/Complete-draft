using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using Autodesk.AutoCAD.Geometry;
using Compass.Models;

namespace Compass.ViewModels;

public sealed class BuildDrillDialogViewModel : INotifyPropertyChanged
{
    public const int MinimumPointCount = 2;
    public const int MaximumPointCount = 15;
    private const string DefaultCombinedScaleFactor = "1.0";

    private readonly string[] _drillNames;
    private bool _isApplyingSourceCascade;
    private bool _isApplyingCombinedScaleFactorSync;
    private int _pointCount = MinimumPointCount;
    private int _selectedZone = 12;
    private string _selectedDrillName;
    private Point3d? _surfacePoint;

    public BuildDrillDialogViewModel(IEnumerable<string> drillNames)
    {
        _drillNames = drillNames?
            .Select(name => (name ?? string.Empty).Trim())
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? Array.Empty<string>();

        if (_drillNames.Length == 0)
        {
            _drillNames = new[] { "DRILL_1" };
        }

        DrillNames = _drillNames;
        _selectedDrillName = _drillNames[0];

        Points = new ObservableCollection<BuildDrillPointViewModel>();
        EnsurePointCount();
    }

    public IReadOnlyList<string> DrillNames { get; }

    public IReadOnlyList<int> PointCountChoices { get; } =
        Enumerable.Range(MinimumPointCount, MaximumPointCount - MinimumPointCount + 1).ToArray();

    public IReadOnlyList<int> ZoneChoices { get; } = new[] { 11, 12 };

    public ObservableCollection<BuildDrillPointViewModel> Points { get; }

    public string SelectedDrillName
    {
        get => _selectedDrillName;
        set
        {
            if (SetField(ref _selectedDrillName, value))
            {
                OnPropertyChanged(nameof(SelectedDrillLetter));
                OnPropertyChanged(nameof(PointTagPreview));
            }
        }
    }

    public int PointCount
    {
        get => _pointCount;
        set
        {
            var normalized = Math.Max(MinimumPointCount, Math.Min(MaximumPointCount, value));
            if (SetField(ref _pointCount, normalized))
            {
                EnsurePointCount();
            }
        }
    }

    public int SelectedZone
    {
        get => _selectedZone;
        set => SetField(ref _selectedZone, value);
    }

    public string SelectedDrillLetter => GetSelectedDrillLetter();

    public bool HasSurfacePoint => _surfacePoint.HasValue;

    public string SurfacePointSummary =>
        _surfacePoint.HasValue
            ? string.Create(
                CultureInfo.InvariantCulture,
                $"X {_surfacePoint.Value.X:0.###}, Y {_surfacePoint.Value.Y:0.###}")
            : "No surface point picked.";

    public string PointTagPreview =>
        HasSurfacePoint
            ? $"{SelectedDrillLetter}1, {SelectedDrillLetter}2, {SelectedDrillLetter}3..."
            : $"{SelectedDrillLetter}2, {SelectedDrillLetter}3, {SelectedDrillLetter}4...";

    public bool TryCreateRequest(out BuildDrillRequest? request, out string error)
    {
        request = null;
        error = string.Empty;

        var drillName = (SelectedDrillName ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(drillName))
        {
            error = "Choose a drill name to build.";
            return false;
        }

        if (SelectedZone != 11 && SelectedZone != 12)
        {
            error = "Zone must be 11 or 12.";
            return false;
        }

        var pointRequests = new List<BuildDrillPointRequest>(Points.Count);
        foreach (var point in Points)
        {
            if (!point.TryCreateRequest(SelectedZone, out var pointRequest, out var pointError) || pointRequest == null)
            {
                error = $"Point {point.Index}: {pointError}";
                return false;
            }

            pointRequests.Add(pointRequest);
        }

        request = new BuildDrillRequest
        {
            DrillName = drillName,
            DrillLetter = GetSelectedDrillLetter(),
            SurfacePoint = _surfacePoint,
            Points = pointRequests
        };
        return true;
    }

    public void SetSurfacePoint(Point3d point)
    {
        _surfacePoint = point;
        OnPropertyChanged(nameof(HasSurfacePoint));
        OnPropertyChanged(nameof(SurfacePointSummary));
        OnPropertyChanged(nameof(PointTagPreview));
    }

    public void ClearSurfacePoint()
    {
        if (!_surfacePoint.HasValue)
        {
            return;
        }

        _surfacePoint = null;
        OnPropertyChanged(nameof(HasSurfacePoint));
        OnPropertyChanged(nameof(SurfacePointSummary));
        OnPropertyChanged(nameof(PointTagPreview));
    }

    private void EnsurePointCount()
    {
        while (Points.Count < _pointCount)
        {
            var point = new BuildDrillPointViewModel(
                Points.Count + 1,
                HandlePointSourceChanged,
                HandleCombinedScaleFactorChanged);
            if (Points.Count > 0)
            {
                point.ApplyCombinedScaleFactorFromSync(Points[0].CombinedScaleFactor);
            }
            else
            {
                point.ApplyCombinedScaleFactorFromSync(DefaultCombinedScaleFactor);
            }

            Points.Add(point);
        }

        while (Points.Count > _pointCount)
        {
            Points.RemoveAt(Points.Count - 1);
        }
    }

    private void HandlePointSourceChanged(int pointIndex, BuildDrillSource source)
    {
        if (_isApplyingSourceCascade || !IsCoordinateSource(source))
        {
            return;
        }

        _isApplyingSourceCascade = true;
        try
        {
            for (var i = pointIndex; i < Points.Count; i++)
            {
                var point = Points[i];
                if (IsCoordinateSource(point.SelectedSource.Value))
                {
                    point.ApplySourceFromCascade(source);
                }
            }
        }
        finally
        {
            _isApplyingSourceCascade = false;
        }
    }

    private static bool IsCoordinateSource(BuildDrillSource source)
    {
        return source == BuildDrillSource.Nad83Utms || source == BuildDrillSource.Nad27Utms;
    }

    private void HandleCombinedScaleFactorChanged(int pointIndex, string combinedScaleFactor)
    {
        if (_isApplyingCombinedScaleFactorSync)
        {
            return;
        }

        _isApplyingCombinedScaleFactorSync = true;
        try
        {
            for (var i = 0; i < Points.Count; i++)
            {
                if (Points[i].Index == pointIndex)
                {
                    continue;
                }

                Points[i].ApplyCombinedScaleFactorFromSync(combinedScaleFactor);
            }
        }
        finally
        {
            _isApplyingCombinedScaleFactorSync = false;
        }
    }

    private string GetSelectedDrillLetter()
    {
        var index = Array.FindIndex(_drillNames, name => string.Equals(name, SelectedDrillName, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            index = 0;
        }

        return ((char)('A' + index)).ToString(CultureInfo.InvariantCulture);
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class BuildDrillPointViewModel : INotifyPropertyChanged
{
    private const string DefaultCombinedScaleFactor = "1.0";
    private readonly Action<int, BuildDrillSource>? _sourceChangedHandler;
    private readonly Action<int, string>? _combinedScaleFactorChangedHandler;
    private readonly BuildDrillSourceOption[] _sourceOptions;
    private readonly DirectionOption<BuildDrillNorthSouthReference>[] _northSouthReferences;
    private readonly DirectionOption<BuildDrillEastWestReference>[] _eastWestReferences;
    private BuildDrillSourceOption _selectedSource;
    private string _xValue = string.Empty;
    private string _yValue = string.Empty;
    private string _section = string.Empty;
    private string _township = string.Empty;
    private string _range = string.Empty;
    private string _meridian = string.Empty;
    private bool _useAtsFabric = true;
    private string _combinedScaleFactor = DefaultCombinedScaleFactor;
    private string _northSouthDistance = string.Empty;
    private string _eastWestDistance = string.Empty;
    private DirectionOption<BuildDrillNorthSouthReference> _selectedNorthSouthReference;
    private DirectionOption<BuildDrillEastWestReference> _selectedEastWestReference;

    public BuildDrillPointViewModel(
        int index,
        Action<int, BuildDrillSource>? sourceChangedHandler = null,
        Action<int, string>? combinedScaleFactorChangedHandler = null)
    {
        Index = index;
        _sourceChangedHandler = sourceChangedHandler;
        _combinedScaleFactorChangedHandler = combinedScaleFactorChangedHandler;
        _sourceOptions = new[]
        {
            new BuildDrillSourceOption(BuildDrillSource.Nad83Utms, "NAD83 UTMS"),
            new BuildDrillSourceOption(BuildDrillSource.Nad27Utms, "NAD27 UTMS"),
            new BuildDrillSourceOption(BuildDrillSource.SectionOffsets, "SECTION OFFSETS")
        };
        _northSouthReferences = new[]
        {
            new DirectionOption<BuildDrillNorthSouthReference>(BuildDrillNorthSouthReference.NorthOfSouth, "N of S"),
            new DirectionOption<BuildDrillNorthSouthReference>(BuildDrillNorthSouthReference.SouthOfNorth, "S of N")
        };
        _eastWestReferences = new[]
        {
            new DirectionOption<BuildDrillEastWestReference>(BuildDrillEastWestReference.EastOfWest, "E of W"),
            new DirectionOption<BuildDrillEastWestReference>(BuildDrillEastWestReference.WestOfEast, "W of E")
        };
        _selectedSource = _sourceOptions[0];
        _selectedNorthSouthReference = _northSouthReferences[0];
        _selectedEastWestReference = _eastWestReferences[0];
    }

    public int Index { get; }

    public string PointLabel => $"Point {Index}";

    public IReadOnlyList<BuildDrillSourceOption> SourceOptions => _sourceOptions;

    public IReadOnlyList<DirectionOption<BuildDrillNorthSouthReference>> NorthSouthReferences => _northSouthReferences;

    public IReadOnlyList<DirectionOption<BuildDrillEastWestReference>> EastWestReferences => _eastWestReferences;

    public BuildDrillSourceOption SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (SetField(ref _selectedSource, value))
            {
                NotifySourcePresentationChanged();
                _sourceChangedHandler?.Invoke(Index, value.Value);
            }
        }
    }

    public void ApplySourceFromCascade(BuildDrillSource source)
    {
        var option = FindSourceOption(source);
        if (option == null || EqualityComparer<BuildDrillSourceOption>.Default.Equals(_selectedSource, option))
        {
            return;
        }

        _selectedSource = option;
        OnPropertyChanged(nameof(SelectedSource));
        NotifySourcePresentationChanged();
    }

    public string XValue
    {
        get => _xValue;
        set => SetField(ref _xValue, value);
    }

    public string YValue
    {
        get => _yValue;
        set => SetField(ref _yValue, value);
    }

    public string Section
    {
        get => _section;
        set => SetField(ref _section, value);
    }

    public string Township
    {
        get => _township;
        set => SetField(ref _township, value);
    }

    public string Range
    {
        get => _range;
        set => SetField(ref _range, value);
    }

    public string Meridian
    {
        get => _meridian;
        set => SetField(ref _meridian, value);
    }

    public bool UseAtsFabric
    {
        get => _useAtsFabric;
        set => SetField(ref _useAtsFabric, value);
    }

    public string CombinedScaleFactor
    {
        get => _combinedScaleFactor;
        set
        {
            if (SetField(ref _combinedScaleFactor, value))
            {
                _combinedScaleFactorChangedHandler?.Invoke(Index, value);
            }
        }
    }

    public string NorthSouthDistance
    {
        get => _northSouthDistance;
        set => SetField(ref _northSouthDistance, value);
    }

    public DirectionOption<BuildDrillNorthSouthReference> SelectedNorthSouthReference
    {
        get => _selectedNorthSouthReference;
        set => SetField(ref _selectedNorthSouthReference, value);
    }

    public string EastWestDistance
    {
        get => _eastWestDistance;
        set => SetField(ref _eastWestDistance, value);
    }

    public DirectionOption<BuildDrillEastWestReference> SelectedEastWestReference
    {
        get => _selectedEastWestReference;
        set => SetField(ref _selectedEastWestReference, value);
    }

    public Visibility CoordinateInputsVisibility =>
        SelectedSource.Value == BuildDrillSource.SectionOffsets
            ? Visibility.Collapsed
            : Visibility.Visible;

    public Visibility SectionInputsVisibility =>
        SelectedSource.Value == BuildDrillSource.SectionOffsets
            ? Visibility.Visible
            : Visibility.Collapsed;

    public string XLabel =>
        SelectedSource.Value == BuildDrillSource.Nad27Utms
            ? "X / Easting (NAD27)"
            : "X / Easting";

    public string YLabel =>
        SelectedSource.Value == BuildDrillSource.Nad27Utms
            ? "Y / Northing (NAD27)"
            : "Y / Northing";

    public void ApplyCombinedScaleFactorFromSync(string combinedScaleFactor)
    {
        if (string.Equals(_combinedScaleFactor, combinedScaleFactor, StringComparison.Ordinal))
        {
            return;
        }

        _combinedScaleFactor = combinedScaleFactor;
        OnPropertyChanged(nameof(CombinedScaleFactor));
    }

    public bool TryCreateRequest(int zone, out BuildDrillPointRequest? request, out string error)
    {
        request = null;
        error = string.Empty;

        if (SelectedSource.Value == BuildDrillSource.SectionOffsets)
        {
            if (!TryParsePositiveNonZeroDouble(CombinedScaleFactor, out var combinedScaleFactor))
            {
                error = "Enter a valid combined scale factor.";
                return false;
            }

            if (!TryParsePositiveDouble(NorthSouthDistance, out var northSouthDistance))
            {
                error = "Enter a valid north/south distance.";
                return false;
            }

            if (!TryParsePositiveDouble(EastWestDistance, out var eastWestDistance))
            {
                error = "Enter a valid east/west distance.";
                return false;
            }

            var section = NormalizeToken(Section);
            var township = NormalizeToken(Township);
            var range = NormalizeToken(Range);
            var meridian = NormalizeToken(Meridian);
            if (string.IsNullOrWhiteSpace(section) ||
                string.IsNullOrWhiteSpace(township) ||
                string.IsNullOrWhiteSpace(range) ||
                string.IsNullOrWhiteSpace(meridian))
            {
                error = "Enter section, township, range, and meridian.";
                return false;
            }

            request = new BuildDrillPointRequest
            {
                Source = SelectedSource.Value,
                Zone = zone,
                Section = section,
                Township = township,
                Range = range,
                Meridian = meridian,
                UseAtsFabric = UseAtsFabric,
                CombinedScaleFactor = combinedScaleFactor,
                NorthSouthDistance = northSouthDistance,
                NorthSouthReference = SelectedNorthSouthReference.Value,
                EastWestDistance = eastWestDistance,
                EastWestReference = SelectedEastWestReference.Value
            };
            return true;
        }

        if (!TryParseDouble(XValue, out var x))
        {
            error = "Enter a valid X / Easting value.";
            return false;
        }

        if (!TryParseDouble(YValue, out var y))
        {
            error = "Enter a valid Y / Northing value.";
            return false;
        }

        request = new BuildDrillPointRequest
        {
            Source = SelectedSource.Value,
            Zone = zone,
            X = x,
            Y = y,
            CombinedScaleFactor = TryParsePositiveNonZeroDouble(CombinedScaleFactor, out var coordinateScaleFactor)
                ? coordinateScaleFactor
                : 1.0
        };
        return true;
    }

    private static bool TryParseDouble(string? value, out double parsed)
    {
        var text = (value ?? string.Empty).Trim();
        return double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out parsed) ||
               double.TryParse(text, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.CurrentCulture, out parsed);
    }

    private static bool TryParsePositiveDouble(string? value, out double parsed)
    {
        if (TryParseDouble(value, out parsed) && parsed >= 0.0)
        {
            return true;
        }

        parsed = 0.0;
        return false;
    }

    private static bool TryParsePositiveNonZeroDouble(string? value, out double parsed)
    {
        if (TryParseDouble(value, out parsed) && parsed > 0.0)
        {
            return true;
        }

        parsed = 0.0;
        return false;
    }

    private static string NormalizeToken(string? value)
    {
        return (value ?? string.Empty).Trim();
    }

    private BuildDrillSourceOption? FindSourceOption(BuildDrillSource source)
    {
        return _sourceOptions.FirstOrDefault(option => option.Value == source);
    }

    private void NotifySourcePresentationChanged()
    {
        OnPropertyChanged(nameof(CoordinateInputsVisibility));
        OnPropertyChanged(nameof(SectionInputsVisibility));
        OnPropertyChanged(nameof(XLabel));
        OnPropertyChanged(nameof(YLabel));
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        if (propertyName == nameof(Index))
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(PointLabel)));
        }
    }
}

public sealed class BuildDrillSourceOption
{
    public BuildDrillSourceOption(BuildDrillSource value, string label)
    {
        Value = value;
        Label = label;
    }

    public BuildDrillSource Value { get; }

    public string Label { get; }
}

public sealed class DirectionOption<T>
{
    public DirectionOption(T value, string label)
    {
        Value = value;
        Label = label;
    }

    public T Value { get; }

    public string Label { get; }
}
