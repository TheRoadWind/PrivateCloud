using System.Net.Sockets;
using System.Net.WebSockets;

namespace RelayCore;

/// <summary>
/// 把一条已经完成升级的 WebSocket 连接桥接到本机 TCP 中转核心。
///
/// 协议对齐（与 GameHub 手机端一致）：
///   · WS 消息      -> 追加 '\n' 后写入 TCP
///   · TCP 收到的字节流 -> 按 '\n' 切行，每行作为一个 WS 文本消息
///
/// 空闲判定：手机端每 15 秒发一条应用层心跳（{"t":"hb"}），若 30 秒收不到任何数据，
/// 认为对方已掉线并主动断开，中转核心随即进入「断线保留」，让它可以原房间号重连。
/// </summary>
public static class WebSocketBridge
{
    private const int IdleTimeoutSeconds = 90;
    private const int BufferSize = 8192;

    public static async Task RunAsync(WebSocket socket, string host, int port, CancellationToken ct)
    {
        using var upstream = new TcpClient { NoDelay = true };
        await upstream.ConnectAsync(host, port, ct).ConfigureAwait(false);
        var up = upstream.GetStream();

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var wsWriteLock = new SemaphoreSlim(1, 1);
        var activity = new Activity();

        var wsToTcp = Task.Run(async () =>
        {
            var buffer = new byte[BufferSize];
            using var frame = new MemoryStream();
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), stop.Token)
                        .ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        break;
                    }

                    activity.Touch();
                    if (result.Count > 0)
                    {
                        frame.Write(buffer, 0, result.Count);
                    }

                    if (!result.EndOfMessage)
                    {
                        continue;
                    }

                    var payload = frame.ToArray();
                    frame.SetLength(0);
                    if (payload.Length == 0)
                    {
                        continue;
                    }

                    var line = new byte[payload.Length + 1];
                    payload.CopyTo(line, 0);
                    line[^1] = (byte)'\n';
                    await up.WriteAsync(line, stop.Token).ConfigureAwait(false);
                    await up.FlushAsync(stop.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                RelayLog.Write($"中转：接收端结束（{ex.GetType().Name}）");
            }
            finally
            {
                stop.Cancel();
                ShutdownWrite(upstream);
            }
        });

        var tcpToWs = Task.Run(async () =>
        {
            var buffer = new byte[4096];
            var pending = new List<byte>();
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var read = await up.ReadAsync(buffer, stop.Token).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    for (var i = 0; i < read; i++)
                    {
                        var b = buffer[i];
                        if (b == (byte)'\n')
                        {
                            var line = Trim(pending);
                            pending.Clear();
                            if (line.Length > 0
                                && !await SendTextAsync(socket, line, wsWriteLock, stop.Token).ConfigureAwait(false))
                            {
                                stop.Cancel();
                                return;
                            }
                        }
                        else if (b != (byte)'\r')
                        {
                            pending.Add(b);
                        }
                    }
                }

                var tail = Trim(pending);
                if (tail.Length > 0)
                {
                    await SendTextAsync(socket, tail, wsWriteLock, stop.Token).ConfigureAwait(false);
                }
            }
            catch
            {
                // 断开即结束
            }
            finally
            {
                stop.Cancel();
                ShutdownWrite(upstream);
            }
        });

        var watchdog = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), stop.Token).ConfigureAwait(false);
                    if (activity.IdleMs() > IdleTimeoutSeconds * 1000L)
                    {
                        RelayLog.Write($"中转：客户端 {IdleTimeoutSeconds} 秒无数据，断开连接");
                        break;
                    }
                }
            }
            catch
            {
                // 正常结束
            }
            finally
            {
                stop.Cancel();
            }
        });

        await Task.WhenAll(wsToTcp, tcpToWs, watchdog).ConfigureAwait(false);
        RelayLog.Write("中转：桥接已结束");
    }

    private static async Task<bool> SendTextAsync(
        WebSocket socket, byte[] payload, SemaphoreSlim writeLock, CancellationToken ct)
    {
        await writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (socket.State != WebSocketState.Open)
            {
                return false;
            }

            await socket.SendAsync(new ArraySegment<byte>(payload), WebSocketMessageType.Text, true, ct)
                .ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            writeLock.Release();
        }
    }

    private static byte[] Trim(List<byte> data)
    {
        var bytes = data.ToArray();
        var start = 0;
        var end = bytes.Length;
        while (start < end && bytes[start] <= 0x20) start++;
        while (end > start && bytes[end - 1] <= 0x20) end--;
        if (start == 0 && end == bytes.Length) return bytes;
        var slice = new byte[end - start];
        Array.Copy(bytes, start, slice, 0, end - start);
        return slice;
    }

    private static void ShutdownWrite(TcpClient client)
    {
        try
        {
            client.Client.Shutdown(SocketShutdown.Send);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>线程安全的「最近一次收到数据」记录。</summary>
    private sealed class Activity
    {
        private long _ticks = Environment.TickCount64;

        public void Touch() => Interlocked.Exchange(ref _ticks, Environment.TickCount64);

        public long IdleMs() => Environment.TickCount64 - Interlocked.Read(ref _ticks);
    }
}
