# YTNotifier v1.2.1 - YouTube新着動画通知システム

YouTube Data API v3 を利用し、指定チャンネルの新着動画を定期監視・通知する WPF 常駐アプリ。

---

## 動作環境

- Windows 10 / 11
- .NET 8.0 以上
- YouTube Data API v3 APIキー

---

## セットアップ

### 1. ビルド

```bash
cd YTNotifier
dotnet restore
dotnet build -c Release
```

### 2. 発行（Self-Contained）

```bash
dotnet publish -c Release -r win-x64 --self-contained -o ./publish
```

### 3. アイコンの準備

`Resources\` フォルダに以下の2ファイルを用意してください。

| ファイル | 用途 | 推奨サイズ |
|---|---|---|
| `app.ico` | ウィンドウ・トレイ通常時 | 16/32/48/256px 内包 |
| `app_warn.ico` | トレイ停止時 | 16/32/48/256px 内包 |

仮アイコンを生成する場合:
```bash
python generate_icons.py
```

---

## YouTube API キーの取得

1. [Google Cloud Console](https://console.developers.google.com) を開く
2. プロジェクトを作成または選択
3. 「APIとサービス」→「ライブラリ」で **YouTube Data API v3** を有効化
4. 「認証情報」→「認証情報を作成」→「APIキー」を選択
5. アプリの「基本設定」画面にAPIキーを貼り付けて保存

---

## ファイル構成

```
YTNotifier/
├── YTNotifier.csproj
├── App.xaml / App.xaml.cs       # 起動・テーマ切替・システムトレイ
│
├── Models/
│   └── AppModels.cs             # AppSettings / ChannelInfo / LogEntry
│
├── Services/
│   ├── SettingsService.cs       # config.json / channels.json の読み書き
│   ├── LoggerService.cs         # UIログ・エラーログ + 日別ファイル出力
│   ├── YouTubeApiService.cs     # YouTube API v3 通信・動画種別判定
│   └── MonitorService.cs        # 定期監視・トースト通知
│
├── Views/
│   ├── MainWindow.xaml          # サイドバー + 3ページ構成のUI
│   └── MainWindow.xaml.cs       # UIロジック・チャンネル行コード生成
│
├── Themes/
│   ├── LightTheme.xaml          # ライトテーマ
│   ├── DarkTheme.xaml           # ダークテーマ
│   └── CommonStyles.xaml        # 共通スタイル（フラットUI）
│
└── Resources/
    ├── app.ico                  # トレイ・ウィンドウアイコン（通常）
    └── app_warn.ico             # トレイアイコン（停止中）
```

---

## データ保存先

`%APPDATA%\YTNotifier\`

| ファイル | 内容 |
|---|---|
| `config.json` | APIキー・テーマ・通知設定・ウィンドウサイズ・サイドバー状態等 |
| `channels.json` | 確認チャンネルリスト（順序・種別設定・表示名含む） |
| `logs\YYYY-MM-DD.log` | 日別ログファイル（全件累積） |

---

## 主要機能

### チャンネル管理
- チャンネルID / ハンドル名（`@name`） / URL で追加
- 追加前にプレビュー確認（チャンネル名・登録者数・アイコン）
- **ドラッグアンドドロップ**でリスト内の順序を変更
- チャンネルアイコンをクリックで有効な種別の最新動画をブラウザで開く
- 右クリックメニュー:
  - 🔔 NEWバッジを消す
  - ✏ 名称を変更（表示名をカスタマイズ）

### 通知種別フィルター
各チャンネルごとに **動画 / Short / ライブ** の通知種別を個別に設定可能。

| 設定例 | クリック時の動作 |
|---|---|
| 動画・ライブ ON | 最新がライブ→ライブを開く、最新が動画→動画を開く |
| 動画のみ ON | Short・ライブをスキップして最新の通常動画を開く |
| 全て OFF | チャンネルページを開く |

### 監視・通知
- 1〜60分間隔（設定変更可）でチャンネルを**並列チェック**
- 複数の新着があっても通知は**最新1件のみ**（多重通知防止）
- 動画種別を `videos.list` + `liveStreamingDetails` で正確に判定
  - Short: 2分30秒（150秒）以内
  - ライブ/アーカイブ: `liveStreamingDetails` フィールドが存在する動画
- **NEW バッジ**: 新着検出時にチャンネル名横に表示、クリック or 右クリックメニューで消去
- Windows トースト通知をクリックで動画をブラウザ再生

### UI
- **フラットデザイン**（シャドウなし・グラデーションなし）
- ライト / ダークモードをリアルタイム切替
- **カスタムタイトルバー**（最小化 / 最大化 / 閉じる）
- 折り畳み可能なサイドバーナビゲーション（状態を保存・復元）
  - 確認リスト・動作ログ・基本設定
- ウィンドウサイズ・位置を終了時に自動保存
- チャンネル追加エリアを折り畳み可能
- 常に前面に表示オプション

### 動作ログ
- 各チャンネルの**最新ステータス1件のみ**をUIに表示（起動毎にリセット）
- **エラーログセクション**（動作ログページ下部）
  - Warning・Error レベルのみ表示
  - ローテーションなし（全件保持）
  - クリアボタンで手動消去
- ファイルには全件累積出力（`logs\YYYY-MM-DD.log`）

### システムトレイ
- `System.Windows.Forms.NotifyIcon` による確実なトレイ常駐
- 右クリックメニュー: ウィンドウを開く / 今すぐチェック / 監視開始・停止 / 終了
- 「タスクトレイに格納」OFF 時は × ボタンでプロセス完全終了

---

## APIコスト目安

| 操作 | コスト |
|---|---|
| チャンネル情報取得（プレビュー） | 1ユニット |
| 新着チェック（activities.list） | 1ユニット × チャンネル数 |
| 動画種別判定（videos.list） | 1ユニット × チェック時の新着数 |
| 1日の消費（10ch・5分間隔） | ≈ 2,880〜3,000ユニット |

YouTube Data API v3 の無料枠は **10,000ユニット/日**。
10チャンネル・5分間隔での運用は無料枠内で十分対応可能。

---

## 注意事項

- Windows 10 以降の **Windowsトースト通知**を使用します
- Self-Contained 発行時は `--self-contained` オプションを付けてください
- APIキー未設定の場合、チャンネル追加・監視機能は動作しません
- MinHeight / MinWidth はそれぞれ 460px に設定されています

---

## 変更履歴

### v1.2.1
- Short判定を2分30秒（150秒）以内に変更

### v1.2.0
- ドラッグアンドドロップによるチャンネル並び替えを追加
- チャンネルアイコンクリックで最新動画を開く（行全体クリックから変更）
- エラーログセクションを動作ログページ下部に追加
- チャンネル右クリックで名称変更・NEWバッジ消去が可能に
- サイドバー折り畳み状態を保存・復元するよう改善
- 常に前面に表示オプションを追加
- ダークモード対応を全UIに拡充

### v1.0.0
- 初回リリース
