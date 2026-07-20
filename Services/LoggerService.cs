using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Application = System.Windows.Application;
using YTNotifier.Constants;
using YTNotifier.Models;

namespace YTNotifier.Services;

public class LoggerService
{
    private static readonly Lazy<LoggerService> _lazy = new(() => new LoggerService());
    public static LoggerService Instance => _lazy.Value;

    public ObservableCollection<LogEntry> Entries { get; } = new();
    public ObservableCollection<LogEntry> TodayEntries { get; } = new();
    private readonly string _logDir;
    private readonly object _fileLock = new();
    private string? _debugDbPath;
    private DateTime _debugDbDate = DateTime.MinValue;
    private readonly object _dbLock = new();
    private const int MaxUiEntries      = 200;
    private const int MaxTodayEntries   = 1000;
    private const int MaxErrorEntries   = 500;
    private DateTime _currentLogDate = DateTime.Today;

    private LoggerService()
    {
        _logDir = Path.Combine(SettingsService.Instance.AppDataDir, AppConstants.DirLogs);
        Directory.CreateDirectory(_logDir);
        LoadTodayLogFile();
    }

    private void LoadTodayLogFile()
    {
        try
        {
            var path = Path.Combine(_logDir, $"{DateTime.Today:yyyy-MM-dd}.log");
            if (!File.Exists(path)) return;
            var lines = File.ReadAllLines(path);
            var start = Math.Max(0, lines.Length - MaxTodayEntries);
            for (var i = start; i < lines.Length; i++)
            {
                var entry = ParseLogLine(lines[i], DateTime.Today);
                if (entry != null) TodayEntries.Add(entry);
            }
        }
        catch { }
    }

    private static LogEntry? ParseLogLine(string line, DateTime date)
    {
        // フォーマット: [HH:mm:ss] [LEVEL  ] [channelName] message
        //          or: [HH:mm:ss] [LEVEL  ] message
        if (line.Length < 20 || line[0] != '[') return null;
        var timeEnd = line.IndexOf(']');
        if (timeEnd < 0) return null;
        if (!TimeSpan.TryParse(line[1..timeEnd], out var time)) return null;

        var rest = line[(timeEnd + 2)..];
        if (rest.Length == 0 || rest[0] != '[') return null;
        var levelEnd = rest.IndexOf(']');
        if (levelEnd < 0) return null;
        var levelStr = rest[1..levelEnd].Trim();
        rest = rest.Length > levelEnd + 2 ? rest[(levelEnd + 2)..] : string.Empty;

        string? channelName = null;
        string message = rest;
        if (rest.StartsWith('['))
        {
            var chEnd = rest.IndexOf(']');
            if (chEnd > 0) { channelName = rest[1..chEnd]; message = rest.Length > chEnd + 2 ? rest[(chEnd + 2)..] : string.Empty; }
        }

        var level = levelStr switch
        {
            "SYSTEM"  => LogLevel.System,
            "INFO"    => LogLevel.Info,
            "WARNING" => LogLevel.Warning,
            "ERROR"   => LogLevel.Error,
            "DEBUG"   => LogLevel.Debug,
            _         => LogLevel.Info
        };
        return new LogEntry { Timestamp = date + time, Level = level, Message = message, ChannelName = channelName };
    }

    public void Log(string message, LogLevel level, string? channelName, LogCategory category)
    {
        var entry = new LogEntry
        {
            Timestamp   = DateTime.Now,
            Level       = level,
            Message     = message,
            ChannelName = channelName
        };

        WriteToDebugDb(entry, category);
        if (level == LogLevel.Debug) return;

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            // 中間リスト不要：逆順インデックスで直接削除
            if (!string.IsNullOrEmpty(channelName))
            {
                for (int i = Entries.Count - 1; i >= 0; i--)
                    if (Entries[i].ChannelName == channelName) Entries.RemoveAt(i);
            }
            else
            {
                for (int i = Entries.Count - 1; i >= 0; i--)
                    if (string.IsNullOrEmpty(Entries[i].ChannelName) && Entries[i].Message == message) Entries.RemoveAt(i);
            }

            Entries.Add(entry);

            while (Entries.Count > MaxUiEntries)
                Entries.RemoveAt(0);

            // 当日ログ（全件・重複削除なし・上限付き）
            if (entry.Timestamp.Date != _currentLogDate)
            {
                TodayEntries.Clear();
                _currentLogDate = entry.Timestamp.Date;
            }
            TodayEntries.Add(entry);
            while (TodayEntries.Count > MaxTodayEntries)
                TodayEntries.RemoveAt(0);
        });
    }

    public void System(string msg, string? ch, LogCategory cat)
        => Log(msg, LogLevel.System, ch, cat);
    public void Info(string msg, string? ch, LogCategory cat)
        => Log(msg, LogLevel.Info, ch, cat);
    public void Debug(string msg, string? ch, LogCategory cat)
        => Log(msg, LogLevel.Debug, ch, cat);
    public void Warning(string msg, string? ch, LogCategory cat)
    {
        Log(msg, LogLevel.Warning, ch, cat);
        LogError(msg, LogLevel.Warning, ch);
    }
    public void Error(string msg, string? ch, LogCategory cat)
    {
        Log(msg, LogLevel.Error, ch, cat);
        LogError(msg, LogLevel.Error, ch);
    }

    private static string FormatLogLine(LogEntry entry)
    {
        var line = $"[{entry.Timestamp:HH:mm:ss}] [{entry.LevelText,-7}]";
        if (entry.ChannelName != null) line += $" [{entry.ChannelName}]";
        line += $" {entry.Message}";
        return line;
    }

    /// <summary>動作ログウィンドウの「ファイルへ保存」ボタンから呼ばれる。当日ログ全件でその日の.logファイルを上書きする</summary>
    public int SaveTodayLogToFile()
    {
        var fileName = $"{DateTime.Today:yyyy-MM-dd}.log";
        var path     = Path.Combine(_logDir, fileName);
        var lines    = TodayEntries.Select(FormatLogLine).ToList();

        lock (_fileLock)
        {
            File.WriteAllLines(path, lines);
        }
        return lines.Count;
    }

    public void ClearUiLog()
    {
        Application.Current?.Dispatcher.Invoke(() => Entries.Clear());
    }

    /// <summary>動作ログウィンドウの当日ログ表示をクリアする（ログファイルは削除しない）</summary>
    public void ClearTodayLog()
    {
        Application.Current?.Dispatcher.Invoke(() => TodayEntries.Clear());
    }

    // ===== ログメンテナンス =====
    /// <summary>指定日数より古いログファイルを削除する</summary>
    public (int deleted, long freedBytes) CleanOldLogs(int retentionDays)
    {
        int deleted = 0;
        long freed  = 0;
        try
        {
            // -1 = 無制限（削除しない）
            if (retentionDays < 0) return (0, 0);

            // 0日 = 当日のみ残す（昨日以前を削除）
            // N日 = N日前より古いものを削除
            var today  = DateTime.Today;
            var cutoff = retentionDays == 0
                ? today  // 今日より前（昨日以前）を削除
                : DateTime.Now.AddDays(-retentionDays);

            foreach (var file in Directory.GetFiles(_logDir, "*.log"))
            {
                var info = new FileInfo(file);
                var fileDate = info.LastWriteTime.Date;

                var shouldDelete = retentionDays == 0
                    ? fileDate < today          // 当日以外を削除
                    : info.LastWriteTime < cutoff;

                if (shouldDelete)
                {
                    freed += info.Length;
                    File.Delete(file);
                    deleted++;
                }
            }

            // traceDBも同じ保持期間でクリーンアップ
            foreach (var file in Directory.GetFiles(_logDir, "trace_*.db"))
            {
                var info = new FileInfo(file);
                var shouldDelete = retentionDays == 0
                    ? info.LastWriteTime.Date < today
                    : info.LastWriteTime < cutoff;

                if (shouldDelete)
                {
                    freed += info.Length;
                    File.Delete(file);
                    deleted++;
                }
            }
        }
        catch { }
        return (deleted, freed);
    }

    /// <summary>ログフォルダの統計情報を取得する</summary>
    public (int fileCount, long totalBytes, DateTime? oldest, DateTime? newest) GetLogStats()
    {
        try
        {
            var files = Directory.GetFiles(_logDir, "*.log")
                .Select(f => new FileInfo(f))
                .OrderBy(f => f.LastWriteTime)
                .ToList();
            if (files.Count == 0) return (0, 0, null, null);
            return (
                files.Count,
                files.Sum(f => f.Length),
                files.First().LastWriteTime,
                files.Last().LastWriteTime
            );
        }
        catch { return (0, 0, null, null); }
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

        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            ErrorEntries.Insert(0, entry);
            while (ErrorEntries.Count > MaxErrorEntries)
                ErrorEntries.RemoveAt(ErrorEntries.Count - 1);
        });
    }

    public void ClearErrorLog()
    {
        Application.Current?.Dispatcher.Invoke(() => ErrorEntries.Clear());
    }

    private void InitDebugDb(DateTime date)
    {
        _debugDbPath = Path.Combine(_logDir, $"trace_{date:yyyyMMdd}.db");
        _debugDbDate = date;
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_debugDbPath}");
        conn.Open();
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS log (
                id  INTEGER PRIMARY KEY AUTOINCREMENT,
                ts  INTEGER NOT NULL,
                lvl INTEGER NOT NULL,
                ch  TEXT,
                cat TEXT,
                msg TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_ts ON log(ts);
            """;
        cmd.ExecuteNonQuery();
    }

    private void WriteToDebugDb(LogEntry entry, LogCategory category)
    {
        if (!SettingsService.Instance.Settings.TraceLogEnabled) return;
        if (_debugDbPath == null) InitDebugDb(entry.Timestamp.Date);
        try
        {
            if (entry.Timestamp.Date > _debugDbDate)
                InitDebugDb(entry.Timestamp.Date);
            lock (_dbLock)
            {
                using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_debugDbPath}");
                conn.Open();
                var cmd = conn.CreateCommand();
                cmd.CommandText = "INSERT INTO log (ts, lvl, ch, cat, msg) VALUES ($ts, $lvl, $ch, $cat, $msg)";
                cmd.Parameters.AddWithValue("$ts",  new DateTimeOffset(entry.Timestamp).ToUnixTimeMilliseconds());
                cmd.Parameters.AddWithValue("$lvl", (int)entry.Level);
                cmd.Parameters.AddWithValue("$ch",  entry.ChannelName ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("$cat", category.ToString());
                cmd.Parameters.AddWithValue("$msg", entry.Message);
                cmd.ExecuteNonQuery();
            }
        }
        catch { }
    }
}
