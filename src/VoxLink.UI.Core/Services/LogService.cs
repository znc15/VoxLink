using System.Collections.Concurrent;
using System.IO;
using System.Text;

namespace VoxLink.UI.Core.Services;

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error
}

public sealed record LogEntry(DateTime Timestamp, LogLevel Level, string Source, string Message);

/// <summary>
/// 进程内日志中心：维护最近若干条内存日志供「日志」页查看，同时把每条日志
/// 异步追加到 %APPDATA%\VoxLink\logs 下的日期文件，便于离线排查翻译与引擎问题。
/// 所有写入都不会抛出异常——日志本身绝不能拖垮主流程。
/// </summary>
public sealed class LogService
{
    private const int MaxMemoryEntries = 1000;
    private const int MaxMessageLength = 4000;

    private static readonly string InfoSource = "app";

    private readonly object _bufferLock = new();
    private readonly List<LogEntry> _buffer = new(MaxMemoryEntries + 1);
    private readonly BlockingCollection<string> _writeQueue = new(new ConcurrentQueue<string>());
    private readonly string _logFilePath;

    public static LogService Instance { get; } = new();

    private LogService()
    {
        try
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "VoxLink",
                "logs");
            Directory.CreateDirectory(directory);
            _logFilePath = Path.Combine(directory, $"VoxLink-{DateTime.Now:yyyyMMdd}.log");
            Info(InfoSource, "VoxLink 日志已启动。");
        }
        catch
        {
            _logFilePath = string.Empty;
        }

        _ = Task.Run(ProcessWriteQueueAsync);
    }

    /// <summary>日志文件所在目录；若初始化失败则为空字符串。</summary>
    public string LogDirectory
    {
        get
        {
            try
            {
                return string.IsNullOrEmpty(_logFilePath)
                    ? string.Empty
                    : Path.GetDirectoryName(_logFilePath) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }

    /// <summary>新日志到达时触发；可能在任意线程触发，订阅方需自行切回 UI 线程。</summary>
    public event EventHandler<LogEntry>? EntryAdded;

    public void Debug(string source, string message) => Append(LogLevel.Debug, source, message);
    public void Info(string source, string message) => Append(LogLevel.Info, source, message);
    public void Warning(string source, string message) => Append(LogLevel.Warning, source, message);
    public void Error(string source, string message) => Append(LogLevel.Error, source, message);

    /// <summary>记录异常及其堆栈，便于排查。</summary>
    public void Error(string source, Exception exception, string? context = null)
    {
        var message = string.IsNullOrWhiteSpace(context)
            ? exception.ToString()
            : context + " -> " + exception;
        Append(LogLevel.Error, source, message);
    }

    /// <summary>返回内存缓冲区的快照（旧到新）。</summary>
    public IReadOnlyList<LogEntry> Snapshot()
    {
        lock (_bufferLock)
        {
            return _buffer.ToArray();
        }
    }

    /// <summary>清空内存缓冲区（不影响已写入磁盘的文件）。</summary>
    public void ClearMemory()
    {
        lock (_bufferLock)
        {
            _buffer.Clear();
        }

        Append(LogLevel.Info, InfoSource, "内存日志已清空。");
    }

    private void Append(LogLevel level, string source, string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return;
        }

        if (message.Length > MaxMessageLength)
        {
            message = message[..MaxMessageLength] + "…";
        }

        var entry = new LogEntry(DateTime.Now, level, source ?? string.Empty, message);

        lock (_bufferLock)
        {
            _buffer.Add(entry);
            if (_buffer.Count > MaxMemoryEntries)
            {
                _buffer.RemoveRange(0, _buffer.Count - MaxMemoryEntries);
            }
        }

        if (_logFilePath.Length > 0)
        {
            var line = new StringBuilder(128)
                .Append(entry.Timestamp.ToString("HH:mm:ss.fff"))
                .Append(" [").Append(LevelTag(level)).Append("] ")
                .Append('[').Append(entry.Source).Append("] ")
                .Append(entry.Message)
                .ToString();
            try
            {
                _writeQueue.Add(line);
            }
            catch (InvalidOperationException)
            {
                // 队列已关闭，忽略。
            }
            catch
            {
                // 静默：日志不能反噬调用方。
            }
        }

        try
        {
            EntryAdded?.Invoke(this, entry);
        }
        catch
        {
            // 订阅方异常不应影响记录。
        }
    }

    private async Task ProcessWriteQueueAsync()
    {
        foreach (var line in _writeQueue.GetConsumingEnumerable())
        {
            try
            {
                await File.AppendAllTextAsync(_logFilePath, line + Environment.NewLine);
            }
            catch
            {
                // 磁盘不可写时静默丢弃，保持进程稳定。
            }
        }
    }

    public static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        _ => "?"
    };

    public static string LevelDisplayName(LogLevel level) => level switch
    {
        LogLevel.Debug => "调试",
        LogLevel.Info => "信息",
        LogLevel.Warning => "警告",
        LogLevel.Error => "错误",
        _ => level.ToString()
    };
}
