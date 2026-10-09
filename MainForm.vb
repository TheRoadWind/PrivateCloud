Imports System.IO
Imports System.IO.Compression
Imports System.Net
Imports System.Net.Http
Imports System.Net.Sockets ' 引用 QRCoder 库
Imports System.Runtime.InteropServices
Imports System.Security.Principal
Imports System.Text
Imports System.Text.RegularExpressions
Imports System.Threading
Imports QRCoder
Imports RelayCore
Public Class MainForm

    ' --- 成员变量 ---
    Private listener As HttpListener
    Private isRunning As Boolean = False
    Private listenThread As Threading.Thread
    Private Const DefaultPort As Integer = 8080

    ' --- GameHub 公网中转 ---
    ' 中转核心跑在本进程内，手机端通过同一条 ngrok 隧道（WebSocket 升级）接入，
    ' 因此「外网开启时段」内即可用公网模式对战。
    Private Const RelayPort As Integer = 45455      ' 中转核心端口（仅本机回环使用）
    Private _relayHost As RelayCore.RelayHost
    Private _relayConn As Integer = 0               ' 当前公网中转连接数（用于空闲保护）
    'Private SharedFolderPath As String = Environment.GetFolderPath(Environment.SpecialFolder.Desktop)

    ' 共享文件夹路径：程序运行目录下的 "Shared" 文件夹
    Private ReadOnly SharedFolderPath As String = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Shared")

    ' 动态生成的控件引用
    Private lblTitle As Label
    Private txtIp As TextBox
    Private txtPort As TextBox
    Private btnStart As Button
    Private btnStop As Button
    Private btnOpenFolder As Button
    Private lblStatus As Label
    Private pbQrCode As PictureBox
    Private pbQrCodeLan As PictureBox   ' 局域网二维码框
    Private pbQrCodeWan As PictureBox   ' 外网二维码框
    Private lblLanHint As Label         ' 局域网二维码提示
    Private lblWanHint As Label         ' 外网二维码提示
    Private lblHint As Label
    Private txtLog As RichTextBox

    ' --- 底部四标签页 ---
    Private tabMain As TabControl
    Private tabHome As TabPage      ' 首页
    Private tabFiles As TabPage     ' 文件
    Private tabShare As TabPage     ' 共享
    Private tabMine As TabPage      ' 我的

    ' --- 文件页控件 ---
    Private lstFiles As ListView          ' 文件列表
    Private txtFilePath As TextBox        ' 当前路径
    Private btnFileUp As Button           ' 返回上级
    Private btnFileRefresh As Button      ' 刷新
    Private btnFileNewFolder As Button    ' 新建文件夹
    Private btnFileRename As Button       ' 重命名
    Private btnFileDelete As Button       ' 删除
    Private btnFileCopy As Button         ' 复制
    Private btnFileCut As Button          ' 剪切
    Private btnFilePaste As Button        ' 粘贴
    Private cmbFileGroup As ComboBox      ' 分组筛选
    Private imgListFiles As ImageList     ' 文件图标列表
    Private cmFileMenu As ContextMenuStrip ' 文件右键菜单
    Private lblFileInfo As Label          ' 文件统计信息

    ' --- 共享页控件 ---
    Private lstTransfer As ListView        ' 传输记录列表
    Private btnTransferDelete As Button    ' 删除选中传输记录
    Private btnTransferRefresh As Button   ' 刷新传输记录
    Private cmbTransferPageSize As ComboBox    ' 每页条数
    Private btnTransferPrev As Button          ' 上一页
    Private btnTransferNext As Button          ' 下一页
    Private lblTransferPageInfo As Label       ' 分页信息
    Private _transferPageIndex As Integer = 0  ' 当前页码（0 起）
    Private _transferPageSize As Integer = 10  ' 每页条数

    ' --- 我的页控件 ---
    Private pbAvatar As PictureBox         ' 头像
    Private lblNickname As Label           ' 昵称显示
    Private btnChangeAvatar As Button      ' 修改头像
    Private txtNickname As TextBox         ' 昵称输入
    Private btnSaveNickname As Button      ' 保存昵称
    Private txtUsername As TextBox         ' 账号输入
    Private txtPassword As TextBox         ' 密码输入
    Private btnSaveAccount As Button       ' 保存账号密码
    Private btnResetSettings As Button     ' 重置密码
    Private chkShowPassword As CheckBox    ' 显示密码
    Private Const AVATAR_FILE As String = "avatar.png"   ' 头像文件名

    ' --- ngrok 相关成员变量 ---
    Private ngrokProcess As Process
    Private ngrokUrl As String = ""
    Private isNgrokRunning As Boolean = False
    Private Const NGROK_EXE_NAME As String = "ngrok.exe"
    Private btnNgrok As Button  '开启按钮

    ' --- 外网自动监听时间段相关 ---
    Private txtNgrokStart As DateTimePicker   ' 外网自动监听开始时间
    Private txtNgrokEnd As DateTimePicker     ' 外网自动监听结束时间
    Private chkAutoNgrok As CheckBox          ' 是否启用外网自动监听
    Private lblNgrokCopy As Label             ' 复制外网地址按钮
    Private lblNgrokStatus As Label           ' 外网自动监听状态提示
    Private tooltipNgrok As ToolTip           ' 外网地址悬浮提示
    Private _loadingNgrokConfig As Boolean = False  ' 正在加载配置标志（防止加载时触发保存）
    Private _isStoppingNgrok As Boolean = False      ' 主动停止 ngrok 标志（区分主动停止与意外退出）
    Private ngrokAutoTimer As System.Timers.Timer   ' 定期检查时间段的定时器
    Private autoNgrokActive As Boolean = False     ' 自动监听是否已启动 ngrok
    Private Const MAX_DAILY_NGROK_MINUTES As Integer = 480  ' 每日免费额度上限（分钟，默认 8 小时）
    Private Const NGROK_CONFIG_FILE As String = "ngrok_auto_config.txt"  ' 配置文件名

    ' --- 运行时配置文件统一存放目录 ---
    Private Const CONFIG_FOLDER As String = "config"

    ' --- 最近浏览记录（首页显示） ---
    Private ReadOnly _recentLock As New Object()
    Private ReadOnly _recentFiles As New List(Of RecentFileItem)
    Private Const MAX_RECENT_COUNT As Integer = 30
    Private Const RECENT_CONFIG_FILE As String = "recent_files.txt"

    Private Class RecentFileItem
        Public Path As String
        Public Name As String
        Public IsFolder As Boolean
        Public Size As Long
        Public Category As String
        Public LastViewed As DateTime
    End Class

    ' --- 文件剪贴板（复制/剪切/粘贴） ---
    Private _clipboardPath As String = ""
    Private _clipboardIsCut As Boolean = False
    Private ReadOnly _clipboardLock As New Object()

    ' --- 用户设置 ---
    Private Const SETTINGS_FILE As String = "user_settings.txt"
    Private Const DEFAULT_USERNAME As String = "admin"
    Private Const DEFAULT_PASSWORD As String = "admin"
    Private Const DEFAULT_NICKNAME As String = "管理员"
    Private Const RESET_PIN As String = "2580"

    ' --- 传输历史记录（共享页显示） ---
    Private ReadOnly _transferLock As New Object()
    Private ReadOnly _transferHistory As New List(Of TransferRecord)
    Private _transferVersion As Long = 0   ' 传输记录版本号（每次增删 +1，用于两端同步）
    Private Const MAX_TRANSFER_COUNT As Integer = 200
    Private Const TRANSFER_CONFIG_FILE As String = "transfer_history.txt"
    Private _lastClientIP As String = ""   ' 当前请求的客户端 IP（用于传输记录）

    Private Class TransferRecord
        Public Id As String          ' 唯一标识（用于删除）
        Public FileName As String
        Public Direction As String   ' "upload" / "download"
        Public Size As Long
        Public [Time] As DateTime
        Public ClientIP As String
        Public FilePath As String    ' 文件完整路径（用于判断是否仍存在）
    End Class



    ' 1. P/Invoke 声明：导入 user32.dll 中的 MessageBoxTimeoutW (Unicode版本)
    <System.Runtime.InteropServices.DllImport("user32.dll", CharSet:=System.Runtime.InteropServices.CharSet.Unicode, SetLastError:=True)>
    Private Shared Function MessageBoxTimeout(hWnd As IntPtr, lpText As String, lpCaption As String,
                                          uType As UInteger, wLanguageId As UShort, dwMilliseconds As Integer) As Integer
    End Function

    ' Shell 文件信息结构（用于获取系统关联图标）
    <StructLayout(LayoutKind.Sequential, CharSet:=CharSet.Auto)>
    Private Structure SHFILEINFO
        Public hIcon As IntPtr
        Public iIcon As Integer
        Public dwAttributes As UInteger
        <MarshalAs(UnmanagedType.ByValTStr, SizeConst:=260)>
        Public szDisplayName As String
        <MarshalAs(UnmanagedType.ByValTStr, SizeConst:=80)>
        Public szTypeName As String
    End Structure

    ' 从 Shell32.dll 获取文件图标
    <DllImport("Shell32.dll", CharSet:=CharSet.Auto)>
    Private Shared Function SHGetFileInfo(ByVal pszPath As String,
                                          ByVal dwFileAttributes As UInteger,
                                          ByRef psfi As SHFILEINFO,
                                          ByVal cbFileInfo As UInteger,
                                          ByVal uFlags As UInteger) As IntPtr
    End Function

    ' 释放由 Shell 返回的图标句柄
    <DllImport("user32.dll", SetLastError:=True)>
    Private Shared Function DestroyIcon(ByVal hIcon As IntPtr) As Boolean
    End Function

    ' 弹出 Windows「打开方式」对话框
    <StructLayout(LayoutKind.Sequential, CharSet:=CharSet.Unicode)>
    Private Structure OPENASINFO
        <MarshalAs(UnmanagedType.LPWStr)>
        Public pcszFile As String       ' 文件完整路径
        <MarshalAs(UnmanagedType.LPWStr)>
        Public pcszClass As String      ' 可选，默认程序 ProgID（Nothing 表示系统自动选择）
        Public oaifInFlags As UInteger  ' 标志（OAIF_ALLOW_REGISTRATION 等）
    End Structure

    <DllImport("Shell32.dll", CharSet:=CharSet.Unicode, SetLastError:=True)>
    Private Shared Function SHOpenWithDialog(ByVal hwnd As IntPtr, ByRef poainfo As OPENASINFO) As Integer
    End Function

    ' 2. 自动弹窗方法
    ''' <summary>
    ''' 显示一个会在指定时间后自动关闭的消息框
    ''' </summary>
    ''' <param name="text">消息内容</param>
    ''' <param name="timeoutSeconds">自动关闭的秒数，默认1秒</param>
    ''' <param name="title">标题，默认"提示"</param>
    Public Shared Sub AutoWindow(text As String, Optional timeoutSeconds As Decimal = 1, Optional title As String = "提示")
        ' 构建显示文本，包含倒计时提示
        Dim displayText As String = $"{text}{vbCrLf}{vbCrLf}将会在 {timeoutSeconds} 秒后自动关闭"

        ' 调用 API
        ' hWnd: IntPtr.Zero 表示没有拥有者窗口（或可以使用 Me.Handle）
        ' uType: 0 表示只有确定按钮 (MB_OK)，无图标。如需图标可组合值，如 64 (信息图标)
        ' wLanguageId: 0 表示使用系统默认语言
        ' dwMilliseconds: 超时时间（毫秒）
        Dim result As Integer = MessageBoxTimeout(IntPtr.Zero, displayText, title, 64, 0, CInt(timeoutSeconds * 1000))
    End Sub

    ''' <summary>
    ''' 检查是否以管理员权限运行
    ''' </summary>
    Private Shared Function IsRunningAsAdministrator() As Boolean
        Try
            Dim identity As WindowsIdentity = WindowsIdentity.GetCurrent()
            Dim principal As New WindowsPrincipal(identity)
            Return principal.IsInRole(WindowsBuiltInRole.Administrator)
        Catch ex As Exception
            Return False
        End Try
    End Function
    ' --- 初始化与界面构建 ---
    ''' <summary>获取配置文件完整路径（统一存放在 config 目录下，自动创建目录）</summary>
    Private Function GetConfigPath(fileName As String) As String
        Dim dir As String = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, CONFIG_FOLDER)
        If Not Directory.Exists(dir) Then
            Directory.CreateDirectory(dir)
        End If
        Return Path.Combine(dir, fileName)
    End Function

    ''' <summary>迁移旧版根目录下的配置文件到 config 目录（兼容旧版本升级）</summary>
    Private Sub MigrateConfigFiles()
        Try
            Dim baseDir As String = AppDomain.CurrentDomain.BaseDirectory
            Dim configDir As String = Path.Combine(baseDir, CONFIG_FOLDER)
            If Not Directory.Exists(configDir) Then
                Directory.CreateDirectory(configDir)
            End If
            Dim oldFiles As String() = {
                "user_settings.txt", "recent_files.txt", "transfer_history.txt",
                "ngrok_auto_config.txt", "ngrok_token.txt", "crash_log.txt",
                "avatar.png", "ngrok.exe"
            }
            For Each f As String In oldFiles
                Dim oldPath As String = Path.Combine(baseDir, f)
                Dim newPath As String = Path.Combine(configDir, f)
                If File.Exists(oldPath) AndAlso Not File.Exists(newPath) Then
                    File.Move(oldPath, newPath)
                End If
            Next
        Catch ex As Exception
            AddLog($"迁移配置文件失败: {ex.Message}")
        End Try
    End Sub

    Private Sub MainForm_Load(sender As Object, e As EventArgs) Handles MyBase.Load

        '管理员权限获取：非管理员时自动提权重启
        If Not CheckAdmin() Then Return

        ' 迁移旧版根目录下的配置文件到 config 目录
        MigrateConfigFiles()

        Me.Text = "私有云盘"
        Me.Size = New Size(620, 760)
        Me.Location = New Point(500, 0)

        ' 【修改点2】确保共享文件夹存在
        If Not Directory.Exists(SharedFolderPath) Then
            Directory.CreateDirectory(SharedFolderPath)
        End If

        ' 动态创建所有控件
        CreateControls()

        ' 初始化外网自动监听时间段设置
        LoadNgrokAutoConfig()

        ' 加载最近浏览记录与用户设置
        LoadRecentConfig()
        LoadTransferConfig()
        Dim autoIp As String = GetLocalIpAddress()
        txtIp.Text = If(Not String.IsNullOrEmpty(autoIp), autoIp, "127.0.0.1")
        txtPort.Text = DefaultPort.ToString()

        ' 传输记录已加载，刷新共享页列表
        RefreshTransferList()

        ' 加载「我的」页数据（头像、昵称、账号）
        LoadMinePageData()

        UpdateUIState(False)

        ' 初始化托盘
        InitializeTrayIcon()

        ' 启动自动监听（等待外部连接）
        StartListeningForFirstConnection()

        ' 启动外网自动监听调度定时器
        StartNgrokAutoScheduler()
    End Sub
    Private Function CheckAdmin() As Boolean

        ' 检查是否以管理员身份运行
        If Not IsRunningAsAdministrator() Then
            ' 不是管理员：先退出当前实例（释放单实例锁），
            ' 再由辅助进程等待本进程完全退出后，以管理员权限重新启动，
            ' 避免因单实例 Mutex 冲突导致提权后的实例无法启动
            Try
                Dim currentPid As Integer = Process.GetCurrentProcess().Id
                Dim exePath As String = Application.ExecutablePath
                Dim psi As New ProcessStartInfo() With {
                    .FileName = "powershell.exe",
                    .Arguments = "-NoProfile -WindowStyle Hidden -Command ""Wait-Process -Id " & currentPid.ToString() &
                                 " -ErrorAction SilentlyContinue; Start-Process -FilePath '" &
                                 exePath.Replace("'", "''") & "' -Verb RunAs""",
                    .UseShellExecute = False,
                    .CreateNoWindow = True
                }
                Process.Start(psi)
            Catch ex As Exception
                ' 辅助进程启动失败（如系统缺少 PowerShell）
                AutoWindow("自动提权失败，请右键点击程序，选择'以管理员身份运行'。" & vbCrLf &
                           "本程序需要以管理员权限运行才能正常工作。",
                           3,
                           "权限不足")
            End Try

            ' 无论是否成功启动辅助进程，当前实例都要退出，
            ' 避免旧实例（非管理员）一直占用单实例锁导致程序无法重新启动
            Application.Exit()
            Return False
        Else
            ' 管理员权限直接检查并配置防火墙规则
            If Not ConfigureFirewallRule(DefaultPort) Then
                MessageBox.Show("防火墙规则配置失败，服务可能无法被外部访问。" & vbCrLf &
                              "请手动在防火墙中允许端口 " & DefaultPort.ToString & " 的入站连接。",
                              "警告",
                              MessageBoxButtons.OK,
                              MessageBoxIcon.Warning)
            End If

            Return True
        End If
    End Function
    ''' <summary>
    ''' 动态创建界面控件（底部四标签结构：首页 / 文件 / 共享 / 我的）
    ''' </summary>
    Private Sub CreateControls()
        ' ---- 通用局部工厂函数（把控件添加到指定父容器）----
        Dim NewLabel = Function(parent As Control, name As String, text As String, x As Integer, y As Integer) As Label
                           Dim l As New Label() With {
                               .Text = text,
                               .Location = New Point(x, y),
                               .AutoSize = True
                           }
                           If Not String.IsNullOrEmpty(name) Then l.Name = name
                           parent.Controls.Add(l)
                           Return l
                       End Function

        Dim NewTextBox = Function(parent As Control, name As String, x As Integer, y As Integer, width As Integer) As TextBox
                             Dim t As New TextBox() With {
                                 .Location = New Point(x, y),
                                 .Width = width
                             }
                             If Not String.IsNullOrEmpty(name) Then t.Name = name
                             parent.Controls.Add(t)
                             Return t
                         End Function

        Dim NewButton = Function(parent As Control, name As String, text As String, x As Integer, y As Integer, width As Integer) As Button
                            Dim b As New Button() With {
                                .Text = text,
                                .Location = New Point(x, y),
                                .Width = width
                            }
                            If Not String.IsNullOrEmpty(name) Then b.Name = name
                            parent.Controls.Add(b)
                            Return b
                        End Function

        Dim NewPictureBox = Function(parent As Control, name As String, x As Integer, y As Integer, w As Integer, h As Integer) As PictureBox
                                Dim p As New PictureBox() With {
                                    .Location = New Point(x, y),
                                    .Size = New Size(w, h),
                                    .SizeMode = PictureBoxSizeMode.Zoom,
                                    .BorderStyle = BorderStyle.FixedSingle
                                }
                                If Not String.IsNullOrEmpty(name) Then p.Name = name
                                parent.Controls.Add(p)
                                Return p
                            End Function

        Dim NewRichTextBox = Function(parent As Control, name As String, x As Integer, y As Integer, w As Integer, h As Integer) As RichTextBox
                                 Dim r As New RichTextBox() With {
                                     .Location = New Point(x, y),
                                     .Size = New Size(w, h),
                                     .ReadOnly = True,
                                     .BackColor = Color.WhiteSmoke,
                                     .Font = New Font("Consolas", 8),
                                     .ScrollBars = RichTextBoxScrollBars.Vertical
                                 }
                                 If Not String.IsNullOrEmpty(name) Then r.Name = name
                                 parent.Controls.Add(r)
                                 Return r
                             End Function

        Dim NewCheckBox = Function(parent As Control, name As String, text As String, x As Integer, y As Integer) As CheckBox
                              Dim c As New CheckBox() With {
                                  .Text = text,
                                  .Location = New Point(x, y),
                                  .AutoSize = True
                              }
                              If Not String.IsNullOrEmpty(name) Then c.Name = name
                              parent.Controls.Add(c)
                              Return c
                          End Function

        Dim NewDateTimePicker = Function(parent As Control, name As String, x As Integer, y As Integer, width As Integer, format As String) As DateTimePicker
                                    Dim d As New DateTimePicker() With {
                                        .Location = New Point(x, y),
                                        .Width = width,
                                        .Format = DateTimePickerFormat.Custom,
                                        .CustomFormat = format,
                                        .ShowUpDown = True
                                    }
                                    If Not String.IsNullOrEmpty(name) Then d.Name = name
                                    parent.Controls.Add(d)
                                    Return d
                                End Function

        ' ---- 底部标签控件 ----
        tabMain = New TabControl() With {
            .Dock = DockStyle.Fill,
            .Font = New Font("Microsoft YaHei", 9)
        }
        Me.Controls.Add(tabMain)

        tabHome = New TabPage("首页")
        tabFiles = New TabPage("文件")
        tabShare = New TabPage("共享")
        tabMine = New TabPage("我的")
        tabMain.TabPages.Add(tabHome)
        tabMain.TabPages.Add(tabFiles)
        tabMain.TabPages.Add(tabShare)
        tabMain.TabPages.Add(tabMine)

        ' ================== 首页：服务控制 + 日志 ==================
        BuildHomePage(tabHome, NewLabel, NewTextBox, NewButton, NewCheckBox, NewDateTimePicker, NewRichTextBox)

        ' ================== 文件：共享文件夹浏览器 ==================
        BuildFilesPage(tabFiles, NewLabel, NewTextBox, NewButton)

        ' ================== 共享：二维码等信息 ==================
        BuildSharePage(tabShare, NewLabel, NewButton, NewPictureBox)

        ' ================== 我的：头像 / 昵称 / 设置 ==================
        BuildMinePage(tabMine, NewLabel, NewTextBox, NewButton, NewPictureBox, NewCheckBox)
    End Sub

    ''' <summary>构建「首页」：服务控制按钮 + 日志</summary>
    Private Sub BuildHomePage(page As TabPage,
                              NewLabel As Func(Of Control, String, String, Integer, Integer, Label),
                              NewTextBox As Func(Of Control, String, Integer, Integer, Integer, TextBox),
                              NewButton As Func(Of Control, String, String, Integer, Integer, Integer, Button),
                              NewCheckBox As Func(Of Control, String, String, Integer, Integer, CheckBox),
                              NewDateTimePicker As Func(Of Control, String, Integer, Integer, Integer, String, DateTimePicker),
                              NewRichTextBox As Func(Of Control, String, Integer, Integer, Integer, Integer, RichTextBox))
        ' 标题
        lblTitle = NewLabel(page, "", "☁️ 私有云盘", 20, 20)
        lblTitle.Font = New Font("Microsoft YaHei", 14, FontStyle.Bold)

        ' IP / Port
        NewLabel(page, "", "本机 IP:", 20, 60)
        txtIp = NewTextBox(page, "", 90, 55, 150)
        NewLabel(page, "", "端口:", 250, 60)
        txtPort = NewTextBox(page, "", 290, 55, 60)

        ' 按钮区域
        btnStart = NewButton(page, "", "启动服务", 20, 100, 100)
        btnStart.BackColor = Color.FromArgb(0, 123, 255)
        btnStart.ForeColor = Color.White
        AddHandler btnStart.Click, AddressOf BtnStart_Click

        btnStop = NewButton(page, "", "停止服务", 130, 100, 100)
        btnStop.Enabled = False
        AddHandler btnStop.Click, AddressOf BtnStop_Click

        btnOpenFolder = NewButton(page, "", "打开私有云盘", 240, 100, 140)
        AddHandler btnOpenFolder.Click, AddressOf BtnOpenFolder_Click

        btnNgrok = NewButton(page, "", "🌐 开启外网", 390, 100, 100)
        btnNgrok.BackColor = Color.FromArgb(52, 199, 89)
        btnNgrok.ForeColor = Color.White
        btnNgrok.Enabled = False
        AddHandler btnNgrok.Click, AddressOf BtnNgrok_Click

        ' 状态
        lblStatus = NewLabel(page, "", "服务未运行", 20, 140)
        lblStatus.ForeColor = Color.Gray

        ' 提示文字（二维码在「共享」页展示）
        Dim lblHomeHint = NewLabel(page, "", "提示:请在「共享」页查看登录二维码", 20, 165)
        lblHomeHint.ForeColor = Color.Gray

        ' 外网自动监听时间段设置
        NewLabel(page, "", "外网自动监听:", 20, 195).Font = New Font("Microsoft YaHei", 9, FontStyle.Bold)
        chkAutoNgrok = NewCheckBox(page, "chkAutoNgrok", "启用", 120, 193)
        AddHandler chkAutoNgrok.CheckedChanged, AddressOf ChkAutoNgrok_CheckedChanged

        txtNgrokStart = NewDateTimePicker(page, "", 170, 193, 60, "HH:mm")
        AddHandler txtNgrokStart.ValueChanged, AddressOf NgrokTime_ValueChanged
        NewLabel(page, "", "至", 235, 195)
        txtNgrokEnd = NewDateTimePicker(page, "", 255, 193, 60, "HH:mm")
        AddHandler txtNgrokEnd.ValueChanged, AddressOf NgrokTime_ValueChanged
        lblNgrokStatus = NewLabel(page, "", "", 325, 195)
        lblNgrokStatus.ForeColor = Color.Gray
        lblNgrokStatus.Font = New Font("Microsoft YaHei", 8)

        ' 公网地址
        Dim lblPublicUrl = NewLabel(page, "lblPublicUrl", "", 20, 225)
        lblPublicUrl.ForeColor = Color.Blue
        lblPublicUrl.Font = New Font("Microsoft YaHei", 9, FontStyle.Bold)
        lblPublicUrl.AutoSize = False
        lblPublicUrl.Size = New Size(430, 20)
        lblPublicUrl.TextAlign = ContentAlignment.MiddleLeft

        ' 复制外网地址按钮
        lblNgrokCopy = NewLabel(page, "lblNgrokCopy", "📋 复制", 470, 225)
        lblNgrokCopy.ForeColor = Color.FromArgb(0, 122, 255)
        lblNgrokCopy.BackColor = Color.FromArgb(230, 240, 255)
        lblNgrokCopy.Font = New Font("Microsoft YaHei", 9, FontStyle.Bold)
        lblNgrokCopy.AutoSize = False
        lblNgrokCopy.Size = New Size(70, 22)
        lblNgrokCopy.TextAlign = ContentAlignment.MiddleCenter
        lblNgrokCopy.BorderStyle = BorderStyle.FixedSingle
        lblNgrokCopy.Cursor = Cursors.Hand
        lblNgrokCopy.Visible = False
        AddHandler lblNgrokCopy.Click, AddressOf LblNgrokCopy_Click

        Dim lblDownloadStatus = NewLabel(page, "lblDownloadStatus", "", 20, 255)
        lblDownloadStatus.ForeColor = Color.FromArgb(255, 149, 0)
        lblDownloadStatus.Font = New Font("Microsoft YaHei", 9)

        ' 操作记录区
        NewLabel(page, "", "操作记录", 20, 285).Font = New Font("Microsoft YaHei", 9, FontStyle.Bold)
        Dim btnClearLog = NewButton(page, "", "清除日志", 450, 280, 90)
        AddHandler btnClearLog.Click, AddressOf BtnClearLog_Click
        txtLog = NewRichTextBox(page, "", 20, 305, 520, 320)
    End Sub

    ''' <summary>构建「共享」：二维码 + 传输记录</summary>
    Private Sub BuildSharePage(page As TabPage,
                               NewLabel As Func(Of Control, String, String, Integer, Integer, Label),
                               NewButton As Func(Of Control, String, String, Integer, Integer, Integer, Button),
                               NewPictureBox As Func(Of Control, String, Integer, Integer, Integer, Integer, PictureBox))
        ' 提示文字：放顶部，预留足够高度避免遮挡二维码
        lblHint = NewLabel(page, "", "提示:启动服务后将会显示登录二维码", 20, 10)
        lblHint.AutoSize = False
        lblHint.Size = New Size(540, 42)
        lblHint.ForeColor = Color.Gray

        ' 局域网二维码框（左），下移避开提示文字
        pbQrCodeLan = NewPictureBox(page, "", 20, 58, 240, 210)
        lblLanHint = NewLabel(page, "", "局域网二维码", 20, 270)
        lblLanHint.ForeColor = Color.Gray
        lblLanHint.AutoSize = False
        lblLanHint.Size = New Size(240, 16)
        lblLanHint.TextAlign = ContentAlignment.MiddleCenter

        ' 外网二维码框（右），初始隐藏
        pbQrCodeWan = NewPictureBox(page, "", 280, 58, 240, 210)
        pbQrCodeWan.Visible = False
        lblWanHint = NewLabel(page, "", "外网二维码（未开启）", 280, 270)
        lblWanHint.ForeColor = Color.Gray
        lblWanHint.AutoSize = False
        lblWanHint.Size = New Size(240, 16)
        lblWanHint.TextAlign = ContentAlignment.MiddleCenter
        lblWanHint.Visible = False

        ' 兼容旧代码对 pbQrCode 的引用，保留为局域网二维码框的别名
        pbQrCode = pbQrCodeLan

        ' 传输记录标题与操作按钮
        NewLabel(page, "", "传输记录", 20, 300).Font = New Font("Microsoft YaHei", 9, FontStyle.Bold)
        btnTransferRefresh = NewButton(page, "", "刷新", 440, 295, 70)
        AddHandler btnTransferRefresh.Click, AddressOf BtnTransferRefresh_Click
        btnTransferDelete = NewButton(page, "", "删除选中", 520, 295, 70)
        btnTransferDelete.ForeColor = Color.Red
        btnTransferDelete.Enabled = False    ' 初始未勾选时禁用
        AddHandler btnTransferDelete.Click, AddressOf BtnTransferDelete_Click

        ' 传输记录列表（编号、文件名、类型、时间），带勾选框支持多选
        lstTransfer = New ListView() With {
            .Location = New Point(20, 325),
            .Size = New Size(570, 270),
            .View = View.Details,
            .FullRowSelect = True,
            .GridLines = True,
            .HideSelection = False,
            .MultiSelect = True,
            .CheckBoxes = True
        }
        lstTransfer.Columns.Add("编号", 40)
        lstTransfer.Columns.Add("文件名", 240)
        lstTransfer.Columns.Add("类型", 60)
        lstTransfer.Columns.Add("时间", 140)
        page.Controls.Add(lstTransfer)
        AddHandler lstTransfer.ItemChecked, AddressOf LstTransfer_ItemChecked

        ' 分页控件
        NewLabel(page, "", "每页", 20, 602)
        cmbTransferPageSize = New ComboBox() With {
            .Location = New Point(55, 597),
            .Width = 60,
            .DropDownStyle = ComboBoxStyle.DropDownList
        }
        For i As Integer = 10 To 100 Step 10
            cmbTransferPageSize.Items.Add(i.ToString())
        Next
        cmbTransferPageSize.SelectedIndex = 0
        page.Controls.Add(cmbTransferPageSize)
        AddHandler cmbTransferPageSize.SelectedIndexChanged, AddressOf CmbTransferPageSize_Changed

        btnTransferPrev = NewButton(page, "", "上一页", 130, 595, 70)
        AddHandler btnTransferPrev.Click, AddressOf BtnTransferPrev_Click
        btnTransferNext = NewButton(page, "", "下一页", 210, 595, 70)
        AddHandler btnTransferNext.Click, AddressOf BtnTransferNext_Click
        lblTransferPageInfo = NewLabel(page, "", "", 290, 600)
        lblTransferPageInfo.ForeColor = Color.Gray
        lblTransferPageInfo.Font = New Font("Microsoft YaHei", 8)

        ' 初始加载传输记录
        RefreshTransferList()
    End Sub

    ''' <summary>构建「我的」：头像 / 昵称 / 设置</summary>
    Private Sub BuildMinePage(page As TabPage,
                              NewLabel As Func(Of Control, String, String, Integer, Integer, Label),
                              NewTextBox As Func(Of Control, String, Integer, Integer, Integer, TextBox),
                              NewButton As Func(Of Control, String, String, Integer, Integer, Integer, Button),
                              NewPictureBox As Func(Of Control, String, Integer, Integer, Integer, Integer, PictureBox),
                              NewCheckBox As Func(Of Control, String, String, Integer, Integer, CheckBox))
        ' 头像
        pbAvatar = NewPictureBox(page, "", 20, 30, 100, 100)
        pbAvatar.BackColor = Color.WhiteSmoke
        pbAvatar.SizeMode = PictureBoxSizeMode.Zoom

        btnChangeAvatar = NewButton(page, "", "修改头像", 130, 30, 100)
        AddHandler btnChangeAvatar.Click, AddressOf BtnChangeAvatar_Click

        lblNickname = NewLabel(page, "", "管理员", 130, 75)
        lblNickname.Font = New Font("Microsoft YaHei", 12, FontStyle.Bold)

        ' 昵称设置
        NewLabel(page, "", "昵称:", 20, 155)
        txtNickname = NewTextBox(page, "", 80, 150, 160)
        btnSaveNickname = NewButton(page, "", "保存昵称", 250, 148, 90)
        AddHandler btnSaveNickname.Click, AddressOf BtnSaveNickname_Click

        ' 账号密码设置
        NewLabel(page, "", "账号:", 20, 200)
        txtUsername = NewTextBox(page, "", 80, 195, 160)
        txtUsername.ReadOnly = True      ' 账号不可修改
        NewLabel(page, "", "密码:", 20, 235)
        txtPassword = NewTextBox(page, "", 80, 230, 160)
        txtPassword.UseSystemPasswordChar = True
        btnSaveAccount = NewButton(page, "", "保存账号密码", 250, 210, 100)
        AddHandler btnSaveAccount.Click, AddressOf BtnSaveAccount_Click

        ' 显示密码复选框（仅用于账号密码输入框）
        chkShowPassword = NewCheckBox(page, "", "显示密码", 250, 250)
        AddHandler chkShowPassword.CheckedChanged, AddressOf ChkShowPassword_Changed

        ' 重置密码（弹窗流程，含 PIN 校验、随机生成/手动输入、二次确认）
        btnResetSettings = NewButton(page, "", "重置密码", 20, 280, 120)
        btnResetSettings.BackColor = Color.FromArgb(255, 82, 82)
        btnResetSettings.ForeColor = Color.White
        AddHandler btnResetSettings.Click, AddressOf BtnResetSettings_Click
    End Sub

    ''' <summary>构建「文件」：共享文件夹浏览器（Win11 风格大图标视图）</summary>
    Private Sub BuildFilesPage(page As TabPage,
                               NewLabel As Func(Of Control, String, String, Integer, Integer, Label),
                               NewTextBox As Func(Of Control, String, Integer, Integer, Integer, TextBox),
                               NewButton As Func(Of Control, String, String, Integer, Integer, Integer, Button))
        ' 工具栏：路径 + 返回上级 + 刷新 + 新建文件夹 + 打开 + 分组
        txtFilePath = NewTextBox(page, "", 20, 20, 270)
        txtFilePath.ReadOnly = True

        btnFileUp = NewButton(page, "", "⬆ 上级", 300, 18, 70)
        AddHandler btnFileUp.Click, AddressOf BtnFileUp_Click

        btnFileRefresh = NewButton(page, "", "🔄 刷新(F5)", 380, 18, 90)
        AddHandler btnFileRefresh.Click, AddressOf BtnFileRefresh_Click

        NewLabel(page, "", "分组:", 480, 23)
        cmbFileGroup = New ComboBox() With {
            .Location = New Point(520, 18),
            .Width = 60,
            .DropDownStyle = ComboBoxStyle.DropDownList
        }
        cmbFileGroup.Items.AddRange(New String() {"全部", "文件夹", "图片", "视频", "音频", "文档", "压缩包", "其他"})
        cmbFileGroup.SelectedIndex = 0
        page.Controls.Add(cmbFileGroup)
        AddHandler cmbFileGroup.SelectedIndexChanged, AddressOf CmbFileGroup_Changed

        ' 文件图标 ImageList（系统真实图标 / 图片缩略图）
        imgListFiles = New ImageList() With {
            .ColorDepth = ColorDepth.Depth32Bit,
            .ImageSize = New Size(48, 48)
        }

        ' 文件列表（Tile 视图：大图标 + 子项）
        lstFiles = New ListView() With {
            .Location = New Point(20, 55),
            .Size = New Size(560, 430),
            .View = View.Tile,
            .FullRowSelect = True,
            .HideSelection = False,
            .MultiSelect = True,
            .LabelEdit = False,
            .LargeImageList = imgListFiles,
            .TileSize = New Size(200, 90)
        }
        page.Controls.Add(lstFiles)
        AddHandler lstFiles.DoubleClick, AddressOf LstFiles_DoubleClick
        ' 鼠标右键：显示菜单
        AddHandler lstFiles.MouseClick, AddressOf LstFiles_MouseClick
        ' 键盘快捷键
        AddHandler lstFiles.KeyDown, AddressOf LstFiles_KeyDown
        ' 允许 ListView 接收键盘焦点
        lstFiles.TabStop = True
        lstFiles.HideSelection = False

        ' 右键菜单
        cmFileMenu = New ContextMenuStrip()
        AddFileMenuItem("打开", "Enter", AddressOf BtnFileOpen_Click)
        ' 打开方式子菜单
        Dim openWithMenu As New ToolStripMenuItem("打开方式")
        AddOpenWithItem(openWithMenu, "记事本", "notepad.exe", "")
        AddOpenWithItem(openWithMenu, "默认浏览器", "", "browser")
        AddOpenWithItem(openWithMenu, "图片查看器", "", "image")
        openWithMenu.DropDownItems.Add(New ToolStripSeparator())
        AddOpenWithItem(openWithMenu, "选择其他程序...", "", "choose")
        cmFileMenu.Items.Add(openWithMenu)
        cmFileMenu.Items.Add(New ToolStripSeparator())
        AddFileMenuItem("复制", "Ctrl+C", AddressOf BtnFileCopy_Click)
        AddFileMenuItem("剪切", "Ctrl+X", AddressOf BtnFileCut_Click)
        AddFileMenuItem("粘贴", "Ctrl+V", AddressOf BtnFilePaste_Click)
        cmFileMenu.Items.Add(New ToolStripSeparator())
        AddFileMenuItem("重命名", "F2", AddressOf BtnFileRename_Click)
        AddFileMenuItem("删除", "Del", AddressOf BtnFileDelete_Click)
        cmFileMenu.Items.Add(New ToolStripSeparator())
        AddFileMenuItem("新建文件夹", "", AddressOf BtnFileNewFolder_Click)
        AddFileMenuItem("刷新", "F5", AddressOf BtnFileRefresh_Click)
        AddFileMenuItem("属性", "", AddressOf BtnFileProperties_Click)
        lstFiles.ContextMenuStrip = cmFileMenu

        lblFileInfo = NewLabel(page, "", "", 20, 495)
        lblFileInfo.ForeColor = Color.Gray
        lblFileInfo.Font = New Font("Microsoft YaHei", 8)

        ' 初始加载
        LoadFileList(SharedFolderPath)
    End Sub

    ''' <summary>向右键菜单添加项（带快捷键显示）</summary>
    Private Sub AddFileMenuItem(text As String, shortcut As String, handler As EventHandler)
        Dim item As New ToolStripMenuItem(text)
        If Not String.IsNullOrEmpty(shortcut) Then item.Text &= $"  ({shortcut})"
        AddHandler item.Click, handler
        cmFileMenu.Items.Add(item)
    End Sub

    ''' <summary>向"打开方式"子菜单添加项</summary>
    Private Sub AddOpenWithItem(parent As ToolStripMenuItem, text As String, program As String, mode As String)
        Dim item As New ToolStripMenuItem(text) With {.Tag = mode}
        AddHandler item.Click, Sub(sender As Object, e As EventArgs)
                                   OpenWithSelected(sender, program, mode)
                               End Sub
        parent.DropDownItems.Add(item)
    End Sub

    ''' <summary>用指定方式打开选中文件</summary>
    Private Sub OpenWithSelected(sender As Object, program As String, mode As String)
        Dim path = GetSelectedPath()
        If String.IsNullOrEmpty(path) Then
            MessageBox.Show("请先选择文件", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If
        Try
            Select Case mode
                Case "browser"
                    Process.Start(New ProcessStartInfo(path) With {.UseShellExecute = True})
                Case "image"
                    ' 用系统默认图片查看器（rundll32 或直接 shell 打开）
                    Process.Start(New ProcessStartInfo(path) With {.UseShellExecute = True})
                Case "choose"
                    ' 调用 Windows 系统"打开方式"对话框（SHOpenWithDialog）
                    ShowOpenWithDialog(path)
                Case Else
                    ' 指定程序打开
                    Process.Start(New ProcessStartInfo(program) With {
                        .Arguments = $"""{path}""",
                        .UseShellExecute = False
                    })
            End Select
        Catch ex As Exception
            MessageBox.Show("打开失败: " & ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    ''' <summary>弹出 Windows「打开方式」对话框</summary>
    Private Sub ShowOpenWithDialog(filePath As String)
        Try
            Dim info As New OPENASINFO() With {
                .pcszFile = filePath,
                .pcszClass = Nothing,
                .oaifInFlags = 0
            }
            Dim hr As Integer = SHOpenWithDialog(Me.Handle, info)
            ' hr = 0 表示成功；负数表示失败。若失败则回退到 rundll32
            If hr < 0 Then
                Throw New InvalidOperationException($"SHOpenWithDialog 返回 HRESULT: 0x{hr:X8}")
            End If
        Catch ex As Exception
            ' 兜底：打开 Windows 设置的「默认应用」页面，让用户手动设置
            Try
                Process.Start(New ProcessStartInfo("ms-settings:defaultapps") With {.UseShellExecute = True})
            Catch ex2 As Exception
                MessageBox.Show("打开方式对话框调用失败: " & ex2.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error)
            End Try
        End Try
    End Sub

    ' ================== 文件浏览器逻辑 ==================

    ''' <summary>当前文件浏览器所在的目录</summary>
    Private _fileCurrentDir As String = ""

    ''' <summary>加载指定目录到文件列表</summary>
    Private Sub LoadFileList(dirPath As String)
        If String.IsNullOrEmpty(dirPath) OrElse Not Directory.Exists(dirPath) Then
            dirPath = SharedFolderPath
            If Not Directory.Exists(dirPath) Then
                Directory.CreateDirectory(dirPath)
            End If
        End If
        _fileCurrentDir = dirPath
        txtFilePath.Text = dirPath
        lstFiles.Items.Clear()
        ' 重建图标缓存（避免目录切换后图标陈旧）
        imgListFiles.Images.Clear()
        _iconCache.Clear()

        Try
            ' 文件夹图标（使用系统"目录"图标）
            Dim folderIconIdx As Integer = AddSystemIcon(Environment.GetFolderPath(Environment.SpecialFolder.Windows), False)

            ' 文件夹在前，文件在后
            For Each d In Directory.GetDirectories(dirPath).OrderBy(Function(x) Path.GetFileName(x), StringComparer.OrdinalIgnoreCase)
                Dim di = New DirectoryInfo(d)
                Dim item = New ListViewItem(Path.GetFileName(d)) With {
                    .Tag = d,
                    .ImageIndex = folderIconIdx
                }
                ' Tile 视图：SubItems(0)=名称已显示，再加 2 行子文本（类型 + 大小）
                item.SubItems.Add("文件夹")
                item.SubItems.Add("")
                lstFiles.Items.Add(item)
            Next

            For Each f In Directory.GetFiles(dirPath).OrderBy(Function(x) Path.GetFileName(x), StringComparer.OrdinalIgnoreCase)
                Dim fi = New FileInfo(f)
                Dim item = New ListViewItem(Path.GetFileName(f)) With {
                    .Tag = f,
                    .ImageIndex = GetFileIconIndex(f)
                }
                item.SubItems.Add(GetFileCategory(f))
                item.SubItems.Add(FormatFileSize(fi.Length))
                lstFiles.Items.Add(item)
            Next

            ApplyFileGroupFilter()
        Catch ex As Exception
            AddLog($"加载文件列表失败: {ex.Message}")
        End Try
    End Sub

    ''' <summary>图标缓存（扩展名 → ImageList 索引）</summary>
    Private ReadOnly _iconCache As New Dictionary(Of String, Integer)(StringComparer.OrdinalIgnoreCase)

    ''' <summary>获取/添加文件的系统图标，返回 ImageList 索引（图片也走系统图标，避免大量解码卡死）</summary>
    Private Function GetFileIconIndex(filePath As String) As Integer
        ' 所有文件（含图片）均按扩展名缓存系统图标，避免逐个解码大图导致 UI 卡死
        Dim ext = Path.GetExtension(filePath).ToLowerInvariant()
        If _iconCache.TryGetValue(ext, Nothing) Then Return _iconCache(ext)
        Dim idx = AddSystemIcon(filePath, True)
        _iconCache(ext) = idx
        Return idx
    End Function

    ''' <summary>从系统（Shell32）提取指定路径的关联图标，加入 ImageList</summary>
    Private Function AddSystemIcon(filePath As String, useShellApi As Boolean) As Integer
        Try
            Dim icon As Icon
            If useShellApi Then
                ' 用 Shell API 提取高质量图标（含各类文件关联图标）
                icon = GetShellIcon(filePath)
                If icon Is Nothing Then
                    icon = Icon.ExtractAssociatedIcon(filePath)
                End If
            Else
                ' 文件夹：用系统目录图标
                icon = GetDirectoryIcon(filePath)
            End If
            If icon Is Nothing Then Return -1
            imgListFiles.Images.Add(icon)
            Return imgListFiles.Images.Count - 1
        Catch
            Return -1
        End Try
    End Function

    ''' <summary>通过 Shell API 获取文件/文件夹真实关联图标（不使用文件属性推断）</summary>
    Private Function GetShellIcon(filePath As String) As Icon
        Try
            Dim shInfo As New SHFILEINFO()
            ' SHGFI_ICON = 0x100, SHGFI_LARGEICON = 0x0（大图标）
            ' 去掉 SHGFI_USEFILEATTRIBUTES，直接读取真实文件的关联图标
            Dim result = SHGetFileInfo(filePath, 0, shInfo, CUInt(Marshal.SizeOf(shInfo)),
                                       &H100 Or &H0)
            If result <> IntPtr.Zero AndAlso shInfo.hIcon <> IntPtr.Zero Then
                ' 复制图标（因为 shInfo.hIcon 由系统管理，必须复制）
                Dim iconCopy = DirectCast(Icon.FromHandle(shInfo.hIcon).Clone(), Icon)
                ' 释放系统句柄
                DestroyIcon(shInfo.hIcon)
                Return iconCopy
            End If
        Catch
        End Try
        Return Nothing
    End Function

    ''' <summary>获取系统目录图标</summary>
    Private Function GetDirectoryIcon(folderPath As String) As Icon
        Try
            Dim shInfo As New SHFILEINFO()
            ' SHGFI_ICON = 0x100, SHGFI_OPENICON = 0x2
            Dim flags = &H100 Or &H2
            Dim result = SHGetFileInfo(folderPath, 0, shInfo, CUInt(Marshal.SizeOf(shInfo)), flags)
            If result <> IntPtr.Zero AndAlso shInfo.hIcon <> IntPtr.Zero Then
                Dim iconCopy = DirectCast(Icon.FromHandle(shInfo.hIcon).Clone(), Icon)
                DestroyIcon(shInfo.hIcon)
                Return iconCopy
            End If
        Catch
        End Try
        Return Nothing
    End Function

    ''' <summary>根据分组筛选当前列表</summary>
    Private Sub ApplyFileGroupFilter()
        If cmbFileGroup Is Nothing OrElse lstFiles Is Nothing Then Return
        Dim group = cmbFileGroup.SelectedItem?.ToString()
        Dim folderCount As Integer = 0
        Dim fileCount As Integer = 0
        Dim totalSize As Long = 0

        ' 倒序遍历以安全移除不符合的项
        For i As Integer = lstFiles.Items.Count - 1 To 0 Step -1
            Dim item As ListViewItem = lstFiles.Items(i)
            Dim tag = item.Tag?.ToString()
            Dim isFolder As Boolean = item.SubItems(1).Text = "文件夹"
            Dim show As Boolean = True
            If group IsNot Nothing AndAlso group <> "全部" Then
                show = If(isFolder, group = "文件夹", group = GetFileCategory(tag))
            End If
            If Not show Then
                lstFiles.Items.RemoveAt(i)
                Continue For
            End If
            If isFolder Then folderCount += 1 Else fileCount += 1
            If Not isFolder AndAlso Not String.IsNullOrEmpty(tag) AndAlso File.Exists(tag) Then
                totalSize += New FileInfo(tag).Length
            End If
        Next

        lblFileInfo.Text = $"文件夹 {folderCount} 个 · 文件 {fileCount} 个 · 共 {FormatFileSize(totalSize)}"
    End Sub

    ''' <summary>获取当前选中的第一个路径</summary>
    Private Function GetSelectedPath() As String
        If lstFiles.SelectedItems.Count = 0 Then Return Nothing
        Return lstFiles.SelectedItems(0).Tag?.ToString()
    End Function

    ''' <summary>获取所有选中路径</summary>
    Private Function GetSelectedPaths() As List(Of String)
        Dim result As New List(Of String)()
        For Each item As ListViewItem In lstFiles.SelectedItems
            If item.Tag IsNot Nothing Then result.Add(item.Tag.ToString())
        Next
        Return result
    End Function

    Private Sub BtnFileUp_Click(sender As Object, e As EventArgs)
        Dim parent = Directory.GetParent(_fileCurrentDir)
        If parent IsNot Nothing Then
            LoadFileList(parent.FullName)
        End If
    End Sub

    Private Sub BtnFileRefresh_Click(sender As Object, e As EventArgs)
        LoadFileList(_fileCurrentDir)
    End Sub

    Private Sub CmbFileGroup_Changed(sender As Object, e As EventArgs)
        ' 重新加载以应用筛选
        LoadFileList(_fileCurrentDir)
    End Sub

    Private Sub LstFiles_DoubleClick(sender As Object, e As EventArgs)
        Dim path = GetSelectedPath()
        If String.IsNullOrEmpty(path) Then Return
        If Directory.Exists(path) Then
            LoadFileList(path)
        Else
            BtnFileOpen_Click(sender, e)
        End If
    End Sub

    ''' <summary>ListView 右键点击：选中所指项并显示菜单</summary>
    Private Sub LstFiles_MouseClick(sender As Object, e As MouseEventArgs)
        If e.Button <> MouseButtons.Right Then Return
        Dim hit = lstFiles.HitTest(e.Location)
        If hit.Item IsNot Nothing Then
            ' 选中右键所指项（若未在已选中集合中）
            If Not hit.Item.Selected Then hit.Item.Selected = True
            hit.Item.Focused = True
        End If
    End Sub

    ''' <summary>ListView 键盘快捷键</summary>
    Private Sub LstFiles_KeyDown(sender As Object, e As KeyEventArgs)
        Select Case e.KeyCode
            ' Ctrl+C / Ctrl+X / Ctrl+V：复制/剪切/粘贴
            Case Keys.C
                If e.Control Then
                    BtnFileCopy_Click(sender, e)
                    e.Handled = True
                End If
            Case Keys.X
                If e.Control Then
                    BtnFileCut_Click(sender, e)
                    e.Handled = True
                End If
            Case Keys.V
                If e.Control Then
                    BtnFilePaste_Click(sender, e)
                    e.Handled = True
                End If
            ' Enter：打开文件/进入文件夹
            Case Keys.Enter
                LstFiles_DoubleClick(sender, e)
                e.Handled = True
            ' F2：重命名
            Case Keys.F2
                BtnFileRename_Click(sender, e)
                e.Handled = True
            ' Delete：删除
            Case Keys.Delete
                BtnFileDelete_Click(sender, e)
                e.Handled = True
            ' F5：刷新
            Case Keys.F5
                BtnFileRefresh_Click(sender, e)
                e.Handled = True
            ' Backspace：返回上级
            Case Keys.Back
                BtnFileUp_Click(sender, e)
                e.Handled = True
        End Select
    End Sub

    ''' <summary>查看属性</summary>
    Private Sub BtnFileProperties_Click(sender As Object, e As EventArgs)
        Dim path = GetSelectedPath()
        If String.IsNullOrEmpty(path) Then
            MessageBox.Show("请先选择文件或文件夹", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If
        ShowPropertiesWindow(path)
    End Sub

    ''' <summary>弹出属性窗口，显示文件/文件夹的详细属性</summary>
    Private Sub ShowPropertiesWindow(path As String)
        Try
            Dim props As New List(Of (Label As String, Value As String))
            Dim isDir As Boolean = Directory.Exists(path)

            If isDir Then
                Dim di = New DirectoryInfo(path)
                props.Add(("名称", di.Name))
                props.Add(("类型", "文件夹"))
                props.Add(("位置", di.Parent?.FullName))
                props.Add(("完整路径", di.FullName))
                ' 统计包含的文件与文件夹数量
                Try
                    Dim fileCount = Directory.GetFiles(path).Length
                    Dim dirCount = Directory.GetDirectories(path).Length
                    props.Add(("包含", $"{fileCount} 个文件，{dirCount} 个子文件夹"))
                Catch
                End Try
                props.Add(("创建时间", di.CreationTime.ToString("yyyy-MM-dd HH:mm:ss")))
                props.Add(("修改时间", di.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")))
            ElseIf File.Exists(path) Then
                Dim fi = New FileInfo(path)
                props.Add(("名称", fi.Name))
                props.Add(("类型", GetFileCategory(path)))
                props.Add(("位置", fi.DirectoryName))
                props.Add(("完整路径", fi.FullName))
                props.Add(("大小", $"{FormatFileSize(fi.Length)}（{fi.Length:N0} 字节）"))
                props.Add(("创建时间", fi.CreationTime.ToString("yyyy-MM-dd HH:mm:ss")))
                props.Add(("修改时间", fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")))
                props.Add(("访问时间", fi.LastAccessTime.ToString("yyyy-MM-dd HH:mm:ss")))
                props.Add(("只读", If(fi.IsReadOnly, "是", "否")))
            Else
                MessageBox.Show("目标不存在", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information)
                Return
            End If

            ' 构建属性窗口
            Dim frm As New Form() With {
                .Text = "属性",
                .StartPosition = FormStartPosition.CenterParent,
                .FormBorderStyle = FormBorderStyle.FixedDialog,
                .MaximizeBox = False,
                .MinimizeBox = False,
                .ClientSize = New Size(520, 46 + (props.Count * 30) + 60)
            }

            Dim y As Integer = 16
            For Each p In props
                Dim lblLabel = New Label() With {
                    .Text = p.Label & ":",
                    .Location = New Point(20, y),
                    .AutoSize = True,
                    .ForeColor = Color.Gray,
                    .Font = New Font("Microsoft YaHei", 9)
                }
                frm.Controls.Add(lblLabel)

                Dim lblValue = New Label() With {
                    .Text = p.Value,
                    .Location = New Point(120, y),
                    .AutoSize = False,
                    .Size = New Size(380, 20),
                    .ForeColor = Color.Black,
                    .Font = New Font("Microsoft YaHei", 9)
                }
                frm.Controls.Add(lblValue)
                y += 30
            Next

            Dim btnClose = New Button() With {
                .Text = "关闭",
                .Location = New Point(220, y + 10),
                .Size = New Size(80, 30)
            }
            AddHandler btnClose.Click, Sub() frm.Close()
            frm.Controls.Add(btnClose)
            frm.AcceptButton = btnClose
            frm.CancelButton = btnClose

            frm.ShowDialog(Me)
        Catch ex As Exception
            MessageBox.Show("获取属性失败: " & ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    Private Sub BtnFileOpen_Click(sender As Object, e As EventArgs)
        Dim path = GetSelectedPath()
        If String.IsNullOrEmpty(path) Then
            MessageBox.Show("请先选择文件或文件夹", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If
        Try
            Process.Start(New ProcessStartInfo() With {.FileName = path, .UseShellExecute = True})
        Catch ex As Exception
            MessageBox.Show("打开失败: " & ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    Private Sub BtnFileNewFolder_Click(sender As Object, e As EventArgs)
        Dim name = InputBox("请输入文件夹名称:", "新建文件夹", "新建文件夹")
        If String.IsNullOrWhiteSpace(name) Then Return
        Try
            Dim newDir = Path.Combine(_fileCurrentDir, name)
            If Directory.Exists(newDir) OrElse File.Exists(newDir) Then
                MessageBox.Show("同名文件或文件夹已存在", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                Return
            End If
            Directory.CreateDirectory(newDir)
            LoadFileList(_fileCurrentDir)
        Catch ex As Exception
            MessageBox.Show("创建失败: " & ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    Private Sub BtnFileRename_Click(sender As Object, e As EventArgs)
        Dim target = GetSelectedPath()
        If String.IsNullOrEmpty(target) Then
            MessageBox.Show("请先选择要重命名的文件或文件夹", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If
        Dim oldName = Path.GetFileName(target)
        Dim newName = InputBox("请输入新名称:", "重命名", oldName)
        If String.IsNullOrWhiteSpace(newName) OrElse newName = oldName Then Return
        Try
            Dim newPath = Path.Combine(Path.GetDirectoryName(target), newName)
            If Directory.Exists(target) Then
                Directory.Move(target, newPath)
            Else
                File.Move(target, newPath)
            End If
            LoadFileList(_fileCurrentDir)
        Catch ex As Exception
            MessageBox.Show("重命名失败: " & ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    Private Sub BtnFileDelete_Click(sender As Object, e As EventArgs)
        Dim paths = GetSelectedPaths()
        If paths.Count = 0 Then
            MessageBox.Show("请先选择要删除的文件或文件夹", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If
        If MessageBox.Show($"确定删除选中的 {paths.Count} 个项目吗？此操作不可恢复。", "确认删除",
                           MessageBoxButtons.YesNo, MessageBoxIcon.Warning) <> DialogResult.Yes Then Return
        Try
            For Each p In paths
                If Directory.Exists(p) Then
                    Directory.Delete(p, True)
                ElseIf File.Exists(p) Then
                    File.Delete(p)
                End If
            Next
            AddLog($"删除 {paths.Count} 个项目")
            LoadFileList(_fileCurrentDir)
        Catch ex As Exception
            MessageBox.Show("删除失败: " & ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    Private Sub BtnFileCopy_Click(sender As Object, e As EventArgs)
        Dim paths = GetSelectedPaths()
        If paths.Count = 0 Then
            MessageBox.Show("请先选择要复制的文件或文件夹", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If
        _fileClipboardSource = paths
        _fileClipboardIsCut = False
        AddLog($"已复制 {paths.Count} 个项目到剪贴板")
    End Sub

    Private Sub BtnFileCut_Click(sender As Object, e As EventArgs)
        Dim paths = GetSelectedPaths()
        If paths.Count = 0 Then
            MessageBox.Show("请先选择要剪切的文件或文件夹", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If
        _fileClipboardSource = paths
        _fileClipboardIsCut = True
        AddLog($"已剪切 {paths.Count} 个项目到剪贴板")
    End Sub

    Private Sub BtnFilePaste_Click(sender As Object, e As EventArgs)
        If _fileClipboardSource Is Nothing OrElse _fileClipboardSource.Count = 0 Then
            MessageBox.Show("剪贴板为空，请先复制或剪切", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If
        Try
            For Each src In _fileClipboardSource
                Dim name = Path.GetFileName(src)
                Dim dest = Path.Combine(_fileCurrentDir, name)
                If String.Equals(src, dest, StringComparison.OrdinalIgnoreCase) Then
                    Continue For
                End If
                ' 目标存在则自动重命名
                dest = GetUniquePath(dest)
                If Directory.Exists(src) Then
                    CopyDirectory(src, dest)
                ElseIf File.Exists(src) Then
                    File.Copy(src, dest)
                End If
            Next
            ' 剪切操作：粘贴完成后删除源文件
            If _fileClipboardIsCut Then
                For Each src In _fileClipboardSource
                    Try
                        If Directory.Exists(src) Then
                            Directory.Delete(src, True)
                        ElseIf File.Exists(src) Then
                            File.Delete(src)
                        End If
                    Catch ex As Exception
                        AddLog($"删除源文件失败: {ex.Message}")
                    End Try
                Next
                _fileClipboardSource = Nothing
                _fileClipboardIsCut = False
            End If
            AddLog("粘贴完成")
            LoadFileList(_fileCurrentDir)
        Catch ex As Exception
            MessageBox.Show("粘贴失败: " & ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error)
        End Try
    End Sub

    ''' <summary>文件浏览器剪贴板（本地文件操作）</summary>
    Private _fileClipboardSource As List(Of String) = Nothing
    Private _fileClipboardIsCut As Boolean = False

    ' ================== 我的页：头像 / 昵称 / 设置 ==================

    ''' <summary>修改头像</summary>
    Private Sub BtnChangeAvatar_Click(sender As Object, e As EventArgs)
        Using dlg As New OpenFileDialog() With {
            .Title = "选择头像图片",
            .Filter = "图片文件|*.png;*.jpg;*.jpeg;*.gif;*.bmp"
        }
            If dlg.ShowDialog() <> DialogResult.OK Then Return
            Try
                ' 保存到 config 目录，统一命名为 avatar.png
                Dim src = dlg.FileName
                Dim dest = GetConfigPath(AVATAR_FILE)
                ' 统一转成 PNG 保存
                Using img = Image.FromFile(src)
                    Using bmp = New Bitmap(img)
                        bmp.Save(dest, System.Drawing.Imaging.ImageFormat.Png)
                    End Using
                End Using
                LoadAvatar()
                AddLog("头像已更新")
            Catch ex As Exception
                MessageBox.Show("设置头像失败: " & ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error)
            End Try
        End Using
    End Sub

    ''' <summary>加载头像（无头像时显示默认占位）</summary>
    Private Sub LoadAvatar()
        Try
            Dim avatarPath = GetConfigPath(AVATAR_FILE)
            If File.Exists(avatarPath) Then
                Using img = Image.FromFile(avatarPath)
                    pbAvatar.Image = New Bitmap(img)
                End Using
            Else
                ' 生成默认占位头像（圆形背景 + 首字母）
                pbAvatar.Image = CreateDefaultAvatar()
            End If
        Catch ex As Exception
            AddLog($"加载头像失败: {ex.Message}")
        End Try
    End Sub

    ''' <summary>生成默认占位头像</summary>
    Private Function CreateDefaultAvatar() As Image
        Dim size As Integer = 100
        Dim bmp As New Bitmap(size, size)
        Using g = Graphics.FromImage(bmp)
            g.SmoothingMode = Drawing2D.SmoothingMode.AntiAlias
            g.Clear(Color.WhiteSmoke)
            Using brush As New SolidBrush(Color.FromArgb(0, 123, 255))
                g.FillEllipse(brush, 0, 0, size, size)
            End Using
            Dim s = ReadUserSettings()
            Dim firstChar = If(Not String.IsNullOrEmpty(s.Nickname), s.Nickname.Substring(0, 1), "管")
            Using f As New Font("Microsoft YaHei", 40, FontStyle.Bold)
                Using brush As New SolidBrush(Color.White)
                    Dim sf As New StringFormat() With {.Alignment = StringAlignment.Center, .LineAlignment = StringAlignment.Center}
                    g.DrawString(firstChar, f, brush, New RectangleF(0, 0, size, size), sf)
                End Using
            End Using
        End Using
        Return bmp
    End Function

    ''' <summary>加载「我的」页数据（昵称、账号、头像）</summary>
    Private Sub LoadMinePageData()
        Dim s = ReadUserSettings()
        lblNickname.Text = s.Nickname
        txtNickname.Text = s.Nickname
        txtUsername.Text = s.Username
        txtPassword.Text = ""   ' 出于安全，不回显密码
        LoadAvatar()
    End Sub

    ''' <summary>保存昵称</summary>
    Private Sub BtnSaveNickname_Click(sender As Object, e As EventArgs)
        Dim nickname = txtNickname.Text.Trim()
        If String.IsNullOrEmpty(nickname) Then
            MessageBox.Show("昵称不能为空", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            Return
        End If
        Dim s = ReadUserSettings()
        SaveUserSettings(nickname, s.Username, s.Password)
        lblNickname.Text = nickname
        LoadAvatar()
        AddLog($"昵称已更新: {nickname}")
        MessageBox.Show("昵称已保存", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information)
    End Sub

    ''' <summary>校验密码强度：数字、字母、符号至少包含两种</summary>
    Private Function ValidatePassword(password As String) As Boolean
        Dim hasDigit As Boolean = False
        Dim hasLetter As Boolean = False
        Dim hasSymbol As Boolean = False
        For Each c As Char In password
            If Char.IsDigit(c) Then
                hasDigit = True
            ElseIf Char.IsLetter(c) Then
                hasLetter = True
            Else
                hasSymbol = True
            End If
        Next
        Dim kinds As Integer = If(hasDigit, 1, 0) + If(hasLetter, 1, 0) + If(hasSymbol, 1, 0)
        Return kinds >= 2
    End Function

    ''' <summary>保存账号密码（密码强度校验）</summary>
    Private Sub BtnSaveAccount_Click(sender As Object, e As EventArgs)
        Dim username = txtUsername.Text.Trim()
        Dim password = txtPassword.Text
        If String.IsNullOrEmpty(username) Then
            MessageBox.Show("账号不能为空", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            Return
        End If
        If String.IsNullOrEmpty(password) Then
            MessageBox.Show("密码不能为空", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            Return
        End If
        If password.Length < 6 Then
            MessageBox.Show("密码长度至少 6 位", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            Return
        End If
        If Not ValidatePassword(password) Then
            MessageBox.Show("密码需包含数字、字母、符号中的至少两种", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            Return
        End If
        Dim s = ReadUserSettings()
        SaveUserSettings(s.Nickname, username, password)
        txtPassword.Text = ""
        AddLog($"账号密码已更新: 账号={username}")
        MessageBox.Show("账号密码已保存", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information)
    End Sub

    ''' <summary>显示/隐藏密码</summary>
    Private Sub ChkShowPassword_Changed(sender As Object, e As EventArgs)
        txtPassword.UseSystemPasswordChar = Not chkShowPassword.Checked
    End Sub

    ''' <summary>生成随机密码（数字+字母+符号混合，满足至少两种类型）</summary>
    Private Function GenerateRandomPassword(length As Integer) As String
        Const digits As String = "0123456789"
        Const letters As String = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ"
        Const symbols As String = "!@#$%^&*()-_=+[]{};:,.<>?"
        Dim all As String = digits & letters & symbols
        Dim rng As New Random()
        Dim sb As New StringBuilder()
        ' 先保证至少包含字母和数字各一个
        sb.Append(letters(rng.Next(letters.Length)))
        sb.Append(digits(rng.Next(digits.Length)))
        For i As Integer = 0 To length - 3
            sb.Append(all(rng.Next(all.Length)))
        Next
        ' 打乱顺序
        Dim arr As Char() = sb.ToString().ToCharArray()
        For i As Integer = arr.Length - 1 To 1 Step -1
            Dim j As Integer = rng.Next(i + 1)
            Dim tmp As Char = arr(i)
            arr(i) = arr(j)
            arr(j) = tmp
        Next
        Return New String(arr)
    End Function

    ''' <summary>重置密码（需电脑 PIN 码，支持随机生成或人工设置）</summary>
    Private Sub BtnResetSettings_Click(sender As Object, e As EventArgs)
        Using frm As New Form() With {
            .Text = "重置密码",
            .StartPosition = FormStartPosition.CenterParent,
            .FormBorderStyle = FormBorderStyle.FixedDialog,
            .MaximizeBox = False,
            .MinimizeBox = False,
            .ClientSize = New Size(380, 300)
        }
            Dim y As Integer = 16

            ' PIN 码
            Dim lblPin = New Label() With {.Text = "电脑登录 PIN 码:", .Location = New Point(20, y), .AutoSize = True}
            frm.Controls.Add(lblPin)
            Dim txtPin = New TextBox() With {.Location = New Point(140, y - 3), .Width = 160, .UseSystemPasswordChar = True}
            frm.Controls.Add(txtPin)
            y += 40

            ' 新密码
            Dim lblNew = New Label() With {.Text = "新密码:", .Location = New Point(20, y), .AutoSize = True}
            frm.Controls.Add(lblNew)
            Dim txtNew = New TextBox() With {.Location = New Point(140, y - 3), .Width = 160, .UseSystemPasswordChar = True}
            frm.Controls.Add(txtNew)
            y += 40

            ' 确认密码
            Dim lblConfirm = New Label() With {.Text = "确认新密码:", .Location = New Point(20, y), .AutoSize = True}
            frm.Controls.Add(lblConfirm)
            Dim txtConfirm = New TextBox() With {.Location = New Point(140, y - 3), .Width = 160, .UseSystemPasswordChar = True}
            frm.Controls.Add(txtConfirm)
            y += 40

            ' 随机生成按钮（仅 PIN 正确时启用）
            Dim btnRandom = New Button() With {.Text = "随机生成", .Location = New Point(140, y - 3), .Width = 90, .Enabled = False}
            frm.Controls.Add(btnRandom)
            AddHandler btnRandom.Click, Sub()
                                            Dim pwd = GenerateRandomPassword(10)
                                            txtNew.Text = pwd
                                            txtConfirm.Text = pwd
                                        End Sub
            y += 44

            ' PIN 实时校验：正确则启用随机生成按钮
            AddHandler txtPin.TextChanged, Sub()
                                               btnRandom.Enabled = txtPin.Text.Trim() = RESET_PIN
                                           End Sub

            ' 显示密码
            Dim chkShow = New CheckBox() With {.Text = "显示密码", .Location = New Point(20, y), .AutoSize = True}
            frm.Controls.Add(chkShow)
            AddHandler chkShow.CheckedChanged, Sub()
                                                   txtNew.UseSystemPasswordChar = Not chkShow.Checked
                                                   txtConfirm.UseSystemPasswordChar = Not chkShow.Checked
                                               End Sub
            y += 36

            ' 确定 / 取消
            Dim btnOk = New Button() With {.Text = "确认重置", .Location = New Point(120, y), .Width = 100}
            Dim btnCancel = New Button() With {.Text = "取消", .Location = New Point(230, y), .Width = 80}
            frm.Controls.Add(btnOk)
            frm.Controls.Add(btnCancel)
            AddHandler btnCancel.Click, Sub() frm.DialogResult = DialogResult.Cancel

            ' 确定按钮逻辑
            Dim newPassword As String = ""
            AddHandler btnOk.Click, Sub()
                                        Dim pin = txtPin.Text.Trim()
                                        If String.IsNullOrEmpty(pin) Then
                                            MessageBox.Show("请输入电脑登录 PIN 码", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information)
                                            Return
                                        End If
                                        If pin <> RESET_PIN Then
                                            MessageBox.Show("PIN 码错误", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                                            Return
                                        End If
                                        Dim pwd = txtNew.Text
                                        Dim confirm = txtConfirm.Text
                                        If String.IsNullOrEmpty(pwd) Then
                                            MessageBox.Show("请输入新密码，或点击「随机生成」", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information)
                                            Return
                                        End If
                                        If pwd.Length < 6 Then
                                            MessageBox.Show("密码长度至少 6 位", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                                            Return
                                        End If
                                        If Not ValidatePassword(pwd) Then
                                            MessageBox.Show("密码需包含数字、字母、符号中的至少两种", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                                            Return
                                        End If
                                        If pwd <> confirm Then
                                            MessageBox.Show("两次输入的密码不一致", "提示", MessageBoxButtons.OK, MessageBoxIcon.Warning)
                                            Return
                                        End If
                                        newPassword = pwd
                                        frm.DialogResult = DialogResult.OK
                                    End Sub

            If frm.ShowDialog(Me) = DialogResult.OK Then
                Dim s = ReadUserSettings()
                SaveUserSettings(s.Nickname, s.Username, newPassword)
                txtPassword.Text = ""
                LoadMinePageData()
                AddLog($"密码已重置: 账号={s.Username}")
                MessageBox.Show($"新密码: {newPassword}", "密码已重置", MessageBoxButtons.OK, MessageBoxIcon.Information)
            End If
        End Using
    End Sub

    ''' <summary>刷新传输记录列表</summary>
    Private Sub RefreshTransferList()
        If lstTransfer Is Nothing Then Return
        lstTransfer.Items.Clear()

        Dim total As Integer
        SyncLock _transferLock
            total = _transferHistory.Count
        End SyncLock

        ' 计算分页范围
        Dim totalPages As Integer = Math.Max(1, CInt(Math.Ceiling(total / CDbl(_transferPageSize))))
        If _transferPageIndex >= totalPages Then _transferPageIndex = totalPages - 1
        If _transferPageIndex < 0 Then _transferPageIndex = 0
        Dim start As Integer = _transferPageIndex * _transferPageSize
        Dim endIndex As Integer = Math.Min(start + _transferPageSize, total)

        SyncLock _transferLock
            For i As Integer = start To endIndex - 1
                Dim item = _transferHistory(i)
                Dim lv = New ListViewItem((i + 1).ToString()) With {.Tag = item.Id}
                lv.SubItems.Add(item.FileName)
                lv.SubItems.Add(If(item.Direction = "upload", "上传", "下载"))
                lv.SubItems.Add(item.Time.ToString("yyyy-MM-dd HH:mm:ss"))
                lstTransfer.Items.Add(lv)
            Next
        End SyncLock

        ' 更新分页信息与按钮状态
        If lblTransferPageInfo IsNot Nothing Then
            lblTransferPageInfo.Text = $"共 {total} 条 · 第 {_transferPageIndex + 1}/{totalPages} 页"
        End If
        If btnTransferPrev IsNot Nothing Then btnTransferPrev.Enabled = _transferPageIndex > 0
        If btnTransferNext IsNot Nothing Then btnTransferNext.Enabled = _transferPageIndex < totalPages - 1
        If btnTransferDelete IsNot Nothing Then btnTransferDelete.Enabled = False
    End Sub

    ''' <summary>刷新传输记录按钮</summary>
    Private Sub BtnTransferRefresh_Click(sender As Object, e As EventArgs)
        _transferPageIndex = 0
        RefreshTransferList()
    End Sub

    ''' <summary>勾选状态变化时更新删除按钮启用状态</summary>
    Private Sub LstTransfer_ItemChecked(sender As Object, e As ItemCheckedEventArgs)
        Dim checkedCount As Integer = 0
        For Each lv As ListViewItem In lstTransfer.Items
            If lv.Checked Then checkedCount += 1
        Next
        If btnTransferDelete IsNot Nothing Then btnTransferDelete.Enabled = checkedCount > 0
    End Sub

    ''' <summary>每页条数变化</summary>
    Private Sub CmbTransferPageSize_Changed(sender As Object, e As EventArgs)
        If cmbTransferPageSize.SelectedItem IsNot Nothing Then
            Dim v As Integer = 10
            If Integer.TryParse(cmbTransferPageSize.SelectedItem.ToString(), v) Then
                _transferPageSize = v
                _transferPageIndex = 0
                RefreshTransferList()
            End If
        End If
    End Sub

    ''' <summary>上一页</summary>
    Private Sub BtnTransferPrev_Click(sender As Object, e As EventArgs)
        If _transferPageIndex > 0 Then
            _transferPageIndex -= 1
            RefreshTransferList()
        End If
    End Sub

    ''' <summary>下一页</summary>
    Private Sub BtnTransferNext_Click(sender As Object, e As EventArgs)
        Dim total As Integer
        SyncLock _transferLock
            total = _transferHistory.Count
        End SyncLock
        Dim totalPages As Integer = Math.Max(1, CInt(Math.Ceiling(total / CDbl(_transferPageSize))))
        If _transferPageIndex < totalPages - 1 Then
            _transferPageIndex += 1
            RefreshTransferList()
        End If
    End Sub

    ''' <summary>删除勾选的传输记录</summary>
    Private Sub BtnTransferDelete_Click(sender As Object, e As EventArgs)
        ' 收集勾选的记录 ID
        Dim ids As New List(Of String)
        For Each lv As ListViewItem In lstTransfer.Items
            If lv.Checked AndAlso lv.Tag IsNot Nothing Then
                ids.Add(lv.Tag.ToString())
            End If
        Next
        If ids.Count = 0 Then
            MessageBox.Show("请先勾选要删除的记录", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information)
            Return
        End If
        If MessageBox.Show($"确定删除勾选的 {ids.Count} 条记录吗？", "确认删除",
                           MessageBoxButtons.YesNo, MessageBoxIcon.Warning) <> DialogResult.Yes Then Return
        Dim removed As Integer = 0
        SyncLock _transferLock
            For Each id In ids
                Dim idx As Integer = _transferHistory.FindIndex(Function(t) t.Id = id)
                If idx >= 0 Then
                    _transferHistory.RemoveAt(idx)
                    removed += 1
                End If
            Next
        End SyncLock
        If removed > 0 Then
            SaveTransferConfig()
            AddLog($"[传输记录] 删除 {removed} 条记录")
        End If
        RefreshTransferList()
    End Sub

    ''' <summary>
    ''' 线程安全的日志记录
    ''' </summary>
    Private Sub AddLog(message As String)
        If Me.InvokeRequired Then
            Me.Invoke(Sub() AddLog(message))
            Return
        End If
        Dim timeStr As String = DateTime.Now.ToString("HH:mm:ss")
        txtLog.AppendText($"[{timeStr}] {message}" & Environment.NewLine)
        txtLog.SelectionStart = txtLog.TextLength
        txtLog.ScrollToCaret()
    End Sub

    ''' <summary>
    ''' 清除日志按钮点击事件
    ''' </summary>
    Private Sub BtnClearLog_Click(sender As Object, e As EventArgs)
        txtLog.Clear()
    End Sub
    ''' <summary>
    ''' 保存日志到文件（程序目录下，文件名包含时间）
    ''' </summary>
    Private Sub SaveLogToFile()
        Try
            Dim logContent As String = txtLog.Text
            If String.IsNullOrWhiteSpace(logContent) Then
                Return
            End If
            Dim fileName As String = "Log_" & DateTime.Now.ToString("yyyyMMdd_HHmmss") & ".txt"
            Dim filePath As String = Path.Combine(Application.StartupPath, "Log", fileName)
            '自动创建目录
            If Not IO.Directory.Exists(IO.Path.GetDirectoryName(filePath)) Then
                IO.Directory.CreateDirectory(IO.Path.GetDirectoryName(filePath))
            End If
            File.WriteAllText(filePath, logContent, Encoding.UTF8)
            ' 可选：在界面上短暂提示
            AutoWindow($"日志已保存到：{fileName}", 0.5, "日志保存")
        Catch ex As Exception
            Console.WriteLine($"保存日志失败: {ex.Message}")
        End Try
    End Sub
    ''' <summary>
    ''' 配置防火墙规则（Windows防火墙）
    ''' </summary>
    Private Function ConfigureFirewallRule(port As Integer) As Boolean
        Try
            Dim ruleName As String = $"VB_FileTransfer_Port_{port}"
            Dim command As String = $"netsh advfirewall firewall show rule name=""{ruleName}"" >nul 2>&1"

            ' 检查规则是否已存在
            Dim checkProcess As New Process() With {
                .StartInfo = New ProcessStartInfo() With {
                    .FileName = "cmd.exe",
                    .Arguments = $"/c {command}",
                    .UseShellExecute = False,
                    .CreateNoWindow = True,
                    .RedirectStandardOutput = True
                }
            }
            checkProcess.Start()
            checkProcess.WaitForExit()

            ' 如果规则不存在，则创建
            If checkProcess.ExitCode <> 0 Then
                Dim createCommand As String = $"netsh advfirewall firewall add rule name=""{ruleName}"" dir=in action=allow protocol=TCP localport={port}"

                Dim createProcess As New Process() With {
                    .StartInfo = New ProcessStartInfo() With {
                        .FileName = "cmd.exe",
                        .Arguments = $"/c {createCommand}",
                        .UseShellExecute = False,
                        .CreateNoWindow = True,
                        .Verb = "runas" ' 以管理员身份运行
                    }
                }
                createProcess.Start()
                createProcess.WaitForExit()

                If createProcess.ExitCode = 0 Then
                    AutoWindow($"已自动添加防火墙规则，允许端口 {port} 的入站连接", 2, "防火墙配置")
                    Return True
                Else
                    Return False
                End If
            Else
                ' 规则已存在
                Return True
            End If
        Catch ex As Exception
            Console.WriteLine("防火墙配置失败: " & ex.Message)
            Return False
        End Try
    End Function

    ''' <summary>
    ''' 更新UI状态（启用/禁用控件）
    ''' </summary>
    Private Sub UpdateUIState(running As Boolean)
        isRunning = running
        btnStart.Enabled = Not running
        btnStop.Enabled = running
        txtIp.Enabled = Not running
        txtPort.Enabled = Not running

        ' ngrok 按钮：服务运行时可用；或 ngrok 已在运行（自动监听）时可手动关闭
        If btnNgrok IsNot Nothing Then
            btnNgrok.Enabled = running OrElse isNgrokRunning
        End If

        If running Then
            lblStatus.Text = "✅ 服务运行中"
            lblStatus.ForeColor = Color.Green
        Else
            lblStatus.Text = "⏹ 服务未运行"
            lblStatus.ForeColor = Color.Gray
            If pbQrCode IsNot Nothing AndAlso pbQrCode.Image IsNot Nothing Then
                pbQrCode.Image.Dispose()
                pbQrCode.Image = Nothing
            End If
            If lblLanHint IsNot Nothing Then
                lblLanHint.ForeColor = Color.Gray
                lblLanHint.Text = "局域网二维码（未启动）"
            End If
        End If
    End Sub

    ' --- 业务逻辑 ---

    Private Sub BtnStart_Click(sender As Object, e As EventArgs)
        ' 如果当前正在等待第一个连接，则停止监听线程
        If listenForFirstConnection Then
            _stopWaiting = True
            If tcpListener IsNot Nothing Then
                tcpListener.Stop()
                tcpListener = Nothing
            End If
            If waitThread IsNot Nothing AndAlso waitThread.IsAlive Then
                waitThread.Join(2000)
                waitThread = Nothing
            End If
            listenForFirstConnection = False
        End If

        ' 如果服务已经在运行，则不做任何事
        If isRunning Then Return

        ' 手动启动服务
        Dim ip As String = txtIp.Text.Trim()
        Dim portStr As String = txtPort.Text.Trim()
        If String.IsNullOrEmpty(ip) OrElse String.IsNullOrEmpty(portStr) Then
            MessageBox.Show("IP和端口不能为空", "错误", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            Return
        End If
        Dim port As Integer
        If Not Integer.TryParse(portStr, port) Then
            MessageBox.Show("端口必须是数字", "错误", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            Return
        End If

        Try
            If Not ConfigureFirewallRule(port) Then
                MessageBox.Show("防火墙规则配置失败，服务可能无法被外部访问。", "警告", MessageBoxButtons.OK, MessageBoxIcon.Warning)
            End If

            listener = New HttpListener()
            listener.Prefixes.Add($"http://*:{port}/")
            listener.Start()

            isRunning = True
            isServiceRunning = True
            UpdateUIState(True)

            listenThread = New Threading.Thread(AddressOf ListenLoop) With {.IsBackground = True}
            listenThread.Start()
            AddLog($"服务手动启动，监听端口 {port}，共享文件夹: {SharedFolderPath}")
            GenerateQrCode(ip, port)
            EnsureRelayCore()

            ' 启动空闲检测
            lastActivityTime = DateTime.Now
            StartInactivityTimer()
        Catch ex As Exception
            MessageBox.Show("启动失败: " & ex.Message, "错误", MessageBoxButtons.OK, MessageBoxIcon.Error)
            UpdateUIState(False)
        End Try
    End Sub

    ' 修改原有 btnStop_Click，支持保持监听模式
    Private Sub BtnStop_Click(sender As Object, e As EventArgs)
        ' 停止服务时：处于外网自动监听时间段内则不关闭 ngrok（保持外网隧道监听），
        ' 时间段外才关闭 ngrok
        If isNgrokRunning AndAlso Not IsInNgrokTimeWindow() Then
            StopNgrok()
        End If

        StopService()
        ' 停止后自动进入待机监听
        StartListeningForFirstConnection()
    End Sub

    Private Sub BtnOpenFolder_Click(sender As Object, e As EventArgs)
        ' 【修改点5】打开程序目录下的 Shared 文件夹
        If Directory.Exists(SharedFolderPath) Then
            Process.Start("explorer.exe", SharedFolderPath)
        Else
            MessageBox.Show("共享文件夹不存在，请先启动服务并上传一个文件。", "提示", MessageBoxButtons.OK, MessageBoxIcon.Information)
        End If
    End Sub

    ''' <summary>
    ''' 监听循环
    ''' </summary>
    Private Sub ListenLoop()
        While isRunning
            Try
                Dim context As HttpListenerContext = listener.GetContext()
                ' 每个请求新开线程处理，避免阻塞
                Dim t As New Threading.Thread(Sub() HandleRequest(context)) With {.IsBackground = True}
                t.Start()
            Catch ex As Exception
                ' 停止时会抛出异常，退出循环
                Exit While
            End Try
        End While
    End Sub

    ' ================== 文件夹管理、登录、上传目标路径相关 ==================
    ' token -> 过期时间（3 分钟后自动注销）
    Private Shared ReadOnly validTokens As New Dictionary(Of String, DateTime)
    Private Shared ReadOnly tokenLocker As New Object()
    Private Const TOKEN_TIMEOUT_MINUTES As Integer = 3

    ''' <summary>获取所有子文件夹列表（相对路径）</summary>
    Private Sub GetFoldersList(res As HttpListenerResponse)
        Try
            Dim folders As New List(Of String) From {""}   ' 根目录
            If Directory.Exists(SharedFolderPath) Then
                CollectSubFolders(SharedFolderPath, "", folders)
            End If
            Dim json As String = JsonArrayToString(folders)

            AddLog($"GetFoldersList 返回: {json}")

            SendJsonResponse(res, json, 200)

            AddLog("文件夹列表获取成功，共 " & (folders.Count - 1) & " 个子文件夹")
        Catch ex As Exception
            AddLog($"GetFoldersList 异常: {ex}")

            SendJsonError(res, "获取文件夹列表失败: " & ex.Message, 500)
        End Try
    End Sub

    Private Sub CollectSubFolders(parentPath As String, relativeParent As String, ByRef list As List(Of String))
        Try
            For Each dir As String In Directory.GetDirectories(parentPath)
                Dim dirName As String = Path.GetFileName(dir)
                Dim relativePath As String = If(String.IsNullOrEmpty(relativeParent), dirName, relativeParent & "/" & dirName)
                list.Add(relativePath)
                CollectSubFolders(dir, relativePath, list)
            Next
        Catch ex As Exception
            ' 忽略无法访问的文件夹
        End Try
    End Sub

    Private Function JsonArrayToString(arr As List(Of String)) As String
        Dim sb As New StringBuilder()
        sb.Append("[")
        For i As Integer = 0 To arr.Count - 1
            sb.Append("""" & EscapeJsonString(arr(i)) & """")
            If i < arr.Count - 1 Then sb.Append(",")
        Next
        sb.Append("]")
        Return sb.ToString()
    End Function

    ''' <summary>登录验证</summary>
    Private Sub HandleLogin(req As HttpListenerRequest, res As HttpListenerResponse)
        Try
            Dim body As String
            Using reader As New StreamReader(req.InputStream, req.ContentEncoding)
                body = reader.ReadToEnd()
            End Using
            AddLog($"收到登录请求，原始数据: {body}")   ' 注意：不要记录密码明文，这里仅用于调试
            Dim params = ParseFormUrlEncoded(body)
            Dim username = params.GetValueOrDefault("username", "")
            Dim password = params.GetValueOrDefault("password", "")
            AddLog($"登录尝试: 用户名={username}")
            ' 使用可配置的账号密码（默认 admin/admin）
            Dim s = ReadUserSettings()
            If username = s.Username AndAlso password = s.Password Then
                Dim token = Guid.NewGuid().ToString()
                SyncLock tokenLocker
                    validTokens(token) = DateTime.Now.AddMinutes(TOKEN_TIMEOUT_MINUTES)
                End SyncLock
                Dim json As String = "{""success"":true,""token"":""" & token & """,""nickname"":""" & EscapeJsonString(s.Nickname) & """}"
                SendJsonResponse(res, json, 200)
                AddLog($"用户 {username} 登录成功 (token {TOKEN_TIMEOUT_MINUTES} 分钟后过期)")
            Else
                AddLog($"用户 {username} 登录失败：密码错误")
                SendJsonError(res, "用户名或密码错误", 401)
            End If
        Catch ex As Exception
            AddLog($"HandleLogin 异常: {ex}")
            SendJsonError(res, "登录处理异常: " & ex.Message, 500)
        End Try
    End Sub

    ''' <summary>注销</summary>
    Private Sub HandleLogout(req As HttpListenerRequest, res As HttpListenerResponse)
        Dim token As String = req.Headers("X-Auth-Token")
        If Not String.IsNullOrEmpty(token) Then
            SyncLock tokenLocker
                validTokens.Remove(token)
            End SyncLock
        End If
        SendJsonResponse(res, "{""success"":true}", 200)
    End Sub
    Private Sub HandleCreateFolder(req As HttpListenerRequest, res As HttpListenerResponse)
        If Not IsAuthenticated(req) Then
            AddLog("新建文件夹请求未通过身份验证")
            SendJsonError(res, "未登录或登录已过期", 401)
            Return
        End If
        Try
            Dim body As String
            Using reader As New StreamReader(req.InputStream, req.ContentEncoding)
                body = reader.ReadToEnd()
            End Using
            AddLog($"收到新建文件夹请求，原始数据: {body}")
            Dim params = ParseFormUrlEncoded(body)
            Dim parentPath = params.GetValueOrDefault("parentPath", "")
            Dim folderName = params.GetValueOrDefault("folderName", "")

            If String.IsNullOrEmpty(folderName) Then
                SendJsonError(res, "文件夹名称不能为空", 400)
                Return
            End If
            ' 安全检查
            If folderName.Contains("..") OrElse folderName.Contains("\") OrElse folderName.Contains("/") Then
                SendJsonError(res, "无效的文件夹名称", 400)
                Return
            End If

            ' 计算目标路径
            Dim targetDir As String
            If String.IsNullOrEmpty(parentPath) Then
                targetDir = SharedFolderPath
            Else
                ' 将前端传来的相对路径（使用正斜杠）转换为 Windows 路径
                Dim cleanParent = parentPath.Replace("/", "\").TrimEnd("\"c)
                targetDir = Path.Combine(SharedFolderPath, cleanParent)
            End If
            Dim newDir As String = Path.Combine(targetDir, folderName)

            AddLog($"目标父文件夹: {targetDir}")
            AddLog($"新文件夹完整路径: {newDir}")

            ' 确保父文件夹存在（理论上应该存在）
            If Not Directory.Exists(targetDir) Then
                AddLog($"父文件夹不存在，尝试创建: {targetDir}")
                Directory.CreateDirectory(targetDir)
            End If

            If Directory.Exists(newDir) Then
                SendJsonError(res, "文件夹已存在", 400)
                Return
            End If

            Directory.CreateDirectory(newDir)
            AddLog($"文件夹创建成功: {newDir}")

            SendJsonResponse(res, "{""success"":true}", 200)
        Catch ex As Exception
            AddLog($"HandleCreateFolder 异常: {ex}")
            SendJsonError(res, "创建文件夹失败: " & ex.Message, 500)
        End Try
    End Sub

    ''' <summary>删除文件或文件夹（需管理员认证）</summary>
    Private Sub HandleDelete(req As HttpListenerRequest, res As HttpListenerResponse)
        If Not IsAuthenticated(req) Then
            AddLog("删除请求未通过身份验证")
            SendJsonError(res, "未登录或登录已过期", 401)
            Return
        End If
        Try
            Dim body As String
            Using reader As New StreamReader(req.InputStream, req.ContentEncoding)
                body = reader.ReadToEnd()
            End Using
            Dim params = ParseFormUrlEncoded(body)
            Dim targetPath = params.GetValueOrDefault("path", "")
            Dim isAdmin = params.GetValueOrDefault("isAdmin", "false") = "true"

            If String.IsNullOrEmpty(targetPath) Then
                SendJsonError(res, "路径不能为空", 400)
                Return
            End If
            ' 安全检查：禁止路径遍历
            If targetPath.Contains("..") OrElse targetPath.Contains("::") Then
                SendJsonError(res, "非法路径", 403)
                Return
            End If

            ' 计算实际删除路径
            Dim fullPath As String
            If isAdmin Then
                ' 管理员全盘浏览：使用绝对路径
                fullPath = Uri.UnescapeDataString(targetPath)
            Else
                ' 普通浏览：相对于共享文件夹
                Dim cleanPath = targetPath.Replace("/", "\").TrimStart("\"c)
                fullPath = Path.Combine(SharedFolderPath, cleanPath)
            End If
            fullPath = Path.GetFullPath(fullPath)

            ' 防止删除共享目录本身或越界
            Dim sharedRoot As String = Path.GetFullPath(SharedFolderPath).TrimEnd("\"c) & "\"
            If Not isAdmin AndAlso Not fullPath.StartsWith(sharedRoot, StringComparison.OrdinalIgnoreCase) Then
                SendJsonError(res, "越界路径", 403)
                Return
            End If

            AddLog($"删除请求: {fullPath} (isAdmin={isAdmin})")

            If Directory.Exists(fullPath) Then
                Directory.Delete(fullPath, True)
                AddLog($"文件夹已删除: {fullPath}")
            ElseIf File.Exists(fullPath) Then
                File.Delete(fullPath)
                AddLog($"文件已删除: {fullPath}")
            Else
                SendJsonError(res, "目标不存在", 404)
                Return
            End If

            SendJsonResponse(res, "{""success"":true}", 200)
        Catch ex As Exception
            AddLog($"HandleDelete 异常: {ex}")
            SendJsonError(res, "删除失败: " & ex.Message, 500)
        End Try
    End Sub

    ' ================== 文件管理（重命名/复制/剪切/粘贴） ==================

    ''' <summary>重命名文件或文件夹（需认证）</summary>
    Private Sub HandleRename(req As HttpListenerRequest, res As HttpListenerResponse)
        If Not IsAuthenticated(req) Then
            SendJsonError(res, "未登录或登录已过期", 401)
            Return
        End If
        Try
            Dim body As String
            Using reader As New StreamReader(req.InputStream, req.ContentEncoding)
                body = reader.ReadToEnd()
            End Using
            Dim params = ParseFormUrlEncoded(body)
            Dim targetPath = params.GetValueOrDefault("path", "")
            Dim newName = params.GetValueOrDefault("newName", "")
            Dim isAdmin = params.GetValueOrDefault("isAdmin", "false") = "true"

            If String.IsNullOrEmpty(targetPath) OrElse String.IsNullOrEmpty(newName) Then
                SendJsonError(res, "参数不完整", 400)
                Return
            End If
            If newName.Contains("\") OrElse newName.Contains("/") OrElse newName.Contains("..") Then
                SendJsonError(res, "非法名称", 400)
                Return
            End If
            If targetPath.Contains("..") Then
                SendJsonError(res, "非法路径", 403)
                Return
            End If

            Dim fullPath As String = If(isAdmin, Uri.UnescapeDataString(targetPath), Path.Combine(SharedFolderPath, targetPath.Replace("/", "\").TrimStart("\"c)))
            fullPath = Path.GetFullPath(fullPath)

            Dim dir As String = Path.GetDirectoryName(fullPath)
            Dim newFullPath As String = Path.Combine(dir, newName)

            If File.Exists(fullPath) Then
                File.Move(fullPath, newFullPath)
                AddLog($"文件重命名: {fullPath} -> {newName}")
            ElseIf Directory.Exists(fullPath) Then
                Directory.Move(fullPath, newFullPath)
                AddLog($"文件夹重命名: {fullPath} -> {newName}")
            Else
                SendJsonError(res, "目标不存在", 404)
                Return
            End If
            SendJsonResponse(res, "{""success"":true}", 200)
        Catch ex As Exception
            AddLog($"HandleRename 异常: {ex}")
            SendJsonError(res, "重命名失败: " & ex.Message, 500)
        End Try
    End Sub

    ''' <summary>复制或剪切（记录到剪贴板）</summary>
    Private Sub HandleCopyMove(req As HttpListenerRequest, res As HttpListenerResponse, isCut As Boolean)
        If Not IsAuthenticated(req) Then
            SendJsonError(res, "未登录或登录已过期", 401)
            Return
        End If
        Try
            Dim body As String
            Using reader As New StreamReader(req.InputStream, req.ContentEncoding)
                body = reader.ReadToEnd()
            End Using
            Dim params = ParseFormUrlEncoded(body)
            Dim targetPath = params.GetValueOrDefault("path", "")
            Dim isAdmin = params.GetValueOrDefault("isAdmin", "false") = "true"

            If String.IsNullOrEmpty(targetPath) Then
                SendJsonError(res, "路径不能为空", 400)
                Return
            End If

            Dim fullPath As String = If(isAdmin, Uri.UnescapeDataString(targetPath), Path.Combine(SharedFolderPath, targetPath.Replace("/", "\").TrimStart("\"c)))
            fullPath = Path.GetFullPath(fullPath)

            SyncLock _clipboardLock
                _clipboardPath = fullPath
                _clipboardIsCut = isCut
            End SyncLock

            AddLog($"{If(isCut, "剪切", "复制")}: {fullPath}")
            SendJsonResponse(res, "{""success"":true}", 200)
        Catch ex As Exception
            AddLog($"HandleCopyMove 异常: {ex}")
            SendJsonError(res, "操作失败: " & ex.Message, 500)
        End Try
    End Sub

    ''' <summary>粘贴（将剪贴板内容复制/移动到目标目录）</summary>
    Private Sub HandlePaste(req As HttpListenerRequest, res As HttpListenerResponse)
        If Not IsAuthenticated(req) Then
            SendJsonError(res, "未登录或登录已过期", 401)
            Return
        End If
        Try
            Dim body As String
            Using reader As New StreamReader(req.InputStream, req.ContentEncoding)
                body = reader.ReadToEnd()
            End Using
            Dim params = ParseFormUrlEncoded(body)
            Dim destDir = params.GetValueOrDefault("destDir", "")
            Dim isAdmin = params.GetValueOrDefault("isAdmin", "false") = "true"

            Dim srcPath As String
            Dim isCut As Boolean
            SyncLock _clipboardLock
                srcPath = _clipboardPath
                isCut = _clipboardIsCut
            End SyncLock

            If String.IsNullOrEmpty(srcPath) Then
                SendJsonError(res, "剪贴板为空，请先复制或剪切", 400)
                Return
            End If

            ' 目标目录
            Dim fullDest As String = If(isAdmin, Uri.UnescapeDataString(destDir), Path.Combine(SharedFolderPath, destDir.Replace("/", "\").TrimStart("\"c)))
            fullDest = Path.GetFullPath(fullDest)
            If Not Directory.Exists(fullDest) Then
                SendJsonError(res, "目标目录不存在", 404)
                Return
            End If

            Dim srcName As String = Path.GetFileName(srcPath)
            Dim destFull As String = Path.Combine(fullDest, srcName)

            ' 避免复制/移动到自身
            If String.Equals(srcPath, destFull, StringComparison.OrdinalIgnoreCase) Then
                SendJsonError(res, "不能粘贴到原位置", 400)
                Return
            End If

            If isCut Then
                If File.Exists(srcPath) Then
                    If File.Exists(destFull) Then File.Delete(destFull)
                    File.Move(srcPath, destFull)
                ElseIf Directory.Exists(srcPath) Then
                    If Directory.Exists(destFull) Then Directory.Delete(destFull, True)
                    Directory.Move(srcPath, destFull)
                End If
                SyncLock _clipboardLock
                    _clipboardPath = ""
                    _clipboardIsCut = False
                End SyncLock
                AddLog($"剪切粘贴: {srcPath} -> {destFull}")
            Else
                If File.Exists(srcPath) Then
                    If File.Exists(destFull) Then
                        ' 同名文件，追加 "(1)"
                        destFull = GetUniquePath(destFull)
                    End If
                    File.Copy(srcPath, destFull)
                ElseIf Directory.Exists(srcPath) Then
                    If Directory.Exists(destFull) Then
                        destFull = GetUniquePath(destFull)
                    End If
                    CopyDirectory(srcPath, destFull)
                End If
                AddLog($"复制粘贴: {srcPath} -> {destFull}")
            End If

            SendJsonResponse(res, "{""success"":true}", 200)
        Catch ex As Exception
            AddLog($"HandlePaste 异常: {ex}")
            SendJsonError(res, "粘贴失败: " & ex.Message, 500)
        End Try
    End Sub

    ''' <summary>生成不重复的目标路径（追加 (1)、(2)...）</summary>
    Private Function GetUniquePath(fullPath As String) As String
        Dim dir As String = Path.GetDirectoryName(fullPath)
        Dim nameNoExt As String = Path.GetFileNameWithoutExtension(fullPath)
        Dim ext As String = Path.GetExtension(fullPath)
        Dim counter As Integer = 1
        Dim candidate As String = fullPath
        While File.Exists(candidate) OrElse Directory.Exists(candidate)
            candidate = Path.Combine(dir, $"{nameNoExt} ({counter}){ext}")
            counter += 1
        End While
        Return candidate
    End Function

    ''' <summary>递归复制目录</summary>
    Private Sub CopyDirectory(srcDir As String, destDir As String)
        Directory.CreateDirectory(destDir)
        For Each srcFile As String In Directory.GetFiles(srcDir)
            File.Copy(srcFile, Path.Combine(destDir, Path.GetFileName(srcFile)))
        Next
        For Each dir As String In Directory.GetDirectories(srcDir)
            CopyDirectory(dir, Path.Combine(destDir, Path.GetFileName(dir)))
        Next
    End Sub

    ' ================== 最近浏览记录 ==================

    ''' <summary>记录一次浏览行为</summary>
    Private Sub RecordRecentView(path As String, name As String, isFolder As Boolean, size As Long, category As String)
        SyncLock _recentLock
            ' 移除已存在的相同路径
            _recentFiles.RemoveAll(Function(r) String.Equals(r.Path, path, StringComparison.OrdinalIgnoreCase))
            _recentFiles.Insert(0, New RecentFileItem With {
                .Path = path,
                .Name = name,
                .IsFolder = isFolder,
                .Size = size,
                .Category = category,
                .LastViewed = DateTime.Now
            })
            ' 限制数量
            While _recentFiles.Count > MAX_RECENT_COUNT
                _recentFiles.RemoveAt(_recentFiles.Count - 1)
            End While
        End SyncLock
        SaveRecentConfig()
    End Sub

    ''' <summary>保存最近浏览记录</summary>
    Private Sub SaveRecentConfig()
        Try
            Dim cfgPath As String = GetConfigPath(RECENT_CONFIG_FILE)
            Dim lines As New List(Of String)
            SyncLock _recentLock
                For Each item In _recentFiles
                    lines.Add($"{item.Path}|{item.Name}|{If(item.IsFolder, 1, 0)}|{item.Size}|{item.Category}|{item.LastViewed:yyyy-MM-dd HH:mm:ss}")
                Next
            End SyncLock
            File.WriteAllLines(cfgPath, lines.ToArray(), Encoding.UTF8)
        Catch ex As Exception
            AddLog($"保存最近浏览记录失败: {ex.Message}")
        End Try
    End Sub

    ''' <summary>加载最近浏览记录</summary>
    Private Sub LoadRecentConfig()
        Try
            Dim cfgPath As String = GetConfigPath(RECENT_CONFIG_FILE)
            If Not File.Exists(cfgPath) Then Return
            Dim lines As String() = File.ReadAllLines(cfgPath, Encoding.UTF8)
            SyncLock _recentLock
                _recentFiles.Clear()
                For Each line As String In lines
                    Dim parts As String() = line.Split("|"c)
                    If parts.Length >= 6 Then
                        Dim isFolder As Boolean = parts(2) = "1"
                        Dim size As Long = 0
                        Long.TryParse(parts(3), size)
                        Dim viewed As DateTime = DateTime.Now
                        DateTime.TryParse(parts(5), viewed)
                        _recentFiles.Add(New RecentFileItem With {
                            .Path = parts(0),
                            .Name = parts(1),
                            .IsFolder = isFolder,
                            .Size = size,
                            .Category = parts(4),
                            .LastViewed = viewed
                        })
                    End If
                Next
            End SyncLock
        Catch ex As Exception
            AddLog($"加载最近浏览记录失败: {ex.Message}")
        End Try
    End Sub

    ''' <summary>获取最近浏览记录</summary>
    Private Sub HandleRecentFiles(res As HttpListenerResponse)
        Try
            Dim items As New List(Of Dictionary(Of String, Object))
            SyncLock _recentLock
                For Each item In _recentFiles
                    ' 跳过已不存在的文件
                    If Not File.Exists(item.Path) AndAlso Not Directory.Exists(item.Path) Then Continue For
                    Dim d As New Dictionary(Of String, Object)
                    d("name") = item.Name
                    d("path") = item.Path
                    d("type") = If(item.IsFolder, "folder", "file")
                    d("size") = item.Size
                    d("category") = item.Category
                    d("lastViewed") = item.LastViewed.ToString("yyyy-MM-dd HH:mm:ss")
                    items.Add(d)
                Next
            End SyncLock

            Dim sb As New StringBuilder()
            sb.Append("{""items"":[")
            For i As Integer = 0 To items.Count - 1
                Dim d = items(i)
                sb.Append("{")
                sb.Append("""name"":""" & EscapeJsonString(d("name").ToString()) & """,")
                sb.Append("""path"":""" & EscapeJsonString(d("path").ToString()) & """,")
                sb.Append("""type"":""" & d("type").ToString() & """,")
                sb.Append("""size"":" & d("size").ToString() & ",")
                sb.Append("""category"":""" & EscapeJsonString(d("category").ToString()) & """,")
                sb.Append("""lastViewed"":""" & EscapeJsonString(d("lastViewed").ToString()) & """")
                sb.Append("}")
                If i < items.Count - 1 Then sb.Append(",")
            Next
            sb.Append("]}")
            SendJsonResponse(res, sb.ToString(), 200)
        Catch ex As Exception
            AddLog($"HandleRecentFiles 异常: {ex}")
            SendJsonError(res, "获取最近浏览失败: " & ex.Message, 500)
        End Try
    End Sub

    ' ================== 传输历史记录 ==================

    ''' <summary>记录一次传输事件（上传/下载）</summary>
    Private Sub RecordTransfer(fileName As String, direction As String, size As Long, clientIP As String, filePath As String)
        SyncLock _transferLock
            _transferHistory.Insert(0, New TransferRecord With {
                .Id = Guid.NewGuid().ToString("N"),
                .FileName = fileName,
                .Direction = direction,
                .Size = size,
                .Time = DateTime.Now,
                .ClientIP = clientIP,
                .FilePath = filePath
            })
            While _transferHistory.Count > MAX_TRANSFER_COUNT
                _transferHistory.RemoveAt(_transferHistory.Count - 1)
            End While
            _transferVersion += 1
        End SyncLock
        SaveTransferConfig()
    End Sub

    ''' <summary>保存传输历史记录</summary>
    Private Sub SaveTransferConfig()
        Try
            Dim cfgPath As String = GetConfigPath(TRANSFER_CONFIG_FILE)
            Dim lines As New List(Of String)
            SyncLock _transferLock
                For Each item In _transferHistory
                    lines.Add($"{item.Id}|{item.FileName}|{item.Direction}|{item.Size}|{item.Time:yyyy-MM-dd HH:mm:ss}|{item.ClientIP}|{item.FilePath}")
                Next
            End SyncLock
            File.WriteAllLines(cfgPath, lines.ToArray(), Encoding.UTF8)
        Catch ex As Exception
            AddLog($"保存传输历史失败: {ex.Message}")
        End Try
    End Sub

    ''' <summary>加载传输历史记录</summary>
    Private Sub LoadTransferConfig()
        Try
            Dim cfgPath As String = GetConfigPath(TRANSFER_CONFIG_FILE)
            If Not File.Exists(cfgPath) Then Return
            Dim lines As String() = File.ReadAllLines(cfgPath, Encoding.UTF8)
            SyncLock _transferLock
                _transferHistory.Clear()
                For Each line As String In lines
                    Dim parts As String() = line.Split("|"c)
                    If parts.Length >= 7 Then
                        Dim size As Long = 0
                        Long.TryParse(parts(3), size)
                        Dim t As DateTime = DateTime.Now
                        DateTime.TryParse(parts(4), t)
                        _transferHistory.Add(New TransferRecord With {
                            .Id = parts(0),
                            .FileName = parts(1),
                            .Direction = parts(2),
                            .Size = size,
                            .Time = t,
                            .ClientIP = parts(5),
                            .FilePath = parts(6)
                        })
                    ElseIf parts.Length = 6 Then
                        ' 兼容 6 段格式（无 FilePath）
                        Dim size As Long = 0
                        Long.TryParse(parts(3), size)
                        Dim t As DateTime = DateTime.Now
                        DateTime.TryParse(parts(4), t)
                        _transferHistory.Add(New TransferRecord With {
                            .Id = parts(0),
                            .FileName = parts(1),
                            .Direction = parts(2),
                            .Size = size,
                            .Time = t,
                            .ClientIP = parts(5)
                        })
                    ElseIf parts.Length = 5 Then
                        ' 兼容旧格式（无 Id 字段），自动生成新 Id
                        Dim size As Long = 0
                        Long.TryParse(parts(2), size)
                        Dim t As DateTime = DateTime.Now
                        DateTime.TryParse(parts(3), t)
                        _transferHistory.Add(New TransferRecord With {
                            .Id = Guid.NewGuid().ToString("N"),
                            .FileName = parts(0),
                            .Direction = parts(1),
                            .Size = size,
                            .Time = t,
                            .ClientIP = parts(4)
                        })
                    End If
                Next
            End SyncLock
        Catch ex As Exception
            AddLog($"加载传输历史失败: {ex.Message}")
        End Try
    End Sub

    ''' <summary>获取传输历史记录（共享页）</summary>
    Private Sub HandleTransferHistory(res As HttpListenerResponse)
        Try
            Dim items As New List(Of Dictionary(Of String, Object))
            Dim version As Long
            SyncLock _transferLock
                version = _transferVersion
                For Each item In _transferHistory
                    Dim d As New Dictionary(Of String, Object)
                    d("id") = item.Id
                    d("fileName") = item.FileName
                    d("direction") = item.Direction
                    d("size") = item.Size
                    d("time") = item.Time.ToString("yyyy-MM-dd HH:mm:ss")
                    d("clientIP") = item.ClientIP
                    ' 判断文件是否仍存在
                    Dim exists As Boolean = False
                    If Not String.IsNullOrEmpty(item.FilePath) Then
                        exists = File.Exists(item.FilePath) OrElse Directory.Exists(item.FilePath)
                    Else
                        ' 无路径记录时，回退检查共享文件夹根目录
                        Dim probe As String = Path.Combine(SharedFolderPath, item.FileName)
                        exists = File.Exists(probe) OrElse Directory.Exists(probe)
                    End If
                    d("exists") = exists
                    items.Add(d)
                Next
            End SyncLock

            Dim sb As New StringBuilder()
            sb.Append("{""version"":" & version & ",""items"":[")
            For i As Integer = 0 To items.Count - 1
                Dim d = items(i)
                sb.Append("{")
                sb.Append("""id"":""" & EscapeJsonString(d("id").ToString()) & """,")
                sb.Append("""fileName"":""" & EscapeJsonString(d("fileName").ToString()) & """,")
                sb.Append("""direction"":""" & d("direction").ToString() & """,")
                sb.Append("""size"":" & d("size").ToString() & ",")
                sb.Append("""time"":""" & EscapeJsonString(d("time").ToString()) & """,")
                sb.Append("""clientIP"":""" & EscapeJsonString(d("clientIP").ToString()) & """,")
                sb.Append("""exists"":" & If(Convert.ToBoolean(d("exists")), "true", "false"))
                sb.Append("}")
                If i < items.Count - 1 Then sb.Append(",")
            Next
            sb.Append("]}")
            SendJsonResponse(res, sb.ToString(), 200)
        Catch ex As Exception
            AddLog($"HandleTransferHistory 异常: {ex}")
            SendJsonError(res, "获取传输历史失败: " & ex.Message, 500)
        End Try
    End Sub

    ''' <summary>删除传输历史记录（支持单个或多个）</summary>
    Private Sub HandleTransferDelete(req As HttpListenerRequest, res As HttpListenerResponse)
        Try
            Dim body As String
            Using reader As New StreamReader(req.InputStream, req.ContentEncoding)
                body = reader.ReadToEnd()
            End Using
            ' 支持 JSON 数组或表单 ids=id1,id2
            Dim ids As New List(Of String)
            Dim params = ParseFormUrlEncoded(body)
            Dim idsParam As String = params.GetValueOrDefault("ids", "")
            If Not String.IsNullOrEmpty(idsParam) Then
                For Each id In idsParam.Split(","c)
                    If Not String.IsNullOrWhiteSpace(id) Then ids.Add(id.Trim())
                Next
            End If

            If ids.Count = 0 Then
                SendJsonError(res, "缺少要删除的记录 ID", 400)
                Return
            End If

            Dim removed As Integer = 0
            SyncLock _transferLock
                For Each id In ids
                    Dim idx As Integer = _transferHistory.FindIndex(Function(t) t.Id = id)
                    If idx >= 0 Then
                        _transferHistory.RemoveAt(idx)
                        removed += 1
                    End If
                Next
                If removed > 0 Then _transferVersion += 1
            End SyncLock

            If removed > 0 Then
                SaveTransferConfig()
                AddLog($"[传输记录] 删除 {removed} 条记录")
                ' 同步刷新主窗体共享页列表（网页端删除时立即同步，无需定时器）
                Try
                    If Me.IsHandleCreated AndAlso Not Me.IsDisposed Then
                        Me.BeginInvoke(Sub()
                                           If lstTransfer IsNot Nothing Then
                                               RefreshTransferList()
                                           End If
                                       End Sub)
                    End If
                Catch
                End Try
            End If

            SendJsonResponse(res, "{""success"":true,""removed"":" & removed & "}", 200)
        Catch ex As Exception
            AddLog($"HandleTransferDelete 异常: {ex}")
            SendJsonError(res, "删除传输记录失败: " & ex.Message, 500)
        End Try
    End Sub

    ' ================== 服务控制接口 ==================

    ''' <summary>获取服务状态</summary>
    Private Sub HandleServiceStatus(res As HttpListenerResponse)
        Try
            Dim ip As String = GetLocalIpAddress()
            Dim port As Integer = DefaultPort
            If txtPort IsNot Nothing AndAlso Not String.IsNullOrEmpty(txtPort.Text) Then
                Integer.TryParse(txtPort.Text.Trim(), port)
            End If
            Dim lanUrl As String = $"http://{ip}:{port}/"

            Dim sb As New StringBuilder()
            sb.Append("{")
            sb.Append("""running"":" & If(isRunning, "true", "false") & ",")
            sb.Append("""ngrokRunning"":" & If(isNgrokRunning, "true", "false") & ",")
            sb.Append("""ip"":""" & EscapeJsonString(ip) & """,")
            sb.Append("""port"":" & port & ",")
            sb.Append("""lanUrl"":""" & EscapeJsonString(lanUrl) & """,")
            sb.Append("""ngrokUrl"":""" & EscapeJsonString(ngrokUrl) & """")
            sb.Append("}")
            SendJsonResponse(res, sb.ToString(), 200)
        Catch ex As Exception
            AddLog($"HandleServiceStatus 异常: {ex}")
            SendJsonError(res, "获取服务状态失败: " & ex.Message, 500)
        End Try
    End Sub

    ''' <summary>启动服务</summary>
    Private Sub HandleServiceStart(req As HttpListenerRequest, res As HttpListenerResponse)
        If Not IsAuthenticated(req) Then
            SendJsonError(res, "未登录或登录已过期", 401)
            Return
        End If
        Try
            If isRunning Then
                SendJsonResponse(res, "{""success"":true,""message"":""服务已在运行""}", 200)
                Return
            End If
            ' 在 UI 线程执行启动
            Me.Invoke(Sub() BtnStart_Click(Nothing, Nothing))
            SendJsonResponse(res, "{""success"":true}", 200)
            AddLog("通过网页启动服务")
        Catch ex As Exception
            AddLog($"HandleServiceStart 异常: {ex}")
            SendJsonError(res, "启动服务失败: " & ex.Message, 500)
        End Try
    End Sub

    ''' <summary>停止服务</summary>
    Private Sub HandleServiceStop(req As HttpListenerRequest, res As HttpListenerResponse)
        If Not IsAuthenticated(req) Then
            SendJsonError(res, "未登录或登录已过期", 401)
            Return
        End If
        Try
            If Not isRunning Then
                SendJsonResponse(res, "{""success"":true,""message"":""服务未运行""}", 200)
                Return
            End If
            Me.Invoke(Sub() BtnStop_Click(Nothing, Nothing))
            SendJsonResponse(res, "{""success"":true}", 200)
            AddLog("通过网页停止服务")
        Catch ex As Exception
            AddLog($"HandleServiceStop 异常: {ex}")
            SendJsonError(res, "停止服务失败: " & ex.Message, 500)
        End Try
    End Sub

    ''' <summary>开启外网（ngrok）</summary>
    Private Sub HandleNgrokStart(req As HttpListenerRequest, res As HttpListenerResponse)
        If Not IsAuthenticated(req) Then
            SendJsonError(res, "未登录或登录已过期", 401)
            Return
        End If
        Try
            If isNgrokRunning Then
                SendJsonResponse(res, "{""success"":true,""message"":""外网已开启""}", 200)
                Return
            End If
            Me.Invoke(Sub() BtnNgrok_Click(Nothing, Nothing))
            SendJsonResponse(res, "{""success"":true}", 200)
            AddLog("通过网页开启外网")
        Catch ex As Exception
            AddLog($"HandleNgrokStart 异常: {ex}")
            SendJsonError(res, "开启外网失败: " & ex.Message, 500)
        End Try
    End Sub

    ''' <summary>关闭外网（ngrok）</summary>
    Private Sub HandleNgrokStop(req As HttpListenerRequest, res As HttpListenerResponse)
        If Not IsAuthenticated(req) Then
            SendJsonError(res, "未登录或登录已过期", 401)
            Return
        End If
        Try
            If Not isNgrokRunning Then
                SendJsonResponse(res, "{""success"":true,""message"":""外网未开启""}", 200)
                Return
            End If
            Me.Invoke(Sub() BtnNgrok_Click(Nothing, Nothing))
            SendJsonResponse(res, "{""success"":true}", 200)
            AddLog("通过网页关闭外网")
        Catch ex As Exception
            AddLog($"HandleNgrokStop 异常: {ex}")
            SendJsonError(res, "关闭外网失败: " & ex.Message, 500)
        End Try
    End Sub

    ' ================== 用户设置 ==================

    ''' <summary>读取用户设置（账号密码、昵称）</summary>
    Private Function ReadUserSettings() As (Nickname As String, Username As String, Password As String)
        Dim nickname As String = DEFAULT_NICKNAME
        Dim username As String = DEFAULT_USERNAME
        Dim password As String = DEFAULT_PASSWORD
        Try
            Dim cfgPath As String = GetConfigPath(SETTINGS_FILE)
            If File.Exists(cfgPath) Then
                Dim parts As String() = File.ReadAllText(cfgPath, Encoding.UTF8).Trim().Split("|"c)
                If parts.Length >= 3 Then
                    nickname = parts(0)
                    username = parts(1)
                    password = parts(2)
                End If
            End If
        Catch ex As Exception
            AddLog($"读取用户设置失败: {ex.Message}")
        End Try
        Return (nickname, username, password)
    End Function

    ''' <summary>保存用户设置</summary>
    Private Sub SaveUserSettings(nickname As String, username As String, password As String)
        Try
            Dim cfgPath As String = GetConfigPath(SETTINGS_FILE)
            File.WriteAllText(cfgPath, $"{nickname}|{username}|{password}", Encoding.UTF8)
        Catch ex As Exception
            AddLog($"保存用户设置失败: {ex.Message}")
        End Try
    End Sub

    ''' <summary>获取用户设置（不返回密码明文）</summary>
    Private Sub HandleSettingsGet(res As HttpListenerResponse)
        Try
            Dim s = ReadUserSettings()
            Dim sb As New StringBuilder()
            sb.Append("{")
            sb.Append("""nickname"":""" & EscapeJsonString(s.Nickname) & """,")
            sb.Append("""username"":""" & EscapeJsonString(s.Username) & """,")
            sb.Append("""hasPassword"":" & If(String.IsNullOrEmpty(s.Password), "false", "true"))
            sb.Append("}")
            SendJsonResponse(res, sb.ToString(), 200)
        Catch ex As Exception
            AddLog($"HandleSettingsGet 异常: {ex}")
            SendJsonError(res, "获取设置失败: " & ex.Message, 500)
        End Try
    End Sub

    ''' <summary>保存用户设置（仅昵称；账号不可修改，密码单独走 changepassword 接口）</summary>
    Private Sub HandleSettingsSave(req As HttpListenerRequest, res As HttpListenerResponse)
        If Not IsAuthenticated(req) Then
            SendJsonError(res, "未登录或登录已过期", 401)
            Return
        End If
        Try
            Dim body As String
            Using reader As New StreamReader(req.InputStream, req.ContentEncoding)
                body = reader.ReadToEnd()
            End Using
            Dim params = ParseFormUrlEncoded(body)
            Dim nickname = params.GetValueOrDefault("nickname", "")

            If String.IsNullOrWhiteSpace(nickname) Then
                SendJsonError(res, "昵称不能为空", 400)
                Return
            End If

            Dim s = ReadUserSettings()
            SaveUserSettings(nickname.Trim(), s.Username, s.Password)
            AddLog($"昵称已保存: {nickname}")
            SendJsonResponse(res, "{""success"":true}", 200)
        Catch ex As Exception
            AddLog($"HandleSettingsSave 异常: {ex}")
            SendJsonError(res, "保存设置失败: " & ex.Message, 500)
        End Try
    End Sub

    ''' <summary>修改密码（需验证原密码）</summary>
    Private Sub HandleChangePassword(req As HttpListenerRequest, res As HttpListenerResponse)
        If Not IsAuthenticated(req) Then
            SendJsonError(res, "未登录或登录已过期", 401)
            Return
        End If
        Try
            Dim body As String
            Using reader As New StreamReader(req.InputStream, req.ContentEncoding)
                body = reader.ReadToEnd()
            End Using
            Dim params = ParseFormUrlEncoded(body)
            Dim oldPassword = params.GetValueOrDefault("oldPassword", "")
            Dim newPassword = params.GetValueOrDefault("newPassword", "")

            If String.IsNullOrEmpty(newPassword) Then
                SendJsonError(res, "新密码不能为空", 400)
                Return
            End If

            Dim s = ReadUserSettings()
            If s.Password <> oldPassword Then
                SendJsonError(res, "原密码错误", 403)
                Return
            End If

            SaveUserSettings(s.Nickname, s.Username, newPassword)
            AddLog($"密码已修改: 账号={s.Username}")
            SendJsonResponse(res, "{""success"":true}", 200)
        Catch ex As Exception
            AddLog($"HandleChangePassword 异常: {ex}")
            SendJsonError(res, "修改密码失败: " & ex.Message, 500)
        End Try
    End Sub

    ''' <summary>重置设置（需 PIN 码）</summary>
    Private Sub HandleSettingsReset(req As HttpListenerRequest, res As HttpListenerResponse)
        Try
            Dim body As String
            Using reader As New StreamReader(req.InputStream, req.ContentEncoding)
                body = reader.ReadToEnd()
            End Using
            Dim params = ParseFormUrlEncoded(body)
            Dim pin = params.GetValueOrDefault("pin", "")

            ' 重置需要输入电脑 PIN 码，若无法获取则使用默认 2580
            Dim expectedPin As String = RESET_PIN
            ' 尝试获取 Windows PIN（通过检查是否存在相关机制，这里简化为固定值）
            If String.IsNullOrEmpty(pin) OrElse pin <> expectedPin Then
                SendJsonError(res, "PIN 码错误", 403)
                Return
            End If

            ' 重置为默认设置
            SaveUserSettings(DEFAULT_NICKNAME, DEFAULT_USERNAME, DEFAULT_PASSWORD)
            AddLog("用户设置已重置为默认值")
            SendJsonResponse(res, "{""success"":true}", 200)
        Catch ex As Exception
            AddLog($"HandleSettingsReset 异常: {ex}")
            SendJsonError(res, "重置失败: " & ex.Message, 500)
        End Try
    End Sub

    ''' <summary>解析 application/x-www-form-urlencoded 字符串</summary>
    Private Function ParseFormUrlEncoded(data As String) As Dictionary(Of String, String)
        Dim dict As New Dictionary(Of String, String)()
        If String.IsNullOrEmpty(data) Then Return dict
        For Each pair As String In data.Split("&"c)
            Dim kv As String() = pair.Split("="c)
            If kv.Length = 2 Then
                Dim key As String = Uri.UnescapeDataString(kv(0))
                Dim value As String = Uri.UnescapeDataString(kv(1))
                dict(key) = value
            End If
        Next
        Return dict
    End Function

    Private Function IsAuthenticated(req As HttpListenerRequest) As Boolean
        Dim token As String = req.Headers("X-Auth-Token")
        If String.IsNullOrEmpty(token) Then Return False
        SyncLock tokenLocker
            Dim expiry As DateTime
            If validTokens.TryGetValue(token, expiry) Then
                ' 检查是否过期，过期则移除
                If expiry > DateTime.Now Then
                    Return True
                Else
                    validTokens.Remove(token)
                    Return False
                End If
            End If
            Return False
        End SyncLock
    End Function


    Private Sub SendJsonResponse(res As HttpListenerResponse, json As String, statusCode As Integer)
        Try
            Dim buffer As Byte() = Encoding.UTF8.GetBytes(json)   ' json 应该是正确格式，如 {"success":true}
            res.ContentType = "application/json; charset=utf-8"
            res.StatusCode = statusCode
            res.ContentLength64 = buffer.Length
            res.OutputStream.Write(buffer, 0, buffer.Length)
        Catch ex As Exception
            ' 客户端断开连接（如反复刷新）或响应已提交：属正常网络情况，
            ' 必须静默处理，否则异常上抛会触发连锁崩溃（进程退出）
            AddLog($"[HTTP] 发送响应失败(客户端可能已断开): {ex.Message}")
        End Try
    End Sub

    Private Sub SendJsonError(res As HttpListenerResponse, message As String, statusCode As Integer)
        Dim json As String = $"{{\""error\"":\""{EscapeJsonString(message)}\""}}"
        SendJsonResponse(res, json, statusCode)
    End Sub

    ''' <summary>
    ''' 处理单个 HTTP 请求
    ''' </summary>
    Private Sub HandleRequest(context As HttpListenerContext)
        ' 更新最后活动时间
        lastActivityTime = DateTime.Now

        Dim req As HttpListenerRequest = context.Request
        Dim res As HttpListenerResponse = context.Response

        ' GameHub 公网中转：WebSocket 升级请求交给中转核心处理
        ' （与网页/文件接口共用同一端口和同一条 ngrok 隧道，手机端地址无需变化）
        If String.Equals(req.Headers("Upgrade"), "websocket", StringComparison.OrdinalIgnoreCase) Then
            HandleRelayWebSocket(context)
            Return
        End If

        Try
            Dim path As String = req.Url.AbsolutePath
            ' 获取客户端信息
            Dim clientIP As String = ""
            Dim userAgent As String = ""
            Try
                Dim remoteEndPoint As IPEndPoint = DirectCast(req.RemoteEndPoint, IPEndPoint)
                If remoteEndPoint IsNot Nothing Then
                    clientIP = remoteEndPoint.Address.ToString()
                End If
            Catch
                clientIP = "unknown"
            End Try
            userAgent = req.UserAgent
            _lastClientIP = clientIP

            If req.HttpMethod = "GET" Then
                If path = "/" OrElse path = "/index.html" Then
                    AddLog($"IP:{clientIP} UA:{userAgent} 访问首页")
                    SendHtml(res, clientIP, userAgent)
                ElseIf path.StartsWith("/download/") Then
                    Dim fileName As String = Uri.UnescapeDataString(path.Substring("/download/".Length))
                    AddLog($"IP:{clientIP} UA:{userAgent} 请求下载: {fileName}")
                    SendFile(res, fileName)
                ElseIf path = "/list" Then
                    ' 新增：返回文件列表
                    SendFileList(res)
                ElseIf path.StartsWith("/info/") Then
                    ' 新增：返回文件信息
                    Dim fileName As String = Uri.UnescapeDataString(path.Substring("/info/".Length))
                    SendFileInfo(res, fileName)
                ElseIf path = "/getFolders" Then
                    GetFoldersList(res)
                    '浏览文件夹：
                ElseIf path.StartsWith("/browse/") Then
                    ' 浏览文件夹内容
                    Dim folderPath As String = Uri.UnescapeDataString(path.Substring("/browse/".Length))
                    AddLog($"IP:{clientIP} 浏览文件夹: {folderPath}")
                    BrowseFolder(res, folderPath)

                ElseIf path = "/logout" Then
                    HandleLogout(req, res)

                ElseIf path = "/getDrives" AndAlso IsAuthenticated(req) Then
                    GetDrivesList(res)
                ElseIf path.StartsWith("/browseAll/") AndAlso IsAuthenticated(req) Then
                    Dim targetPath As String = Uri.UnescapeDataString(path.Substring("/browseAll/".Length))
                    BrowseAll(res, targetPath)
                ElseIf path.StartsWith("/downloadAll/") AndAlso IsAuthenticated(req) Then
                    Dim filePath As String = Uri.UnescapeDataString(path.Substring("/downloadAll/".Length))
                    DownloadAll(res, filePath)
                ElseIf path = "/recent" Then
                    HandleRecentFiles(res)
                ElseIf path = "/transfer/history" Then
                    HandleTransferHistory(res)
                ElseIf path = "/service/status" Then
                    HandleServiceStatus(res)
                ElseIf path = "/settings/get" Then
                    HandleSettingsGet(res)
                ElseIf path = "/archive/list" Then
                    HandleArchiveList(req, res)
                ElseIf path.StartsWith("/libs/") Then
                    ' 静态资源（本地 JS 库等）
                    SendStaticFile(res, path.Substring("/libs/".Length))
                Else
                    Send404(res)
                End If
            ElseIf req.HttpMethod = "POST" Then
                If path = "/upload" Then
                    HandleUpload(req, res, clientIP, userAgent)
                ElseIf path = "/login" Then
                    HandleLogin(req, res)
                ElseIf path = "/createFolder" Then
                    HandleCreateFolder(req, res)
                ElseIf path = "/delete" Then
                    HandleDelete(req, res)
                ElseIf path = "/rename" Then
                    HandleRename(req, res)
                ElseIf path = "/copy" Then
                    HandleCopyMove(req, res, isCut:=False)
                ElseIf path = "/cut" Then
                    HandleCopyMove(req, res, isCut:=True)
                ElseIf path = "/paste" Then
                    HandlePaste(req, res)
                ElseIf path = "/service/start" Then
                    HandleServiceStart(req, res)
                ElseIf path = "/service/stop" Then
                    HandleServiceStop(req, res)
                ElseIf path = "/ngrok/start" Then
                    HandleNgrokStart(req, res)
                ElseIf path = "/ngrok/stop" Then
                    HandleNgrokStop(req, res)
                ElseIf path = "/settings/save" Then
                    HandleSettingsSave(req, res)
                ElseIf path = "/settings/changepassword" Then
                    HandleChangePassword(req, res)
                ElseIf path = "/settings/reset" Then
                    HandleSettingsReset(req, res)
                ElseIf path = "/transfer/delete" Then
                    HandleTransferDelete(req, res)
                Else
                    Send404(res)
                End If
            Else
                Send404(res)
            End If
        Catch ex As Exception
            AddLog($"HandleRequest 全局异常: {ex}")
            Try
                Send500(res, ex.Message)
            Catch
                ' 响应已提交或客户端断开：绝不让异常从 catch 块逃逸（否则进程崩溃）
            End Try
        Finally
            Try
                res.Close()
            Catch ex As Exception
            End Try
        End Try
    End Sub
    ''' <summary>获取所有磁盘根目录（管理员）</summary>
    Private Sub GetDrivesList(res As HttpListenerResponse)
        Try
            Dim drivesInfo As New List(Of Dictionary(Of String, Object))
            For Each drive As IO.DriveInfo In IO.DriveInfo.GetDrives()
                If drive.IsReady Then
                    ' 构建显示名称，如 "本地磁盘 (C:)"
                    Dim volumeLabel As String = drive.VolumeLabel
                    If String.IsNullOrEmpty(volumeLabel) Then
                        volumeLabel = "本地磁盘"
                    End If
                    Dim driveLetter As String = drive.Name.TrimEnd("\"c)
                    Dim displayName As String = $"{volumeLabel} ({driveLetter})"

                    Dim info As New Dictionary(Of String, Object)
                    info("name") = displayName
                    info("path") = drive.Name
                    info("freeSpace") = drive.AvailableFreeSpace
                    info("totalSize") = drive.TotalSize
                    drivesInfo.Add(info)
                End If
            Next

            ' 手动构建 JSON 字符串（避免引入额外库）
            Dim jsonBuilder As New StringBuilder()
            jsonBuilder.Append("[")
            For i As Integer = 0 To drivesInfo.Count - 1
                Dim d = drivesInfo(i)
                jsonBuilder.Append("{")
                jsonBuilder.Append("""name"":""" & EscapeJsonString(d("name").ToString()) & """,")
                jsonBuilder.Append("""path"":""" & EscapeJsonString(d("path").ToString()) & """,")
                jsonBuilder.Append("""freeSpace"":" & d("freeSpace") & ",")
                jsonBuilder.Append("""totalSize"":" & d("totalSize"))
                jsonBuilder.Append("}")
                If i < drivesInfo.Count - 1 Then jsonBuilder.Append(",")
            Next
            jsonBuilder.Append("]")
            Dim json As String = jsonBuilder.ToString()

            SendJsonResponse(res, json, 200)
        Catch ex As Exception
            AddLog($"GetDrivesList 异常: {ex}")
            SendJsonError(res, "获取磁盘列表失败: " & ex.Message, 500)
        End Try
    End Sub

    ''' <summary>浏览任意路径（管理员）</summary>
    Private Sub BrowseAll(res As HttpListenerResponse, targetPath As String)
        Try
            targetPath = Uri.UnescapeDataString(targetPath)
            ' 安全检查：禁止路径遍历
            If targetPath.Contains("..") OrElse targetPath.Contains("::") Then
                SendTextError(res, "非法路径", 403)
                Return
            End If

            ' 如果路径为空，返回磁盘列表（相当于根目录）
            If String.IsNullOrEmpty(targetPath) Then
                GetDrivesList(res)
                Return
            End If

            Dim fullPath As String = targetPath
            If Not Directory.Exists(fullPath) Then
                SendJsonError(res, "目录不存在", 404)
                Return
            End If

            Dim items As New List(Of Dictionary(Of String, Object))

            ' 添加上级目录（如果不是根目录）
            If Not String.IsNullOrEmpty(targetPath) AndAlso targetPath.Length > 3 Then
                Dim parent As String = Directory.GetParent(fullPath).FullName
                Dim parentItem As New Dictionary(Of String, Object)
                parentItem("name") = ".."
                parentItem("type") = "parent"
                parentItem("path") = parent
                parentItem("size") = 0
                parentItem("lastModified") = ""
                items.Add(parentItem)
            End If

            ' 子文件夹
            For Each dir As String In Directory.GetDirectories(fullPath)
                Dim dirInfo As New DirectoryInfo(dir)
                Dim item As New Dictionary(Of String, Object)
                item("name") = dirInfo.Name
                item("type") = "folder"
                item("path") = dir
                item("size") = 0
                item("lastModified") = dirInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
                items.Add(item)
            Next

            ' 文件
            For Each file As String In Directory.GetFiles(fullPath)
                Dim fileInfo As New FileInfo(file)
                Dim item As New Dictionary(Of String, Object)
                item("name") = fileInfo.Name
                item("type") = "file"
                item("path") = file
                item("size") = fileInfo.Length
                item("lastModified") = fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
                item("category") = GetFileCategory(fileInfo.Name)
                items.Add(item)
            Next

            ' 构建 JSON（使用 TryGetValue 避免键缺失）
            Dim jsonBuilder As New StringBuilder()
            jsonBuilder.Append("{""currentPath"":""" & EscapeJsonString(targetPath) & """,""items"":[")
            For i As Integer = 0 To items.Count - 1
                Dim item = items(i)
                jsonBuilder.Append("{")
                jsonBuilder.Append("""name"":""" & EscapeJsonString(item("name").ToString()) & """,")
                jsonBuilder.Append("""type"":""" & item("type").ToString() & """,")
                jsonBuilder.Append("""path"":""" & EscapeJsonString(item("path").ToString()) & """,")
                ' 安全获取 size
                Dim sizeVal As Long = 0
                If item.ContainsKey("size") Then
                    sizeVal = Convert.ToInt64(item("size"))
                End If
                jsonBuilder.Append("""size"":" & sizeVal & ",")
                jsonBuilder.Append("""lastModified"":""" & EscapeJsonString(item("lastModified").ToString()) & """")
                If item.ContainsKey("category") Then
                    jsonBuilder.Append(",""category"":""" & EscapeJsonString(item("category").ToString()) & """")
                End If
                jsonBuilder.Append("}")
                If i < items.Count - 1 Then jsonBuilder.Append(",")
            Next
            jsonBuilder.Append("]}")
            Dim json As String = jsonBuilder.ToString()
            Dim buffer As Byte() = Encoding.UTF8.GetBytes(json)
            res.ContentType = "application/json; charset=utf-8"
            res.StatusCode = 200
            res.ContentLength64 = buffer.Length
            res.OutputStream.Write(buffer, 0, buffer.Length)

        Catch ex As Exception
            AddLog($"BrowseAll 异常: {ex}")
            SendJsonError(res, "浏览失败: " & ex.Message, 500)
        End Try
    End Sub

    ''' <summary>下载任意文件（管理员）</summary>
    Private Sub DownloadAll(res As HttpListenerResponse, filePath As String)
        Try
            filePath = Uri.UnescapeDataString(filePath)
            If Not File.Exists(filePath) Then
                Send404(res)
                Return
            End If
            ' 记录下载
            Dim dlFi As New FileInfo(filePath)
            RecordTransfer(dlFi.Name, "download", dlFi.Length, _lastClientIP, filePath)
            ' 直接调用现有的 SendSingleFile 方法（它接受绝对路径）
            SendSingleFile(res, filePath)
        Catch ex As Exception
            AddLog($"DownloadAll 异常: {ex}")
            Send500(res, ex.Message)
        End Try
    End Sub

    ''' <summary>列出压缩包内容（支持 zip/rar/7z/tar/gz 等，使用 SharpCompress）</summary>
    Private Sub HandleArchiveList(req As HttpListenerRequest, res As HttpListenerResponse)
        Try
            Dim rawPath As String = If(req.QueryString("path"), "")
            Dim isAdmin As Boolean = If(req.QueryString("isAdmin"), "false") = "true"

            If String.IsNullOrEmpty(rawPath) Then
                AddLog("[压缩包] 请求缺少 path 参数")
                SendJsonError(res, "缺少路径参数", 400)
                Return
            End If

            ' 管理员浏览磁盘时需登录；普通共享文件无需登录
            If isAdmin AndAlso Not IsAuthenticated(req) Then
                AddLog("[压缩包] 未登录访问磁盘压缩包")
                SendJsonError(res, "未登录或登录已过期", 401)
                Return
            End If

            ' 路径安全检查：防止路径穿越
            If rawPath.Contains("..") Then
                AddLog($"[压缩包] 拒绝非法路径: {rawPath}")
                SendJsonError(res, "非法路径", 403)
                Return
            End If

            ' 路径解析：管理员用绝对路径，普通共享文件拼接 SharedFolderPath 根目录
            Dim archivePath As String = If(isAdmin, Uri.UnescapeDataString(rawPath), Path.Combine(SharedFolderPath, Uri.UnescapeDataString(rawPath).Replace("/", "\").TrimStart("\"c)))
            archivePath = Path.GetFullPath(archivePath)

            AddLog($"[压缩包] 请求列出: {archivePath} (isAdmin={isAdmin})")

            If Not File.Exists(archivePath) Then
                AddLog($"[压缩包] 文件不存在: {archivePath}")
                SendJsonError(res, "文件不存在", 404)
                Return
            End If

            ' 使用 SharpCompress 列出压缩包条目
            Dim entries As New List(Of Dictionary(Of String, Object))
            Using archive As SharpCompress.Archives.IArchive = SharpCompress.Archives.ArchiveFactory.Open(archivePath)
                For Each entry As SharpCompress.Archives.IArchiveEntry In archive.Entries
                    Dim isDir As Boolean = entry.IsDirectory
                    Dim item As New Dictionary(Of String, Object)
                    item("name") = If(isDir, entry.Key.TrimEnd("/"c, "\"c), entry.Key)
                    item("isDir") = isDir
                    item("size") = If(isDir, 0L, entry.Size)
                    entries.Add(item)
                Next
            End Using

            ' 构建 JSON
            Dim sb As New StringBuilder()
            sb.Append("{""entries"":[")
            For i As Integer = 0 To entries.Count - 1
                Dim e = entries(i)
                sb.Append("{")
                sb.Append("""name"":""" & EscapeJsonString(e("name").ToString()) & """,")
                sb.Append("""isDir"":" & If(Convert.ToBoolean(e("isDir")), "true", "false") & ",")
                sb.Append("""size"":" & Convert.ToInt64(e("size")).ToString())
                sb.Append("}")
                If i < entries.Count - 1 Then sb.Append(",")
            Next
            sb.Append("]}")
            AddLog($"[压缩包] 解析成功: {archivePath}，共 {entries.Count} 个条目")
            SendJsonResponse(res, sb.ToString(), 200)
        Catch ex As Exception
            AddLog($"[压缩包] HandleArchiveList 异常: {ex}")
            SendJsonError(res, "解析压缩包失败: " & ex.Message, 500)
        End Try
    End Sub

    ''' <summary>
    ''' 浏览文件夹内容（返回 JSON）
    ''' </summary>
    Private Sub BrowseFolder(res As HttpListenerResponse, folderPath As String)
        Try
            ' 1. URL 解码（前端 encodeURIComponent 编码）
            folderPath = Uri.UnescapeDataString(folderPath)
            Console.WriteLine($"[BrowseFolder] 原始路径: '{folderPath}'")

            ' 2. 清理路径：去除首尾空格、多余斜杠，统一为正斜杠
            folderPath = folderPath.Trim()
            folderPath = folderPath.Replace("\", "/").Replace("//", "/")
            If folderPath = "/" Then folderPath = ""
            Dim originalPath As String = folderPath

            ' 3. 安全验证：禁止路径遍历
            If folderPath.Contains("..") OrElse folderPath.Contains("::") Then
                SendTextError(res, "禁止访问：非法路径", 403)
                Return
            End If

            ' 4. 构建完整物理路径（将正斜杠转为 Windows 反斜杠）
            Dim physicalSubPath As String = folderPath.Replace("/", "\")
            Dim fullPath As String = Path.Combine(SharedFolderPath, physicalSubPath)
            fullPath = Path.GetFullPath(fullPath)
            Console.WriteLine($"[BrowseFolder] 物理路径: '{fullPath}'")

            ' 5. 确保路径在共享文件夹内
            Dim sharedFullPath As String = Path.GetFullPath(SharedFolderPath)
            If Not fullPath.StartsWith(sharedFullPath, StringComparison.OrdinalIgnoreCase) Then
                SendTextError(res, "禁止访问：路径超出共享文件夹范围", 403)
                Return
            End If

            ' 6. 如果文件夹不存在，返回空列表（状态200，JSON格式）
            If Not Directory.Exists(fullPath) Then
                Console.WriteLine($"[BrowseFolder] 文件夹不存在: {fullPath}")
                Dim emptyJson As String = "{""currentPath"":""" & EscapeJsonString(originalPath) & """,""items"":[]}"
                Dim buffer As Byte() = Encoding.UTF8.GetBytes(emptyJson)
                res.ContentType = "application/json; charset=utf-8"
                res.StatusCode = 200
                res.ContentLength64 = buffer.Length
                res.OutputStream.Write(buffer, 0, buffer.Length)
                Return
            End If

            ' 7. 获取文件夹内容（使用 Dictionary 代替匿名类型）
            Dim folderItems As New List(Of Dictionary(Of String, Object))

            ' 添加返回上级目录
            If Not String.IsNullOrEmpty(originalPath) Then
                Dim parentPath As String = Path.GetDirectoryName(originalPath)
                If Not String.IsNullOrEmpty(parentPath) Then
                    parentPath = parentPath.Replace("\", "/")
                    Dim item As New Dictionary(Of String, Object)
                    item("name") = ".."
                    item("type") = "parent"
                    item("category") = "返回上级"
                    item("path") = parentPath
                    item("size") = 0
                    item("lastModified") = ""
                    folderItems.Add(item)
                End If
            End If

            ' 获取子文件夹
            For Each subDir As String In Directory.GetDirectories(fullPath)
                Dim dirInfo As New DirectoryInfo(subDir)
                Dim dirName As String = Path.GetFileName(subDir)
                Dim relativePath As String = If(String.IsNullOrEmpty(originalPath), dirName, Path.Combine(originalPath, dirName))
                relativePath = relativePath.Replace("\", "/")

                ' 统计子文件夹内的文件数量（可选，不统计也可设为0）
                Dim fileCount As Integer = Directory.GetFiles(subDir, "*", SearchOption.AllDirectories).Length
                Dim subDirCount As Integer = Directory.GetDirectories(subDir, "*", SearchOption.AllDirectories).Length

                Dim item As New Dictionary(Of String, Object)
                item("name") = dirName
                item("size") = 0
                item("lastModified") = dirInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
                item("type") = "folder"
                item("category") = "文件夹"
                item("path") = relativePath
                item("fileCount") = fileCount
                item("folderCount") = subDirCount
                item("totalItems") = fileCount + subDirCount
                folderItems.Add(item)
            Next

            ' 获取文件
            For Each file As String In Directory.GetFiles(fullPath)
                Dim fileInfo As New FileInfo(file)
                Dim fileName As String = Path.GetFileName(file)
                Dim relativePath As String = If(String.IsNullOrEmpty(originalPath), fileName, Path.Combine(originalPath, fileName))
                relativePath = relativePath.Replace("\", "/")

                Dim item As New Dictionary(Of String, Object)
                item("name") = fileName
                item("size") = fileInfo.Length
                item("lastModified") = fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
                item("type") = "file"
                item("category") = GetFileCategory(fileName)
                item("path") = relativePath
                folderItems.Add(item)
            Next

            ' 8. 构建 JSON 响应
            Dim jsonBuilder As New StringBuilder()
            jsonBuilder.Append("{")
            jsonBuilder.Append("""currentPath"":""" & EscapeJsonString(originalPath) & """,")
            jsonBuilder.Append("""items"":[")

            For i As Integer = 0 To folderItems.Count - 1
                Dim item = folderItems(i)
                jsonBuilder.Append("{")
                jsonBuilder.Append("""name"":""" & EscapeJsonString(item("name").ToString()) & """,")
                jsonBuilder.Append("""size"":" & item("size").ToString() & ",")
                jsonBuilder.Append("""lastModified"":""" & EscapeJsonString(item("lastModified").ToString()) & """,")
                jsonBuilder.Append("""type"":""" & item("type").ToString() & """,")
                jsonBuilder.Append("""category"":""" & EscapeJsonString(item("category").ToString()) & """,")
                jsonBuilder.Append("""path"":""" & EscapeJsonString(item("path").ToString()) & """")
                ' 如果是文件夹，添加额外字段
                If item("type").ToString() = "folder" Then
                    jsonBuilder.Append(",""fileCount"":" & item("fileCount").ToString() & ",")
                    jsonBuilder.Append("""folderCount"":" & item("folderCount").ToString() & ",")
                    jsonBuilder.Append("""totalItems"":" & item("totalItems").ToString())
                End If
                jsonBuilder.Append("}")
                If i < folderItems.Count - 1 Then jsonBuilder.Append(",")
            Next
            jsonBuilder.Append("]}")

            Dim json As String = jsonBuilder.ToString()
            Dim buffer1 As Byte() = Encoding.UTF8.GetBytes(json)

            res.ContentType = "application/json; charset=utf-8"
            res.StatusCode = 200
            res.ContentLength64 = buffer1.Length
            res.OutputStream.Write(buffer1, 0, buffer1.Length)

        Catch ex As Exception
            MessageBox.Show($"[BrowseFolder ERROR] {ex}")
            SendTextError(res, "服务器错误: " & ex.Message, 500)
        End Try
    End Sub
    Private Sub SendTextError(res As HttpListenerResponse, message As String, statusCode As Integer)
        Dim buffer As Byte() = Encoding.UTF8.GetBytes(message)
        res.ContentType = "text/plain; charset=utf-8"
        res.StatusCode = statusCode
        res.ContentLength64 = buffer.Length
        res.OutputStream.Write(buffer, 0, buffer.Length)
    End Sub

    ''' <summary>
    ''' 发送文件列表（JSON格式）- 支持文件和文件夹
    ''' </summary>
    Private Sub SendFileList(res As HttpListenerResponse)
        Try
            ' 确保共享文件夹存在
            If Not Directory.Exists(SharedFolderPath) Then
                Directory.CreateDirectory(SharedFolderPath)
            End If

            ' 获取Shared文件夹中的所有文件和文件夹
            Dim allItems As New List(Of Object)

            ' 1. 获取所有文件
            Dim files As String() = Directory.GetFiles(SharedFolderPath)
            For Each filePath As String In files
                Dim fileInfo As New FileInfo(filePath)
                allItems.Add(New With {
                .name = Path.GetFileName(filePath),
                .size = fileInfo.Length,
                .lastModified = fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                .type = "file",
                .category = GetFileCategory(Path.GetFileName(filePath))
            })
            Next

            ' 2. 获取所有文件夹
            Dim directories As String() = Directory.GetDirectories(SharedFolderPath)
            For Each dirPath As String In directories
                Dim dirInfo As New DirectoryInfo(dirPath)
                allItems.Add(New With {
                .name = Path.GetFileName(dirPath) & "/",  ' 文件夹名称后加斜杠表示是文件夹
                .size = 0,
                .lastModified = dirInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"),
                .type = "folder",
                .category = "文件夹"
            })
            Next

            ' 按名称排序
            allItems = allItems.OrderBy(Function(x) x.name).ToList()

            ' 转换为JSON - 修复转义问题
            Dim jsonBuilder As New StringBuilder()
            jsonBuilder.Append("[")
            For i As Integer = 0 To allItems.Count - 1
                Dim item = allItems(i)
                jsonBuilder.Append("{")

                ' 正确转义文件名中的所有特殊字符
                Dim escapedName As String = EscapeJsonString(item.name)
                jsonBuilder.Append($"""name"":""{escapedName}"",")
                jsonBuilder.Append($"""size"":{item.size},")
                jsonBuilder.Append($"""lastModified"":""{item.lastModified}"",")
                jsonBuilder.Append($"""type"":""{item.type}"",")
                jsonBuilder.Append($"""category"":""{item.category}""")

                jsonBuilder.Append("}")
                If i < allItems.Count - 1 Then
                    jsonBuilder.Append(",")
                End If
            Next
            jsonBuilder.Append("]")

            Dim json As String = jsonBuilder.ToString()
            Dim buffer As Byte() = Encoding.UTF8.GetBytes(json)

            res.ContentType = "application/json; charset=utf-8"
            res.ContentLength64 = buffer.Length
            res.OutputStream.Write(buffer, 0, buffer.Length)

        Catch ex As Exception
            Send500(res, "获取文件列表失败: " & ex.Message)
        End Try
    End Sub

    ''' <summary>
    ''' 根据文件名获取文件分类
    ''' </summary>
    Private Function GetFileCategory(fileName As String) As String
        Dim ext As String = Path.GetExtension(fileName).ToLower()

        Select Case ext
            Case ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp", ".ico", ".svg"
                Return "图片"
            Case ".mp4", ".avi", ".mov", ".wmv", ".flv", ".mkv", ".mpg", ".mpeg"
                Return "视频"
            Case ".mp3", ".wav", ".wma", ".aac", ".flac", ".ogg"
                Return "音频"
            Case ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx", ".txt", ".rtf"
                Return "文档"
            Case ".zip", ".rar", ".7z", ".tar", ".gz", ".bz2", ".iso"
                Return "压缩包"
            Case ".exe", ".msi", ".bat", ".cmd", ".sh"
                Return "程序"
            Case Else
                Return "其他"
        End Select
    End Function

    ''' <summary>
    ''' 转义 JSON 字符串中的特殊字符
    ''' </summary>
    Private Function EscapeJsonString(input As String) As String
        If String.IsNullOrEmpty(input) Then Return ""

        Dim sb As New StringBuilder()
        For Each c As Char In input
            Select Case c
                Case """"c
                    sb.Append("\""")
                Case "\"c
                    sb.Append("\\")
                Case "/"c
                    sb.Append("\/")
                Case ControlChars.Back
                    sb.Append("\b")
                Case ControlChars.FormFeed
                    sb.Append("\f")
                Case ControlChars.Lf
                    sb.Append("\n")
                Case ControlChars.Cr
                    sb.Append("\r")
                Case ControlChars.Tab
                    sb.Append("\t")
                Case Else
                    ' 检查是否为控制字符（ASCII < 32）
                    If AscW(c) < 32 Then
                        sb.AppendFormat("\u{0:x4}", AscW(c))
                    Else
                        sb.Append(c)
                    End If
            End Select
        Next
        Return sb.ToString()
    End Function

    ''' <summary>
    ''' 发送文件（单个文件或文件夹ZIP）- 优化版
    ''' </summary>
    Private Sub SendFile(res As HttpListenerResponse, fileName As String)
        Try
            ' 1. 调试日志 - 记录原始输入
            Console.WriteLine($"[DEBUG] SendFile 开始处理: 原始文件名 = '{fileName}'")

            ' 2. 双重解码保护（处理可能的双重编码情况）
            Dim decodedFileName As String = fileName
            Try
                ' 先尝试解码一次
                decodedFileName = Uri.UnescapeDataString(fileName)
                Console.WriteLine($"[DEBUG] 第一次解码后: '{decodedFileName}'")

                ' 如果解码后仍然包含编码字符，再解码一次
                If decodedFileName.Contains("%") Then
                    decodedFileName = Uri.UnescapeDataString(decodedFileName)
                    Console.WriteLine($"[DEBUG] 第二次解码后: '{decodedFileName}'")
                End If
            Catch ex As Exception
                Console.WriteLine($"[DEBUG] 解码异常: {ex.Message}")
                ' 解码失败时使用原始文件名
            End Try

            ' 3. 清理路径分隔符
            Dim cleanName As String = decodedFileName
            cleanName = cleanName.Replace("/", Path.DirectorySeparatorChar)
            cleanName = cleanName.Replace("\", Path.DirectorySeparatorChar)

            ' 4. 移除路径开头的斜杠（如果有）
            If cleanName.StartsWith(Path.DirectorySeparatorChar) Then
                cleanName = cleanName.Substring(1)
            End If

            ' 5. 安全验证：禁止路径遍历攻击
            If cleanName.Contains("..") OrElse cleanName.Contains("::") Then
                Console.WriteLine($"[SECURITY] 路径遍历攻击检测: {cleanName}")
                Send403(res, "禁止访问：非法路径")
                Return
            End If

            ' 6. 构建完整路径
            Dim filePath As String = Path.Combine(SharedFolderPath, cleanName)
            Console.WriteLine($"[DEBUG] 组合路径: SharedFolderPath='{SharedFolderPath}', cleanName='{cleanName}'")
            Console.WriteLine($"[DEBUG] 完整路径: '{filePath}'")

            ' 7. 规范化路径（解决相对路径问题）
            Try
                filePath = Path.GetFullPath(filePath)
                Console.WriteLine($"[DEBUG] 规范化后路径: '{filePath}'")
            Catch ex As Exception
                Console.WriteLine($"[ERROR] 路径规范化失败: {ex.Message}")
                Send500(res, "路径解析失败")
                Return
            End Try

            ' 8. 确保路径在共享文件夹内（安全性检查）
            If Not filePath.StartsWith(Path.GetFullPath(SharedFolderPath), StringComparison.OrdinalIgnoreCase) Then
                Console.WriteLine($"[SECURITY] 路径越界检测: filePath='{filePath}', SharedFolderPath='{SharedFolderPath}'")
                Send403(res, "禁止访问：路径超出共享文件夹范围")
                Return
            End If

            ' 9. 检查文件/文件夹是否存在
            Console.WriteLine($"[DEBUG] 检查文件是否存在: {File.Exists(filePath)}")
            Console.WriteLine($"[DEBUG] 检查文件夹是否存在: {Directory.Exists(filePath)}")

            If File.Exists(filePath) Then
                ' 单个文件下载
                Console.WriteLine($"[INFO] 开始下载文件: {filePath}")
                ' 记录最近浏览（下载/预览均计入）
                Dim fi As New FileInfo(filePath)
                RecordRecentView(filePath, fi.Name, False, fi.Length, GetFileCategory(fi.Name))
                RecordTransfer(fi.Name, "download", fi.Length, _lastClientIP, filePath)
                SendSingleFile(res, filePath)
            ElseIf Directory.Exists(filePath) Then
                ' 文件夹下载（打包为ZIP）
                Console.WriteLine($"[INFO] 开始下载文件夹: {filePath}")
                Dim folderName As String = Path.GetFileName(cleanName)
                If String.IsNullOrEmpty(folderName) Then
                    folderName = "folder"
                End If
                RecordRecentView(filePath, folderName, True, 0, "文件夹")
                SendFolderAsZip(res, filePath, folderName)
            Else
                ' 文件不存在
                Console.WriteLine($"[ERROR] 文件不存在: {filePath}")
                Console.WriteLine($"[DEBUG] 原始输入: {fileName}")
                Console.WriteLine($"[DEBUG] 解码后: {decodedFileName}")
                Console.WriteLine($"[DEBUG] 清理后: {cleanName}")
                Console.WriteLine($"[DEBUG] 共享文件夹: {SharedFolderPath}")

                ' 尝试列出共享文件夹内容以帮助调试
                Try
                    If Directory.Exists(SharedFolderPath) Then
                        Dim files = Directory.GetFiles(SharedFolderPath)
                        Dim dirs = Directory.GetDirectories(SharedFolderPath)

                    Else
                        Console.WriteLine($"[ERROR] 共享文件夹不存在: {SharedFolderPath}")
                    End If
                Catch ex2 As Exception
                    Console.WriteLine($"[DEBUG] 列出文件夹内容失败: {ex2.Message}")
                End Try

                Send404(res)
            End If

        Catch ex As Exception
            ' 记录详细错误信息以便调试
            Console.WriteLine($"[ERROR] SendFile 异常: {ex}")
            Console.WriteLine($"[DEBUG] 堆栈跟踪: {ex.StackTrace}")
            Send500(res, $"文件下载失败: {ex.Message}")
        End Try
    End Sub
    ' --- 传输任务多行进度条（下载/上传共用） ---
    Private ReadOnly _transferTasks As New Dictionary(Of String, TransferTask)(StringComparer.OrdinalIgnoreCase)
    Private _uploadTaskCounter As Integer = 0   ' 上传任务唯一 ID 计数器（多文件并发时区分）

    Private Class TransferTask
        Public Id As String
        Public Name As String
        Public Direction As String   ' "下载" 或 "上传"
        Public Current As Long
        Public Total As Long
        Public Speed As Double
        Public LastTime As DateTime
        Public LastBytes As Long
        Public Done As Boolean
    End Class

    ''' <summary>获取或创建传输任务</summary>
    Private Function GetTransferTask(id As String, name As String, direction As String, total As Long) As TransferTask
        SyncLock _transferLock
            Dim t As TransferTask = Nothing
            If _transferTasks.TryGetValue(id, t) Then
                If t.Total <= 0 Then t.Total = total
                Return t
            End If
            t = New TransferTask() With {
                .Id = id,
                .Name = name,
                .Direction = direction,
                .Total = total,
                .LastTime = DateTime.MinValue,
                .LastBytes = 0,
                .Done = False
            }
            _transferTasks(id) = t
            Return t
        End SyncLock
    End Function

    ''' <summary>移除已完成任务（延迟保留由调用方控制）</summary>
    Private Sub RemoveTransferTask(id As String)
        SyncLock _transferLock
            _transferTasks.Remove(id)
        End SyncLock
    End Sub

    ''' <summary>
    ''' 渲染后端日志中的多行进度条（显示所有活跃传输任务）
    ''' 在日志末尾维护一个进度区，每个活跃任务一行文字进度条。
    ''' </summary>
    Private Sub RenderTransferProgress()
        If txtLog Is Nothing Then Return
        Dim tasks As List(Of TransferTask)
        SyncLock _transferLock
            tasks = _transferTasks.Values.Where(Function(t) Not t.Done).ToList()
        End SyncLock

        Dim lines As New List(Of String)(txtLog.Lines)

        ' 移除旧进度区（以 [进度] 标记开头的行）
        Dim kept As New List(Of String)()
        For Each ln In lines
            If Not ln.Contains("[进度]") Then
                kept.Add(ln)
            End If
        Next

        ' 追加新的进度区
        If tasks.Count > 0 Then
            Dim timeStr As String = DateTime.Now.ToString("HH:mm:ss")
            For Each t In tasks
                Dim percent As Double = If(t.Total > 0, t.Current * 100.0 / t.Total, 0)
                Dim bar As String = BuildTextBar(percent)
                Dim speedStr As String = If(t.Speed > 0, FormatFileSize(CLng(t.Speed)) & "/s", "")
                kept.Add($"[{timeStr}] [进度] {t.Direction} {t.Name} {bar} {percent:F1}% {FormatFileSize(t.Current)}/{FormatFileSize(t.Total)} {speedStr}")
            Next
        End If

        txtLog.Lines = kept.ToArray()
        txtLog.SelectionStart = txtLog.TextLength
        txtLog.ScrollToCaret()
    End Sub

    ''' <summary>生成文字进度条</summary>
    Private Function BuildTextBar(percent As Double) As String
        If percent < 0 Then percent = 0
        If percent > 100 Then percent = 100
        Dim totalBlocks As Integer = 20
        Dim filled As Integer = CInt(Math.Round(percent / 100 * totalBlocks))
        Dim bar As New StringBuilder()
        For i As Integer = 0 To totalBlocks - 1
            If i < filled Then bar.Append("█") Else bar.Append("░")
        Next
        Return bar.ToString()
    End Function

    Private Sub UpdateDownloadProgress(current As Long, total As Long, fileName As String, Optional final As Boolean = False)
        If Me.InvokeRequired Then
            Me.Invoke(Sub() UpdateDownloadProgress(current, total, fileName, final))
            Return
        End If
        If total <= 0 Then Return

        Dim id As String = "dl:" & fileName
        Dim task As TransferTask = GetTransferTask(id, fileName, "下载", total)
        Dim now As DateTime = DateTime.Now

        ' 速度计算（节流 0.5 秒）
        Dim shouldRender As Boolean = final
        If Not final Then
            If task.LastTime = DateTime.MinValue Then
                task.LastTime = now
                task.LastBytes = current
                shouldRender = True
            Else
                Dim elapsed As Double = (now - task.LastTime).TotalSeconds
                If elapsed >= 0.5 Then
                    Dim delta As Long = current - task.LastBytes
                    If delta > 0 AndAlso elapsed > 0 Then
                        task.Speed = delta / elapsed
                    End If
                    task.LastTime = now
                    task.LastBytes = current
                    shouldRender = True
                End If
            End If
        End If

        task.Current = current
        task.Total = total

        If final Then
            task.Done = True
            task.Current = total
            ' 完成：移除任务并记录一条完成日志
            RemoveTransferTask(id)
            RenderTransferProgress()
            AddLog($"下载完成: {fileName} ({FormatFileSize(total)})")
            Return
        End If

        If shouldRender Then RenderTransferProgress()
    End Sub


    ''' <summary>
    ''' 发送单个文件（流式传输，节省内存）- 优化版
    ''' </summary>
    Private Sub SendSingleFile(res As HttpListenerResponse, filePath As String)
        Try
            Dim fileInfo As New FileInfo(filePath)
            AddLog($"开始发送文件: {fileInfo.Name} (大小: {FormatFileSize(fileInfo.Length)})")

            ' 设置响应头
            res.ContentType = GetMimeType(fileInfo.Name)

            ' 文件名编码处理：确保中文文件名正确显示
            Dim encodedFileName As String = Uri.EscapeDataString(fileInfo.Name)
            res.AddHeader("Content-Disposition", $"attachment; filename*=UTF-8''{encodedFileName}")

            ' 添加缓存控制头
            res.AddHeader("Cache-Control", "no-cache, no-store, must-revalidate")
            res.AddHeader("Pragma", "no-cache")
            res.AddHeader("Expires", "0")

            ' 设置文件大小
            res.ContentLength64 = fileInfo.Length

            ' 使用 Using 确保文件流正确关闭
            Using fs As FileStream = File.OpenRead(filePath)
                ' 设置缓冲区大小（64KB）
                Dim bufferSize As Integer = 64 * 1024
                Dim buffer(bufferSize - 1) As Byte
                Dim bytesRead As Integer
                Dim totalSent As Long = 0
                Do
                    bytesRead = fs.Read(buffer, 0, buffer.Length)
                    If bytesRead > 0 Then
                        res.OutputStream.Write(buffer, 0, bytesRead)

                        totalSent += bytesRead
                        ' 更新后端日志进度（每秒更新几次）
                        UpdateDownloadProgress(totalSent, fileInfo.Length, fileInfo.Name)

                    End If
                Loop While bytesRead > 0

                ' 确保输出流刷新
                res.OutputStream.Flush()
            End Using

            UpdateDownloadProgress(fileInfo.Length, fileInfo.Length, fileInfo.Name, final:=True)
            AddLog($"文件发送完成: {fileInfo.Name}")

        Catch ex As Exception
            Console.WriteLine($"[ERROR] SendSingleFile 异常: {ex}")
            ' 如果响应头已经发送，不能再修改状态码
            Try
                If Not res.OutputStream.CanWrite Then
                    Send500(res, $"文件传输失败: {ex.Message}")
                End If
            Catch
                ' 忽略
            End Try
        End Try
    End Sub


    ''' <summary>
    ''' 将文件夹打包为ZIP并发送 - 修复版
    ''' </summary>
    Private Sub SendFolderAsZip(res As HttpListenerResponse, folderPath As String, folderName As String)
        Dim tempZipPath As String = Path.GetTempFileName() & ".zip"

        Try
            ' 1. 创建 ZIP 文件 (使用上面的修复版方法)
            CreateZipFromFolder(folderPath, tempZipPath)

            ' 2. 验证文件是否生成成功
            If Not File.Exists(tempZipPath) OrElse New FileInfo(tempZipPath).Length = 0 Then
                Throw New Exception("ZIP文件生成失败或为空")
            End If

            ' 3. 获取文件大小
            Dim zipInfo As New FileInfo(tempZipPath)

            ' 4. 设置响应头
            res.ContentType = "application/zip"
            ' 注意：文件名最好进行 URL 编码，防止中文文件名乱码
            Dim encodedFileName As String = Uri.EscapeDataString(folderName & ".zip")
            res.AddHeader("Content-Disposition", $"attachment; filename*=UTF-8''{encodedFileName}")
            res.ContentLength64 = zipInfo.Length
            Dim zipFileName As String = folderName & ".zip"

            ' 5. 流式发送 ZIP 文件
            Using fs As FileStream = File.OpenRead(tempZipPath)
                Dim bufferSize As Integer = 64 * 1024
                Dim buffer(bufferSize - 1) As Byte
                Dim bytesRead As Integer
                Dim totalSent As Long = 0

                Do
                    bytesRead = fs.Read(buffer, 0, buffer.Length)
                    If bytesRead > 0 Then
                        res.OutputStream.Write(buffer, 0, bytesRead)
                        totalSent += bytesRead
                        UpdateDownloadProgress(totalSent, zipInfo.Length, zipFileName)
                    End If
                Loop While bytesRead > 0

                res.OutputStream.Flush()
            End Using

            UpdateDownloadProgress(zipInfo.Length, zipInfo.Length, zipFileName, final:=True)
        Catch ex As Exception
            Console.WriteLine("SendFolderAsZip Error: " & ex.ToString())

        Finally
            ' 6. 清理临时文件
            Try
                If File.Exists(tempZipPath) Then
                    File.Delete(tempZipPath)
                End If
            Catch
                ' 忽略删除错误
            End Try
        End Try
    End Sub

    ''' <summary>
    ''' 递归创建ZIP文件 - 修复版
    ''' </summary>
    Private Sub CreateZipFromFolder(sourceFolder As String, zipFilePath As String)
        ' 如果文件已存在，先删除，避免追加模式导致的问题
        If File.Exists(zipFilePath) Then
            File.Delete(zipFilePath)
        End If

        ' 使用 ZipFile.CreateFromDirectory 是最简单且最可靠的方法
        ' 它会自动处理递归和流关闭
        Try
            System.IO.Compression.ZipFile.CreateFromDirectory(
            sourceFolder,
            zipFilePath,
            System.IO.Compression.CompressionLevel.Optimal,
            False ' includeBaseDirectory: False 表示不包含顶层文件夹名，只包含内容
        )
        Catch ex As Exception
            Throw New Exception("创建ZIP失败: " & ex.Message)
        End Try
    End Sub


    ''' <summary>
    ''' 递归添加文件夹内容到ZIP
    ''' </summary>
    Private Sub AddFolderToZip(sourceFolder As String, relativePath As String, archive As ZipArchive)
        ' 添加当前目录下的文件
        For Each filePath As String In Directory.GetFiles(sourceFolder)
            Dim fileName As String = Path.GetFileName(filePath)
            Dim entryName As String = Path.Combine(relativePath, fileName).Replace("\", "/") ' ZIP内部使用正斜杠

            ' CreateEntryFromFile 是 .NET 4.5+ 的便捷方法
            archive.CreateEntryFromFile(filePath, entryName)
        Next

        ' 递归添加子目录
        For Each dirPath As String In Directory.GetDirectories(sourceFolder)
            Dim dirName As String = Path.GetFileName(dirPath)
            Dim newRelativePath As String = Path.Combine(relativePath, dirName)
            AddFolderToZip(dirPath, newRelativePath, archive)
        Next
    End Sub

    ''' <summary>
    ''' 发送单个文件信息（JSON格式）
    ''' </summary>
    Private Sub SendFileInfo(res As HttpListenerResponse, fileName As String)
        Try
            Dim filePath As String = Path.Combine(SharedFolderPath, fileName)

            If File.Exists(filePath) Then
                Dim fileInfo As New FileInfo(filePath)
                ' 使用转义函数处理文件名
                Dim escapedName As String = EscapeJsonString(fileName)
                Dim json As String = $"{{\""name\"":\""{escapedName}\"",\""size\"":{fileInfo.Length},\""lastModified\"":\""{fileInfo.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")}\""}}"
                Dim buffer As Byte() = Encoding.UTF8.GetBytes(json)

                res.ContentType = "application/json; charset=utf-8"
                res.ContentLength64 = buffer.Length
                res.OutputStream.Write(buffer, 0, buffer.Length)
            Else
                Send404(res)
            End If
        Catch ex As Exception
            Send500(res, "获取文件信息失败: " & ex.Message)
        End Try
    End Sub


    ''' <summary>
    ''' 根据文件扩展名获取MIME类型
    ''' </summary>
    Private Function GetMimeType(fileName As String) As String
        Dim ext As String = Path.GetExtension(fileName).ToLower()
        Select Case ext
            Case ".jpg", ".jpeg"
                Return "image/jpeg"
            Case ".png"
                Return "image/png"
            Case ".gif"
                Return "image/gif"
            Case ".pdf"
                Return "application/pdf"
            Case ".txt"
                Return "text/plain"
            Case ".zip"
                Return "application/zip"
            Case ".rar"
                Return "application/x-rar-compressed"
            Case ".doc", ".docx"
                Return "application/msword"
            Case ".xls", ".xlsx"
                Return "application/vnd.ms-excel"
            Case ".mp3"
                Return "audio/mpeg"
            Case ".mp4"
                Return "video/mp4"
            Case Else
                Return "application/octet-stream"
        End Select
    End Function
    ''' <summary>
    ''' 发送403禁止访问
    ''' </summary>
    Private Sub Send403(res As HttpListenerResponse, message As String)
        Try
            Dim msg As Byte() = Encoding.UTF8.GetBytes($"403 Forbidden: {message}")
            res.StatusCode = 403
            res.ContentLength64 = msg.Length
            res.OutputStream.Write(msg, 0, msg.Length)
        Catch
            ' 客户端已断开：静默处理
        End Try
    End Sub
    Private Sub SendHtml(res As HttpListenerResponse, Optional clientIP As String = "", Optional userAgent As String = "")
        Try
            ' 从独立网页文件读取 HTML（位于程序目录 web/index.html）
            Dim htmlPath As String = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web", "index.html")
            If Not File.Exists(htmlPath) Then
                AddLog("网页文件缺失: " & htmlPath)
                Send500(res, "网页文件缺失: web/index.html")
                Return
            End If

            Dim html As String = File.ReadAllText(htmlPath, Encoding.UTF8)
            Dim buffer As Byte() = Encoding.UTF8.GetBytes(html)

            res.ContentType = "text/html; charset=utf-8"
            res.ContentLength64 = buffer.Length
            res.OutputStream.Write(buffer, 0, buffer.Length)

            ' 提示外部设备连接成功
            Me.Invoke(Sub()
                          AutoWindow($"外部设备IP:{clientIP} UA:{userAgent}连接成功!!!", 2, "外部设备连接成功提示")
                      End Sub)
        Catch ex As Exception
            AddLog($"SendHtml 读取网页文件失败: {ex.Message}")
            Send500(res, ex.Message)
        End Try
    End Sub
    Private Function FormatFileSize(bytes As Long) As String
        If bytes < 0 Then Return "0 B"
        Dim sizes As String() = {"B", "KB", "MB", "GB", "TB"}
        Dim i As Integer = 0
        Dim dbytes As Double = bytes
        While dbytes >= 1024 AndAlso i < sizes.Length - 1
            dbytes /= 1024
            i += 1
        End While
        Return $"{dbytes:0.##} {sizes(i)}"
    End Function

    ' --- 上传进度跟踪（使用多任务进度条） ---
    Private Sub UpdateUploadProgress(taskId As String, current As Long, total As Long, Optional final As Boolean = False)
        If Me.InvokeRequired Then
            Me.Invoke(Sub() UpdateUploadProgress(taskId, current, total, final))
            Return
        End If
        If total <= 0 Then Return

        ' 上传任务使用唯一 id（taskId），支持多文件并发上传互不覆盖
        ' 任务名带编号，便于区分多个并发上传
        Dim taskNum As Integer = 0
        If taskId.StartsWith("ul:") Then Integer.TryParse(taskId.Substring(3), taskNum)
        Dim task As TransferTask = GetTransferTask(taskId, $"前端上传#{taskNum}", "上传", total)
        Dim now As DateTime = DateTime.Now

        Dim shouldRender As Boolean = final
        If Not final Then
            If task.LastTime = DateTime.MinValue Then
                task.LastTime = now
                task.LastBytes = current
                shouldRender = True
            Else
                Dim elapsed As Double = (now - task.LastTime).TotalSeconds
                If elapsed >= 0.5 Then
                    Dim delta As Long = current - task.LastBytes
                    If delta > 0 AndAlso elapsed > 0 Then
                        task.Speed = delta / elapsed
                    End If
                    task.LastTime = now
                    task.LastBytes = current
                    shouldRender = True
                End If
            End If
        End If

        task.Current = current
        task.Total = total

        If final Then
            task.Done = True
            task.Current = total
            RemoveTransferTask(taskId)
            RenderTransferProgress()
            AddLog($"前端上传完成: {FormatFileSize(total)}")
            Return
        End If

        If shouldRender Then RenderTransferProgress()
    End Sub
    Private Sub HandleUpload(req As HttpListenerRequest, res As HttpListenerResponse,
                         Optional clientIP As String = "", Optional userAgent As String = "")
        ' 为本次上传请求分配唯一任务 ID（多文件并发上传时互不覆盖）
        Dim taskId As String = "ul:" & Interlocked.Increment(_uploadTaskCounter).ToString()
        Try
            ' 检查是否为 multipart
            If Not req.ContentType.Contains("multipart/form-data") Then
                Send400(res, "无效的请求类型")
                Return
            End If

            ' 提取 boundary
            Dim boundary As String = ""
            Dim parts As String() = req.ContentType.Split(";"c)
            For Each part In parts
                If part.Trim().StartsWith("boundary=") Then
                    boundary = part.Trim().Substring(9).Trim(""""c)
                    Exit For
                End If
            Next
            If String.IsNullOrEmpty(boundary) Then
                Send400(res, "无法解析 boundary")
                Return
            End If

            ' 将上传数据保存到临时文件（避免内存溢出）
            Dim tempFile As String = Path.GetTempFileName()
            Try
                Dim totalBytes As Long = req.ContentLength64
                Dim uploadedBytes As Long = 0

                Using inputStream As Stream = req.InputStream
                    Using fs As New FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, FileOptions.SequentialScan)
                        Dim buffer(81920) As Byte
                        Dim read As Integer
                        While True
                            read = inputStream.Read(buffer, 0, buffer.Length)
                            If read = 0 Then Exit While
                            fs.Write(buffer, 0, read)
                            uploadedBytes += read
                            If totalBytes > 0 Then
                                UpdateUploadProgress(taskId, uploadedBytes, totalBytes)
                            End If
                        End While
                    End Using
                End Using
                If totalBytes > 0 Then
                    UpdateUploadProgress(taskId, uploadedBytes, totalBytes, final:=True)
                End If

                ' 使用文件流解析 multipart
                Using fs As New FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, FileOptions.SequentialScan)
                    Dim boundaryBytes As Byte() = Encoding.UTF8.GetBytes("--" & boundary)
                    Dim crlf As Byte() = Encoding.UTF8.GetBytes(vbCrLf)
                    Dim crlfcrlf As Byte() = Encoding.UTF8.GetBytes(vbCrLf & vbCrLf)
                    Dim fileCount As Integer = 0
                    Dim pos As Long = 0
                    Dim fileLen As Long = fs.Length

                    ' 存储前端传来的目标路径（子文件夹）
                    Dim uploadTargetPath As String = ""

                    ' 查找第一个 boundary
                    pos = FindInStream(fs, boundaryBytes, 0)
                    If pos < 0 Then
                        Send400(res, "未找到 boundary")
                        Return
                    End If
                    pos += boundaryBytes.Length
                    If PeekMatch(fs, pos, crlf) Then pos += crlf.Length

                    While pos < fileLen
                        Dim nextBoundaryPos As Long = FindInStream(fs, boundaryBytes, pos)
                        If nextBoundaryPos < 0 Then Exit While

                        Dim headerStart As Long = pos
                        Dim headerEnd As Long = FindInStream(fs, crlfcrlf, headerStart)
                        If headerEnd < 0 OrElse headerEnd >= nextBoundaryPos Then Exit While

                        Dim headerLength As Integer = CInt(headerEnd - headerStart)
                        fs.Seek(headerStart, SeekOrigin.Begin)
                        Dim headerBytes(headerLength - 1) As Byte
                        fs.ReadExactly(headerBytes, 0, headerLength)
                        Dim headerStr As String = Encoding.UTF8.GetString(headerBytes)

                        ' 检查是否是 targetPath 字段（普通表单字段）
                        If headerStr.Contains("name=""targetPath""") Then
                            ' 提取数据内容作为目标路径
                            Dim dataStart As Long = headerEnd + 4
                            Dim dataEnd As Long = nextBoundaryPos - 2
                            If dataEnd > dataStart Then
                                Dim dataLength As Long = dataEnd - dataStart
                                fs.Seek(dataStart, SeekOrigin.Begin)
                                Dim targetBuffer(dataLength - 1) As Byte
                                fs.ReadExactly(targetBuffer, 0, targetBuffer.Length)
                                uploadTargetPath = Encoding.UTF8.GetString(targetBuffer).Trim()
                                AddLog($"接收到上传目标路径: {uploadTargetPath}")
                            End If
                            ' 跳过该部分
                            pos = nextBoundaryPos + boundaryBytes.Length
                            If PeekMatch(fs, pos, crlf) Then pos += crlf.Length
                            Continue While
                        End If

                        ' 解析文件部分
                        Dim fileName As String = ExtractFileName(headerStr)
                        If Not String.IsNullOrEmpty(fileName) Then
                            Dim dataStart As Long = headerEnd + 4
                            Dim dataEnd As Long = nextBoundaryPos - 2
                            If dataEnd > dataStart Then
                                Dim dataLength As Long = dataEnd - dataStart

                                ' 安全验证
                                If Not fileName.Contains("..\") AndAlso Not fileName.Contains("../") Then
                                    ' 构建保存路径：如果指定了子文件夹，则拼接到路径中
                                    Dim relativePath As String = fileName
                                    If Not String.IsNullOrEmpty(uploadTargetPath) Then
                                        ' 将前端传来的正斜杠路径转换为 Windows 反斜杠
                                        Dim targetDir = uploadTargetPath.Replace("/", "\")
                                        relativePath = Path.Combine(targetDir, fileName)
                                    End If
                                    Dim savePath As String = Path.Combine(SharedFolderPath, relativePath)
                                    Dim saveDir As String = Path.GetDirectoryName(savePath)
                                    If Not Directory.Exists(saveDir) Then
                                        Directory.CreateDirectory(saveDir)
                                    End If
                                    ' 重名文件直接覆盖（FileMode.Create），不再自动追加序号

                                    ' 流式写入文件
                                    Using outFs As New FileStream(savePath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, FileOptions.SequentialScan)
                                        fs.Seek(dataStart, SeekOrigin.Begin)
                                        Dim buffer(81920) As Byte
                                        Dim remaining As Long = dataLength
                                        While remaining > 0
                                            Dim toRead As Integer = CInt(Math.Min(buffer.Length, remaining))
                                            Dim readBytes As Integer = fs.Read(buffer, 0, toRead)
                                            If readBytes = 0 Then Exit While
                                            outFs.Write(buffer, 0, readBytes)
                                            remaining -= readBytes
                                        End While
                                    End Using
                                    fileCount += 1
                                    AddLog($"上传文件: {relativePath} (大小: {dataLength} 字节)")
                                    RecordTransfer(fileName, "upload", dataLength, clientIP, savePath)
                                End If
                            End If
                        End If

                        ' 移动到下一个 boundary 之后
                        pos = nextBoundaryPos + boundaryBytes.Length
                        If PeekMatch(fs, pos, crlf) Then pos += crlf.Length
                    End While

                    ' 返回成功
                    Dim responseMsg As String = $"成功上传 {fileCount} 个文件"
                    Dim bufferOut As Byte() = Encoding.UTF8.GetBytes(responseMsg)
                    res.StatusCode = 200
                    res.ContentLength64 = bufferOut.Length
                    res.OutputStream.Write(bufferOut, 0, bufferOut.Length)
                    AddLog($"IP:{clientIP} UA:{userAgent} 上传完成，共 {fileCount} 个文件")
                End Using
            Finally
                Try : File.Delete(tempFile) : Catch : End Try
            End Try

        Catch ex As Exception
            Console.WriteLine("Upload Error: " & ex.ToString())
            Send500(res, "上传失败: " & ex.Message)
        End Try
    End Sub
    ''' <summary>
    ''' 在流中查找字节序列的位置
    ''' </summary>
    Private Function FindInStream(stream As Stream, pattern As Byte(), startPos As Long) As Long
        Const BUFFER_SIZE As Integer = 81920
        Dim buffer(BUFFER_SIZE - 1) As Byte
        Dim patternLen As Integer = pattern.Length
        If patternLen = 0 Then Return -1

        stream.Seek(startPos, SeekOrigin.Begin)
        Dim pos As Long = startPos
        While pos <= stream.Length - patternLen
            Dim toRead As Integer = CInt(Math.Min(BUFFER_SIZE, stream.Length - pos))
            stream.ReadExactly(buffer, 0, toRead)
            For i As Integer = 0 To toRead - patternLen
                Dim match As Boolean = True
                For j As Integer = 0 To patternLen - 1
                    If buffer(i + j) <> pattern(j) Then
                        match = False
                        Exit For
                    End If
                Next
                If match Then Return pos + i
            Next
            pos += toRead - patternLen + 1
            stream.Seek(pos, SeekOrigin.Begin)
        End While
        Return -1
    End Function

    ''' <summary>
    ''' 检查流指定位置是否匹配字节序列（不移动位置）
    ''' </summary>
    Private Function PeekMatch(stream As Stream, pos As Long, pattern As Byte()) As Boolean
        If pos + pattern.Length > stream.Length Then Return False
        Dim oldPos As Long = stream.Position
        stream.Seek(pos, SeekOrigin.Begin)
        Dim buffer(pattern.Length - 1) As Byte
        Dim readBytes As Integer = stream.Read(buffer, 0, buffer.Length)
        stream.Seek(oldPos, SeekOrigin.Begin)
        If readBytes <> pattern.Length Then Return False
        For i As Integer = 0 To pattern.Length - 1
            If buffer(i) <> pattern(i) Then Return False
        Next
        Return True
    End Function

    ''' <summary>
    ''' 从头部提取文件名
    ''' </summary>
    Private Function ExtractFileName(header As String) As String
        Dim pattern As String = "filename=""(.*?)"""
        Dim match As Match = Regex.Match(header, pattern)
        Return If(match.Success, match.Groups(1).Value, "")
    End Function

    ''' <summary>
    ''' 生成唯一的文件路径
    ''' </summary>
    ''' <summary>
    ''' 上传的文件信息类
    ''' </summary>
    Private Class UploadedFile
        Public Property FileName As String
        Public Property Data As Byte()
    End Class



    ''' <summary>
    ''' ngrok 按钮点击事件 - 开启/关闭外网隧道
    ''' </summary>
    Private Async Sub BtnNgrok_Click(sender As Object, e As EventArgs)
        btnNgrok.Enabled = False

        Try
            If isNgrokRunning Then
                Await StopNgrokAsync()
            Else
                Await StartNgrokAsync()
            End If
        Catch ex As Exception
            AddLog($"ngrok 操作失败: {ex.Message}")
            AutoWindow($"操作失败: {ex.Message}", 3, "错误")
        Finally
            btnNgrok.Enabled = True
        End Try
    End Sub

    ' ========== 外网自动监听时间段相关方法 ==========

    ''' <summary>保存外网自动监听配置到文件</summary>
    Private Sub SaveNgrokAutoConfig()
        Try
            Dim cfgPath As String = GetConfigPath(NGROK_CONFIG_FILE)
            Dim startStr As String = txtNgrokStart.Value.ToString("HH:mm")
            Dim endStr As String = txtNgrokEnd.Value.ToString("HH:mm")
            Dim enabled As String = If(chkAutoNgrok.Checked, "1", "0")
            File.WriteAllText(cfgPath, $"{enabled}|{startStr}|{endStr}", Encoding.UTF8)
        Catch ex As Exception
            AddLog($"保存外网自动监听配置失败: {ex.Message}")
        End Try
    End Sub

    ''' <summary>从文件加载外网自动监听配置</summary>
    Private Sub LoadNgrokAutoConfig()
        Try
            ' 加载期间禁止触发保存，避免用默认时间覆盖配置
            _loadingNgrokConfig = True

            Dim cfgPath As String = GetConfigPath(NGROK_CONFIG_FILE)
            If File.Exists(cfgPath) Then
                Dim parts As String() = File.ReadAllText(cfgPath, Encoding.UTF8).Trim().Split("|"c)
                If parts.Length >= 3 Then
                    ' 先设置时间，再设置勾选状态（避免 CheckedChanged 过早触发保存）
                    Dim st As DateTime
                    Dim ed As DateTime
                    If DateTime.TryParse(parts(1), st) Then
                        txtNgrokStart.Value = DateTime.Today.Add(st.TimeOfDay)
                    End If
                    If DateTime.TryParse(parts(2), ed) Then
                        txtNgrokEnd.Value = DateTime.Today.Add(ed.TimeOfDay)
                    End If
                    chkAutoNgrok.Checked = parts(0) = "1"
                End If
            Else
                ' 默认值：不启用，时间段 09:00 - 18:00
                txtNgrokStart.Value = DateTime.Today.AddHours(9)
                txtNgrokEnd.Value = DateTime.Today.AddHours(18)
                chkAutoNgrok.Checked = False
            End If
        Catch ex As Exception
            AddLog($"加载外网自动监听配置失败: {ex.Message}")
        Finally
            _loadingNgrokConfig = False
        End Try
        UpdateNgrokAutoStatusLabel()
    End Sub

    ''' <summary>勾选框状态变化事件</summary>
    Private Sub ChkAutoNgrok_CheckedChanged(sender As Object, e As EventArgs)
        If txtNgrokStart Is Nothing OrElse txtNgrokEnd Is Nothing Then Return
        ' 校验时间段长度不超过每日免费额度
        Dim minutes As Integer = GetNgrokTimeWindowMinutes()
        If chkAutoNgrok.Checked AndAlso minutes > MAX_DAILY_NGROK_MINUTES Then
            AutoWindow($"时间段长度（{minutes} 分钟）超过每日免费额度（{MAX_DAILY_NGROK_MINUTES} 分钟），已自动限制。", 3, "额度提示")
        End If
        If Not _loadingNgrokConfig Then
            SaveNgrokAutoConfig()
        End If
        UpdateNgrokAutoStatusLabel()
    End Sub

    ''' <summary>时间选择器变化事件：保存配置</summary>
    Private Sub NgrokTime_ValueChanged(sender As Object, e As EventArgs)
        If _loadingNgrokConfig Then Return
        If txtNgrokStart Is Nothing OrElse txtNgrokEnd Is Nothing Then Return
        SaveNgrokAutoConfig()
        UpdateNgrokAutoStatusLabel()
    End Sub

    ''' <summary>计算时间段长度（分钟），支持跨天</summary>
    Private Function GetNgrokTimeWindowMinutes() As Integer
        Dim startMins As Integer = (txtNgrokStart.Value.Hour * 60) + txtNgrokStart.Value.Minute
        Dim endMins As Integer = (txtNgrokEnd.Value.Hour * 60) + txtNgrokEnd.Value.Minute
        If endMins > startMins Then
            Return endMins - startMins
        ElseIf endMins < startMins Then
            ' 跨天
            Return 1440 - startMins + endMins
        Else
            Return 0
        End If
    End Function

    ''' <summary>判断当前时间是否在设定的自动监听时间段内</summary>
    Private Function IsInNgrokTimeWindow() As Boolean
        If Not chkAutoNgrok.Checked Then Return False
        Dim nowMins As Integer = (DateTime.Now.Hour * 60) + DateTime.Now.Minute
        Dim startMins As Integer = (txtNgrokStart.Value.Hour * 60) + txtNgrokStart.Value.Minute
        Dim endMins As Integer = (txtNgrokEnd.Value.Hour * 60) + txtNgrokEnd.Value.Minute

        If startMins = endMins Then Return False
        If endMins > startMins Then
            Return nowMins >= startMins AndAlso nowMins < endMins
        Else
            ' 跨天
            Return nowMins >= startMins OrElse nowMins < endMins
        End If
    End Function

    ''' <summary>启动外网自动监听调度定时器（每5秒检查一次）</summary>
    Private Sub StartNgrokAutoScheduler()
        If ngrokAutoTimer IsNot Nothing Then Return
        ngrokAutoTimer = New System.Timers.Timer(5000) With {.AutoReset = True}
        AddHandler ngrokAutoTimer.Elapsed, AddressOf NgrokAutoScheduler_Tick
        ngrokAutoTimer.Start()

        ' 窗体加载完成后立即执行一次检查，避免等待第一个定时周期
        Me.BeginInvoke(Sub()
                           NgrokAutoScheduler_Tick(Nothing, Nothing)
                       End Sub)
    End Sub

    ''' <summary>调度定时器触发：进入时间段自动启动 ngrok，离开则停止</summary>
    Private Sub NgrokAutoScheduler_Tick(sender As Object, e As EventArgs)
        Try
            Dim inWindow As Boolean = IsInNgrokTimeWindow()

            If inWindow Then
                ' 时间段内：如果未启动 ngrok 且未处于自动启动流程中，则自动启动
                If Not isNgrokRunning AndAlso Not autoNgrokActive Then
                    autoNgrokActive = True
                    RunOnUI(Sub()
                                AddLog("已进入外网自动监听时间段，尝试自动开启外网...")
                                Dim t As Task = StartNgrokForAutoAsync()
                            End Sub)
                End If
            Else
                ' 时间段外：如果 ngrok 是自动启动的，则停止
                If autoNgrokActive AndAlso isNgrokRunning Then
                    autoNgrokActive = False
                    RunOnUI(Sub()
                                AddLog("已离开外网自动监听时间段，自动关闭外网...")
                                StopNgrok()
                            End Sub)
                ElseIf autoNgrokActive Then
                    autoNgrokActive = False
                End If
            End If
        Catch ex As Exception
            ' 忽略调度异常
        End Try
    End Sub

    ''' <summary>在 UI 线程安全地执行操作（避免 Invoke 在 UI 线程内死锁）</summary>
    Private Sub RunOnUI(action As Action)
        If Me.IsHandleCreated AndAlso Not Me.IsDisposed Then
            If Me.InvokeRequired Then
                Me.BeginInvoke(action)
            Else
                action()
            End If
        End If
    End Sub

    ''' <summary>自动监听模式下启动 ngrok（不要求服务已运行，直接隧道到端口）</summary>
    Private Async Function StartNgrokForAutoAsync() As Task
        Try
            Dim port As Integer
            If Not Integer.TryParse(txtPort.Text.Trim(), port) Then
                port = DefaultPort
            End If

            Dim ngrokPath As String = GetConfigPath(NGROK_EXE_NAME)
            If Not File.Exists(ngrokPath) Then
                AddLog("未找到 ngrok.exe，无法自动开启外网监听")
                autoNgrokActive = False
                Return
            End If

            ' 直接启动 ngrok 进程（不强制要求服务已启动）
            Await Task.Run(Sub() StartNgrokProcessDirect(ngrokPath, port))
        Catch ex As Exception
            AddLog($"自动开启外网失败: {ex.Message}")
            autoNgrokActive = False
        End Try
    End Function

    ''' <summary>直接启动 ngrok 进程（供自动监听调用，服务可未启动）</summary>
    Private Sub StartNgrokProcessDirect(ngrokPath As String, port As Integer)
        Try
            ' 清理旧进程
            Try
                Dim killProcess As New Process() With {
                    .StartInfo = New ProcessStartInfo() With {
                        .FileName = "taskkill",
                        .Arguments = "/f /im ngrok.exe",
                        .UseShellExecute = False,
                        .CreateNoWindow = True
                    }
                }
                killProcess.Start()
                killProcess.WaitForExit(3000)
                killProcess.Dispose()
            Catch
            End Try

            If ngrokProcess IsNot Nothing AndAlso Not ngrokProcess.HasExited Then
                ngrokProcess.Kill()
                ngrokProcess.Dispose()
                ngrokProcess = Nothing
            End If

            Dim token As String = LoadNgrokAuthToken()
            If String.IsNullOrEmpty(token) Then
                token = "3IBOG3gWebOvBUL7ww36ZAKryYY_7ZsiJQdM25w27EBFnDabY"
            End If

            ' 配置认证
            Dim cfg As New Process() With {
                .StartInfo = New ProcessStartInfo() With {
                    .FileName = ngrokPath,
                    .Arguments = $"config add-authtoken {token}",
                    .UseShellExecute = False,
                    .CreateNoWindow = True
                }
            }
            cfg.Start()
            cfg.WaitForExit(5000)
            cfg.Dispose()

            ' 启动 ngrok
            Dim si As New ProcessStartInfo() With {
                .FileName = ngrokPath,
                .Arguments = $"http {port} --log=stdout --log-level=debug --region=jp",
                .UseShellExecute = False,
                .CreateNoWindow = True,
                .RedirectStandardOutput = True,
                .RedirectStandardError = True,
                .StandardOutputEncoding = Encoding.UTF8,
                .StandardErrorEncoding = Encoding.UTF8
            }

            ngrokProcess = New Process() With {
                .StartInfo = si,
                .EnableRaisingEvents = True
            }

            RemoveHandler ngrokProcess.OutputDataReceived, AddressOf NgrokOutputHandler
            RemoveHandler ngrokProcess.ErrorDataReceived, AddressOf NgrokErrorHandler
            RemoveHandler ngrokProcess.Exited, AddressOf NgrokExitedHandler
            AddHandler ngrokProcess.OutputDataReceived, AddressOf NgrokOutputHandler
            AddHandler ngrokProcess.ErrorDataReceived, AddressOf NgrokErrorHandler
            AddHandler ngrokProcess.Exited, AddressOf NgrokExitedHandler

            ngrokProcess.Start()
            ngrokProcess.BeginOutputReadLine()
            ngrokProcess.BeginErrorReadLine()

            isNgrokRunning = True

            Me.Invoke(Sub()
                          AddLog("✅ 外网自动监听已开启，等待外网访问自动启动服务...")
                          UpdateNgrokAutoStatusLabel()
                      End Sub)
        Catch ex As Exception
            AddLog($"自动启动 ngrok 失败: {ex.Message}")
            autoNgrokActive = False
        End Try
    End Sub

    ''' <summary>复制外网地址按钮点击事件</summary>
    Private Sub LblNgrokCopy_Click(sender As Object, e As EventArgs)
        If String.IsNullOrEmpty(ngrokUrl) Then Return
        Try
            Clipboard.SetText(ngrokUrl)
            AutoWindow("外网地址已复制到剪贴板", 1, "复制成功")
        Catch ex As Exception
            AddLog($"复制外网地址失败: {ex.Message}")
        End Try
    End Sub

    ''' <summary>更新时间段状态提示标签</summary>
    Private Sub UpdateNgrokAutoStatusLabel()
        If lblNgrokStatus Is Nothing Then Return
        If chkAutoNgrok.Checked Then
            Dim mins As Integer = GetNgrokTimeWindowMinutes()
            lblNgrokStatus.Text = $"已启用 ({mins}分钟/日, 限额{MAX_DAILY_NGROK_MINUTES}分钟)"
            lblNgrokStatus.ForeColor = Color.FromArgb(0, 122, 255)
        Else
            lblNgrokStatus.Text = "未启用"
            lblNgrokStatus.ForeColor = Color.Gray
        End If
    End Sub

    ''' <summary>
    ''' 更新外网二维码框的显示/隐藏。
    ''' 显示条件：ngrok 正在运行且已获取到外网地址
    ''' （ngrok 运行说明：要么处于自动监听时间段内自动启动，要么用户手动开启外网）。
    ''' </summary>
    Private Sub UpdateWanQrVisibility()
        If pbQrCodeWan Is Nothing OrElse lblWanHint Is Nothing Then Return

        Dim show As Boolean = isNgrokRunning AndAlso Not String.IsNullOrEmpty(ngrokUrl)
        pbQrCodeWan.Visible = show
        lblWanHint.Visible = show

        If Not show Then
            ' 隐藏时清空外网二维码并复位提示
            If pbQrCodeWan.Image IsNot Nothing Then
                pbQrCodeWan.Image.Dispose()
                pbQrCodeWan.Image = Nothing
            End If
            lblWanHint.ForeColor = Color.Gray
            lblWanHint.Text = "外网二维码（未开启）"
        End If
    End Sub

    Private Async Function StartNgrokAsync() As Task
        Try
            ' 1. 检查服务是否已启动
            If Not isRunning Then
                AutoWindow("请先启动本地服务（点击'启动服务'按钮）", 2, "提示")
                Return
            End If

            ' 2. 获取端口
            Dim port As Integer
            If Not Integer.TryParse(txtPort.Text.Trim(), port) Then
                port = DefaultPort
            End If

            ' 3. 检查 ngrok.exe 是否存在
            Dim ngrokPath As String = GetConfigPath(NGROK_EXE_NAME)

            If Not File.Exists(ngrokPath) Then
                AutoWindow(
                "未找到 ngrok.exe" & vbCrLf & vbCrLf &
                "请按以下步骤操作：" & vbCrLf &
                "1. 访问 https://bin.ngrok.com/c/bNyj1mQVY4c/ngrok-v3-stable-windows-amd64.zip" & vbCrLf &
                "2. 下载 Windows 64-bit 版本" & vbCrLf &
                "3. 解压后将 ngrok.exe 放入程序目录" & vbCrLf &
                "4. 重新点击'开启外网'",
                5,
                "缺少 ngrok"
            )
                Return
            End If

            ' 4. 启动 ngrok 进程
            Await StartNgrokProcessAsync(ngrokPath, port)

        Catch ex As Exception
            AddLog($"启动 ngrok 失败: {ex.Message}")
            Throw
        End Try
    End Function

    ''' <summary>
    ''' 启动 ngrok 进程（异步）
    ''' </summary>
    ''' <summary>
    ''' 启动 ngrok 进程（异步）- 修复版
    ''' </summary>
    Private Function StartNgrokProcessAsync(ngrokPath As String, port As Integer) As Task
        Return Task.Run(Sub()
                            Try
                                ' ========== 先强制结束所有 ngrok 进程 ==========
                                Try
                                    Dim killProcess As New Process() With {
                                        .StartInfo = New ProcessStartInfo() With {
                                            .FileName = "taskkill",
                                            .Arguments = "/f /im ngrok.exe",
                                            .UseShellExecute = False,
                                            .CreateNoWindow = True,
                                            .RedirectStandardOutput = True
                                        }
                                    }
                                    killProcess.Start()
                                    killProcess.WaitForExit(3000)
                                    killProcess.Dispose()
                                    AddLog("已清理旧的 ngrok 进程")
                                Catch ex As Exception
                                    ' 忽略
                                End Try

                                ' 如果已有进程，先停止
                                If ngrokProcess IsNot Nothing AndAlso Not ngrokProcess.HasExited Then
                                    ngrokProcess.Kill()
                                    ngrokProcess.Dispose()
                                    ngrokProcess = Nothing
                                End If

                                Dim AUTH_TOKEN As String = LoadNgrokAuthToken()
                                If String.IsNullOrEmpty(AUTH_TOKEN) Then
                                    AUTH_TOKEN = "3IBOG3gWebOvBUL7ww36ZAKryYY_7ZsiJQdM25w27EBFnDabY"
                                End If
                                Me.Invoke(Sub()
                                              AddLog("正在配置 ngrok 认证...")
                                              btnNgrok.Text = "⏳ 认证中..."
                                          End Sub)

                                ' 配置认证
                                Dim configProcess As New Process() With {
                                    .StartInfo = New ProcessStartInfo() With {
                                        .FileName = ngrokPath,
                                        .Arguments = $"config add-authtoken {AUTH_TOKEN}",
                                        .UseShellExecute = False,
                                        .CreateNoWindow = True,
                                        .RedirectStandardOutput = True,
                                        .RedirectStandardError = True
                                    }
                                }
                                configProcess.Start()
                                configProcess.WaitForExit(5000)
                                configProcess.Dispose()

                                Me.Invoke(Sub() AddLog("✅ ngrok 认证配置成功"))

                                ' ========== 启动 ngrok ==========
                                Dim startInfo As New ProcessStartInfo() With {
                                    .FileName = ngrokPath,
                                    .Arguments = $"http {port} --log=stdout --log-level=debug --region=jp",
                                    .UseShellExecute = False,
                                    .CreateNoWindow = True,
                                    .RedirectStandardOutput = True,
                                    .RedirectStandardError = True,
                                    .StandardOutputEncoding = Encoding.UTF8,
                                    .StandardErrorEncoding = Encoding.UTF8
                                }

                                ngrokProcess = New Process() With {
                                    .StartInfo = startInfo,
                                    .EnableRaisingEvents = True
                                }

                                RemoveHandler ngrokProcess.OutputDataReceived, AddressOf NgrokOutputHandler
                                RemoveHandler ngrokProcess.ErrorDataReceived, AddressOf NgrokErrorHandler
                                RemoveHandler ngrokProcess.Exited, AddressOf NgrokExitedHandler

                                AddHandler ngrokProcess.OutputDataReceived, AddressOf NgrokOutputHandler
                                AddHandler ngrokProcess.ErrorDataReceived, AddressOf NgrokErrorHandler
                                AddHandler ngrokProcess.Exited, AddressOf NgrokExitedHandler

                                ngrokProcess.Start()
                                ngrokProcess.BeginOutputReadLine()
                                ngrokProcess.BeginErrorReadLine()

                                isNgrokRunning = True

                                Me.Invoke(Sub()
                                              btnNgrok.Text = "🔴 关闭外网"
                                              btnNgrok.BackColor = Color.FromArgb(255, 59, 48)
                                              AddLog("ngrok 已启动，正在获取公网地址...")

                                              Dim lblStatus As Label = Me.Controls.Find("lblDownloadStatus", True).FirstOrDefault()
                                              If lblStatus IsNot Nothing Then
                                                  lblStatus.Text = "⏳ 正在连接 ngrok..."
                                                  lblStatus.ForeColor = Color.FromArgb(0, 122, 255)
                                                  lblStatus.Visible = True
                                              End If
                                          End Sub)

                                ' 超时检测 60 秒
                                Dim timeoutTimer As New System.Timers.Timer(60000) With {.AutoReset = False}
                                AddHandler timeoutTimer.Elapsed, Sub(sender2, e2)
                                                                     If String.IsNullOrEmpty(ngrokUrl) AndAlso isNgrokRunning Then
                                                                         Me.Invoke(Sub()
                                                                                       AddLog("⚠️ ngrok 获取地址超时 (60秒)")
                                                                                       Dim lblStatus As Label = Me.Controls.Find("lblDownloadStatus", True).FirstOrDefault()
                                                                                       If lblStatus IsNot Nothing Then
                                                                                           lblStatus.Text = "⚠️ 连接超时，请检查网络"
                                                                                           lblStatus.ForeColor = Color.Red
                                                                                           lblStatus.Visible = True
                                                                                       End If
                                                                                   End Sub)
                                                                     End If
                                                                     timeoutTimer.Dispose()
                                                                 End Sub
                                timeoutTimer.Start()

                            Catch ex As Exception
                                AddLog($"启动 ngrok 进程失败: {ex.Message}")
                                Me.Invoke(Sub()
                                              btnNgrok.Text = "🌐 开启外网"
                                              btnNgrok.BackColor = Color.FromArgb(52, 199, 89)
                                              isNgrokRunning = False
                                          End Sub)
                            End Try
                        End Sub)
    End Function

    ''' <summary>
    ''' 加载保存的 ngrok 认证令牌
    ''' </summary>
    Private Function LoadNgrokAuthToken() As String
        Try
            Dim tokenFile As String = GetConfigPath("ngrok_token.txt")
            If File.Exists(tokenFile) Then
                Return File.ReadAllText(tokenFile).Trim()
            Else
                Dim datafile As String = InputBox("请输入认证口令", "认证令牌输入", "3IBOG3gWebOvBUL7ww36ZAKryYY_7ZsiJQdM25w27EBFnDabY")
                SaveNgrokAuthToken(datafile)
            End If
        Catch ex As Exception
            AddLog($"读取认证令牌失败: {ex.Message}")
        End Try
        Return ""
    End Function

    ''' <summary>
    ''' 保存 ngrok 认证令牌
    ''' </summary>
    Private Sub SaveNgrokAuthToken(token As String)
        Try
            Dim tokenFile As String = GetConfigPath("ngrok_token.txt")
            File.WriteAllText(tokenFile, token)
            AddLog("认证令牌已保存")
        Catch ex As Exception
            AddLog($"保存认证令牌失败: {ex.Message}")
        End Try
    End Sub
    ''' <summary>
    ''' 停止 ngrok（异步）
    ''' </summary>
    Private Function StopNgrokAsync() As Task
        Return Task.Run(Sub()
                            Try


                                ' 标记为主动停止，避免 NgrokExitedHandler 误报"意外退出"
                                _isStoppingNgrok = True

                                ' 停止 ngrok 进程
                                If ngrokProcess IsNot Nothing AndAlso Not ngrokProcess.HasExited Then
                                    ' 进程未重定向 StandardInput，无法通过 stdin 优雅关闭，直接 Kill
                                    Try
                                        ngrokProcess.Kill()
                                        ngrokProcess.WaitForExit(2000)
                                    Catch
                                    End Try

                                    ngrokProcess.Dispose()
                                    ngrokProcess = Nothing
                                End If

                                isNgrokRunning = False
                                ngrokUrl = ""

                                Me.Invoke(Sub()
                                              btnNgrok.Text = "🌐 开启外网"
                                              btnNgrok.BackColor = Color.FromArgb(52, 199, 89)

                                              Dim lblPublicUrl As Label = Me.Controls.Find("lblPublicUrl", True).FirstOrDefault()
                                              If lblPublicUrl IsNot Nothing Then
                                                  lblPublicUrl.Text = ""
                                              End If

                                              ' 隐藏复制地址按钮
                                              If lblNgrokCopy IsNot Nothing Then
                                                  lblNgrokCopy.Visible = False
                                              End If

                                              Dim lblStatus As Label = Me.Controls.Find("lblDownloadStatus", True).FirstOrDefault()
                                              If lblStatus IsNot Nothing Then
                                                  lblStatus.Text = ""
                                                  lblStatus.Visible = False
                                              End If

                                              Dim pb As ProgressBar = Me.Controls.Find("pbDownloadProgress", True).FirstOrDefault()
                                              If pb IsNot Nothing Then
                                                  pb.Visible = False
                                              End If

                                              lblHint.Text = "提示:启动服务后将会显示登录二维码"

                                              ' 隐藏外网二维码框
                                              UpdateWanQrVisibility()

                                              ' 重新生成局域网二维码
                                              Dim ip As String = txtIp.Text.Trim()
                                              Dim port As Integer
                                              If Integer.TryParse(txtPort.Text.Trim(), port) Then
                                                  GenerateQrCode(ip, port)
                                              End If

                                              AddLog("ngrok 已停止")
                                          End Sub)

                            Catch ex As Exception
                                AddLog($"停止 ngrok 失败: {ex.Message}")
                            Finally
                                _isStoppingNgrok = False
                            End Try
                        End Sub)
    End Function
    ''' <summary>
    ''' 处理 ngrok 输出（解析公网地址）
    ''' </summary>
    Private Sub NgrokOutputHandler(sender As Object, e As DataReceivedEventArgs)
        If e.Data Is Nothing Then Return

        Dim line As String = e.Data.Trim()

        ' 调试日志
        If line.Length > 0 Then
            Console.WriteLine($"ngrok: {line}")
            ' 记录关键信息
            If line.Contains("url") OrElse line.Contains("Forwarding") OrElse line.Contains("error") OrElse line.Contains("ERR_") Then
                Me.Invoke(Sub() AddLog($"ngrok: {line}"))
            End If
        End If

        ' 检查错误
        If line.Contains("ERR_NGROK") Then
            Me.Invoke(Sub()
                          AddLog($"⚠️ ngrok 错误: {line}")
                          ' 如果是域名冲突错误，自动重试
                          If line.Contains("ERR_NGROK_334") Then
                              AddLog("检测到域名冲突，正在重试...")
                          End If
                      End Sub)
            Return
        End If

        Dim url As String = ""

        ' 匹配 Forwarding 格式
        Dim match As Match = Regex.Match(line, "Forwarding\s+(https?://[^\s]+)")
        If match.Success Then
            url = match.Groups(1).Value
        End If

        ' 匹配 started tunnel
        If String.IsNullOrEmpty(url) Then
            Dim match2 As Match = Regex.Match(line, "started tunnel\s+(https?://[^\s]+)")
            If match2.Success Then
                url = match2.Groups(1).Value
            End If
        End If

        ' 匹配通用 URL
        If String.IsNullOrEmpty(url) Then
            Dim match3 As Match = Regex.Match(line, "(https?://[a-z0-9-]+\.ngrok-[a-z]+\.dev)")
            If match3.Success Then
                url = match3.Groups(1).Value
            End If
        End If

        If Not String.IsNullOrEmpty(url) AndAlso url <> ngrokUrl Then
            ngrokUrl = url
            Me.Invoke(Sub()
                          Dim lblPublicUrl As Label = Me.Controls.Find("lblPublicUrl", True).FirstOrDefault()
                          If lblPublicUrl IsNot Nothing Then
                              lblPublicUrl.Text = $"🌐 {url}"
                              lblPublicUrl.ForeColor = Color.Green
                              If tooltipNgrok Is Nothing Then tooltipNgrok = New ToolTip()
                              tooltipNgrok.SetToolTip(lblPublicUrl, url)
                              tooltipNgrok.SetToolTip(lblNgrokCopy, $"复制外网地址:{vbCrLf}{url}")
                          End If

                          lblHint.Text = $"✅ 外网已开启!{vbCrLf}手机扫描二维码或访问: {url}"

                          Try
                              GenerateWanQrCode(url)
                              UpdateWanQrVisibility()
                          Catch ex As Exception
                              ' 忽略
                          End Try

                          AddLog($"✅ ngrok 公网地址: {url}")

                          Dim lblStatus As Label = Me.Controls.Find("lblDownloadStatus", True).FirstOrDefault()
                          If lblStatus IsNot Nothing Then
                              lblStatus.Text = "✅ 外网已开启"
                              lblStatus.ForeColor = Color.Green
                              lblStatus.Visible = True
                          End If

                          ' 显示复制地址按钮
                          If lblNgrokCopy IsNot Nothing Then
                              lblNgrokCopy.Visible = True
                          End If

                          ' 更新外网按钮为"关闭外网"状态（自动监听启动时服务可能未运行）
                          If btnNgrok IsNot Nothing Then
                              btnNgrok.Text = "🔴 关闭外网"
                              btnNgrok.BackColor = Color.FromArgb(255, 59, 48)
                              btnNgrok.Enabled = True
                          End If
                      End Sub)
        End If
    End Sub

    ''' <summary>
    ''' 处理 ngrok 错误输出
    ''' </summary>
    Private Sub NgrokErrorHandler(sender As Object, e As DataReceivedEventArgs)
        If e.Data Is Nothing Then Return
        Dim line As String = e.Data.Trim()

        ' 只记录重要错误
        If line.Contains("error") OrElse line.Contains("failed") OrElse line.Contains("FATA") Then
            AddLog($"ngrok 错误: {line}")
        End If
    End Sub

    ''' <summary>
    ''' ngrok 进程退出事件
    ''' </summary>
    Private Sub NgrokExitedHandler(sender As Object, e As EventArgs)
        Me.Invoke(Sub()
                      ' 主动停止时不提示"意外退出"
                      If _isStoppingNgrok Then Return

                      If isNgrokRunning Then
                          AddLog("⚠️ ngrok 进程意外退出")
                          isNgrokRunning = False
                          ngrokUrl = ""
                          btnNgrok.Text = "🌐 开启外网"
                          btnNgrok.BackColor = Color.FromArgb(52, 199, 89)

                          Dim lblPublicUrl As Label = Me.Controls.Find("lblPublicUrl", True).FirstOrDefault()
                          If lblPublicUrl IsNot Nothing Then
                              lblPublicUrl.Text = "❌ 外网已断开"
                              lblPublicUrl.ForeColor = Color.Red
                          End If

                          ' 隐藏复制地址按钮
                          If lblNgrokCopy IsNot Nothing Then
                              lblNgrokCopy.Visible = False
                          End If

                          ' 清空"外网已开启"状态标签
                          Dim lblDlStatus As Label = Me.Controls.Find("lblDownloadStatus", True).FirstOrDefault()
                          If lblDlStatus IsNot Nothing Then
                              lblDlStatus.Text = ""
                              lblDlStatus.Visible = False
                          End If

                          ' 隐藏外网二维码框
                          UpdateWanQrVisibility()

                          ' 尝试自动重连（可选）
                          ' StartNgrok()
                      End If
                  End Sub)
    End Sub

    ''' <summary>
    ''' 停止 ngrok
    ''' </summary>
    Private Sub StopNgrok()
        Try
            ' 标记为主动停止，避免 NgrokExitedHandler 误报"意外退出"
            _isStoppingNgrok = True

            If ngrokProcess IsNot Nothing AndAlso Not ngrokProcess.HasExited Then
                ' 进程未重定向 StandardInput，无法通过 stdin 优雅关闭，直接 Kill
                Try
                    ngrokProcess.Kill()
                    ngrokProcess.WaitForExit(2000)
                Catch
                End Try
                ngrokProcess.Dispose()
                ngrokProcess = Nothing
            End If

            isNgrokRunning = False
            ngrokUrl = ""

            btnNgrok.Text = "🌐 开启外网"
            btnNgrok.BackColor = Color.FromArgb(52, 199, 89)

            Dim lblPublicUrl As Label = Me.Controls.Find("lblPublicUrl", True).FirstOrDefault()
            If lblPublicUrl IsNot Nothing Then
                lblPublicUrl.Text = ""
            End If

            ' 隐藏复制地址按钮
            If lblNgrokCopy IsNot Nothing Then
                lblNgrokCopy.Visible = False
            End If

            ' 清空"外网已开启"状态标签
            Dim lblDlStatus As Label = Me.Controls.Find("lblDownloadStatus", True).FirstOrDefault()
            If lblDlStatus IsNot Nothing Then
                lblDlStatus.Text = ""
                lblDlStatus.Visible = False
            End If

            lblHint.Text = "提示:启动服务后将会显示登录二维码"

            ' 隐藏外网二维码框
            UpdateWanQrVisibility()

            ' 重新生成局域网二维码
            Dim ip As String = txtIp.Text.Trim()
            Dim port As Integer
            If Integer.TryParse(txtPort.Text.Trim(), port) Then
                GenerateQrCode(ip, port)
            End If

            AddLog("ngrok 已停止")
            AutoWindow("外网隧道已关闭", 1, "提示")

        Catch ex As Exception
            AddLog($"停止 ngrok 失败: {ex.Message}")
        Finally
            _isStoppingNgrok = False
        End Try
    End Sub


    ''' <summary>
    ''' 生成局域网二维码（显示到局域网二维码框）
    ''' </summary>
    Private Sub GenerateLanQrCode(host As String, port As Integer)
        GenerateQrCodeTo(pbQrCodeLan, host, port)
        If lblLanHint IsNot Nothing Then
            lblLanHint.ForeColor = Color.Green
            lblLanHint.Text = "局域网二维码（可用）"
        End If
    End Sub

    ''' <summary>
    ''' 生成外网二维码（显示到外网二维码框）
    ''' </summary>
    Private Sub GenerateWanQrCode(url As String)
        GenerateQrCodeTo(pbQrCodeWan, url, 443)
        If lblWanHint IsNot Nothing Then
            lblWanHint.ForeColor = Color.Green
            lblWanHint.Text = "外网二维码（可用）"
        End If
    End Sub

    ''' <summary>
    ''' 通用二维码生成，输出到指定 PictureBox
    ''' </summary>
    Private Sub GenerateQrCodeTo(target As PictureBox, host As String, port As Integer)
        If target Is Nothing Then Return
        Try
            ' 判断是否使用 HTTPS（端口443或地址以https开头）
            Dim useHttps As Boolean = (port = 443) OrElse host.StartsWith("https://")

            ' 清理 host（去掉协议前缀）
            Dim cleanHost As String = host.Replace("https://", "").Replace("http://", "")

            ' 构建 URL
            Dim protocol As String = If(useHttps, "https", "http")
            Dim url As String

            If port = 443 AndAlso useHttps Then
                url = $"https://{cleanHost}/"
            ElseIf port = 80 AndAlso Not useHttps Then
                url = $"http://{cleanHost}/"
            Else
                url = $"{protocol}://{cleanHost}:{port}/"
            End If

            ' 使用 QRCoder 生成二维码
            Dim qrGenerator As New QRCodeGenerator()
            Dim qrCodeData As QRCodeData = qrGenerator.CreateQrCode(url, QRCodeGenerator.ECCLevel.Q)
            Dim qrCode As New QRCode(qrCodeData)

            Dim qrImage As Bitmap = qrCode.GetGraphic(20)

            target.Image?.Dispose()
            target.Image = qrImage

        Catch ex As Exception
            AddLog($"生成二维码失败: {ex.Message}")
        End Try
    End Sub

    ''' <summary>
    ''' 兼容旧代码的 GenerateQrCode 方法，支持域名（生成局域网二维码）
    ''' </summary>
    Private Sub GenerateQrCode(host As String, port As Integer)
        GenerateLanQrCode(host, port)

        ' 更新提示文字（保留已有信息）
        If lblHint IsNot Nothing Then
            If Not String.IsNullOrEmpty(lblHint.Text) AndAlso Not lblHint.Text.Contains("外网") Then
                lblHint.Text = $"手机扫描二维码访问"
            End If
        End If
    End Sub

    Private Function GetLocalIpAddress() As String
        Try
            Dim host As IPHostEntry = Dns.GetHostEntry(Dns.GetHostName())
            For Each ip As IPAddress In host.AddressList
                If ip.AddressFamily = AddressFamily.InterNetwork Then
                    If ip.ToString().StartsWith("10.") OrElse ip.ToString().StartsWith("192.168.") Then
                        Return ip.ToString()
                    End If
                End If
            Next
        Catch ex As Exception
        End Try
        Return ""
    End Function

    Private Sub Send404(res As HttpListenerResponse)
        Try
            Dim msg As Byte() = Encoding.UTF8.GetBytes("404 Not Found")
            res.StatusCode = 404
            res.ContentLength64 = msg.Length
            res.OutputStream.Write(msg, 0, msg.Length)
        Catch
            ' 客户端已断开：静默处理
        End Try
    End Sub

    ''' <summary>提供静态资源文件（如 web/libs/ 下的 JS 库），供局域网离线使用</summary>
    Private Sub SendStaticFile(res As HttpListenerResponse, relativePath As String)
        Try
            ' 防止路径穿越：仅允许文件名，不含目录分隔符
            Dim fileName As String = Path.GetFileName(relativePath)
            If String.IsNullOrEmpty(fileName) Then
                Send404(res)
                Return
            End If
            Dim filePath As String = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "web", "libs", fileName)
            If Not File.Exists(filePath) Then
                Send404(res)
                Return
            End If

            Dim buffer As Byte() = File.ReadAllBytes(filePath)
            ' 根据扩展名设置 Content-Type
            Dim ext As String = Path.GetExtension(filePath).ToLowerInvariant()
            Select Case ext
                Case ".js"
                    res.ContentType = "application/javascript; charset=utf-8"
                Case ".css"
                    res.ContentType = "text/css; charset=utf-8"
                Case ".png"
                    res.ContentType = "image/png"
                Case ".jpg", ".jpeg"
                    res.ContentType = "image/jpeg"
                Case ".gif"
                    res.ContentType = "image/gif"
                Case ".svg"
                    res.ContentType = "image/svg+xml"
                Case ".ico"
                    res.ContentType = "image/x-icon"
                Case Else
                    res.ContentType = "application/octet-stream"
            End Select
            res.ContentLength64 = buffer.Length
            res.OutputStream.Write(buffer, 0, buffer.Length)
        Catch ex As Exception
            AddLog($"SendStaticFile 异常: {ex.Message}")
            Send404(res)
        End Try
    End Sub

    Private Sub Send400(res As HttpListenerResponse, info As String)
        Try
            Dim msg As Byte() = Encoding.UTF8.GetBytes($"400 Bad Request: {info}")
            res.StatusCode = 400
            res.ContentLength64 = msg.Length
            res.OutputStream.Write(msg, 0, msg.Length)
        Catch
            ' 客户端已断开：静默处理
        End Try
    End Sub

    Private Sub Send500(res As HttpListenerResponse, info As String)
        Try
            Dim msg As Byte() = Encoding.UTF8.GetBytes($"500 Error: {info}")
            res.StatusCode = 500
            res.ContentLength64 = msg.Length
            res.OutputStream.Write(msg, 0, msg.Length)
        Catch
            ' 响应已提交或客户端已断开：静默处理，禁止在异常处理链中再抛异常
        End Try
    End Sub
    ' 托盘相关
    Private WithEvents notifyIcon As NotifyIcon
    Private notifyContextMenu As ContextMenuStrip

    ' 自动监听相关（等待第一个连接）
    Private tcpListener As TcpListener
    Private waitThread As Threading.Thread
    Private listenForFirstConnection As Boolean = False

    ' 服务空闲检测
    Private lastActivityTime As DateTime = DateTime.MinValue
    Private inactivityTimer As System.Timers.Timer    ' 注意使用 System.Timers.Timer
    Private isServiceRunning As Boolean = False   ' 实际服务是否运行（与 isRunning 同步）
    Private _stopWaiting As Boolean = False

    Private Sub InitializeTrayIcon()
        notifyIcon = New NotifyIcon() With {
            .Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath),
            .Text = "私有云盘",
            .Visible = True
        }

        notifyContextMenu = New ContextMenuStrip()
        Dim showItem As New ToolStripMenuItem("显示主程序")
        AddHandler showItem.Click, AddressOf TrayShowWindow
        Dim exitItem As New ToolStripMenuItem("关闭程序")
        AddHandler exitItem.Click, AddressOf TrayExitApplication
        notifyContextMenu.Items.Add(showItem)
        notifyContextMenu.Items.Add(exitItem)
        notifyIcon.ContextMenuStrip = notifyContextMenu

        AddHandler notifyIcon.DoubleClick, AddressOf TrayShowWindow
    End Sub

    Private Sub TrayShowWindow(sender As Object, e As EventArgs)
        Me.Show()
        Me.WindowState = FormWindowState.Normal
        Me.BringToFront()
    End Sub

    Private Sub TrayExitApplication(sender As Object, e As EventArgs)
        ' 停止外网自动监听调度定时器
        If ngrokAutoTimer IsNot Nothing Then
            ngrokAutoTimer.Stop()
            ngrokAutoTimer.Dispose()
            ngrokAutoTimer = Nothing
        End If

        ' 新增：停止 ngrok
        If isNgrokRunning Then
            StopNgrok()
        End If

        If isRunning Then
            StopService()
        End If
        notifyIcon.Visible = False
        notifyIcon.Dispose()
        Application.Exit()
    End Sub

    ' 重写窗体关闭和大小调整
    Protected Overrides Sub OnResize(e As EventArgs)
        MyBase.OnResize(e)
        If Me.WindowState = FormWindowState.Minimized Then
            Me.Hide()
        End If
    End Sub

    Protected Overrides Sub OnFormClosing(e As FormClosingEventArgs)
        If e.CloseReason = CloseReason.UserClosing Then
            e.Cancel = True
            Me.Hide()
        Else
            MyBase.OnFormClosing(e)
        End If
    End Sub

    ' 启动监听模式（等待第一个连接）
    Private Sub StartListeningForFirstConnection()
        If listenForFirstConnection Then Return
        listenForFirstConnection = True
        _stopWaiting = False

        Dim port As Integer
        If Not Integer.TryParse(txtPort.Text.Trim(), port) Then
            port = DefaultPort
        End If

        Try
            tcpListener = New TcpListener(IPAddress.Any, port)
            tcpListener.Start()
            AddLog($"端口 {port} 监听已启动，等待外部设备访问以自动启动服务...")
            lblHint.Text = "程序后台运行，外部访问将自动启动服务"
            lblStatus.Text = "⏳ 待机监听中..."
            lblStatus.ForeColor = Color.Gray

            waitThread = New Threading.Thread(AddressOf WaitForFirstConnection) With {.IsBackground = True}
            waitThread.Start(port)
        Catch ex As Exception
            AddLog("启动监听失败: " & ex.Message)
        End Try
    End Sub

    Private Sub WaitForFirstConnection(state As Object)
        Dim port As Integer = CInt(state)
        Try
            ' 循环等待连接，直到主动停止
            While Not _stopWaiting
                Try
                    Dim client As TcpClient = tcpListener.AcceptTcpClient()
                    ' 如果已经被要求停止，则关闭客户端并退出
                    If _stopWaiting Then
                        client.Close()
                        Exit While
                    End If

                    Dim remoteEP As IPEndPoint = DirectCast(client.Client.RemoteEndPoint, IPEndPoint)
                    Dim clientIP As String = remoteEP.Address.ToString()
                    AddLog($"检测到外部连接来自 {clientIP}，正在启动文件服务...")

                    ' 关闭 TcpListener 释放端口（后续不再接受新连接）
                    tcpListener.Stop()
                    tcpListener = Nothing
                    listenForFirstConnection = False

                    ' 稍等端口释放
                    Threading.Thread.Sleep(100)

                    ' 启动服务（UI线程）
                    Me.Invoke(Sub() StartServiceAfterWakeup(clientIP))
                    client.Close()
                    Exit While
                Catch ex As Exception
                    ' 当 tcpListener.Stop() 被调用后，AcceptTcpClient 会抛出异常
                    ' 如果是正常停止导致的异常，直接退出循环
                    If _stopWaiting Then
                        Exit While
                    Else
                        ' 其他异常记录日志
                        AddLog("等待连接时出错: " & ex.Message)
                        Exit While
                    End If
                End Try
            End While
        Catch ex As Exception
            ' 最终异常处理
            AddLog("监听线程退出: " & ex.Message)
        End Try
    End Sub

    Private Sub StartServiceAfterWakeup(clientIP As String)
        ' 调用启动服务核心逻辑
        Dim ip As String = txtIp.Text.Trim()
        Dim port As Integer
        If Not Integer.TryParse(txtPort.Text.Trim(), port) Then
            port = DefaultPort
        End If

        Try
            If Not ConfigureFirewallRule(port) Then
                AddLog("防火墙规则配置失败，服务可能无法被外部访问")
            End If

            listener = New HttpListener()
            listener.Prefixes.Add($"http://*:{port}/")
            listener.Start()

            isRunning = True
            isServiceRunning = True
            UpdateUIState(True)   ' 会更新按钮状态和标签

            listenThread = New Threading.Thread(AddressOf ListenLoop) With {.IsBackground = True}
            listenThread.Start()

            AddLog($"文件服务已启动（端口 {port}），共享文件夹: {SharedFolderPath}")
            GenerateQrCode(ip, port)
            EnsureRelayCore()

            ' 初始化空闲检测定时器
            lastActivityTime = DateTime.Now
            StartInactivityTimer()

            ' 弹窗提示
            AutoWindow($"来自 {clientIP} 的访问已触发服务启动", 2, "服务已启动")
        Catch ex As Exception
            AddLog("启动文件服务失败: " & ex.Message)
            UpdateUIState(False)
            ' 启动失败，重新进入待机监听
            StartListeningForFirstConnection()
        End Try
    End Sub

    ' 启动空闲检测定时器（每10秒检查一次）
    Private Sub StartInactivityTimer()
        If inactivityTimer Is Nothing Then
            inactivityTimer = New Timers.Timer(10000) ' 10秒
            AddHandler inactivityTimer.Elapsed, AddressOf CheckInactivity
            inactivityTimer.AutoReset = True
        End If
        inactivityTimer.Start()
    End Sub

    Private Sub CheckInactivity(sender As Object, e As Timers.ElapsedEventArgs)
        If Not isRunning Then
            inactivityTimer?.Stop()
            Return
        End If

        ' 有手机正在通过公网中转对战：视为活跃，避免对局中途被自动停服务
        If Threading.Interlocked.CompareExchange(_relayConn, 0, 0) > 0 Then
            lastActivityTime = DateTime.Now
            Return
        End If

        Dim idleTime = DateTime.Now - lastActivityTime
        If idleTime.TotalMinutes >= 10 Then
            Me.Invoke(Sub() AutoStopService())
        End If
    End Sub

    Private Sub AutoStopService()
        If Not isRunning Then Return
        AddLog("服务空闲超过10分钟，自动停止服务...")
        StopService()
        ' 停止服务后自动重新进入待机监听模式
        StartListeningForFirstConnection()
    End Sub

    Private Sub StopService()
        ' 停止 TcpListener 并通知等待线程退出
        If tcpListener IsNot Nothing Then
            _stopWaiting = True
            tcpListener.Stop()
            tcpListener = Nothing
        End If
        ' 等待等待线程结束（最多2秒）
        If waitThread IsNot Nothing AndAlso waitThread.IsAlive Then
            waitThread.Join(2000)
            waitThread = Nothing
        End If
        listenForFirstConnection = False

        ' 停止 HttpListener 部分（原有代码）
        Try
            If listener IsNot Nothing Then
                listener.Stop()
                listener.Close()
                listener = Nothing
            End If
            If listenThread IsNot Nothing AndAlso listenThread.IsAlive Then
                ' listenThread 的循环会在 listener.Stop 后抛出异常退出，无需 Abort
                listenThread.Join(1000)
                listenThread = Nothing
            End If
        Catch ex As Exception
            AddLog("停止服务异常: " & ex.Message)
        End Try

        ' 停止空闲检测定时器
        inactivityTimer?.Stop()

        isRunning = False
        isServiceRunning = False
        StopRelayCore()
        UpdateUIState(False)

        ' 清空局域网二维码（外网二维码若 ngrok 仍在时间段内运行则保留）
        If pbQrCode.Image IsNot Nothing Then
            pbQrCode.Image.Dispose()
            pbQrCode.Image = Nothing
        End If
        If lblLanHint IsNot Nothing Then
            lblLanHint.ForeColor = Color.Gray
            lblLanHint.Text = "局域网二维码（未启动）"
        End If

        ' 刷新外网二维码框显示状态（ngrok 在时间段内运行时保持显示）
        UpdateWanQrVisibility()

        lblHint.Text = "提示:外部设备访问将自动启动服务"
        ' 保存日志并清空
        SaveLogToFile()
        txtLog.Clear()
        AddLog("服务已停止，日志已保存并清空")
    End Sub

    ' ================== GameHub 公网中转 ==================

    ''' <summary>
    ''' 确保公网中转核心已在本进程内运行（服务启动时调用，重复调用无副作用）。
    ''' </summary>
    Private Sub EnsureRelayCore()
        If _relayHost IsNot Nothing AndAlso _relayHost.IsRunning Then Return

        Try
            RelayCore.RelayLog.Sink = Sub(msg) AddLog("[中转] " & msg)
            RelayCore.RelayLog.EchoToConsole = False

            _relayHost = New RelayCore.RelayHost()
            If _relayHost.Start(RelayPort) Then
                AddLog($"公网中转已就绪（本机 TCP {RelayPort}）· 外网时段内手机填公网地址即可建房/加入")
            Else
                AddLog("公网中转启动失败：端口可能被占用")
            End If
        Catch ex As Exception
            AddLog("公网中转启动异常：" & ex.Message)
        End Try
    End Sub

    ''' <summary>停止公网中转核心并释放端口。</summary>
    Private Sub StopRelayCore()
        Try
            If _relayHost IsNot Nothing Then
                _relayHost.Stop()
                _relayHost.Dispose()
                _relayHost = Nothing
            End If
        Catch ex As Exception
            AddLog("停止公网中转异常：" & ex.Message)
        End Try
    End Sub

    ''' <summary>
    ''' 处理 GameHub 手机端的中转 WebSocket：把 WS 连接桥接到本机中转核心。
    ''' 与网页请求共用同一端口，靠 Upgrade 头区分，因此公网地址与「私有云盘」完全一致。
    ''' </summary>
    Private Sub HandleRelayWebSocket(context As HttpListenerContext)
        EnsureRelayCore()

        Dim host As RelayCore.RelayHost = _relayHost
        If host Is Nothing Then
            Try
                context.Response.StatusCode = 503
                context.Response.Close()
            Catch
                ' ignore
            End Try
            Return
        End If

        Interlocked.Increment(_relayConn)
        Try
            Dim wsContext = context.AcceptWebSocketAsync(Nothing).GetAwaiter().GetResult()
            AddLog("公网中转：手机已连入")
            host.HandleWebSocketAsync(wsContext.WebSocket, CancellationToken.None).GetAwaiter().GetResult()
            AddLog("公网中转：连接已断开")
        Catch ex As Exception
            AddLog("公网中转连接异常：" & ex.Message)
        Finally
            Interlocked.Decrement(_relayConn)
        End Try
    End Sub


End Class