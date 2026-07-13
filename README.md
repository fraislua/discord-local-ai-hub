# Discord統合パーソナルAI推論システム

Discordをフロントエンドとして、自宅のローカルLLM（LM Studio）とクラウドAI（Google AI Studio / Gemini）を切り替えて利用できる自分専用のAIチャットシステムです。

24時間稼働のミニPC（Proxmox LXC: 1コア / RAM 1GB）上でBotを常時稼働させ、重い推論処理はデスクトップPC上のLM Studio、またはクラウドAPIへルーティングする3層構成になっています。

```
[Discord] ⇄ [ミニPC: Discord Bot (中継サーバー)] ⇄ [デスクトップPC: LM Studio]
                                               ⇄ [Google AI Studio (Gemini)]
```

## 主な機能

- **ストリーミング表示** — 商用AIアプリのように、生成テキストがリアルタイムに流れて表示されます（Discordの文字数上限・更新頻度の制約に対応）
- **マルチプロバイダー対応** — `/model` コマンドでローカルLLMとGeminiをその場で切り替え可能（Factoryパターンによる動的ルーティング）
- **会話コンテキストの保持** — スレッド単位で会話履歴をSQLiteに保存し、モデルを切り替えても文脈を維持
- **添付ファイル解析** — テキスト / ソースコード（.cs, .py など）/ 画像（VLM対応モデルのみ）の読み込みに対応
- **思考プロセスの表示** — reasoning対応モデルの `<think>` 内容を折りたたみ表示
- **省リソース環境向けの防御的実装**
  - 画像のDecompression Bomb対策（Content-Lengthチェック → ダウンロード中のサイズ監視 → ピクセル総数制限の3段防御）
  - トークナイザーによる厳密なコンテキスト管理と履歴の自動トリミング
  - Gemini APIのロール交互制約への自動対応（履歴マージ・ダミー挿入）
  - 429 Rate Limit の型安全なハンドリング（カスタム例外）

## 動作環境

- .NET 8 以降
- Discord Bot（Message Content Intent 有効）
- LM Studio（デスクトップPC側でサーバーモード起動、モデルをロード済みであること）
- Google AI Studio APIキー（Gemini利用時）

### 依存ライブラリ（NuGet）

- Discord.Net
- Microsoft.EntityFrameworkCore.Sqlite
- SixLabors.ImageSharp
- Tokenizers.HuggingFace

## セットアップ

1. リポジトリをクローンし、依存パッケージを復元します。

   ```bash
   dotnet restore
   ```

2. `appsettings.example.json` を `appsettings.json` にコピーし、各値を設定します。

   | キー | 内容 |
   |---|---|
   | `Token` | Discord Botのトークン |
   | `LmStudioEndpoint` | LM StudioのAPIエンドポイント（例: `http://<デスクトップPCのIP>:1234/v1/chat/completions`） |
   | `GeminiApiKey` | Google AI StudioのAPIキー |
   | `ChatAiChannelId` | 日常AI対話用チャンネルのID |
   | `UnityAgentChannelId` | Unity開発用チャンネルのID |

3. トークン数カウント用に、使用モデル（Gemma 4）の `tokenizer.json` を実行ファイルと同じディレクトリに配置します（Hugging Faceのモデルページから入手できます。未配置の場合は文字数ベースの概算モードで動作します）。

4. 起動します。

   ```bash
   dotnet run
   ```

   初回起動時に `chat_history.db`（SQLite）が自動生成されます。

## 使い方

- 対象チャンネルにメッセージを送ると自動でスレッドが作成され、AIとの対話が始まります
- `/model` — そのチャンネル・スレッドで使用するAIモデルを切り替えます
- 生成中は「🛑 生成を停止」ボタンでいつでも中断できます
- ファイルや画像をドラッグ＆ドロップで添付すると、内容を読み込んで回答します

## ファイル構成

| ファイル | 役割 |
|---|---|
| `Program.cs` | エントリポイント。Discordイベント処理、モデル選択UI、エラーハンドリング |
| `ChatOrchestrator.cs` | 会話処理の中核。履歴管理・添付処理・プロバイダー呼び出しの統括 |
| `IAiProvider.cs` | AIプロバイダーの共通インターフェース |
| `LmStudioProvider.cs` | LM Studio（OpenAI互換API）との通信・SSEパース |
| `GoogleAiStudioProvider.cs` | Gemini APIとの通信。ロール交互制約への対応を内包 |
| `StreamResponseHandler.cs` | ストリーミング表示。Discordメッセージの分割・更新制御 |
| `AttachmentProcessor.cs` | 添付ファイル・画像の処理。メモリ保護機構を内包 |
| `TokenManager.cs` | トークナイザーによるトークン数カウント（シングルトン） |
| `ModelRegistry.cs` | 利用可能モデルの定義とメタデータ |
| `ChatDbContext.cs` | SQLiteによる会話履歴の永続化（EF Core） |
| `AiModels.cs` | リクエスト/レスポンスの共通データ型 |

## 注意事項

- 本システムは個人利用を想定しています。`appsettings.json`（トークン・APIキーを含む）は絶対にコミットしないでください。
- LM Studioは事前にGUIでモデルをロードしておく必要があります（オンデマンドロード非対応）。
- `chat_history.db` には会話内容がそのまま保存されます。

## ライセンス / 作者

大学演習の成果物として作成。
作者: 鈴木涼月
