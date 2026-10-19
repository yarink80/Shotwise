using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Shotwise.Core.Platform;

/// <summary>Redacts sensitive text before it reaches a log sink.</summary>
public static class LogRedaction
{
    private static string Replace(string text, string pattern, string replacement) =>
        Regex.Replace(text, pattern, replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        try
        {
            text = Replace(text, @"\b(Authorization\s*[:=]\s*)[^\r\n]+", "$1[redacted]");
            text = Replace(text, @"\b(Bearer|Basic)\s+[A-Za-z0-9._~+/=-]+", "$1 [redacted]");
            text = Replace(text, @"([?&](?:t|token|access_token|refresh_token|code|key|api_key|secret|password|sig|signature)=)[^&#\s]+", "$1[redacted]");
            text = Replace(text, "(\\b(?:token|access_token|refresh_token|api_key|secret|password)\\b[\\\"']?\\s*[:=]\\s*[\\\"']?)[^\\s,;\\\"'}]+", "$1[redacted]");
            return Replace(text, @"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", "[email]");
        }
        catch (RegexMatchTimeoutException) { return "[redacted: oversized log message]"; }
    }

    public static ILogger Wrap(ILogger logger) => new RedactingLogger(logger);

    private sealed class RedactingLogger(ILogger inner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(Redact(state.ToString()));
        public bool IsEnabled(LogLevel level) => inner.IsEnabled(level);
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(level)) return;
            var message = Redact(formatter(state, exception) + (exception is null ? "" : "\n" + exception));
            inner.Log(level, id, message, null, static (s, _) => s);
        }
    }
}
