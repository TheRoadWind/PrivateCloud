Imports Microsoft.VisualBasic.ApplicationServices

Namespace My
    ' The following events are available for MyApplication:
    ' Startup: Raised when the application starts, before the startup form is created.
    ' Shutdown: Raised after all application forms are closed. This event is not raised if the application terminates abnormally.
    ' UnhandledException: Raised if the application encounters an unhandled exception.
    ' StartupNextInstance: Raised when launching a single-instance application and the application is already active.
    ' NetworkAvailabilityChanged: Raised when the network connection is connected or disconnected.

    ' **NEW** ApplyApplicationDefaults: Raised when the application queries default values to be set for the application.

    ' Example:
    ' Private Sub MyApplication_ApplyApplicationDefaults(sender As Object, e As ApplyApplicationDefaultsEventArgs) Handles Me.ApplyApplicationDefaults
    '
    '   ' Setting the application-wide default Font:
    '   e.Font = New Font(FontFamily.GenericSansSerif, 12, FontStyle.Regular)
    '
    '   ' Setting the HighDpiMode for the Application:
    '   e.HighDpiMode = HighDpiMode.PerMonitorV2
    '
    '   ' If a splash dialog is used, this sets the minimum display time:
    '   e.MinimumSplashScreenDisplayTime = 4000
    ' End Sub

    Partial Friend Class MyApplication

        ''' <summary>
        ''' 单实例模式下，程序已在运行时再次启动：
        ''' 恢复已有实例的主窗口到前台，避免用户以为程序无法启动
        ''' </summary>
        Private Sub MyApplication_StartupNextInstance(sender As Object, e As StartupNextInstanceEventArgs) Handles Me.StartupNextInstance
            e.BringToForeground = True

            Dim f As Global.PrivateCloud.MainForm = TryCast(Me.MainForm, Global.PrivateCloud.MainForm)
            If f IsNot Nothing Then
                If f.WindowState = FormWindowState.Minimized Then
                    f.WindowState = FormWindowState.Normal
                End If
                f.Show()
                f.BringToFront()
            End If
        End Sub

        ''' <summary>
        ''' 全局未处理异常兜底：记录日志并阻止程序崩溃退出
        ''' （如 HTTP 客户端断开引发的连锁异常等非致命错误）
        ''' </summary>
        Private Sub MyApplication_UnhandledException(sender As Object, e As Microsoft.VisualBasic.ApplicationServices.UnhandledExceptionEventArgs) Handles Me.UnhandledException
            Try
                Dim configDir As String = IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "config")
                If Not IO.Directory.Exists(configDir) Then
                    IO.Directory.CreateDirectory(configDir)
                End If
                Dim logPath As String = IO.Path.Combine(configDir, "crash_log.txt")
                IO.File.AppendAllText(logPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} 未处理异常: {e.Exception}{Environment.NewLine}")
            Catch
            End Try
            ' 阻止程序因未处理异常直接退出
            e.ExitApplication = False
        End Sub

    End Class
End Namespace
