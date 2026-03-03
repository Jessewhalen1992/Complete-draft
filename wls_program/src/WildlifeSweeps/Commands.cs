using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

namespace WildlifeSweeps
{
    public class Commands : IExtensionApplication
    {
        private static PaletteHost? _paletteHost;
        internal static PluginSettings Settings { get; } = new PluginSettings();

        public void Initialize()
        {
        }

        public void Terminate()
        {
        }

        [CommandMethod("WLS_PALETTE")]
        public void ShowPalette()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            if (_paletteHost == null || _paletteHost.IsDisposed)
            {
                _paletteHost = new PaletteHost(Settings);
            }

            _paletteHost.Show();
        }

        [CommandMethod("COMPLETEFROMPHOTOS")]
        public void RunCompleteFromPhotos()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            var editor = doc.Editor;
            var service = new CompleteFromPhotosService();
            var settings = Settings.Clone();
            service.Execute(doc, editor, settings);
        }

        [CommandMethod("PHOTOTEXTCHECK")]
        public void RunPhotoToTextCheck()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            var editor = doc.Editor;
            var service = new PhotoToTextCheckService();
            var settings = Settings.Clone();
            service.Execute(doc, editor, settings);
        }

        [CommandMethod("IMPORTKMZKML")]
        public void RunImportKmzKml()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null)
            {
                return;
            }

            var editor = doc.Editor;
            var service = new ImportKmzKmlService();
            service.Execute(doc, editor);
        }
    }
}
