using Microsoft.Extensions.Logging;

namespace DataBridge.Desktop.ViewModels;

/// <summary>Log events raised by the desktop shell. Ids and operation names only; never user input.</summary>
internal static partial class DesktopLog
{
    [LoggerMessage(EventId = 2000, Level = LogLevel.Information, Message = "DataBridge started")]
    public static partial void Started(ILogger logger);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Critical, Message = "The local data store could not be prepared")]
    public static partial void StartupFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Error, Message = "Operation {Operation} failed unexpectedly")]
    public static partial void OperationFailed(ILogger logger, Exception exception, string operation);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Information, Message = "Operation {Operation} was cancelled")]
    public static partial void OperationCancelled(ILogger logger, string operation);

    [LoggerMessage(EventId = 2004, Level = LogLevel.Critical, Message = "Unhandled exception ({Source})")]
    public static partial void Unhandled(ILogger logger, Exception exception, string source);
}
