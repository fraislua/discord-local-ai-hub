using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace DiscordAIBot
{
    // MCPツールの引数の検証・正規化。SDKの引数バインドで失敗すると、呼び出し元には
    // "An error occurred invoking 'ask'." としか返らず、どの引数が悪いのか分からなかった。
    // また省略可能な引数に文字列"null"を渡すと、そのまま"null"という名前のセッション等として
    // 扱われていた(provisioning/057)
    public static class McpArguments
    {
        // 文字列引数の"null"/"undefined"/空文字(前後の空白を除いて判定)は「省略」とみなす
        public static string? NormalizeOptional(string? value) =>
            IsNullLike(value) ? null : value;

        // ツール呼び出しの前段で引数を検証するフィルター(Program.csでWithRequestFiltersに登録する)。
        // 引数のバインドはツールのメソッドに入る前に行われ、失敗すると詳細の無いエラーになるため、
        // その手前で入力スキーマに照らして検証し、不正ならツールを呼ばずに理由を返す
        public static McpRequestHandler<CallToolRequestParams, CallToolResult> ValidationFilter(
            McpRequestHandler<CallToolRequestParams, CallToolResult> next) =>
            async (context, cancellationToken) =>
            {
                McpServerTool? tool = context.MatchedPrimitive as McpServerTool;
                if (tool is null && context.Params?.Name is { } toolName)
                {
                    context.Server.ServerOptions.ToolCollection?.TryGetPrimitive(toolName, out tool);
                }

                if (tool is not null)
                {
                    string? error = Validate(tool.ProtocolTool.InputSchema, context.Params?.Arguments);
                    if (error is not null)
                    {
                        context.Services?.GetService<ILoggerFactory>()?.CreateLogger(nameof(McpArguments))
                            .LogInformation("MCP 引数エラー: tool={Tool} {Error}", tool.ProtocolTool.Name, error);

                        return new CallToolResult
                        {
                            IsError = true,
                            Content = [new TextContentBlock { Text = error }]
                        };
                    }
                }

                return await next(context, cancellationToken);
            };

        // ツールの入力スキーマ(JSON Schema)に照らして引数を検証し、問題があれば呼び出し元向けの
        // メッセージを返す(問題が無ければnull)。見るのは必須引数の欠落・未知の引数名・型の不一致
        // (配列の要素の型を含む)のみ
        public static string? Validate(JsonElement inputSchema, IEnumerable<KeyValuePair<string, JsonElement>>? arguments)
        {
            if (inputSchema.ValueKind != JsonValueKind.Object) return null;

            var args = arguments?.ToDictionary(kv => kv.Key, kv => kv.Value) ?? new Dictionary<string, JsonElement>();
            var errors = new List<string>();

            if (inputSchema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array)
            {
                foreach (var name in required.EnumerateArray().Select(r => r.GetString()).OfType<string>())
                {
                    if (!args.ContainsKey(name))
                    {
                        errors.Add($"必須の引数 '{name}' が指定されていません。");
                    }
                }
            }

            if (inputSchema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object)
            {
                foreach (var (name, value) in args)
                {
                    if (!properties.TryGetProperty(name, out var propertySchema))
                    {
                        string known = string.Join(", ", properties.EnumerateObject().Select(p => p.Name));
                        errors.Add($"未知の引数 '{name}' です(指定できる引数: {known})。");
                        continue;
                    }

                    string? typeError = CheckType(name, propertySchema, value);
                    if (typeError is not null)
                    {
                        errors.Add(typeError);
                    }
                }
            }

            return errors.Count == 0 ? null : "引数が不正です。" + string.Join("", errors);
        }

        private static string? CheckType(string name, JsonElement schema, JsonElement value)
        {
            var allowed = AllowedTypes(schema);
            if (allowed.Count == 0) return null; // 型の指定が無い引数は検証しない

            if (!Matches(allowed, value))
            {
                string message = $"引数 '{name}' は {string.Join(" または ", allowed)} で指定してください(受け取った値: {Describe(value)})。";
                if (value.ValueKind == JsonValueKind.String && allowed.Contains("null") && IsNullLike(value.GetString()))
                {
                    message += "指定しない場合は、引数ごと省略するか、文字列ではなくJSONのnullを渡してください。";
                }
                return message;
            }

            if (value.ValueKind == JsonValueKind.Array && schema.TryGetProperty("items", out var items))
            {
                int index = 0;
                foreach (var element in value.EnumerateArray())
                {
                    string? itemError = CheckType($"{name}[{index}]", items, element);
                    if (itemError is not null) return itemError;
                    index++;
                }
            }

            return null;
        }

        private static List<string> AllowedTypes(JsonElement schema)
        {
            if (schema.ValueKind != JsonValueKind.Object || !schema.TryGetProperty("type", out var type))
            {
                return new List<string>();
            }

            return type.ValueKind switch
            {
                JsonValueKind.String => new List<string> { type.GetString()! },
                JsonValueKind.Array => type.EnumerateArray().Select(t => t.GetString()).OfType<string>().ToList(),
                _ => new List<string>()
            };
        }

        private static bool Matches(List<string> allowed, JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => allowed.Contains("string"),
            JsonValueKind.Number => allowed.Contains("number") || (allowed.Contains("integer") && value.TryGetInt64(out _)),
            JsonValueKind.True or JsonValueKind.False => allowed.Contains("boolean"),
            JsonValueKind.Null => allowed.Contains("null"),
            JsonValueKind.Array => allowed.Contains("array"),
            JsonValueKind.Object => allowed.Contains("object"),
            _ => false
        };

        private static string Describe(JsonElement value) => value.ValueKind switch
        {
            JsonValueKind.String => $"文字列 {value.GetRawText()}",
            JsonValueKind.Number => $"数値 {value.GetRawText()}",
            JsonValueKind.True or JsonValueKind.False => $"真偽値 {value.GetRawText()}",
            JsonValueKind.Null => "null",
            JsonValueKind.Array => "配列",
            JsonValueKind.Object => "オブジェクト",
            _ => value.ValueKind.ToString()
        };

        private static bool IsNullLike(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return true;
            string trimmed = value.Trim();
            return trimmed.Equals("null", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("undefined", StringComparison.OrdinalIgnoreCase);
        }
    }
}
