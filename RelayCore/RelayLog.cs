namespace RelayCore;

/// <summary>
/// 中转核心的日志出口。宿主（PrivateCloud）可把 <see cref="Sink"/> 指向自己的日志面板，
/// 未接管时退化为控制台输出。
/// </summary>
public static class RelayLog
{
    /// <summary>日志接收器，由宿主设置（例如写入界面日志）。</summary>
    public static Action<string>? Sink { get; set; }

    /// <summary>是否输出到控制台（宿主已接管时可关掉）。</summary>
    public static bool EchoToConsole { get; set; } = true;

    public static void Write(string message)
    {
        var sink = Sink;
        if (sink is not null)
        {
            try
            {
                sink(message);
                return;
            }
            catch
            {
                // 宿主日志异常不应影响中转
            }
        }

        if (EchoToConsole)
        {
            try
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] {message}");
            }
            catch
            {
                // 无控制台时忽略
            }
        }
    }
}
