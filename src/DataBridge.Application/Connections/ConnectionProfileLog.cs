using Microsoft.Extensions.Logging;

namespace DataBridge.Application.Connections;

/// <summary>
/// Log events for connection profiles. They carry ids and outcomes only: never profile names, server
/// names, user names or passwords (NFR-SEC-003).
/// </summary>
internal static partial class ConnectionProfileLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Information, Message = "Created connection profile {ProfileId}; password storage: {StorageOutcome}")]
    public static partial void ProfileCreated(ILogger logger, Guid profileId, SecretStorageKind storageOutcome);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Information, Message = "Updated connection profile {ProfileId}; password storage: {StorageOutcome}")]
    public static partial void ProfileUpdated(ILogger logger, Guid profileId, SecretStorageKind storageOutcome);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Information, Message = "Renamed connection profile {ProfileId}")]
    public static partial void ProfileRenamed(ILogger logger, Guid profileId);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Information, Message = "Deleted connection profile {ProfileId}")]
    public static partial void ProfileDeleted(ILogger logger, Guid profileId);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Information, Message = "Connection profile {ProfileId} was not saved: {ErrorCodes}")]
    public static partial void ProfileRejected(ILogger logger, Guid profileId, string errorCodes);

    [LoggerMessage(EventId = 1010, Level = LogLevel.Warning, Message = "OS credential store unavailable; the password of connection profile {ProfileId} is kept for this session only: {Reason}")]
    public static partial void PasswordKeptForSession(ILogger logger, Guid profileId, string reason);

    [LoggerMessage(EventId = 1011, Level = LogLevel.Warning, Message = "Could not remove credential store entry {CredentialReference}: {Reason}")]
    public static partial void PasswordNotRemoved(ILogger logger, Exception? exception, string credentialReference, string reason);
}
