using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using WinFormsTimer = System.Windows.Forms.Timer;

namespace DpSnip
{
    internal enum RecordingFormat
    {
        Gif,
        Mp4
    }

    internal sealed class RecordingForm : Form
    {
        private const int FrameIntervalMs = 100;
        private const int GifDelayCentiseconds = 10;

        private readonly Rectangle _captureRect;
        private readonly RecordingFormat _format;
        private readonly AppSettings _settings;
        private readonly TrayApp _app;
        private readonly float _scale;
        private readonly int _width;
        private readonly int _height;
        private readonly string _sessionDir;
        private readonly string _tempFile;
        private readonly string _framesDir;
        private readonly Font _fontTitle;
        private readonly Font _fontTimer;
        private readonly Font _fontButton;
        private readonly Font _fontSub;

        private readonly WinFormsTimer _uiTimer;
        private readonly WinFormsTimer _startTimer;
        private readonly ManualResetEvent _stopEvent;

        private Thread _worker;
        private volatile WasapiAudioRecorder _audioRecorder;
        private volatile bool _recordingStarted;
        private volatile bool _stopping;
        private volatile bool _stopRequested;
        private volatile bool _aborted;
        private volatile bool _workerRunning;
        private bool _workerFinished;
        private bool _allowClose;
        private bool _prompted;
        private bool _hoverStop;
        private int _frameCount;
        private string _error;
        private DateTime _startedUtc;
        private int _spinAngle;
        private int _tickCount;
        private Rectangle _stopRect;

        public RecordingForm(Rectangle captureRect, RecordingFormat format, AppSettings settings, TrayApp app)
        {
            _format = format;
            _settings = settings;
            _app = app;
            _scale = Math.Max(0.9f, Native.GetUiScaleForPoint(new Point(
                captureRect.Left + captureRect.Width / 2,
                captureRect.Top + captureRect.Height / 2)));

            int width = Math.Max(2, captureRect.Width);
            int height = Math.Max(2, captureRect.Height);
            if ((width & 1) != 0) width--;
            if ((height & 1) != 0) height--;
            _width = width;
            _height = height;
            _captureRect = new Rectangle(captureRect.Left, captureRect.Top, width, height);

            _sessionDir = Path.Combine(Path.GetTempPath(), "DpSnip",
                "rec_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_sessionDir);
            _framesDir = Path.Combine(_sessionDir, "frames");
            _tempFile = Path.Combine(_sessionDir, format == RecordingFormat.Gif ? "output.gif" : "output.mp4");

            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.FromArgb(30, 34, 43);
            ClientSize = new Size(
                (int)Math.Round(354f * _scale),
                (int)Math.Round(62f * _scale));
            DoubleBuffered = true;
            FormBorderStyle = FormBorderStyle.None;
            ImeMode = ImeMode.Disable;
            KeyPreview = true;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = (format == RecordingFormat.Gif ? "GIF 录制" : "MP4 录制") + " - DpSnip";
            TopMost = true;

            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);

            Rectangle work = Native.GetMonitorWorkArea(new Point(
                captureRect.Left + captureRect.Width / 2,
                captureRect.Top + captureRect.Height / 2));
            Location = CalculateLocation(work);

            _fontTitle = DrawUtil.CreateFont(12.8f * _scale, FontStyle.Bold);
            _fontTimer = DrawUtil.CreateFont(12.8f * _scale, FontStyle.Bold);
            _fontButton = DrawUtil.CreateFont(12.2f * _scale, FontStyle.Bold);
            _fontSub = DrawUtil.CreateFont(9.6f * _scale, FontStyle.Regular);

            _stopEvent = new ManualResetEvent(false);

            _uiTimer = new WinFormsTimer();
            _uiTimer.Interval = 150;
            _uiTimer.Tick += OnUiTick;
            _uiTimer.Start();

            _startTimer = new WinFormsTimer();
            _startTimer.Interval = 300;
            _startTimer.Tick += OnStartTimerTick;
            _startTimer.Start();

            Region old = Region;
            Region = DrawUtil.RoundedRegion(
                new Rectangle(0, 0, ClientSize.Width, ClientSize.Height),
                (int)Math.Round(10f * _scale));
            if (old != null)
            {
                old.Dispose();
            }
        }

        internal void Abort()
        {
            if (_aborted)
            {
                return;
            }

            _aborted = true;
            _stopRequested = true;
            try
            {
                _startTimer.Stop();
            }
            catch
            {
            }

            try
            {
                _uiTimer.Stop();
            }
            catch
            {
            }

            try
            {
                _stopEvent.Set();
            }
            catch
            {
            }

            WasapiAudioRecorder audio = _audioRecorder;
            if (audio != null)
            {
                audio.RequestStop();
            }

            _allowClose = true;
            try
            {
                Close();
            }
            catch
            {
            }
        }

        private Point CalculateLocation(Rectangle work)
        {
            int width = Width;
            int height = Height;
            int gap = (int)Math.Round(10f * _scale);
            int x = _captureRect.Left;
            int y = _captureRect.Bottom + gap;
            bool placed = false;

            if (y + height <= work.Bottom - 6)
            {
                placed = true;
            }
            else
            {
                int aboveY = _captureRect.Top - height - gap;
                if (aboveY >= work.Top + 6)
                {
                    y = aboveY;
                    placed = true;
                }
            }

            if (!placed)
            {
                int rightX = _captureRect.Right + gap;
                int leftX = _captureRect.Left - width - gap;
                if (rightX + width <= work.Right - 6)
                {
                    x = rightX;
                    y = Math.Max(work.Top + 6, Math.Min(work.Bottom - height - 6, _captureRect.Top));
                    placed = true;
                }
                else if (leftX >= work.Left + 6)
                {
                    x = leftX;
                    y = Math.Max(work.Top + 6, Math.Min(work.Bottom - height - 6, _captureRect.Top));
                    placed = true;
                }
            }

            if (!placed)
            {
                // Very large selections leave no room outside; keep the controller usable.
                y = _captureRect.Bottom - height - (int)Math.Round(14f * _scale);
                x = _captureRect.Right - width - (int)Math.Round(14f * _scale);
            }

            if (x + width > work.Right - 6)
            {
                x = work.Right - width - 6;
            }

            if (x < work.Left + 6)
            {
                x = work.Left + 6;
            }

            if (y < work.Top + 6)
            {
                y = work.Top + 6;
            }

            if (y + height > work.Bottom - 6)
            {
                y = work.Bottom - height - 6;
            }

            return new Point(x, y);
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ClassStyle |= 0x00020000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Native.SetForegroundWindow(Handle);
            Activate();
            Invalidate();
        }

        private void OnStartTimerTick(object sender, EventArgs e)
        {
            _startTimer.Stop();
            if (_aborted || _stopRequested)
            {
                return;
            }

            _recordingStarted = true;
            _startedUtc = DateTime.UtcNow;
            _worker = new Thread(WorkerMain);
            _worker.IsBackground = true;
            _worker.Name = "DpSnipScreenRecorder";
            _workerRunning = true;
            _worker.Start();
            Invalidate();
        }

        private void OnUiTick(object sender, EventArgs e)
        {
            _tickCount++;
            _spinAngle = (_spinAngle + 28) % 360;
            Invalidate();
        }

        private void WorkerMain()
        {
            try
            {
                if (_format == RecordingFormat.Gif)
                {
                    RecordGif();
                }
                else
                {
                    RecordMp4();
                }
            }
            catch (Exception ex)
            {
                _error = ex.Message;
                AppLog.WriteException("Recording", ex);
            }
            finally
            {
                WasapiAudioRecorder audio = _audioRecorder;
                if (audio != null)
                {
                    audio.RequestStop();
                }

                _workerRunning = false;
                _workerFinished = true;
                if (_aborted || IsDisposed)
                {
                    CleanupSession();
                }

                try
                {
                    if (!IsDisposed && IsHandleCreated)
                    {
                        BeginInvoke(new Action(OnWorkerCompleted));
                    }
                }
                catch
                {
                }
            }
        }

        private void RecordGif()
        {
            using (GifRecorder recorder = new GifRecorder(
                _tempFile, _width, _height, GifDelayCentiseconds))
            {
                Stopwatch clock = Stopwatch.StartNew();
                long nextFrameAt = 0;
                while (true)
                {
                    bool stopping = _stopRequested;
                    if (stopping && _frameCount > 0)
                    {
                        break;
                    }

                    using (Bitmap frame = CaptureFrame())
                    {
                        recorder.AddFrame(frame);
                    }

                    _frameCount++;
                    if (_stopRequested)
                    {
                        break;
                    }

                    nextFrameAt += FrameIntervalMs;
                    long remaining = nextFrameAt - clock.ElapsedMilliseconds;
                    if (remaining > 0)
                    {
                        _stopEvent.WaitOne((int)remaining);
                    }
                }
            }
        }

        private void RecordMp4()
        {
            Directory.CreateDirectory(_framesDir);
            WasapiAudioRecorder audio = null;
            try
            {
                audio = new WasapiAudioRecorder(Path.Combine(_sessionDir, "system-audio.wav"));
                _audioRecorder = audio;
                audio.Start();
            }
            catch (Exception ex)
            {
                AppLog.WriteException("StartSystemAudio", ex);
            }

            List<string> frames = new List<string>();
            try
            {
                Stopwatch clock = Stopwatch.StartNew();
                long nextFrameAt = 0;
                while (true)
                {
                    bool stopping = _stopRequested;
                    if (stopping && _frameCount > 0)
                    {
                        break;
                    }

                    string path = Path.Combine(_framesDir, "frame_" + _frameCount.ToString("D6") + ".png");
                    using (Bitmap frame = CaptureFrame())
                    {
                        SaveFrame(frame, path);
                    }

                    frames.Add(path);
                    _frameCount++;
                    if (_stopRequested)
                    {
                        break;
                    }

                    nextFrameAt += FrameIntervalMs;
                    long remaining = nextFrameAt - clock.ElapsedMilliseconds;
                    if (remaining > 0)
                    {
                        _stopEvent.WaitOne((int)remaining);
                    }
                }
            }
            finally
            {
                if (audio != null)
                {
                    audio.RequestStop();
                    audio.WaitForFinish(3000);
                }
            }

            string audioPath = null;
            if (audio != null && audio.HasData && audio.Error == null)
            {
                audioPath = audio.OutputPath;
            }

            Mp4Encoder.EncodeImageSequence(
                frames, _tempFile, _width, _height, 10, FrameIntervalMs, audioPath);
        }

        private Bitmap CaptureFrame()
        {
            Bitmap bitmap = new Bitmap(_width, _height, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics g = Graphics.FromImage(bitmap))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.CopyFromScreen(
                        _captureRect.Left,
                        _captureRect.Top,
                        0,
                        0,
                        new Size(_width, _height),
                        CopyPixelOperation.SourceCopy);
                    g.CompositingMode = CompositingMode.SourceOver;
                    Native.DrawCursor(g, _captureRect.Left, _captureRect.Top);
                }

                return bitmap;
            }
            catch
            {
                bitmap.Dispose();
                throw;
            }
        }

        private static void SaveFrame(Bitmap bitmap, string path)
        {
            try
            {
                // PNG keeps text and UI edges crisp before the final H.264 encode.
                bitmap.Save(path, ImageFormat.Png);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("保存录制帧失败：" + ex.Message, ex);
            }
        }

        private void StopRecording()
        {
            if (_stopping || _workerFinished)
            {
                return;
            }

            if (!_recordingStarted)
            {
                Abort();
                return;
            }

            _stopping = true;
            _stopRequested = true;
            try
            {
                _stopEvent.Set();
            }
            catch
            {
            }

            WasapiAudioRecorder audio = _audioRecorder;
            if (audio != null)
            {
                audio.RequestStop();
            }

            Invalidate();
        }

        private void OnWorkerCompleted()
        {
            if (IsDisposed)
            {
                return;
            }

            _stopping = true;
            _uiTimer.Stop();
            if (_aborted)
            {
                CleanupSession();
                _allowClose = true;
                Close();
                return;
            }

            if (!string.IsNullOrEmpty(_error))
            {
                CleanupSession();
                ShowMessage("录制失败", "录制或编码失败：\n\n" + _error);
                _allowClose = true;
                Close();
                return;
            }

            if (_frameCount == 0)
            {
                CleanupSession();
                ShowMessage("录制失败", "没有捕获到任何画面。");
                _allowClose = true;
                Close();
                return;
            }

            SaveRecording();
        }

        private void SaveRecording()
        {
            if (_prompted)
            {
                return;
            }

            _prompted = true;
            string initial = _settings.LastSaveDirectory;
            if (string.IsNullOrEmpty(initial) || !Directory.Exists(initial))
            {
                initial = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            }

            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = _format == RecordingFormat.Gif ? "保存 GIF 动图" : "保存 MP4 视频";
                dialog.InitialDirectory = initial;
                dialog.Filter = _format == RecordingFormat.Gif
                    ? "GIF 动图 (*.gif)|*.gif"
                    : "MP4 视频 (*.mp4)|*.mp4";
                dialog.DefaultExt = _format == RecordingFormat.Gif ? "gif" : "mp4";
                dialog.FilterIndex = 1;
                dialog.AddExtension = true;
                dialog.OverwritePrompt = true;
                dialog.RestoreDirectory = true;
                dialog.FileName = "DpSnip_" +
                    (_format == RecordingFormat.Gif ? "GIF_" : "MP4_") +
                    DateTime.Now.ToString("yyyyMMdd_HHmmss");

                bool oldTopMost = TopMost;
                DialogResult result;
                TopMost = false;
                try
                {
                    result = dialog.ShowDialog(this);
                }
                finally
                {
                    TopMost = oldTopMost;
                }

                if (result != DialogResult.OK)
                {
                    CleanupSession();
                    _allowClose = true;
                    Close();
                    return;
                }

                try
                {
                    string target = dialog.FileName;
                    string requiredExtension = _format == RecordingFormat.Gif ? ".gif" : ".mp4";
                    if (!target.EndsWith(requiredExtension, StringComparison.OrdinalIgnoreCase))
                    {
                        target += requiredExtension;
                    }

                    string directory = Path.GetDirectoryName(target);
                    File.Copy(_tempFile, target, true);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        _settings.LastSaveDirectory = directory;
                        _settings.Save();
                    }

                    if (_app != null)
                    {
                        _app.ShowToast(
                            _format == RecordingFormat.Gif ? "GIF 已保存" : "MP4 已保存",
                            Path.GetFileName(target) + "\n" + directory);
                    }

                    CleanupSession();
                    _allowClose = true;
                    Close();
                }
                catch (Exception ex)
                {
                    AppLog.WriteException("SaveRecording", ex);
                    ShowMessage("保存失败", "无法保存录制文件：\n\n" + ex.Message +
                        "\n\n临时文件仍然保留在：\n" + _tempFile);
                    CleanupSession();
                    _allowClose = true;
                    Close();
                }
            }
        }

        private void ShowMessage(string title, string text)
        {
            bool oldTopMost = TopMost;
            TopMost = false;
            try
            {
                MessageBox.Show(this, text, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                TopMost = oldTopMost;
            }
        }

        private void CleanupSession()
        {
            try
            {
                if (Directory.Exists(_sessionDir))
                {
                    Directory.Delete(_sessionDir, true);
                }
            }
            catch
            {
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            using (SolidBrush background = new SolidBrush(Color.FromArgb(250, 30, 34, 43)))
            {
                g.FillRectangle(background, ClientRectangle);
            }

            using (Pen border = new Pen(Color.FromArgb(52, 255, 255, 255), 1f))
            {
                g.DrawRectangle(border, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
            }

            float pad = 16f * _scale;
            float dotSize = 9f * _scale;
            float centerY = ClientSize.Height / 2f;

            if (_stopping)
            {
                RectangleF spinner = new RectangleF(pad, centerY - 7f * _scale, 14f * _scale, 14f * _scale);
                using (Pen arc = new Pen(AppPalette.Accent, Math.Max(1.4f, 1.8f * _scale)))
                {
                    arc.StartCap = LineCap.Round;
                    arc.EndCap = LineCap.Round;
                    g.DrawArc(arc, spinner, _spinAngle, 260f);
                }
            }
            else
            {
                int alpha = _recordingStarted ? (220 + (_tickCount % 2 == 0 ? 35 : 0)) : 180;
                using (SolidBrush dot = new SolidBrush(Color.FromArgb(alpha, 237, 78, 83)))
                {
                    g.FillEllipse(dot, pad, centerY - dotSize / 2f, dotSize, dotSize);
                }
            }

            float textX = pad + dotSize + 9f * _scale;
            float titleWidth = 122f * _scale;
            string title;
            if (_stopping)
            {
                title = _format == RecordingFormat.Gif ? "正在生成 GIF…" : "正在生成 MP4…";
            }
            else if (_recordingStarted)
            {
                title = _format == RecordingFormat.Gif ? "GIF 录制中" : "MP4 录制中";
            }
            else
            {
                title = _format == RecordingFormat.Gif ? "准备录制 GIF" : "准备录制 MP4";
            }

            DrawUtil.DrawText(g, title, _fontTitle, AppPalette.Text,
                new RectangleF(textX, 10f * _scale, titleWidth, 22f * _scale),
                StringAlignment.Near, StringAlignment.Center, true);

            string subtitle;
            if (_stopping)
            {
                subtitle = "请稍候，正在完成文件";
            }
            else if (_format == RecordingFormat.Mp4 && _recordingStarted)
            {
                WasapiAudioRecorder audio = _audioRecorder;
                if (audio != null && audio.IsActive)
                {
                    subtitle = "含系统声音 · 点击停止结束";
                }
                else if (audio != null && audio.Error != null)
                {
                    subtitle = "系统声音不可用 · 仅录制画面";
                }
                else
                {
                    subtitle = "点击右侧停止按钮结束录制";
                }
            }
            else
            {
                subtitle = "点击右侧停止按钮结束录制";
            }

            DrawUtil.DrawText(g, subtitle, _fontSub, AppPalette.MutedText,
                new RectangleF(textX, 31f * _scale, titleWidth + 50f * _scale, 18f * _scale),
                StringAlignment.Near, StringAlignment.Center, true);

            float stopLeft = ClientSize.Width - pad - 68f * _scale;
            if (_recordingStarted && !_stopping)
            {
                string elapsed = FormatElapsed(DateTime.UtcNow - _startedUtc);
                float timerLeft = stopLeft - 10f * _scale - 74f * _scale;
                DrawUtil.DrawText(g, elapsed, _fontTimer, Color.FromArgb(226, 231, 240),
                    new RectangleF(timerLeft, 10f * _scale, 74f * _scale, 22f * _scale),
                    StringAlignment.Far, StringAlignment.Center, true);
            }

            RectangleF stop = new RectangleF(
                stopLeft,
                centerY - 17f * _scale,
                68f * _scale,
                34f * _scale);
            _stopRect = Rectangle.Round(stop);

            if (_recordingStarted && !_stopping)
            {
                Color fill = _hoverStop ? Color.FromArgb(245, 95, 100) : Color.FromArgb(220, 65, 72);
                DrawUtil.FillRounded(g, fill, stop, 7f * _scale);
                DrawUtil.DrawText(g, "停止", _fontButton, Color.White, stop,
                    StringAlignment.Center, StringAlignment.Center, true);
            }
            else if (_stopping)
            {
                DrawUtil.DrawText(g, "处理中", _fontSub, AppPalette.MutedText, stop,
                    StringAlignment.Center, StringAlignment.Center, true);
            }
        }

        private static string FormatElapsed(TimeSpan elapsed)
        {
            if (elapsed.TotalHours >= 1)
            {
                return string.Format("{0:0}:{1:00}:{2:00}",
                    (int)elapsed.TotalHours, elapsed.Minutes, elapsed.Seconds);
            }

            return string.Format("{0:00}:{1:00}", elapsed.Minutes, elapsed.Seconds);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            bool hover = _recordingStarted && !_stopping && _stopRect.Contains(e.Location);
            Cursor = hover ? Cursors.Hand : Cursors.Default;
            if (hover != _hoverStop)
            {
                _hoverStop = hover;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hoverStop = false;
            Cursor = Cursors.Default;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            if (_recordingStarted && !_stopping && _stopRect.Contains(e.Location))
            {
                StopRecording();
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape)
            {
                if (_recordingStarted && !_stopping)
                {
                    StopRecording();
                }
                else if (!_recordingStarted)
                {
                    Abort();
                }

                return true;
            }

            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true;
                if (_recordingStarted && !_stopping)
                {
                    StopRecording();
                }
                else if (!_recordingStarted)
                {
                    Abort();
                }

                return;
            }

            base.OnFormClosing(e);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            try
            {
                _startTimer.Stop();
                _startTimer.Dispose();
                _uiTimer.Stop();
                _uiTimer.Dispose();
            }
            catch
            {
            }

            WasapiAudioRecorder audio = _audioRecorder;
            if (audio != null)
            {
                audio.RequestStop();
                if (!_workerRunning)
                {
                    audio.Dispose();
                }
            }

            if (!_workerRunning)
            {
                CleanupSession();
            }

            _fontTitle.Dispose();
            _fontTimer.Dispose();
            _fontButton.Dispose();
            _fontSub.Dispose();
            if (!_workerRunning)
            {
                try
                {
                    _stopEvent.Dispose();
                }
                catch
                {
                }
            }

            base.OnFormClosed(e);
        }
    }
}
