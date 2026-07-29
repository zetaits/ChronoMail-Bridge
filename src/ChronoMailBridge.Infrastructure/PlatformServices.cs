using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using ChronoMailBridge.Core;
using Google;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace ChronoMailBridge.Infrastructure;

public sealed class DefaultErrorClassifier : IErrorClassifier
{
    public ErrorClassification Classify(Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return new(ErrorKind.Permanent, "cancelled");
        }

        if (exception is TimeoutException or IOException or ImapProtocolException or ServiceNotConnectedException)
        {
            return new(ErrorKind.Temporary, exception.GetType().Name);
        }

        if (exception is AuthenticationException)
        {
            return new(ErrorKind.Authentication, "authentication_failed");
        }

        if (exception is GoogleApiException google)
        {
            string? reason = google.Error?.Errors?.FirstOrDefault()?.Reason;
            if (google.HttpStatusCode == HttpStatusCode.TooManyRequests ||
                reason is "rateLimitExceeded" or "userRateLimitExceeded")
            {
                return new(ErrorKind.RateLimited, reason ?? "http_429");
            }

            if ((int)google.HttpStatusCode >= 500)
            {
                return new(ErrorKind.Temporary, $"http_{(int)google.HttpStatusCode}");
            }

            if (google.HttpStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return new(ErrorKind.Authentication, $"http_{(int)google.HttpStatusCode}");
            }

            return new(ErrorKind.Permanent, $"http_{(int)google.HttpStatusCode}");
        }

        if (exception is HttpRequestException http)
        {
            return new(
                http.StatusCode is null || (int)http.StatusCode >= 500
                    ? ErrorKind.Temporary
                    : ErrorKind.Permanent,
                http.StatusCode is null ? "network_error" : $"http_{(int)http.StatusCode}");
        }

        if (exception is InvalidDataException)
        {
            return new(ErrorKind.Permanent, "invalid_mime_or_size");
        }

        return new(ErrorKind.Unknown, exception.GetType().Name);
    }
}

public sealed class WindowsPowerManagement : IPowerManagement
{
    [Flags]
    private enum ExecutionState : uint
    {
        Continuous = 0x80000000,
        SystemRequired = 0x00000001
    }

    public void PreventSleep()
    {
        ExecutionState result = SetThreadExecutionState(
            ExecutionState.Continuous | ExecutionState.SystemRequired);
        if (result == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public void Restore() => SetThreadExecutionState(ExecutionState.Continuous);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState executionState);
}

public sealed class SerilogTechnicalLog : ITechnicalLog, IDisposable
{
    private readonly Logger _logger;

    public SerilogTechnicalLog(string logsDirectory)
    {
        Directory.CreateDirectory(logsDirectory);
        _logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(
                Path.Combine(logsDirectory, "chronomail-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                fileSizeLimitBytes: 20 * 1024 * 1024,
                rollOnFileSizeLimit: true,
                formatProvider: System.Globalization.CultureInfo.InvariantCulture,
                outputTemplate:
                    "{Timestamp:O} [{Level:u3}] event={EventName} id={TechnicalId} code={Code}{NewLine}")
            .CreateLogger();
    }

    public void Information(string eventName, long? technicalId = null, string? code = null) =>
        Write(LogEventLevel.Information, eventName, technicalId, code);

    public void Warning(string eventName, long? technicalId = null, string? code = null) =>
        Write(LogEventLevel.Warning, eventName, technicalId, code);

    public void ErrorEvent(string eventName, long? technicalId = null, string? code = null) =>
        Write(LogEventLevel.Error, eventName, technicalId, code);

    public void Dispose() => (_logger as IDisposable)?.Dispose();

    private void Write(LogEventLevel level, string eventName, long? id, string? code)
    {
        string safeEvent = Sanitize(eventName);
        string? safeCode = code is null ? null : Sanitize(code);
        _logger.Write(level, "{EventName} {TechnicalId} {Code}", safeEvent, id, safeCode);
    }

    private static string Sanitize(string value)
    {
        string safe = new(value.Where(character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.').ToArray());
        return safe.Length > 80 ? safe[..80] : safe;
    }
}

public sealed class NullTechnicalLog : ITechnicalLog
{
    public void Information(string eventName, long? technicalId = null, string? code = null) { }
    public void Warning(string eventName, long? technicalId = null, string? code = null) { }
    public void ErrorEvent(string eventName, long? technicalId = null, string? code = null) { }
}

public sealed class NoOpPowerManagement : IPowerManagement
{
    public void PreventSleep() { }
    public void Restore() { }
}
