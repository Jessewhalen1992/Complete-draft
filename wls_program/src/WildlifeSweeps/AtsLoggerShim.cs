using System;

namespace AtsBackgroundBuilder
{
    // Minimal logger shim to reuse ATS section-index reader logic in WLS.
    public sealed class Logger
    {
        private readonly Action<string>? _sink;

        public Logger(Action<string>? sink = null)
        {
            _sink = sink;
        }

        public void WriteLine(string message)
        {
            if (!string.IsNullOrWhiteSpace(message))
            {
                _sink?.Invoke(message);
            }
        }
    }
}
