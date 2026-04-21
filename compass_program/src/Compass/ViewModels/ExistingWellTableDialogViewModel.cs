using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Compass.Models;

namespace Compass.ViewModels;

public sealed class ExistingWellTableDialogViewModel : INotifyPropertyChanged
{
    public sealed class CoordinateFormatOption
    {
        public CoordinateFormatOption(string label, ExistingWellTableCoordinateFormat format)
        {
            Label = label;
            Format = format;
        }

        public string Label { get; }

        public ExistingWellTableCoordinateFormat Format { get; }
    }

    public sealed class ZoneOption
    {
        public ZoneOption(string label, int? zone)
        {
            Label = label;
            Zone = zone;
        }

        public string Label { get; }

        public int? Zone { get; }
    }

    private CoordinateFormatOption _selectedCoordinateFormat;
    private ZoneOption _selectedZone;

    public ExistingWellTableDialogViewModel()
    {
        CoordinateFormatOptions = new[]
        {
            new CoordinateFormatOption("NAD83 UTMS", ExistingWellTableCoordinateFormat.Nad83Utms),
            new CoordinateFormatOption("NAD27 UTMS", ExistingWellTableCoordinateFormat.Nad27Utms),
            new CoordinateFormatOption("NAD83 LAT/LONG", ExistingWellTableCoordinateFormat.Nad83LatLong),
            new CoordinateFormatOption("NAD27 LAT/LONG", ExistingWellTableCoordinateFormat.Nad27LatLong)
        };
        ZoneOptions = new[]
        {
            new ZoneOption("Auto (match Complete CORDS)", null),
            new ZoneOption("11", 11),
            new ZoneOption("12", 12)
        };

        _selectedCoordinateFormat = CoordinateFormatOptions[0];
        _selectedZone = ZoneOptions[0];
    }

    public IReadOnlyList<CoordinateFormatOption> CoordinateFormatOptions { get; }

    public IReadOnlyList<ZoneOption> ZoneOptions { get; }

    public CoordinateFormatOption SelectedCoordinateFormat
    {
        get => _selectedCoordinateFormat;
        set
        {
            if (SetField(ref _selectedCoordinateFormat, value))
            {
                OnPropertyChanged(nameof(FirstHeaderPreview));
                OnPropertyChanged(nameof(SecondHeaderPreview));
            }
        }
    }

    public ZoneOption SelectedZone
    {
        get => _selectedZone;
        set => SetField(ref _selectedZone, value);
    }

    public string FirstHeaderPreview => SelectedCoordinateFormat.Format switch
    {
        ExistingWellTableCoordinateFormat.Nad83Utms => "NORTHING (NAD83)",
        ExistingWellTableCoordinateFormat.Nad27Utms => "NORTHING (NAD27)",
        ExistingWellTableCoordinateFormat.Nad83LatLong => "LATITUDE (NAD83)",
        ExistingWellTableCoordinateFormat.Nad27LatLong => "LATITUDE (NAD27)",
        _ => "NORTHING (NAD83)"
    };

    public string SecondHeaderPreview => SelectedCoordinateFormat.Format switch
    {
        ExistingWellTableCoordinateFormat.Nad83Utms => "EASTING (NAD83)",
        ExistingWellTableCoordinateFormat.Nad27Utms => "EASTING (NAD27)",
        ExistingWellTableCoordinateFormat.Nad83LatLong => "LONGITUDE (NAD83)",
        ExistingWellTableCoordinateFormat.Nad27LatLong => "LONGITUDE (NAD27)",
        _ => "EASTING (NAD83)"
    };

    public bool TryCreateConfiguration(out ExistingWellTableConfiguration? configuration, out string error)
    {
        configuration = null;
        error = string.Empty;

        if (SelectedCoordinateFormat == null)
        {
            error = "Choose a coordinate format.";
            return false;
        }

        if (SelectedZone == null)
        {
            error = "Choose a zone option.";
            return false;
        }

        configuration = new ExistingWellTableConfiguration
        {
            CoordinateFormat = SelectedCoordinateFormat.Format,
            Zone = SelectedZone.Zone
        };
        return true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

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

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
