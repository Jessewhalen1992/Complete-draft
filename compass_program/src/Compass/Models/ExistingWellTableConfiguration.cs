namespace Compass.Models;

public enum ExistingWellTableCoordinateFormat
{
    Nad83Utms,
    Nad27Utms,
    Nad83LatLong,
    Nad27LatLong
}

public sealed class ExistingWellTableConfiguration
{
    public ExistingWellTableCoordinateFormat CoordinateFormat { get; set; } = ExistingWellTableCoordinateFormat.Nad83Utms;

    public int? Zone { get; set; }
}
