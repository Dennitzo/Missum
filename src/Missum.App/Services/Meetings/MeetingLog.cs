using Microsoft.Extensions.Logging;

namespace Missum.App.Services.Meetings;

internal static partial class MeetingLog
{
    [LoggerMessage(EventId = 4100, Level = LogLevel.Information, Message = "Claude Meetings host listening on port {Port}")]
    public static partial void HostStarted(ILogger logger, int port);

    [LoggerMessage(EventId = 4101, Level = LogLevel.Warning, Message = "Claude Meetings host could not start")]
    public static partial void HostStartFailed(ILogger logger, Exception? exception);

    [LoggerMessage(EventId = 4102, Level = LogLevel.Warning, Message = "Meeting observer failed in {Stage}")]
    public static partial void ObserverFailed(ILogger logger, string stage, Exception exception);

    [LoggerMessage(EventId = 4103, Level = LogLevel.Warning, Message = "Meeting could not be saved")]
    public static partial void SaveFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4104, Level = LogLevel.Warning, Message = "Meeting folder could not be loaded")]
    public static partial void LoadFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4105, Level = LogLevel.Warning, Message = "Meeting agent update failed")]
    public static partial void AgentFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4106, Level = LogLevel.Warning, Message = "Meeting speech recognition failed")]
    public static partial void SpeechFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 4107, Level = LogLevel.Debug, Message = "Camera frame dropped")]
    public static partial void CameraFrameDropped(ILogger logger, Exception exception);
}
