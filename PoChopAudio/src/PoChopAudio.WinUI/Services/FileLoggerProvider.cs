using System.Text;
using Microsoft.Extensions.Logging;

namespace PoChopAudio.WinUI.Services;

/// <summary>
/// Appends log lines to a file under %LOCALAPPDATA%\PoChopAudio\logs.
///
/// <para>
/// The app registered <c>AddLogging()</c> with no providers, so every carefully source-generated
/// log message went nowhere. That is fine until something goes wrong in a path with no visible
/// output -- a head crop landing in the wrong place tells you nothing about whether the face
/// detector ran, found nothing, or was overruled.
/// </para>
/// <para>
/// Deliberately minimal: no rotation policy beyond a size cap, no async queue, no external
/// dependency. A desktop app writing a handful of lines per photo does not need more.
/// </para>
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private const long MaxBytes = 2 * 1024 * 1024;

    private readonly string _path;
    private readonly Lock _gate = new();

    public FileLoggerProvider()
    {
        LogDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PoChopAudio", "logs");
        Directory.CreateDirectory(LogDirectory);
        _path = Path.Combine(LogDirectory, "pochopaudio.log");
    }

    /// <summary>The folder holding the log, so a settings page can open it in Explorer.</summary>
    public string LogDirectory { get; }

    /// <summary>Where the log is being written, for surfacing to the user.</summary>
    public string LogFilePath => _path;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Write(string line)
    {
        lock (_gate)
        {
            try
            {
                // Start over rather than grow without bound; the recent lines are the useful ones.
                if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
                {
                    File.Delete(_path);
                }

                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (IOException)
            {
                // Logging must never take the app down.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    public void Dispose()
    {
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            ArgumentNullException.ThrowIfNull(formatter);

            var shortCategory = category[(category.LastIndexOf('.') + 1)..];
            var line = $"{DateTimeOffset.Now:HH:mm:ss.fff} {logLevel,-11} {shortCategory}: {formatter(state, exception)}";

            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            owner.Write(line);
        }
    }
}
