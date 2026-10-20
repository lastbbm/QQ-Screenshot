using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace DpSnip
{
    internal static class Program
    {
        private const string MutexName = @"Local\DpSnip.SingleInstance.7B4E1E2A";

        [STAThread]
        private static void Main(string[] args)
        {
            Native.TryEnablePerMonitorDpiAwareness();

            bool autoStart = false;
            if (args != null)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    if (string.Equals(args[i], "--autostart", StringComparison.OrdinalIgnoreCase))
                    {
                        autoStart = true;
                    }
                }
            }

            bool createdNew;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    ActivateExistingInstance(autoStart);
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += OnThreadException;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

                try
                {
                    using (TrayApp app = new TrayApp(autoStart))
                    {
                        Application.Run(app);
                    }
                }
                catch (Exception ex)
                {
                    AppLog.WriteException("Fatal", ex);
                    MessageBox.Show(
                        "DpSnip 无法启动：\n\n" + ex.Message,
                        "DpSnip",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }

                GC.KeepAlive(mutex);
            }
        }

        private static void ActivateExistingInstance(bool quiet)
        {
            for (int attempt = 0; attempt < 12; attempt++)
            {
                IntPtr handle = Native.FindWindow(null, TrayApp.HiddenWindowTitle);
                if (handle != IntPtr.Zero)
                {
                    if (!quiet)
                    {
                        Native.PostMessage(handle, Native.WM_APP_ACTIVATE, IntPtr.Zero, IntPtr.Zero);
                    }

                    return;
                }

                Thread.Sleep(80);
            }

            if (!quiet)
            {
                MessageBox.Show(
                    "DpSnip 已经在运行。\n\n请在系统托盘中找到蓝色截图图标，或按 Ctrl+Alt+A。",
                    "DpSnip",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
        }

        private static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            AppLog.WriteException("UI thread", e == null ? null : e.Exception);
            try
            {
                MessageBox.Show(
                    "DpSnip 遇到了一个错误：\n\n" +
                    (e == null || e.Exception == null ? "未知错误" : e.Exception.Message) +
                    "\n\n你可以继续使用截图功能。",
                    "DpSnip",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
            catch
            {
            }
        }

        private static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            AppLog.WriteException("Unhandled", e == null ? null : e.ExceptionObject as Exception);
        }
    }

    internal static class ScreenCapture
    {
        public static Rectangle GetVirtualBounds()
        {
            return Native.GetVirtualScreenBounds();
        }

        public static Bitmap CaptureVirtualScreen()
        {
            Rectangle bounds = Native.GetVirtualScreenBounds();
            if (bounds.Width <= 0 || bounds.Height <= 0)
            {
                throw new InvalidOperationException("无法获取屏幕尺寸。");
            }

            Bitmap bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                    graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
                }

                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }
    }

    internal sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        private const int HotkeyId = 0x4450;
        private bool _registered;
        private bool _disposed;

        public event EventHandler HotkeyPressed;
        public event EventHandler ActivateRequested;

        public HotkeyWindow()
        {
            CreateParams createParams = new CreateParams();
            createParams.Caption = TrayApp.HiddenWindowTitle;
            createParams.X = -32000;
            createParams.Y = -32000;
            createParams.Width = 0;
            createParams.Height = 0;
            createParams.Style = 0;
            createParams.ExStyle = 0x00000080; // WS_EX_TOOLWINDOW
            CreateHandle(createParams);
        }

        public bool IsRegistered
        {
            get { return _registered; }
        }

        public bool TryRegister()
        {
            if (_disposed || Handle == IntPtr.Zero)
            {
                return false;
            }

            if (_registered)
            {
                Native.UnregisterHotKey(Handle, HotkeyId);
                _registered = false;
            }

            _registered = Native.RegisterHotKey(
                Handle,
                HotkeyId,
                Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_NOREPEAT,
                0x41); // A

            if (_registered)
            {
                AppLog.Write("Global hotkey Ctrl+Alt+A registered.");
            }
            else
            {
                int error = Marshal.GetLastWin32Error();
                AppLog.Write("Global hotkey registration failed. Win32Error=" +
                    error.ToString(CultureInfo.InvariantCulture));
            }

            return _registered;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
            {
                EventHandler handler = HotkeyPressed;
                if (handler != null)
                {
                    try
                    {
                        handler(this, EventArgs.Empty);
                    }
                    catch (Exception ex)
                    {
                        AppLog.WriteException("Hotkey handler", ex);
                    }
                }

                return;
            }

            if (m.Msg == Native.WM_APP_ACTIVATE)
            {
                EventHandler handler = ActivateRequested;
                if (handler != null)
                {
                    try
                    {
                        handler(this, EventArgs.Empty);
                    }
                    catch (Exception ex)
                    {
                        AppLog.WriteException("Activate handler", ex);
                    }
                }

                return;
            }

            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                if (_registered && Handle != IntPtr.Zero)
                {
                    Native.UnregisterHotKey(Handle, HotkeyId);
                }
            }
            catch
            {
            }

            _registered = false;
            if (Handle != IntPtr.Zero)
            {
                DestroyHandle();
            }

            GC.SuppressFinalize(this);
        }
    }

    internal sealed class TrayApp : ApplicationContext
    {
        internal const string HiddenWindowTitle = "DpSnip::Hidden::Window::7B4E1E2A";

        private readonly bool _autoStart;
        private readonly AppSettings _settings;
        private readonly NotifyIcon _notifyIcon;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _startupItem;
        private readonly ToolStripMenuItem _retryHotkeyItem;
        private readonly HotkeyWindow _hotkeyWindow;
        private readonly Icon _trayIcon;
        private readonly WinFormsTimer _captureTimer;
        private readonly WinFormsTimer _firstRunTimer;
        private CaptureForm _captureForm;
        private bool _exitRequested;
        private bool _disposed;

        public TrayApp(bool autoStart)
        {
            _autoStart = autoStart;
            _settings = new AppSettings();
            _trayIcon = AppIcon.CreateTrayIcon();
            _menu = new ContextMenuStrip();
            _menu.ShowImageMargin = false;
            _menu.Font = new Font("Microsoft YaHei UI", 9.2f, FontStyle.Regular, GraphicsUnit.Point);
            _menu.ShowItemToolTips = true;

            ToolStripMenuItem captureItem = new ToolStripMenuItem("截图\tCtrl+Alt+A");
            captureItem.Font = new Font(_menu.Font, FontStyle.Bold);
            captureItem.Click += delegate { BeginCapture(true, 170); };
            _menu.Items.Add(captureItem);

            _menu.Items.Add(new ToolStripSeparator());

            _startupItem = new ToolStripMenuItem("开机启动");
            _startupItem.CheckOnClick = true;
            _startupItem.Checked = StartupManager.IsEnabled();
            _startupItem.Click += OnStartupClicked;
            _menu.Items.Add(_startupItem);

            _retryHotkeyItem = new ToolStripMenuItem("重新注册热键");
            _retryHotkeyItem.Visible = false;
            _retryHotkeyItem.Click += OnRetryHotkeyClicked;
            _menu.Items.Add(_retryHotkeyItem);

            ToolStripMenuItem aboutItem = new ToolStripMenuItem("关于 DpSnip");
            aboutItem.Click += delegate { ShowAbout(); };
            _menu.Items.Add(aboutItem);

            _menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出");
            exitItem.Click += delegate { ExitApp(); };
            _menu.Items.Add(exitItem);

            _menu.Opening += delegate
            {
                _startupItem.Checked = StartupManager.IsEnabled();
                _retryHotkeyItem.Visible = !_hotkeyWindow.IsRegistered;
            };

            _notifyIcon = new NotifyIcon();
            _notifyIcon.Icon = _trayIcon;
            _notifyIcon.Text = "DpSnip 截图  ·  Ctrl+Alt+A";
            _notifyIcon.Visible = true;
            _notifyIcon.ContextMenuStrip = _menu;
            _notifyIcon.DoubleClick += delegate { BeginCapture(false, 40); };
            _notifyIcon.BalloonTipClicked += delegate { BeginCapture(false, 40); };

            _hotkeyWindow = new HotkeyWindow();
            _hotkeyWindow.HotkeyPressed += delegate { BeginCapture(false, 55); };
            _hotkeyWindow.ActivateRequested += delegate
            {
                if (_captureForm != null && !_captureForm.IsDisposed)
                {
                    _captureForm.BringToFront();
                    _captureForm.Activate();
                }
                else
                {
                    BeginCapture(false, 40);
                }
            };

            bool hotkeyOk = _hotkeyWindow.TryRegister();
            _retryHotkeyItem.Visible = !hotkeyOk;

            _captureTimer = new WinFormsTimer();
            _captureTimer.Tick += OnCaptureTimerTick;

            _firstRunTimer = new WinFormsTimer();
            _firstRunTimer.Interval = 800;
            _firstRunTimer.Tick += OnFirstRunTimerTick;
            _firstRunTimer.Start();

            AppLog.Write("DpSnip started. AutoStart=" + autoStart);
        }

        private void OnFirstRunTimerTick(object sender, EventArgs e)
        {
            _firstRunTimer.Stop();
            _firstRunTimer.Dispose();

            if (_exitRequested)
            {
                return;
            }

            if (!_hotkeyWindow.IsRegistered)
            {
                ShowBalloon(
                    "热键注册失败",
                    "Ctrl+Alt+A 可能已被 QQ、微信或其他截图工具占用。\n右键托盘图标可手动截图或重新注册。",
                    ToolTipIcon.Warning,
                    7000);
            }
            else if (!_autoStart)
            {
                ShowBalloon(
                    "DpSnip 已就绪",
                    "按 Ctrl+Alt+A 框选截图。右键托盘图标可设置开机启动或退出。",
                    ToolTipIcon.Info,
                    5000);
            }
        }

        private void BeginCapture(bool fromMenu, int delayMilliseconds)
        {
            if (_exitRequested)
            {
                return;
            }

            if (_captureForm != null && !_captureForm.IsDisposed)
            {
                _captureForm.BringToFront();
                _captureForm.Activate();
                return;
            }

            if (fromMenu)
            {
                _menu.Close();
            }

            _captureTimer.Stop();
            _captureTimer.Interval = Math.Max(0, delayMilliseconds);
            _captureTimer.Start();
        }

        private void OnCaptureTimerTick(object sender, EventArgs e)
        {
            _captureTimer.Stop();
            DoCapture();
        }

        private void DoCapture()
        {
            if (_exitRequested)
            {
                return;
            }

            Bitmap screen = null;
            try
            {
                screen = ScreenCapture.CaptureVirtualScreen();
                Rectangle bounds = ScreenCapture.GetVirtualBounds();
                CaptureForm form = new CaptureForm(screen, bounds, _settings, this);
                screen = null;
                _captureForm = form;
                form.FormClosed += delegate
                {
                    if (object.ReferenceEquals(_captureForm, form))
                    {
                        _captureForm = null;
                    }
                };
                form.Show();
                form.Activate();
                Native.SetForegroundWindow(form.Handle);
            }
            catch (Exception ex)
            {
                AppLog.WriteException("Capture", ex);
                if (screen != null)
                {
                    screen.Dispose();
                }

                MessageBox.Show(
                    "无法截取屏幕：\n\n" + ex.Message +
                    "\n\n如果正在播放受保护视频或使用安全桌面，系统会阻止截图。",
                    "DpSnip",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        internal void ShowToast(string title, string text)
        {
            ShowBalloon(title, text, ToolTipIcon.None, 2400);
        }

        private void ShowBalloon(string title, string text, ToolTipIcon icon, int timeout)
        {
            try
            {
                _notifyIcon.BalloonTipTitle = title;
                _notifyIcon.BalloonTipText = text;
                _notifyIcon.BalloonTipIcon = icon;
                _notifyIcon.ShowBalloonTip(timeout);
            }
            catch
            {
            }
        }

        private void OnStartupClicked(object sender, EventArgs e)
        {
            bool wantEnabled = _startupItem.Checked;
            try
            {
                StartupManager.SetEnabled(wantEnabled);
                ShowBalloon(
                    "开机启动",
                    wantEnabled ? "已开启。登录 Windows 后 DpSnip 会自动在托盘运行。" : "已关闭开机启动。",
                    ToolTipIcon.None,
                    2500);
            }
            catch (Exception ex)
            {
                AppLog.WriteException("Startup", ex);
                _startupItem.Checked = !wantEnabled;
                MessageBox.Show(
                    "无法修改开机启动设置：\n\n" + ex.Message,
                    "DpSnip",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private void OnRetryHotkeyClicked(object sender, EventArgs e)
        {
            bool ok = _hotkeyWindow.TryRegister();
            _retryHotkeyItem.Visible = !ok;
            if (ok)
            {
                ShowBalloon("热键已启用", "现在可以使用 Ctrl+Alt+A 截图。", ToolTipIcon.Info, 2500);
            }
            else
            {
                MessageBox.Show(
                    "仍然无法注册 Ctrl+Alt+A。\n\n" +
                    "请关闭可能占用该快捷键的程序（例如 QQ、微信、其他截图工具），然后重试。",
                    "DpSnip",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
            }
        }

        private void ShowAbout()
        {
            Version version = typeof(TrayApp).Assembly.GetName().Version;
            MessageBox.Show(
                "DpSnip  " + version.ToString(3) + "\n\n" +
                "轻量的 Windows 截图、标注与文字识别工具。\n\n" +
                "全局热键：Ctrl+Alt+A\n" +
                "开机启动：右键托盘图标设置\n\n" +
                "文字识别使用 Windows 内置 OCR，截图不会上传到网络。\n\n" +
                "单文件绿色版 · 数据仅保存在本机",
                "关于 DpSnip",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private void ExitApp()
        {
            if (_exitRequested)
            {
                return;
            }

            _exitRequested = true;
            _captureTimer.Stop();
            _firstRunTimer.Stop();

            if (_captureForm != null && !_captureForm.IsDisposed)
            {
                try
                {
                    _captureForm.Close();
                }
                catch
                {
                }
            }

            try
            {
                _notifyIcon.Visible = false;
            }
            catch
            {
            }

            _hotkeyWindow.Dispose();
            AppLog.Write("DpSnip exited.");
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (disposing)
                {
                    if (!_exitRequested)
                    {
                        _exitRequested = true;
                        _hotkeyWindow.Dispose();
                    }

                    _notifyIcon.Visible = false;
                    _notifyIcon.Dispose();
                    _menu.Dispose();
                    _trayIcon.Dispose();
                    _captureTimer.Dispose();
                    _firstRunTimer.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }
}




