using System.Text;
using Eden.Infrastructure.Diagnostics;
using Microsoft.Extensions.Logging;
using ZLogger;
using ZLogger.Providers;

namespace Eden.Server.Logging;

internal sealed class BufferedServerLog : IAsyncDisposable
{
    internal const int QueueCapacity = 4096;
    private readonly ZLoggerStreamLoggerProvider _output;
    private readonly ZLoggerStreamLoggerProvider _error;
    private readonly IDisposable _redirection;
    private int _disposed;

    internal BufferedServerLog(ServerLog log, Stream output, Stream error, TimeProvider? clock = null, int capacity = QueueCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        var options = new ZLoggerOptions
        {
            FullMode = BackgroundBufferFullMode.Block,
            BackgroundBufferCapacity = capacity,
            TimeProvider = clock,
            InternalErrorLogger = failure => System.Console.Error.WriteLine($"Server log output failed: {failure.Message}"),
        };
        options.UsePlainTextFormatter(formatter =>
            formatter.SetPrefixFormatter(
                $"{0:HH:mm:ss} [{1}] ",
                (in MessageTemplate template, in LogInfo info) => template.Format(info.Timestamp.Local, LevelName(info.LogLevel))
            )
        );
        _output = new(output, options);
        _error = new(error, options);
        var information = _output.CreateLogger("Eden");
        var errors = _error.CreateLogger("Eden");
        try
        {
            _redirection = log.Redirect(
                (level, message) =>
                {
                    switch (level)
                    {
                        case ServerLogLevel.Debug:
                            information.ZLogDebug($"{message}");
                            break;
                        case ServerLogLevel.Information:
                            information.ZLogInformation($"{message}");
                            break;
                        case ServerLogLevel.Warning:
                            errors.ZLogWarning($"{message}");
                            break;
                        case ServerLogLevel.Error:
                            errors.ZLogError($"{message}");
                            break;
                        case ServerLogLevel.Trace:
                            information.ZLogTrace($"{message}");
                            break;
                    }
                }
            );
        }
        catch
        {
            _output.Dispose();
            _error.Dispose();
            throw;
        }
    }

    internal static BufferedServerLog Start()
    {
        System.Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        return new(ServerLog.Console, System.Console.OpenStandardOutput(), System.Console.OpenStandardError());
    }

    private static string LevelName(LogLevel level) =>
        ServerLog.LevelName(
            level switch
            {
                LogLevel.Debug => ServerLogLevel.Debug,
                LogLevel.Information => ServerLogLevel.Information,
                LogLevel.Warning => ServerLogLevel.Warning,
                LogLevel.Error => ServerLogLevel.Error,
                LogLevel.Trace => ServerLogLevel.Trace,
                _ => throw new ArgumentOutOfRangeException(nameof(level)),
            }
        );

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _redirection.Dispose();
            try
            {
                await _output.DisposeAsync();
            }
            finally
            {
                await _error.DisposeAsync();
            }
        }
    }
}
