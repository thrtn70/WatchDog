using System.IO;
using Microsoft.Extensions.Logging;

namespace TikrClipr.App.Logging;

/// <summary>
/// Simple file-based logger that writes to a rolling daily log file.
/// Logs to %APPDATA%\TikrClipr\logs\tikrclipr-YYYY-MM-DD.log
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logDirectory;
    private readonly LogLevel _minLevel;
    private readonly object _lock = new();
    private StreamWriter? _writer;
    private string _currentDate = string.Empty;

    public FileLoggerProvider(LogLevel minLevel = LogLevel.Information)
    {
        _minLevel = minLevel;
        _logDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "TikrClipr", "logs");

        Directory.CreateDirectory(_logDirectory);
    }

    public ILogger CreateLogger(string categoryName)
        => new FileLogger(this, categoryName, _minLevel);

    public void Dispose()
    {
        lock (_lock)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    internal void WriteLog(string message)
    {
        lock (_lock)
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            if (today != _currentDate)
            {
                _writer?.Dispose();
                var logPath = Path.Combine(_logDirectory, $"tikrclipr-{today}.log");
                _writer = new StreamWriter(logPath, append: true) { AutoFlush = true };
                _currentDate = today;
            }

            _writer?.WriteLine(message);
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category, LogLevel minLevel) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= minLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
            var level = logLevel.ToString().ToUpperInvariant()[..3]; // INF, WRN, ERR, etc.
            var shortCategory = category.Contains('.') ? category[(category.LastIndexOf('.') + 1)..] : category;
            var message = $"[{timestamp}] [{level}] [{shortCategory}] {formatter(state, exception)}";

            if (exception is not null)
                message += Environment.NewLine + exception;

            provider.WriteLog(message);
        }
    }
}
