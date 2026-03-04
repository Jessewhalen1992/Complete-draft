using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.EditorInput;
using DrawingImage = System.Drawing.Image;
using DrawingPropertyItem = System.Drawing.Imaging.PropertyItem;

namespace WildlifeSweeps
{
    internal static class PhotoGpsMetadataReader
    {
        private const int GpsLatitudeRefId = 0x0001;
        private const int GpsLatitudeId = 0x0002;
        private const int GpsLongitudeRefId = 0x0003;
        private const int GpsLongitudeId = 0x0004;

        internal sealed record GpsPhotoMetadata(string ImagePath, string FileName, double Latitude, double Longitude);

        public static List<GpsPhotoMetadata> LoadGpsPhotos(
            string folder,
            Editor editor,
            bool deduplicateByCoordinate,
            string missingGpsSummaryFormat)
        {
            var photos = new List<GpsPhotoMetadata>();
            var files = Directory.EnumerateFiles(folder, "*.*", SearchOption.TopDirectoryOnly)
                .Where(path => path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                               || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase));

            var skipped = 0;
            var duplicateCount = 0;
            HashSet<(double Latitude, double Longitude)>? uniqueByCoordinate = deduplicateByCoordinate
                ? new HashSet<(double Latitude, double Longitude)>()
                : null;
            foreach (var path in files)
            {
                try
                {
                    using var image = DrawingImage.FromFile(path);
                    if (TryReadGpsCoordinate(image, GpsLatitudeRefId, GpsLatitudeId, out var lat) &&
                        TryReadGpsCoordinate(image, GpsLongitudeRefId, GpsLongitudeId, out var lon))
                    {
                        if (uniqueByCoordinate != null)
                        {
                            var key = (Math.Round(lat, 6), Math.Round(lon, 6));
                            if (!uniqueByCoordinate.Add(key))
                            {
                                duplicateCount++;
                                continue;
                            }
                        }

                        photos.Add(new GpsPhotoMetadata(
                            path,
                            Path.GetFileNameWithoutExtension(path) ?? path,
                            lat,
                            lon));
                    }
                    else
                    {
                        skipped++;
                    }
                }
                catch (Exception ex)
                {
                    skipped++;
                    editor.WriteMessage($"\nFailed to read GPS from {Path.GetFileName(path)}: {ex.Message}");
                }
            }

            if (skipped > 0)
            {
                if (string.IsNullOrWhiteSpace(missingGpsSummaryFormat))
                {
                    editor.WriteMessage($"\nSkipped {skipped} JPG(s) without GPS metadata.");
                }
                else
                {
                    editor.WriteMessage(string.Format(CultureInfo.InvariantCulture, missingGpsSummaryFormat, skipped));
                }
            }

            if (duplicateCount > 0)
            {
                editor.WriteMessage($"\nSkipped {duplicateCount} JPG(s) with duplicate GPS coordinates.");
            }

            return photos;
        }

        private static bool TryReadGpsCoordinate(DrawingImage image, int refId, int valueId, out double coordinate)
        {
            coordinate = 0.0;

            if (!TryGetPropertyItem(image, refId, out var refItem))
            {
                return false;
            }

            if (!TryGetPropertyItem(image, valueId, out var valueItem))
            {
                return false;
            }

            var hemisphere = ReadAscii(refItem);
            var values = ReadRationals(valueItem);
            if (values.Count < 3)
            {
                return false;
            }

            var decimalValue = values[0] + (values[1] / 60.0) + (values[2] / 3600.0);
            if (string.Equals(hemisphere, "S", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(hemisphere, "W", StringComparison.OrdinalIgnoreCase))
            {
                decimalValue = -decimalValue;
            }

            coordinate = decimalValue;
            return true;
        }

        private static bool TryGetPropertyItem(DrawingImage image, int id, out DrawingPropertyItem? item)
        {
            try
            {
                item = image.GetPropertyItem(id);
                return true;
            }
            catch (ArgumentException)
            {
                item = null;
                return false;
            }
        }

        private static string ReadAscii(DrawingPropertyItem? item)
        {
            if (item?.Value == null)
            {
                return string.Empty;
            }

            return Encoding.ASCII.GetString(item.Value).Trim('\0', ' ');
        }

        private static List<double> ReadRationals(DrawingPropertyItem? item)
        {
            var values = new List<double>();
            if (item?.Value == null)
            {
                return values;
            }

            var bytes = item.Value;
            for (var offset = 0; offset + 7 < bytes.Length; offset += 8)
            {
                var numerator = BitConverter.ToUInt32(bytes, offset);
                var denominator = BitConverter.ToUInt32(bytes, offset + 4);
                if (denominator == 0)
                {
                    values.Add(0.0);
                }
                else
                {
                    values.Add(numerator / (double)denominator);
                }
            }

            return values;
        }
    }
}
