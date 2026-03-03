using Autodesk.AutoCAD.EditorInput;

namespace WildlifeSweeps
{
    internal static class PromptHelper
    {
        public static int PromptForInt(Editor editor, string message, int defaultValue)
        {
            var options = new PromptIntegerOptions(message)
            {
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = defaultValue,
                UseDefaultValue = true
            };

            var result = editor.GetInteger(options);
            return result.Status == PromptStatus.OK ? result.Value : defaultValue;
        }

        public static double PromptForDouble(Editor editor, string message, double defaultValue)
        {
            var options = new PromptDoubleOptions(message)
            {
                AllowNegative = false,
                AllowZero = false,
                DefaultValue = defaultValue,
                UseDefaultValue = true
            };

            var result = editor.GetDouble(options);
            return result.Status == PromptStatus.OK ? result.Value : defaultValue;
        }

        public static string PromptForUtmZone(Editor editor, string current)
        {
            var options = new PromptKeywordOptions("\nDrawing's NAD83 UTM zone [11/12] <11>: ")
            {
                AllowNone = true
            };
            options.Keywords.Add("11");
            options.Keywords.Add("12");
            options.Keywords.Default = NormalizeUtmZone(current);

            var result = editor.GetKeywords(options);
            return result.Status == PromptStatus.OK ? result.StringResult : options.Keywords.Default;
        }

        public static string NormalizeUtmZone(string? value)
        {
            return value switch
            {
                "12" => "12",
                _ => "11"
            };
        }
    }
}
