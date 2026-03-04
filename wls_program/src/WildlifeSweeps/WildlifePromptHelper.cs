using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Windows;

namespace WildlifeSweeps
{
    internal static class WildlifePromptHelper
    {
        public static string PromptForUtmZone(Editor editor, string current, string promptMessage = "\nPhoto UTM zone [11/12] <11>: ")
        {
            var options = new PromptKeywordOptions(promptMessage)
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

        public static string? PromptForJpg(string promptMessage)
        {
            var dialog = new OpenFileDialog(
                promptMessage,
                "",
                "jpg;jpeg",
                "jpg",
                OpenFileDialog.OpenFileDialogFlags.DoNotTransferRemoteFiles);
            return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dialog.Filename : null;
        }
    }
}
