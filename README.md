# Discord統合パーソナルAI推論システム

Discordをフロントエンドとして、自宅のローカルLLM（llama.cpp / llama-swap）とクラウドAI（Vertex AI経由のGemini・Grok、OpenAI）を切り替えて利用できる自分専用のAIチャットシステムです。同一プロセス内で、Tailscale経由のMCPサーバー機能（Claude Code等からの直接呼び出し）も提供します。

24時間稼働のミニPC（Proxmox LXC: 2コア / RAM 1GB）上でBotを常時稼働させ、重い推論処理はデスクトップPC上のllama.cpp（llama-swap経由）、またはクラウドAPIへルーティングする3層構成になっています。

```
[Discord]     ⇄ [ミニPC: Discord Bot (中継サーバー)] ⇄ [デスクトップPC: llama-swap + llama.cpp]
[Claude Code] ⇄ [       ↑ 同一プロセス、MCPサーバー]  ⇄ [Vertex AI (Gemini / Grok) / OpenAI]
```

## 主な機能

- **ストリーミング表示** — 商用AIアプリのように、生成テキストがリアルタイムに流れて表示されます（Discordの文字数上限・更新頻度の制約に対応）
- **マルチプロバイダー対応** — `/model` コマンドでローカルLLM・Vertex AI経由のGemini/Grok・OpenAIをその場で切り替え可能（Factoryパターンによる動的ルーティング）
- **会話コンテキストの保持** — スレッド単位で会話履歴をSQLiteに保存し、モデルを切り替えても文脈を維持
- **添付ファイル解析** — テキスト / ソースコード（.cs, .py など）/ 画像（VLM対応モデルのみ）の読み込みに対応
- **思考プロセスの表示** — reasoning対応モデルの `<think>` 内容を折りたたみ表示（ローカルのreasoningモデル向け。Vertex AI経由のGemini / Grokは内部思考をテキストとして返さないため対象外）
- **省リソース環境向けの防御的実装**
  - 画像のDecompression Bomb対策（Content-Lengthチェック → ダウンロード中のサイズ監視 → ピクセル総数制限の3段防御）
  - トークナイザーによる厳密なコンテキスト管理と履歴の自動トリミング
  - Gemini APIのロール交互制約への自動対応（履歴マージ・ダミー挿入。Google AI Studio / Vertex AI共通）
  - 429 Rate Limit の型安全なハンドリング（カスタム例外）

## 対応モデル

`/model` コマンドで以下13モデルを切り替えられます（`ModelRegistry.cs`で定義）。

| モデル | 実行先 | 画像対応 |
|---|---|---|
| Ternary-Bonsai-2-27B (`ternary-bonsai-2-27b`)、既定 | ローカル (llama.cpp、PrismML版) | ○ |
| Gemma-4-12B-VLM (`google/gemma-4-12b-qat`) | ローカル (llama.cpp) | ○ |
| Gemma-4-E4B-Uncensored (`gemma-4-e4b-uncensored-hauhaucs-aggressive`) | ローカル (llama.cpp) | ○ |
| Qwen3.8-27B (`qwen/qwen3.8-27b`) | ローカル (llama.cpp) | ○ |
| Qwen3.8-27B-56K (`qwen3.8-27b-56k`) | ローカル (llama.cpp) | ○ |
| Ornith-1.5-35B-A3B (`ornith-1.5-35b-a3b`) | ローカル (llama.cpp) | ○ |
| Ornith-1.5-9B (`ornith-1.5-9b`) | ローカル (llama.cpp) | ○ |
| Gemini 3.8 Flash (`gemini-3.8-flash`) | Vertex AI | ○ |
| Gemini 4 Argon (`gemini-4-argon`) | Vertex AI | ○ |
| Grok 4.7 (`xai/grok-4.7`) | Vertex AI (プレビュー) | × (VertexGrokProviderが画像を送らないため無効) |
| GPT-6.1 Sol (`gpt-6.1-sol`) | OpenAI API | ○ |
| GPT-6 Astra (`gpt-6-astra`) | OpenAI API | ○ |
| GPT-5.6 Luna (`gpt-5.6-luna`) | OpenAI API | ○ |

OpenAI系はOpenAIのデータ共有プログラム(無料枠)を前提とした運用（`OpenAiQuota.cs`）。
6.1 Sol+Astraは合算で25万トークン/日(大型枠)、Lunaは250万トークン/日(軽量枠)の日次上限があり、
`Program.cs`側で送信前に事前チェックし、上限に達する場合は課金を避けるため送信自体を
拒否する（実際に上限に達した実績は無いため、値は公称値ベースで要継続確認）。

## 動作環境

- .NET 8 以降
- Discord Bot（Message Content Intent 有効）
- llama-swap + llama.cpp（デスクトップPC側。OpenAI互換の`/v1`で、モデルの自動ロード/切替に対応 — 詳細は「注意事項」参照。LM Studioのサーバーモードでも動く）
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
   | `LmStudioEndpoint` | ローカルLLM（llama-swap、またはLM Studio）のOpenAI互換エンドポイント（例: `http://<デスクトップPCのIP>:8080/v1/chat/completions`）。設定名はLM Studio時代のまま |
   | `GeminiApiKey` | Google AI StudioのAPIキー（現在は割り当てモデル無し。当面残置） |
   | `ChatAiChannelId` | AI対話用チャンネルのID |
   | `GoogleAdcCredentialPath` | Vertex AI認証用ADC JSONファイルへのパス（例: `secrets/google-adc.json`。**git管理対象外・絶対にコミットしないこと**） |
   | `VertexProjectId` | Vertex AIを呼び出すGoogle CloudプロジェクトID |
   | `VertexRegion` | Vertex AIのリージョン（例: `global`） |
   | `OpenAiApiKey` | OpenAI APIキー（GPT-6.1 Sol / GPT-6 Astra / GPT-5.6 Luna用。データ共有プログラムを有効化した状態での利用を前提とする） |
   | `OpenAiGatewayEndpoint` | （任意）OpenAIをゲートウェイ（Bifrost）経由にする場合の送り先（例: `http://127.0.0.1:8070/v1/chat/completions`）。空ならOpenAIへ直接送る。ゲートウェイ側で他の利用と合算した日次無料枠の上限を守る |
   | `OpenAiGatewayKey` | （`OpenAiGatewayEndpoint`を設定した場合は必須）ゲートウェイの仮想キー。このとき`OpenAiApiKey`は使われない |
   | `OpenAiGatewayModels` | （任意）モデルIDからゲートウェイのモデル名への対応表。無いモデルは`openai/<モデルID>`として送る |
   | `McpListenUrl` | MCPサーバー(Kestrel)の待受URL。**Tailscale IPを明示的に指定すること**（例: `http://100.x.x.x:5100`）。`0.0.0.0`を指定するとLAN内からもアクセス可能になるため使用しないこと |
   | `DesktopMacAddress` | （任意）デスクトップPCのMACアドレス（例: `9C:6B:00:7C:58:E0`）。設定するとローカルモデルの要求時にデスクトップPCが応答しなければWake on LANで起こして待つ。未設定なら起こさず失敗を返す |
   | `DesktopWolBroadcast` | （任意）Wake on LANの送り先のブロードキャストアドレス（例: `192.168.0.255`。既定は`255.255.255.255`）。ミニPCとデスクトップPCが同じLANにいる必要がある |
   | `DesktopWakeTimeoutSeconds` | （任意）Wake on LANを送ってから応答を待つ最大秒数（既定180） |
   | `WebUiApiKey` | （任意）OpenAI互換窓口（`/v1`）のAPIキー。Open WebUI等のクライアントに`Authorization: Bearer`で送らせる。未設定なら`/v1`は無効 |

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
事前ブロックは「当日の使用量＋今回のプロンプトの見積もり（`TokenManager`の推定値×1.5＋固定分、画像は1枚2,000）
＋出力上限」で判定し、Discord側も履歴・添付を組み立てた後に同じ基準で再判定します。途中で中断された呼び出し
（MCPクライアント側の中断・Discordの停止ボタン）は使用量が届かないため、出力上限まで使った前提の推定値を
`Source`が`mcp-cancelled`/`discord-cancelled`のレコードとして記録します。

モデルが正常に書き終えなかった場合（出力上限到達・フィルタ等）、`ask`は本文末尾に`[⚠ ...]`の注記
（`finish_reason`・トークン内訳）を付け、`compare`は該当モデルの`warning`・`finishReason`フィールドで
知らせます。本文が空の応答は、`ask`ではエラー、`compare`では該当モデルの`error`になります。
OpenAIモデルの出力上限（`max_completion_tokens`）は推論トークン込みで32,000です。

ツールの引数は、呼び出し前に入力スキーマに照らして検証します（`McpArguments.cs`）。必須引数の欠落・未知の
引数名・型の不一致は、どの引数がなぜ不正かを示すエラーになります。省略可能な引数は省略でき、文字列の`"null"`・
`"undefined"`・空文字は省略として扱います。

### 提供リソース

| リソース | URI | 内容 |
|---|---|---|
| Available Models | `models://registry` | `ask`の`model`引数に指定できる全モデルの一覧をJSONで返す。`ModelRegistry.cs`から動的に生成されるため、モデル追加時もこのリソースが自動的に最新化される。コンテキスト長・画像対応可否・思考深さの対応段階に加え、コスト特性（`free` / `paid_shared_pool` / `free_within_daily_quota`）とデータ共有プログラムの有無（`dataSharingProgram`）を含む |

呼び出し中は、MCPプロトコルの[Progress notifications](https://modelcontextprotocol.io/specification/2025-06-18/basic/utilities/progress)
（`notifications/progress`）を約3秒間隔で送信します。クライアントが`tools/call`のリクエストに
`progressToken`を含めた場合のみ実際に送信され（含めない場合は何も送られず、通常のツール呼び出しと
変わらない）、多くのMCPクライアントはこれをタイムアウト延長のシグナルとして扱います。ローカルモデル
（特に`effort: High`以上）は数十秒〜数分かかることがあり、この仕組みでクライアント側タイムアウトを
回避します。送信はモデルからのデータ到着とは無関係なタイマーで行うため（`McpModelCall.cs`）、最初の
トークン待ち・推論中・`compare`の各モデルの生成中も途切れません。実機検証では、ローカルQwen3.8-27B・`effort: High`で166秒かかった呼び出しでも、
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

## OpenAI互換窓口（Web UI用）

Open WebUI等のOpenAI互換クライアントから、Discordと同じモデル群を使うための窓口。MCPと同じKestrel
（`McpListenUrl`、Tailscale IP限定）で、`WebUiApiKey`を設定した場合のみ有効になる（`OpenAiCompatEndpoints.cs`）。

- `GET <McpListenUrl>/v1/models` / `POST <McpListenUrl>/v1/chat/completions`（`Authorization: Bearer <WebUiApiKey>`必須）
- クライアントから各APIやローカルLLMへ直接つながせず、MCPと同じ`McpModelCall`経由で呼ぶ。無料枠の事前ブロック・
  コスト記録（`UsageRecords`の`Source=webui`、途中切断は`webui-cancelled`）・ローカルLLM要求の直列化をDiscord/MCPと共有する
- ステートレス: 会話履歴はクライアント側が持ち、毎回`messages`に会話全体が届く前提。system/developerはシステム
  プロンプト、最後のuserが今回の入力、それより前が履歴。コンテキスト上限を超える分は古い履歴から削る
- `stream`の有無両対応。ストリームではローカルのreasoningモデルの思考を`reasoning_content`として中継し、
  思考中に本文を流さないモデル（Vertex経由のGemini等）のために15秒ごとにSSEのコメント行を送って接続を保つ
- `reasoning_effort`（none/minimal/low/medium/high/xhigh）をeffortに対応させる。未指定はモデルごとの既定(Medium。Qwen3.8・BonsaiはLow)。`temperature`未指定はクラウド0.7、ローカルはllama-swap側の推奨値
- 画像は今回の入力のみ、data URI（base64）のみ受け付ける（外部URLは取りに行かない）。Discordの添付と同じ
  サイズ・解像度制限と長辺1024pxへの縮小を通す。履歴中の画像は`[画像]`という文字に置き換える
- 出力上限で切れた等の場合はMCPの`ask`と同じ注記を本文末尾に付ける。ツール呼び出し（function calling）は非対応

## ファイル構成

| ファイル | 役割 |
|---|---|
| `Program.cs` | エントリポイント。Discordイベント処理、モデル選択UI、エラーハンドリング |
| `ChatOrchestrator.cs` | 会話処理の中核。履歴管理・添付処理・プロバイダー呼び出しの統括 |
| `IAiProvider.cs` | AIプロバイダーの共通インターフェース |
| `LmStudioProvider.cs` | ローカルLLM（llama-swap / LM Studio のOpenAI互換API）との通信・SSEパース |
| `DesktopWaker.cs` | デスクトップPCの死活確認とWake on LANによる自動起動 |
| `GoogleAiStudioProvider.cs` | Gemini API（Google AI Studio / APIキー認証）との通信。現在は割り当てモデル無し |
| `GoogleAdcTokenProvider.cs` | ADC(Application Default Credentials)からVertex AI用アクセストークンを取得（ライブラリ側キャッシュ・自動リフレッシュに依存） |
| `VertexGeminiProvider.cs` | Vertex AIネイティブエンドポイント経由のGemini通信。ロール交互制約等はGoogleAiStudioProviderと同一ロジック |
| `VertexGrokProvider.cs` | Vertex AIのOpenAI互換エンドポイント経由のGrok通信 |
| `OpenAiProvider.cs` | OpenAI Chat Completions APIとの直接通信（GPT-6.1 Sol / GPT-6 Astra / GPT-5.6 Luna） |
| `OpenAiQuota.cs` | OpenAIデータ共有プログラムの日次無料枠プール定義（大型枠/軽量枠） |
| `AskTool.cs` | MCPサーバー機能の`ask`ツール。Discord側のオーケストレーションを経由せずIAiProviderを直接呼ぶステートレスな単発呼び出し |
| `CompareModelsTool.cs` | MCPサーバー機能の`compare`ツール。同じプロンプトを複数モデルに順番に投げ、部分失敗を許容しつつ結果をJSON配列で返す |
| `OpenAiCompatEndpoints.cs` | OpenAI互換窓口（`/v1/models`・`/v1/chat/completions`）。Open WebUI等から使う |
| `McpModelCall.cs` | MCPツール（`ask`/`compare`）とOpenAI互換窓口共通の1モデル呼び出し処理。ストリーム受信・終了理由（finish_reason）の収集と判定・進捗通知・コスト記録をまとめる |
| `McpArguments.cs` | MCPツールの引数の検証（入力スキーマに照らした必須・未知・型のチェック）と、文字列の`"null"`等の正規化。ツール呼び出しの前段にフィルターとして組み込む |
| `ModelRegistryResource.cs` | MCPサーバー機能の`models://registry`リソース。`ModelRegistry.cs`からモデル一覧・コスト特性を動的に生成しJSONで公開 |
| `StreamResponseHandler.cs` | ストリーミング表示。Discordメッセージの分割・更新制御 |
| `AttachmentProcessor.cs` | 添付ファイル・画像の処理。メモリ保護機構を内包 |
| `TokenManager.cs` | トークナイザーによるトークン数カウント（シングルトン） |
| `ModelRegistry.cs` | 利用可能モデルの定義とメタデータ |
| `ChatDbContext.cs` | SQLiteによる会話履歴の永続化（EF Core） |
| `AiModels.cs` | リクエスト/レスポンスの共通データ型 |

## 注意事項

- 本システムは個人利用を想定しています。`appsettings.json`（トークン・APIキーを含む）、`secrets/google-adc.json`（Vertex AI認証情報）は絶対にコミットしないでください。
- ローカルLLMはモデルの事前ロードが不要です。デスクトップPCのllama-swapが、未ロードのモデルIDへのリクエストで該当モデルのllama-serverを起動し、別モデルへの切替時は入れ替えます（1度に動くのは1モデル。冷えた状態からの読み込みは実測6〜17秒。既存のHTTPタイムアウト設定（10分）内に収まる）。ただし切替直後の初回応答はこの分だけ遅延します。
- デスクトップPCが落ちているとき（`DesktopMacAddress`設定時）は、ローカルLLMへの要求でWake on LANを送り、llama-swapの`/v1/models`が応答するまで待ってから処理します（`DesktopWaker.cs`。Discord・MCP・`/v1`のすべてが対象。待っている間も30秒ごとに送り直し、同時に来た要求は同じ起動を待つ）。Discordでは「デスクトップPCを起動しています」と表示し、起動後に生成を始めます。
- ローカルLLMへの要求はプロセス全体で直列化されます（`LmStudioProvider`内の`SemaphoreSlim(1,1)`。Discordチャット・MCPの`ask`/`compare`のすべてが対象）。別モデルへの要求が重なった場合、後の要求は先の要求のストリームが完了（または中断）するまで待ってから開始され、1秒以上待ったときはログに`[LmStudio] 他のリクエストの完了を N 秒待ってから開始しました`と出ます。待ち行列の上限やタイムアウトは設けていないため、長い生成が続く間は後続の要求もその分遅れます。クラウドモデルへの要求は直列化の対象外です。
- `chat_history.db` には会話内容がそのまま保存されます。

## ライセンス

大学演習の成果物として作成。
