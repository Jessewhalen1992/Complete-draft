using System;

namespace Compass.Infrastructure.Logging;

/// <summary>
/// Slim abstraction to keep logging pluggable.
/// </summary>
public interface ILog
{
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? exception = null);
    void Debug(string message);
    IDisposable BeginScope(string scopeMessage);
}
