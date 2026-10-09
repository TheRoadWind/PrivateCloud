using System.Net.WebSockets;

namespace RelayCore;

/// <summary>
/// 公网中转门面：在宿主进程内启动 TCP 中转核心，并把已升级的 WebSocket 连接接入。
///
/// 典型接入方式（PrivateCloud）：HTTP 监听里判断到 <c>Upgrade: websocket</c> 时，
/// 调用 <c>context.AcceptWebSocketAsync</c> 拿到 WebSocket，再交给
/// <see cref="HandleWebSocketAsync"/>，就能复用同一条 ngrok 隧道提供公网对战。
/// </summary>
public sealed class RelayHost : IDisposable
{
    private RelayServer? _server;

    /// <summary>断线后房间保留时长（秒）。</summary>
    public int GraceSeconds { get; set; } = 120;

    /// <summary>中转核心实际监听的端口（仅本机使用）。</summary>
    public int Port { get; private set; }

    public bool IsRunning => _server is not null;

    /// <summary>当前活动房间数。</summary>
    public int RoomCount => _server?.RoomCount ?? 0;

    /// <summary>启动中转核心；端口被占用等失败时返回 false。</summary>
    public bool Start(int port)
    {
        if (_server is not null)
        {
            return true;
        }

        try
        {
            var server = new RelayServer(port, GraceSeconds);
            server.Start();
            _server = server;
            Port = port;
            RelayLog.Write($"中转核心已启动（TCP {port}，断线保留 {GraceSeconds} 秒）");
            return true;
        }
        catch (Exception ex)
        {
            RelayLog.Write("中转核心启动失败：" + ex.Message);
            _server = null;
            return false;
        }
    }

    public void Stop()
    {
        var server = _server;
        _server = null;
        if (server is null)
        {
            return;
        }

        try
        {
            server.Stop();
        }
        catch
        {
            // ignore
        }

        RelayLog.Write("中转核心已停止");
    }

    /// <summary>处理一条已升级的 WebSocket 连接（阻塞到该连接结束）。</summary>
    public async Task HandleWebSocketAsync(WebSocket socket, CancellationToken ct)
    {
        var server = _server;
        if (server is null)
        {
            RelayLog.Write("中转：收到连接，但中转核心未运行，直接关闭");
            try
            {
                await socket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "relay off", ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }

            return;
        }

        await WebSocketBridge.RunAsync(socket, "127.0.0.1", server.Port, ct).ConfigureAwait(false);
    }

    public void Dispose() => Stop();
}
