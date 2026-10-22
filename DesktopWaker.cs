using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordAIBot
{
    // ローカルモデルの要求時にデスクトップPC(llama-swap)が落ちていれば、Wake on LANで起こして
    // 応答するまで待つ(083)。api-relayはデスクトップと同じ192.168.0.0/24にいるため、HAを通さず
    // ハブから直接マジックパケットを送る(HAのトークンをハブに置かないため)。
    // Discord・MCP・/v1のすべてがLmStudioProvider経由でここを通る
    public sealed class DesktopWaker
    {
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
        // 1回目で起きなかった場合に備えて、待っている間も定期的に送り直す
        private static readonly TimeSpan ResendInterval = TimeSpan.FromSeconds(30);

        private readonly HttpClient _httpClient;
        private readonly string _modelsUrl;
        private readonly byte[]? _magicPacket;
        private readonly IPEndPoint _broadcastEndPoint;
        private readonly TimeSpan _wakeTimeout;

        // 同時に複数の要求が来ても、起動待ちは1つにまとめる
        private readonly object _wakeLock = new();
        private Task<bool>? _wakeTask;

        public DesktopWaker(HttpClient httpClient, string chatCompletionsUrl, string? macAddress, string? broadcastAddress, int wakeTimeoutSeconds)
        {
            _httpClient = httpClient;
            _modelsUrl = chatCompletionsUrl.Replace("/chat/completions", "/models");
            _magicPacket = string.IsNullOrWhiteSpace(macAddress) ? null : BuildMagicPacket(macAddress);
            _broadcastEndPoint = new IPEndPoint(IPAddress.Parse(string.IsNullOrWhiteSpace(broadcastAddress) ? "255.255.255.255" : broadcastAddress), 9);
            _wakeTimeout = TimeSpan.FromSeconds(wakeTimeoutSeconds > 0 ? wakeTimeoutSeconds : 180);
        }

        // MACアドレスが設定されていなければ起動はせず、従来どおり死活確認だけ行う
        public bool Enabled => _magicPacket != null;

        public TimeSpan WakeTimeout => _wakeTimeout;

        public async Task<bool> IsOnlineAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                using var response = await _httpClient.GetAsync(_modelsUrl, cts.Token);
                return response.IsSuccessStatusCode;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                return false;
            }
        }

        // 起動していればすぐtrue。落ちていればWoLを送り、応答するかWakeTimeoutを過ぎるまで待つ。
        // 起動待ちは呼び出し元の中断とは切り離して続ける(他の要求が同じ起動を待っているため)
        public async Task<bool> EnsureOnlineAsync(CancellationToken cancellationToken)
        {
            if (await IsOnlineAsync(cancellationToken)) return true;
            if (!Enabled) return false;

            Task<bool> wakeTask;
            lock (_wakeLock)
            {
                if (_wakeTask is null || _wakeTask.IsCompleted)
                {
                    _wakeTask = Task.Run(WakeAndWaitAsync);
                }
                wakeTask = _wakeTask;
            }
            return await wakeTask.WaitAsync(cancellationToken);
        }

        private async Task<bool> WakeAndWaitAsync()
        {
            var stopwatch = Stopwatch.StartNew();
            var nextSendAt = TimeSpan.Zero;
            Console.WriteLine($"[DesktopWaker] デスクトップPCが応答しないため、Wake on LANを送って起動を待ちます(最大{_wakeTimeout.TotalSeconds:F0}秒)");

            while (stopwatch.Elapsed < _wakeTimeout)
            {
                if (stopwatch.Elapsed >= nextSendAt)
                {
                    await SendMagicPacketAsync();
                    nextSendAt += ResendInterval;
                }

                await Task.Delay(PollInterval);

                if (await IsOnlineAsync())
                {
                    Console.WriteLine($"[DesktopWaker] デスクトップPCが応答しました({stopwatch.Elapsed.TotalSeconds:F0}秒)");
                    return true;
                }
            }

            Console.WriteLine($"[Warning] [DesktopWaker] {_wakeTimeout.TotalSeconds:F0}秒待ってもデスクトップPCが応答しませんでした");
            return false;
        }

        private async Task SendMagicPacketAsync()
        {
            try
            {
                using var udp = new UdpClient { EnableBroadcast = true };
                // UDPは届いた保証がないので、1回につき3つ送る
                for (int i = 0; i < 3; i++)
                {
                    await udp.SendAsync(_magicPacket!, _magicPacket!.Length, _broadcastEndPoint);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Warning] [DesktopWaker] Wake on LANの送信に失敗しました: {ex.Message}");
            }
        }

        // FFを6バイト、続けてMACアドレスを16回
        private static byte[] BuildMagicPacket(string macAddress)
        {
            var mac = macAddress.Split(':', '-')
                .Select(part => byte.Parse(part, NumberStyles.HexNumber, CultureInfo.InvariantCulture))
                .ToArray();
            if (mac.Length != 6)
            {
                throw new FormatException($"MACアドレスの形式が不正です: {macAddress}");
            }

            var packet = new byte[6 + 16 * 6];
            for (int i = 0; i < 6; i++) packet[i] = 0xFF;
            for (int i = 0; i < 16; i++) Array.Copy(mac, 0, packet, 6 + i * 6, 6);
            return packet;
        }
    }
}
