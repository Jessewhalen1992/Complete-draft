using System;
using Autodesk.AutoCAD.Windows;

namespace WildlifeSweeps
{
    public sealed class PaletteHost : IDisposable
    {
        private readonly PaletteSet _paletteSet;
        private readonly PaletteControl _control;

        public bool IsDisposed { get; private set; }

        public PaletteHost(PluginSettings settings)
        {
            _paletteSet = new PaletteSet("Wildlife Sweeps")
            {
                Style = PaletteSetStyles.ShowAutoHideButton | PaletteSetStyles.ShowCloseButton,
                MinimumSize = new System.Drawing.Size(360, 620)
            };

            _control = new PaletteControl(settings);
            _paletteSet.Add("WLS Tools", _control);
        }

        public void Show()
        {
            _paletteSet.Visible = true;
        }

        public void Dispose()
        {
            if (IsDisposed)
            {
                return;
            }

            IsDisposed = true;
            _paletteSet.Dispose();
        }
    }
}
