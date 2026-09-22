using System.IO;
using YTNotifier.Models;

namespace YTNotifier.Services;

/// <summary>
/// 通知済みの動画を記録し、再起動によるカーソルの巻き戻りで発生する重複通知を弾く。
/// 記録先は conf\notified_history.tsv（1行 = 通知日時＋TAB＋判定キー）。
/// バックアップ・インポートの対象外であり、失われても実害は「重複通知が1度出る」だけとする。
/// </summary>
public class NotifiedRecordService
{
    /// <summary>記録ファイル名（conf 直下）</summary>
    private const string FileNotifiedHistory = "notified_history.tsv";

    /// <summary>上書き時に使う一時ファイルの拡張子</summary>
    private const string TempFileExtension = ".tmp";

    /// <summary>1行の「通知日時」と「判定キー」の区切り</summary>
    private const char FieldSeparator = '\t';

    /// <summary>判定キー内の要素（動画ID・種別・予告/開始）の区切り</summary>
    private const char KeySeparator = '|';

    /// <summary>通知日時の書式（ローカル時刻）</summary>
    private const string TimestampFormat = "yyyy-MM-ddTHH:mm:ss";

    /// <summary>判定キーの要素数（動画ID・種別・予告/開始）</summary>
    private const int KeyPartCount = 3;

    /// <summary>判定キーで「予告（待機所）通知」を表す記号</summary>
    private const string UpcomingMark = "U";

    /// <summary>判定キーで「開始（公開）通知」を表す記号</summary>
    private const string PublishedMark = "P";

    /// <summary>記録の保持日数。これより古い行は起動時に捨てる</summary>
    private const int RetentionDays = 14;

    private static readonly Lazy<NotifiedRecordService> _lazy = new(() => new NotifiedRecordService());
    public static NotifiedRecordService Instance => _lazy.Value;

    private readonly string _historyPath;

    // メモリ上の判定キー集合と、ファイル追記の直列化用ロック（並列チャンネルチェックから同時に呼ばれる）
    private readonly HashSet<string> _notifiedKeys = new();
    private readonly object _recordLock = new();

    private NotifiedRecordService()
    {
        _historyPath = Path.Combine(SettingsService.Instance.ConfDir, FileNotifiedHistory);
    }

    /// <summary>動画ID・動画種別・予告か開始かの3点を連結した判定キーを作る</summary>
    private static string BuildKey(string videoId, VideoKind kind, bool isUpcoming)
        => $"{videoId}{KeySeparator}{(int)kind}{KeySeparator}{(isUpcoming ? UpcomingMark : PublishedMark)}";

    /// <summary>
    /// 起動時に1回だけ呼ぶ。ファイルを読み込んでメモリ上の集合を作り、
    /// 同時に保持日数を過ぎた行を捨てて書き直す。
    /// </summary>
    public void Load()
    {
        lock (_recordLock)
        {
            _notifiedKeys.Clear();

            string[] lines;
            try
            {
                if (!File.Exists(_historyPath)) return;
                lines = File.ReadAllLines(_historyPath, System.Text.Encoding.UTF8);
            }
            catch (Exception ex)
            {
                // 読めない場合は空の状態で開始する（起動は止めない）
                AppLogger.Log(LogMsg.NotifiedRecordLoadFailed, null, ex.Message);
                return;
            }

            var expireBefore = DateTime.Now.AddDays(-RetentionDays);
            var survivingLines = new List<string>(lines.Length);
            var discardedAny = false;

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) { discardedAny = true; continue; }

                var separatorIndex = line.IndexOf(FieldSeparator);
                if (separatorIndex <= 0 || separatorIndex >= line.Length - 1)
                {
                    discardedAny = true;
                    continue;
                }

                var timestampText = line[..separatorIndex];
                var key           = line[(separatorIndex + 1)..];

                if (!DateTime.TryParseExact(
                        timestampText, TimestampFormat,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var notifiedAt))
                {
                    discardedAny = true;
                    continue;
                }

                if (notifiedAt < expireBefore)
                {
                    discardedAny = true;
                    continue;
                }

                if (key.Split(KeySeparator).Length != KeyPartCount)
                {
                    discardedAny = true;
                    continue;
                }

                _notifiedKeys.Add(key);
                survivingLines.Add(line);
            }

            if (discardedAny) RewriteHistoryFile(survivingLines);
        }
    }

    /// <summary>この動画・種別・段階の組み合わせが既に通知済みかを返す</summary>
    public bool IsNotified(string videoId, VideoKind kind, bool isUpcoming)
    {
        var key = BuildKey(videoId, kind, isUpcoming);
        lock (_recordLock)
        {
            return _notifiedKeys.Contains(key);
        }
    }

    /// <summary>通知したことをメモリ上の集合へ追加し、ファイルへ1行追記する</summary>
    public void Record(string videoId, VideoKind kind, bool isUpcoming)
    {
        var key = BuildKey(videoId, kind, isUpcoming);
        lock (_recordLock)
        {
            _notifiedKeys.Add(key);
            try
            {
                var timestampText = DateTime.Now.ToString(
                    TimestampFormat, System.Globalization.CultureInfo.InvariantCulture);
                var line = $"{timestampText}{FieldSeparator}{key}{Environment.NewLine}";
                File.AppendAllText(_historyPath, line, System.Text.Encoding.UTF8);
            }
            catch
            {
                // 追記に失敗しても通知処理は止めない（メモリ上の記録だけ残す）
            }
        }
    }

    /// <summary>
    /// 残す行だけでファイルを書き直す。一時ファイルへ書いてから差し替えることで、
    /// 途中でプロセスが終了しても元ファイルが破損しないようにする。
    /// </summary>
    private void RewriteHistoryFile(List<string> survivingLines)
    {
        try
        {
            var tempPath = _historyPath + TempFileExtension;
            File.WriteAllLines(tempPath, survivingLines, System.Text.Encoding.UTF8);
            File.Move(tempPath, _historyPath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLogger.Log(LogMsg.NotifiedRecordLoadFailed, null, ex.Message);
        }
    }
}
