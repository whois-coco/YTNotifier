using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Application = System.Windows.Application;
using YTNotifier.Models;

namespace YTNotifier.Services;

public class LoggerService
{
    private static LoggerService? _instance;
    public static LoggerService Instance => _instance ??= new LoggerService();

    public ObservableCollection<LogEntry> Entries { get; } = new();
    private readonly string _logDir;
    private const int MaxUiEntries = 200;

    private LoggerService()
    {
        _logDir = Path.Combine(SettingsService.Instance.AppDataDir, "logs");
        Directory.CreateDirectory(_logDir);
    }

    public void Log(string message, LogLevel level = LogLevel.Info, string? channelName = null)
    {
        var entry = new LogEntry
        {
            Timestamp   = DateTime.Now,
            Level       = level,
            Message     = message,
            ChannelName = channelName
        };

        Application.Current?.Dispatcher.Invoke(() =>
        {
            if (!string.IsNullOrEmpty(channelName))
            {
                // チャンネル名付き: 同じチャンネルの既存エントリを全削除して最新1件のみ保持
                var toRemove = Entries
                    .Where(e => e.ChannelName == channelName)
                    .ToList();
                foreach (var e in toRemove)
                    Entries.Remove(e);
            }
            else
            {
                // システムメッセージ: 同じメッセージの既存エントリを全削除して最新1件のみ保持
                var toRemove = Entries
                    .Where(e => string.IsNullOrEmpty(e.ChannelName) && e.Message == message)
                    .ToList();
                foreach (var e in toRemove)
                    Entries.Remove(e);
            }

            // 先頭に追加
            Entries.Insert(0, entry);

            // 上限超えたら末尾を削除
            while (Entries.Count > MaxUiEntries)
                Entries.RemoveAt(Entries.Count - 1);
        });

        WriteToFile(entry);
    }

    public void Info(string msg, string? ch = null)    => Log(msg, LogLevel.Info,    ch);
    public void Success(string msg, string? ch = null) => Log(msg, LogLevel.Success, ch);
    public void Warning(string msg, string? ch = null)
    {
        Log(msg, LogLevel.Warning, ch);
        LogError(msg, LogLevel.Warning, ch);
    }
    public void Error(string msg, string? ch = null)
    {
        Log(msg, LogLevel.Error, ch);
        LogError(msg, LogLevel.Error, ch);
    }

    private void WriteToFile(LogEntry entry)
    {
        try
        {
            var fileName = $"{entry.Timestamp:yyyy-MM-dd}.log";
            var path     = Path.Combine(_logDir, fileName);
            var line     = $"[{entry.Timestamp:HH:mm:ss}] [{entry.LevelText,-7}]";
            if (entry.ChannelName != null) line += $" [{entry.ChannelName}]";
            line += $" {entry.Message}";
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch { }
    }

    public void ClearUiLog()
    {
        Application.Current?.Dispatcher.Invoke(() => Entries.Clear());
    }

    // ===== エラーログ専用コレクション（ローテーションなし・全件保持） =====
    public ObservableCollection<LogEntry> ErrorEntries { get; } = new();

    public void LogError(string message, LogLevel level, string? channelName = null)
    {
        var entry = new LogEntry
        {
            Timestamp   = DateTime.Now,
            Level       = level,
            Message     = message,
            ChannelName = channelName
        };

        Application.Current?.Dispatcher.Invoke(() =>
        {
            ErrorEntries.Insert(0, entry);
        });

        WriteToFile(entry);
    }

    public void ClearErrorLog()
    {
        Application.Current?.Dispatcher.Invoke(() => ErrorEntries.Clear());
    }
}
