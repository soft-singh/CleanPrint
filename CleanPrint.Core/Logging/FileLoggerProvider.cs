using System;
using System.IO;
using Microsoft.Extensions.Logging;

namespace CleanPrint.Core.Logging;

/// <summary>
/// Appends log lines to a file. Both the Watcher and the App run with no visible console window,
/// so this is the only practical way to see what happened without attaching a debugger. Each
/// process should point this at its own file (e.g. watcher.log / app.log) under
/// <see cref="Paths.CleanPrintPaths.LogsRoot"/> so a hung/crashed Watcher doesn't interleave with
/// App logs.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _logFilePath;
    private readonly object _writeLock = new();

    public FileLoggerProvider(string logFilePath)
    {
        _logFilePath = logFilePath;
        Directory.CreateDirectory(Path.GetDirectoryName(logFilePath)!);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, _logFilePath, _writeLock);

    public void Dispose() { }

    private sealed class FileLogger : ILogger
    {
        private readonly string _category;
        private readonly string _path;
        private readonly object _writeLock;

        public FileLogger(string category, string path, object writeLock)
        {
            _category = category;
            _path = path;
            _writeLock = writeLock;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Debug;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;

            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{logLevel}] {_category}: {formatter(state, exception)}";
            if (exception is not null)
                line += Environment.NewLine + exception;

            lock (_writeLock)
            {
                try { File.AppendAllText(_path, line + Environment.NewLine); }
                catch { /* logging must never crash the app */ }
            }
        }
    }
}
