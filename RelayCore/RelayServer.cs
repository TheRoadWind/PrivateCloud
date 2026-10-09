using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace RelayCore;

/// <summary>
/// TCP 中转核心（等价于 tools\relay.py，并新增「断线保留 / 重连」能力）。
///
/// 两端都主动连进来，服务器按「房间号」配对后互相转发字节流。
/// 协议：第一行是 join 握手 JSON，之后是 GameHub 自己的行式 JSON 消息。
///
///     {"t":"join","room":""}        -> 新建房间，服务器分配 6 位房间号，本端角色 host
///     {"t":"join","room":"739142"}  -> 加入房间，本端角色 guest
///     {"t":"room","room":"739142","role":"host"}
///     {"t":"paired"}
///     {"t":"error","msg":"..."}
///
/// 断线保留：
///   · 一端断开后，房间号与其对端连接会保留 GraceSeconds 秒，等待断开方用同一个房间号重连；
///   · 建房时优先复用「上一个房间号」，这样 ngrok 重连 / 手机重开后，对方无需换房间号；
///   · 建房后 300 秒内无人加入，或重连等待超时，房间才会真正关闭。
/// </summary>
public sealed class RelayServer
{
    /// <summary>建房后等待对手加入的最长时间（秒）。</summary>
    private const int WaitTimeoutSeconds = 300;

    private readonly object _sync = new();
    private readonly Dictionary<string, Room> _rooms = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly int _graceSeconds;
    private TcpListener? _listener;

    /// <summary>上一个用过的房间号，重建时优先复用。</summary>
    private string? _lastCode;

    public int Port { get; }

    /// <summary>当前活动房间数（供宿主显示状态）。</summary>
    public int RoomCount
    {
        get
        {
            lock (_sync)
            {
                return _rooms.Count;
            }
        }
    }

    public RelayServer(int port, int graceSeconds)
    {
        Port = port;
        _graceSeconds = graceSeconds;
    }

    public void Start()
    {
        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start();
        _ = Task.Run(AcceptLoopAsync);
    }

    public void Stop()
    {
        try { _cts.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }

        List<Room> snapshot;
        lock (_sync)
        {
            snapshot = _rooms.Values.ToList();
            _rooms.Clear();
        }

        foreach (var room in snapshot)
        {
            CloseClient(room.Host);
            CloseClient(room.Guest);
        }
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested && _listener is not null)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
            }
            catch
            {
                break;
            }

            client.NoDelay = true;
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            var firstLine = await ReadLineAsync(client.GetStream()).ConfigureAwait(false);
            if (firstLine is null)
            {
                CloseClient(client);
                return;
            }

            var want = ParseRoom(firstLine).Trim();

            Room? room;
            string role;
            var reject = false;
            var rejoin = false;

            lock (_sync)
            {
                if (want.Length == 0)
                {
                    // 建房：优先复用上一个房间号，避免重连后对方要换号
                    var code = _lastCode is not null && !_rooms.ContainsKey(_lastCode)
                        ? _lastCode
                        : NewCodeLocked();
                    room = new Room(code);
                    _rooms[code] = room;
                    room.Host = client;
                    _lastCode = code;
                    role = "host";
                }
                else if (_rooms.TryGetValue(want, out var found))
                {
                    if (found.Host is null)
                    {
                        found.Host = client;
                        role = "host";
                        rejoin = found.Broken;
                    }
                    else if (found.Guest is null)
                    {
                        found.Guest = client;
                        role = "guest";
                        rejoin = found.Broken;
                    }
                    else
                    {
                        reject = true;
                        role = "";
                    }

                    room = found;
                }
                else
                {
                    reject = true;
                    role = "";
                    room = null;
                }
            }

            if (reject || room is null)
            {
                RelayLog.Write($"加入失败：房间 {want} 不存在或已满");
                await SendAsync(client, "{\"t\":\"error\",\"msg\":\"房间不存在或已满\"}").ConfigureAwait(false);
                CloseClient(client);
                return;
            }

            RelayLog.Write($"房间 {room.Code}：{role} 已连接" + (rejoin ? "（重连）" : ""));

            var sent = await SendAsync(client, $"{{\"t\":\"room\",\"room\":\"{room.Code}\",\"role\":\"{role}\"}}")
                .ConfigureAwait(false);
            if (!sent)
            {
                DropRole(room, role);
                CloseClient(client);
                return;
            }

            // 双方齐全（首次配对或断线重连补齐）时，由后到的一方通知双方并放行转发
            TcpClient? peer = null;
            var complete = false;
            lock (_sync)
            {
                complete = room.Complete;
                peer = room.PeerOf(role);
            }

            if (complete && peer is not null)
            {
                await AnnouncePairedAsync(room, client, peer).ConfigureAwait(false);
            }

            await SessionLoopAsync(client, room, role).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RelayLog.Write("连接异常: " + ex.Message);
            CloseClient(client);
        }
    }

    /// <summary>
    /// 单个连接的会话循环：等待配对 -> 转发 -> 断线后进入保留期等待重连 -> 再转发。
    /// </summary>
    private async Task SessionLoopAsync(TcpClient client, Room room, string role)
    {
        var firstRound = true;

        while (true)
        {
            var timeout = firstRound ? WaitTimeoutSeconds : _graceSeconds;
            var ready = await WaitForReadyAsync(room, client, timeout).ConfigureAwait(false);
            if (!ready)
            {
                // 首次：建房后无人加入；之后：断线保留超时
                if (firstRound)
                {
                    await SendAsync(client, "{\"t\":\"error\",\"msg\":\"等待超时，房间已关闭\"}")
                        .ConfigureAwait(false);
                    RelayLog.Write($"房间 {room.Code} 等待超时，已关闭");
                }
                else
                {
                    RelayLog.Write($"房间 {room.Code} 重连超时，已关闭");
                }

                DropRole(room, role);
                CloseRoom(room.Code);
                CloseClient(client);
                return;
            }

            firstRound = false;

            TcpClient? peer;
            lock (_sync)
            {
                peer = room.PeerOf(role);
            }

            if (peer is null)
            {
                DropRole(room, role);
                CloseClient(client);
                return;
            }

            var session = room.Session;
            await PumpAsync(client, peer, session.Token).ConfigureAwait(false);

            // 本次会话结束：让对端的转发也立即退出，双方一起回到「等待重连」状态
            try { session.Cancel(); } catch { }

            if (!IsAlive(client))
            {
                // 本端断开：交给对端决定是否进入保留期
                DropRole(room, role);
                if (!IsAlive(peer))
                {
                    CloseRoom(room.Code);
                }
                else
                {
                    RelayLog.Write($"房间 {room.Code}：{role} 已断开，对端保留 {_graceSeconds} 秒等待重连");
                }

                CloseClient(client);
                return;
            }

            // 本端还活着 => 是对端断开，进入保留期等待重连
            RelayLog.Write($"房间 {room.Code}：{Other(role)} 断开，保留 {_graceSeconds} 秒等待重连");
            lock (_sync)
            {
                room.ClearRole(Other(role));
                room.Ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                room.Broken = true;
            }

            CloseClient(peer);
        }
    }

    /// <summary>等待房间双方齐全；期间每 2 秒检查一次自己是否还活着。</summary>
    private async Task<bool> WaitForReadyAsync(Room room, TcpClient client, int timeout)
    {
        var waited = 0;
        while (waited < timeout)
        {
            Task readyTask;
            lock (_sync)
            {
                readyTask = room.Ready.Task;
            }

            if (readyTask.IsCompleted)
            {
                return true;
            }

            var step = Math.Min(2, timeout - waited);
            var winner = await Task.WhenAny(readyTask, Task.Delay(TimeSpan.FromSeconds(step)))
                .ConfigureAwait(false);
            if (winner == readyTask)
            {
                return true;
            }

            waited += step;
            if (!IsAlive(client))
            {
                return false;
            }
        }

        return false;
    }

    private async Task AnnouncePairedAsync(Room room, TcpClient self, TcpClient peer)
    {
        lock (_sync)
        {
            // 新一轮会话：换一个取消令牌，断开旧会话的残留转发
            room.Session = new CancellationTokenSource();
            room.Broken = false;
        }

        await SendAsync(peer, "{\"t\":\"paired\"}").ConfigureAwait(false);
        await SendAsync(self, "{\"t\":\"paired\"}").ConfigureAwait(false);
        lock (_sync)
        {
            room.Ready.TrySetResult();
        }

        RelayLog.Write($"房间 {room.Code} 配对成功，开始转发");
    }

    private static async Task PumpAsync(TcpClient src, TcpClient dst, CancellationToken token)
    {
        try
        {
            var from = src.GetStream();
            var to = dst.GetStream();
            await from.CopyToAsync(to, token).ConfigureAwait(false);
        }
        catch
        {
            // EOF / 取消 / 对端出错，都算本次转发结束
        }
    }

    private string NewCodeLocked()
    {
        string code;
        do
        {
            code = Random.Shared.Next(100000, 999999).ToString();
        }
        while (_rooms.ContainsKey(code));

        return code;
    }

    private void DropRole(Room room, string role)
    {
        lock (_sync)
        {
            room.ClearRole(role);
            if (room.Host is null && room.Guest is null)
            {
                _rooms.Remove(room.Code);
            }
        }
    }

    /// <summary>关闭并移除房间（连带关闭两端连接）。</summary>
    private void CloseRoom(string code)
    {
        Room? room;
        lock (_sync)
        {
            _rooms.TryGetValue(code, out room);
            _rooms.Remove(code);
        }

        if (room is null)
        {
            return;
        }

        RelayLog.Write($"房间 {code} 已关闭");
        CloseClient(room.Host);
        CloseClient(room.Guest);
    }

    private static string Other(string role) => role == "host" ? "guest" : "host";

    private static bool IsAlive(TcpClient? client)
    {
        if (client is null)
        {
            return false;
        }

        try
        {
            var socket = client.Client;
            if (socket is null || !socket.Connected)
            {
                return false;
            }

            // 可读但没有数据 => 对端已关闭
            return !(socket.Poll(0, SelectMode.SelectRead) && socket.Available == 0);
        }
        catch
        {
            return false;
        }
    }

    private static string ParseRoom(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("room", out var value))
            {
                return value.GetString() ?? "";
            }
        }
        catch
        {
            // 非 JSON 也当作空房间号处理（建房）
        }

        return "";
    }

    private static async Task<bool> SendAsync(TcpClient client, string json)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            await client.GetStream().WriteAsync(bytes).ConfigureAwait(false);
            await client.GetStream().FlushAsync().ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读一行（直到 '\n'），仅用于握手，故逐字节读取即可。</summary>
    private static async Task<string?> ReadLineAsync(NetworkStream stream)
    {
        var buffer = new List<byte>(256);
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one).ConfigureAwait(false);
            if (read <= 0)
            {
                return buffer.Count == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
            }

            if (one[0] == (byte)'\n')
            {
                return Encoding.UTF8.GetString(buffer.ToArray());
            }

            if (one[0] != (byte)'\r')
            {
                buffer.Add(one[0]);
            }
        }
    }

    private static void CloseClient(TcpClient? client)
    {
        if (client is null)
        {
            return;
        }

        try
        {
            client.Client.Shutdown(SocketShutdown.Both);
        }
        catch
        {
            // ignore
        }

        try
        {
            client.Close();
        }
        catch
        {
            // ignore
        }
    }

    private sealed class Room
    {
        public Room(string code)
        {
            Code = code;
        }

        public string Code { get; }

        public TcpClient? Host { get; set; }

        public TcpClient? Guest { get; set; }

        /// <summary>双方齐全并可开始转发的信号（重连时会换成新的）。</summary>
        public TaskCompletionSource Ready { get; set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>本次会话的取消令牌：任一端断开时用它让另一端也退出转发。</summary>
        public CancellationTokenSource Session { get; set; } = new();

        /// <summary>是否处于「一端断开、等待重连」的保留状态。</summary>
        public bool Broken { get; set; }

        public bool Complete => Host is not null && Guest is not null;

        public TcpClient? PeerOf(string role) => role == "host" ? Guest : Host;

        public void ClearRole(string role)
        {
            if (role == "host")
            {
                Host = null;
            }
            else
            {
                Guest = null;
            }
        }
    }
}
