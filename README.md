# Discord統合パーソナルAI推論システム

Discordをフロントエンドとして、自宅のローカルLLM（LM Studio）とクラウドAI（Vertex AI経由のGemini・Grok、OpenAI）を切り替えて利用できる自分専用のAIチャットシステムです。同一プロセス内で、Tailscale経由のMCPサーバー機能（Claude Code等からの直接呼び出し）も提供します。

24時間稼働のミニPC（Proxmox LXC: 2コア / RAM 1GB）上でBotを常時稼働させ、重い推論処理はデスクトップPC上のLM Studio、またはクラウドAPIへルーティングする3層構成になっています。

```
[Discord]     ⇄ [ミニPC: Discord Bot (中継サーバー)] ⇄ [デスクトップPC: LM Studio]
[Claude Code] ⇄ [       ↑ 同一プロセス、MCPサーバー]  ⇄ [Vertex AI (Gemini / Grok) / OpenAI]
```

## 主な機能

- **ストリーミング表示** — 商用AIアプリのように、生成テキストがリアルタイムに流れて表示されます（Discordの文字数上限・更新頻度の制約に対応）
- **マルチプロバイダー対応** — `/model` コマンドでローカルLLM・Vertex AI経由のGemini/Grokをその場で切り替え可能（Factoryパターンによる動的ルーティング）
- **会話コンテキストの保持** — スレッド単位で会話履歴をSQLiteに保存し、モデルを切り替えても文脈を維持
- **添付ファイル解析** — テキスト / ソースコード（.cs, .py など）/ 画像（VLM対応モデルのみ）の読み込みに対応
- **思考プロセスの表示** — reasoning対応モデルの `<think>` 内容を折りたたみ表示（LM Studioのreasoningモデル向け。Vertex AI経由のGemini 3.8 Flash / Grok 4.6は内部思考をテキストとして返さないため対象外）
- **省リソース環境向けの防御的実装**
  - 画像のDecompression Bomb対策（Content-Lengthチェック → ダウンロード中のサイズ監視 → ピクセル総数制限の3段防御）
  - トークナイザーによる厳密なコンテキスト管理と履歴の自動トリミング
  - Gemini APIのロール交互制約への自動対応（履歴マージ・ダミー挿入。Google AI Studio / Vertex AI共通）
  - 429 Rate Limit の型安全なハンドリング（カスタム例外）

## 対応モデル

`/model` コマンドで以下8モデルを切り替えられます（`ModelRegistry.cs`で定義）。

| モデル | 実行先 | 画像対応 |
|---|---|---|
| Gemma-4-12B-VLM (`google/gemma-4-12b-qat`) | ローカル (LM Studio) | ○ |
| Gemma-4-E4B-Uncensored (`gemma-4-e4b-uncensored-hauhaucs-aggressive`) | ローカル (LM Studio) | ○ |
| Qwen3.8-27B (`qwen/qwen3.8-27b`) | ローカル (LM Studio) | ○ |
| Gemini 3.8 Flash (`gemini-3.8-flash`) | Vertex AI | ○ |
| Grok 4.6 (`xai/grok-4.6`) | Vertex AI | × (未検証のため無効化) |
| GPT-5.6 Sol (`gpt-5.6-sol`) | OpenAI API | ○ |
| GPT-5.6 Terra (`gpt-5.6-terra`) | OpenAI API | ○ |
| GPT-5.6 Luna (`gpt-5.6-luna`) | OpenAI API | ○ |

GPT-5.6系はOpenAIのデータ共有プログラム(無料枠)を前提とした運用（`OpenAiQuota.cs`）。
Sol単独で25万トークン/日、Terra+Lunaは合算で250万トークン/日の日次上限があり、
`Program.cs`側で送信前に事前チェックし、上限に達する場合は課金を避けるため送信自体を
拒否する（実際に上限に達した実績は無いため、値は公称値ベースで要継続確認）。

## 動作環境

- .NET 8 以降
- Discord Bot（Message Content Intent 有効）
- LM Studio（デスクトップPC側でサーバーモード起動。モデルの自動ロード/切替に対応 — 詳細は「注意事項」参照）
- Google Cloudプロジェクト（Vertex AI API有効化済み、ADC(Application Default Credentials)のJSON認証情報）
- Google AI Studio APIキー（現在は割り当てモデル無し。将来的な復帰用に設定項目のみ残置）
- Tailscale（MCPサーバー機能を使う場合。ホスト・接続元の両方が同一Tailnetに参加している必要あり）

### 依存ライブラリ（NuGet）

- Discord.Net
- Microsoft.EntityFrameworkCore.Sqlite
- SixLabors.ImageSharp
- Tokenizers.HuggingFace
- Google.Apis.Auth
- ModelContextProtocol / ModelContextProtocol.AspNetCore

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
   | `GeminiApiKey` | Google AI StudioのAPIキー（現在は割り当てモデル無し。当面残置） |
   | `ChatAiChannelId` | AI対話用チャンネルのID |
   | `GoogleAdcCredentialPath` | Vertex AI認証用ADC JSONファイルへのパス（例: `secrets/google-adc.json`。**git管理対象外・絶対にコミットしないこと**） |
   | `VertexProjectId` | Vertex AIを呼び出すGoogle CloudプロジェクトID |
   | `VertexRegion` | Vertex AIのリージョン（例: `global`） |
   | `OpenAiApiKey` | OpenAI APIキー（GPT-5.6 Sol/Terra/Luna用。データ共有プログラムを有効化した状態での利用を前提とする） |
   | `McpListenUrl` | MCPサーバー(Kestrel)の待受URL。**Tailscale IPを明示的に指定すること**（例: `http://100.x.x.x:5100`）。`0.0.0.0`を指定するとLAN内からもアクセス可能になるため使用しないこと |

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

## MCPサーバー機能

Discord bot機能と同一プロセス内で、[MCP (Model Context Protocol)](https://modelcontextprotocol.io/) の
Streamable HTTPサーバーを提供します（`ModelContextProtocol.AspNetCore`公式SDK使用）。同じTailnet
（Tailscale VPN）内の他端末で動くClaude Codeなどから、単発の質問・軽いコード生成をこのシステム経由の
AIモデルに直接投げられます。

- エンドポイント: `<McpListenUrl>/mcp`（例: `http://100.x.x.x:5100/mcp`）
- ヘルスチェック: `<McpListenUrl>/health`
- 前提: api-relay(このシステムを動かすホスト)・接続元の両方が同じTailnetに参加していること
- 接続する側のAIエージェント(Claude Code等)向けの使い方は[MCP_AGENT_GUIDE.md](./MCP_AGENT_GUIDE.md)を参照

### 接続方法（Claude Code側）

```bash
claude mcp add --transport http discord-ai-hub http://<TailscaleのIP>:5100/mcp
```

### 提供ツール

| ツール | 内容 |
|---|---|
| `ask(prompt, model?, session_id?, effort?, temperature?)` | 質問・軽いコード生成をAIモデルに投げ、応答テキストを返す。`session_id`省略時は毎回独立したリクエスト（履歴なし）、指定時はプロセスメモリ上の短期履歴（TTL20分）で会話を継続。`model`省略時はローカルの既定モデル、`/model`で選択可能なIDを指定すればクラウドモデルも使用可能。`effort`（思考の深さ）・`temperature`も指定可能。詳細な引数は[MCP_AGENT_GUIDE.md](./MCP_AGENT_GUIDE.md)を参照 |
| `compare(prompt, models, effort?, temperature?)` | 同じプロンプトを複数モデル（`models`は配列）に順番に投げ、それぞれの応答をJSON配列で比較する。1モデルの失敗（無効なID・無料枠切れ・コンテキスト超過・呼び出しエラー）は他モデルの結果に影響しない（該当モデルの`error`フィールドに理由が入るのみ）。`session_id`には非対応（毎回独立したリクエスト）。data sharing前提のOpenAIモデルを含めるかどうかは`ask`と同様に呼び出し側の判断に委ねる（ツール側では除外しない） |

MCP経由でクラウドモデル（Vertex AI / OpenAI）を使用した場合も、Discord経由と同じ`UsageRecords`
テーブル・同じコスト計算式・同じOpenAI日次無料枠の事前ブロックロジックを共有します（実装の二重化なし）。

モデルが正常に書き終えなかった場合（出力上限到達・フィルタ等）、`ask`は本文末尾に`[⚠ ...]`の注記
（`finish_reason`・トークン内訳）を付け、`compare`は該当モデルの`warning`・`finishReason`フィールドで
知らせます。本文が空の応答は、`ask`ではエラー、`compare`では該当モデルの`error`になります。
OpenAIモデルの出力上限（`max_completion_tokens`）は推論トークン込みで32,000です。

### 提供リソース

| リソース | URI | 内容 |
|---|---|---|
| Available Models | `models://registry` | `ask`の`model`引数に指定できる全モデルの一覧をJSONで返す。`ModelRegistry.cs`から動的に生成されるため、モデル追加時もこのリソースが自動的に最新化される。コンテキスト長・画像対応可否・思考深さの対応段階に加え、コスト特性（`free` / `paid_shared_pool` / `free_within_daily_quota`）とデータ共有プログラムの有無（`dataSharingProgram`）を含む |

呼び出し中は、MCPプロトコルの[Progress notifications](https://modelcontextprotocol.io/specification/2025-06-18/basic/utilities/progress)
（`notifications/progress`）を約3秒間隔で送信します。クライアントが`tools/call`のリクエストに
`progressToken`を含めた場合のみ実際に送信され（含めない場合は何も送られず、通常のツール呼び出しと
変わらない）、多くのMCPクライアントはこれをタイムアウト延長のシグナルとして扱います。ローカルモデル
（特に`effort: High`以上）は数十秒〜数分かかることがあり、この仕組みでクライアント側タイムアウトを
回避します。実機検証では、ローカルQwen3.8-27B・`effort: High`で166秒かかった呼び出しでも、
約3秒おきの進捗通知（経過秒数・受信済み文字数を含む）を挟みつつ最終応答まで正常完了することを確認済み。

### セキュリティ

- Kestrelは`McpListenUrl`で指定したTailscale IPにのみバインド（`0.0.0.0`は使用しない。LAN内からのアクセスを防ぐため）
- Tailscale自体がインストール時に自動設定するiptables統合（`ts-input`チェーン）により、Tailscale
  インターフェース以外から届くTailscale CGNATレンジ(`100.64.0.0/10`)宛のパケットはポートを問わず
  ドロップされる（`iptables -L ts-input -n -v`で確認可能）。バインドアドレス制限と合わせた多層防御
- アプリケーションレベルの認証(トークン等)は現状未実装。Tailnet内は信頼済みの端末のみという前提
- 呼び出し元の可視性確保のため、`ask`ツールの呼び出しごとに呼び出し元IP(Tailscale IP)・
  `session_id`・モデル・所要時間等を構造化ログとして記録(`journalctl -u discord-ai-hub`で
  確認可能、`caller=100.x.x.x session=... model=...`の形式)。認証機構ではなく監査・
  トラブルシューティング目的の可視化であり、アクセス制御自体は上記のネットワークレベルの
  多層防御に引き続き依存する

## ファイル構成

| ファイル | 役割 |
|---|---|
| `Program.cs` | エントリポイント。Discordイベント処理、モデル選択UI、エラーハンドリング |
| `ChatOrchestrator.cs` | 会話処理の中核。履歴管理・添付処理・プロバイダー呼び出しの統括 |
| `IAiProvider.cs` | AIプロバイダーの共通インターフェース |
| `LmStudioProvider.cs` | LM Studio（OpenAI互換API）との通信・SSEパース |
| `GoogleAiStudioProvider.cs` | Gemini API（Google AI Studio / APIキー認証）との通信。現在は割り当てモデル無し |
| `GoogleAdcTokenProvider.cs` | ADC(Application Default Credentials)からVertex AI用アクセストークンを取得（ライブラリ側キャッシュ・自動リフレッシュに依存） |
| `VertexGeminiProvider.cs` | Vertex AIネイティブエンドポイント経由のGemini通信。ロール交互制約等はGoogleAiStudioProviderと同一ロジック |
| `VertexGrokProvider.cs` | Vertex AIのOpenAI互換エンドポイント経由のGrok通信 |
| `OpenAiProvider.cs` | OpenAI Chat Completions APIとの直接通信（GPT-5.6 Sol/Terra/Luna） |
| `OpenAiQuota.cs` | OpenAIデータ共有プログラムの日次無料枠プール定義（大型枠/軽量枠） |
| `AskTool.cs` | MCPサーバー機能の`ask`ツール。Discord側のオーケストレーションを経由せずIAiProviderを直接呼ぶステートレスな単発呼び出し |
| `CompareModelsTool.cs` | MCPサーバー機能の`compare`ツール。同じプロンプトを複数モデルに順番に投げ、部分失敗を許容しつつ結果をJSON配列で返す |
| `McpModelCall.cs` | MCPツール（`ask`/`compare`）共通の1モデル呼び出し処理。ストリーム受信・終了理由（finish_reason）の収集と判定・進捗通知・コスト記録をまとめる |
| `ModelRegistryResource.cs` | MCPサーバー機能の`models://registry`リソース。`ModelRegistry.cs`からモデル一覧・コスト特性を動的に生成しJSONで公開 |
| `StreamResponseHandler.cs` | ストリーミング表示。Discordメッセージの分割・更新制御 |
| `AttachmentProcessor.cs` | 添付ファイル・画像の処理。メモリ保護機構を内包 |
| `TokenManager.cs` | トークナイザーによるトークン数カウント（シングルトン） |
| `ModelRegistry.cs` | 利用可能モデルの定義とメタデータ |
| `ChatDbContext.cs` | SQLiteによる会話履歴の永続化（EF Core） |
| `AiModels.cs` | リクエスト/レスポンスの共通データ型 |

## 注意事項

- 本システムは個人利用を想定しています。`appsettings.json`（トークン・APIキーを含む）、`secrets/google-adc.json`（Vertex AI認証情報）は絶対にコミットしないでください。
- LM Studioはモデルの事前ロードが不要です。未ロードのモデルIDへリクエストすると自動的にロードされ、別モデルへの切替時も自動でアンロード→ロードが行われます（実測: 軽量モデルで約9秒、最重量のQwen3.8-27Bで約20秒程度。既存のHTTPタイムアウト設定（10分）内に収まることを確認済み）。ただし切替直後の初回応答はこの分だけ遅延します。
- 同時アクセス制御は未実装です（単一ユーザー運用前提）。複数ユーザーが同時に別スレッドから異なるローカルモデルをリクエストした場合、LM Studio側のモデル切替が競合する可能性があるため、複数人での本格利用前に対応要否を確認してください。
- `chat_history.db` には会話内容がそのまま保存されます。

## ライセンス

大学演習の成果物として作成。
