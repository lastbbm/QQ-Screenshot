using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace DpSnip
{
    internal enum CaptureTool
    {
        Select,
        Rectangle,
        Ellipse,
        Pen,
        Arrow
    }

    internal enum BarAction
    {
        Select,
        Rectangle,
        Ellipse,
        Pen,
        Arrow,
        Color,
        Width,
        Undo,
        Ocr,
        Gif,
        Mp4,
        Save,
        Close,
        Done
    }

    internal enum DragMode
    {
        None,
        Creating,
        Moving,
        Resizing,
        Drawing
    }

    internal sealed class Annotation
    {
        public CaptureTool Kind;
        public Color Color;
        public float Width;
        public List<PointF> Points = new List<PointF>();

        public Annotation(CaptureTool kind, Color color, float width)
        {
            Kind = kind;
            Color = color;
            Width = width;
        }

        public bool HasArea
        {
            get
            {
                if (Points.Count < 2)
                {
                    return false;
                }

                if (Kind == CaptureTool.Arrow)
                {
                    float dx = Points[1].X - Points[0].X;
                    float dy = Points[1].Y - Points[0].Y;
                    return (dx * dx + dy * dy) >= 64f;
                }

                float w = Math.Abs(Points[1].X - Points[0].X);
                float h = Math.Abs(Points[1].Y - Points[0].Y);
                return w >= 3f || h >= 3f || Kind == CaptureTool.Pen;
            }
        }

        public RectangleF GetBounds()
        {
            if (Points.Count == 0)
            {
                return RectangleF.Empty;
            }

            float minX = Points[0].X;
            float minY = Points[0].Y;
            float maxX = minX;
            float maxY = minY;
            for (int i = 1; i < Points.Count; i++)
            {
                if (Points[i].X < minX) minX = Points[i].X;
                if (Points[i].Y < minY) minY = Points[i].Y;
                if (Points[i].X > maxX) maxX = Points[i].X;
                if (Points[i].Y > maxY) maxY = Points[i].Y;
            }

            float padding = Math.Max(6f, Width * 2.5f + 4f);
            return new RectangleF(
                minX - padding,
                minY - padding,
                maxX - minX + padding * 2f,
                maxY - minY + padding * 2f);
        }

        public void Render(Graphics g, Point offset)
        {
            if (Points.Count == 0)
            {
                return;
            }

            Color strokeColor = Color.FromArgb(245, Color);
            using (Pen pen = new Pen(strokeColor, Math.Max(1f, Width)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;

                switch (Kind)
                {
                    case CaptureTool.Rectangle:
                        if (Points.Count >= 2)
                        {
                            RectangleF r = RectFromPoints(Points[0], Points[1]);
                            r.Offset(offset.X, offset.Y);
                            if (r.Width > 0f && r.Height > 0f)
                            {
                                g.DrawRectangle(pen, r.X, r.Y, r.Width, r.Height);
                            }
                        }
                        break;

                    case CaptureTool.Ellipse:
                        if (Points.Count >= 2)
                        {
                            RectangleF er = RectFromPoints(Points[0], Points[1]);
                            er.Offset(offset.X, offset.Y);
                            if (er.Width > 0f && er.Height > 0f)
                            {
                                g.DrawEllipse(pen, er);
                            }
                        }
                        break;

                    case CaptureTool.Pen:
                        DrawPen(g, pen, offset);
                        break;

                    case CaptureTool.Arrow:
                        if (Points.Count >= 2)
                        {
                            DrawArrow(g, pen, offset);
                        }
                        break;
                }
            }
        }

        private void DrawPen(Graphics g, Pen pen, Point offset)
        {
            if (Points.Count == 1)
            {
                using (SolidBrush dot = new SolidBrush(Color.FromArgb(245, Color)))
                {
                    float radius = Math.Max(1f, Width / 2f);
                    g.FillEllipse(dot,
                        offset.X + Points[0].X - radius,
                        offset.Y + Points[0].Y - radius,
                        radius * 2f, radius * 2f);
                }
                return;
            }

            PointF[] points = new PointF[Points.Count];
            for (int i = 0; i < Points.Count; i++)
            {
                points[i] = new PointF(Points[i].X + offset.X, Points[i].Y + offset.Y);
            }

            if (points.Length == 2)
            {
                g.DrawLine(pen, points[0], points[1]);
                return;
            }

            using (GraphicsPath path = new GraphicsPath())
            {
                path.AddCurve(points, 0.32f);
                g.DrawPath(pen, path);
            }
        }

        private void DrawArrow(Graphics g, Pen pen, Point offset)
        {
            PointF start = new PointF(Points[0].X + offset.X, Points[0].Y + offset.Y);
            PointF tip = new PointF(Points[1].X + offset.X, Points[1].Y + offset.Y);
            float dx = tip.X - start.X;
            float dy = tip.Y - start.Y;
            float length = (float)Math.Sqrt(dx * dx + dy * dy);
            if (length < 1f)
            {
                return;
            }

            float ux = dx / length;
            float uy = dy / length;
            float headLength = Math.Min(length * 0.48f, Math.Max(10f, Width * 3.6f));
            float headWidth = Math.Max(7f, headLength * 0.72f);
            PointF baseCenter = new PointF(tip.X - ux * headLength, tip.Y - uy * headLength);
            PointF left = new PointF(
                baseCenter.X - uy * headWidth * 0.5f,
                baseCenter.Y + ux * headWidth * 0.5f);
            PointF right = new PointF(
                baseCenter.X + uy * headWidth * 0.5f,
                baseCenter.Y - ux * headWidth * 0.5f);

            g.DrawLine(pen,
                start.X, start.Y,
                tip.X - ux * headLength * 0.68f,
                tip.Y - uy * headLength * 0.68f);

            using (SolidBrush brush = new SolidBrush(Color.FromArgb(248, Color)))
            {
                g.FillPolygon(brush, new PointF[] { tip, left, right });
            }

            using (Pen outline = new Pen(Color.FromArgb(80, Color), 0.7f))
            {
                g.DrawPolygon(outline, new PointF[] { tip, left, right });
            }
        }

        private static RectangleF RectFromPoints(PointF a, PointF b)
        {
            float x = Math.Min(a.X, b.X);
            float y = Math.Min(a.Y, b.Y);
            float w = Math.Abs(a.X - b.X);
            float h = Math.Abs(a.Y - b.Y);
            return new RectangleF(x, y, w, h);
        }
    }

    internal sealed class CaptureForm : Form
    {
        private static readonly Color[] AnnotationPalette = new Color[]
        {
            Color.FromArgb(229, 57, 53),
            Color.FromArgb(251, 140, 20),
            Color.FromArgb(253, 216, 53),
            Color.FromArgb(67, 160, 71),
            Color.FromArgb(0, 191, 165),
            Color.FromArgb(30, 136, 229),
            Color.FromArgb(142, 36, 170),
            Color.FromArgb(236, 64, 122),
            Color.FromArgb(255, 255, 255),
            Color.FromArgb(158, 158, 158),
            Color.FromArgb(17, 19, 24)
        };

        private static readonly float[] WidthLevels = new float[] { 2.4f, 4.2f, 7f };
        private static readonly string[] WidthNames = new string[] { "细", "中", "粗" };

        private readonly Bitmap _screen;
        private readonly Rectangle _screenBounds;
        private readonly AppSettings _settings;
        private readonly TrayApp _app;

        private readonly List<Annotation> _annotations = new List<Annotation>();
        private readonly List<Annotation> _redo = new List<Annotation>();

        private Color _color;
        private int _widthLevel;
        private CaptureTool _tool;
        private bool _hasSelection;
        private Rectangle _selection;
        private DragMode _dragMode;
        private int _resizeHandle = -1;
        private Point _dragAnchor;
        private Rectangle _dragStartSelection;
        private Annotation _drawing;
        private float _uiScale = 1f;

        private Font _fontBadge;
        private Font _fontTooltip;
        private Font _fontButton;
        private Font _fontIcon;
        private Font _fontRecord;
        private Font _fontPaletteTitle;

        private Rectangle _toolbarBounds;
        private Rectangle _paletteBounds;
        private List<ToolbarButton> _barButtons;
        private List<float> _dividerPositions;
        private List<PaletteCell> _paletteCells;
        private BarAction? _hoverAction;
        private int _hoverPalette = -1;
        private bool _paletteOpen;
        private bool _closing;
        private bool _resourcesDisposed;
        private Cursor _crosshairCursor;

        private OcrForm _ocrForm;

        public CaptureForm(Bitmap screen, Rectangle screenBounds, AppSettings settings, TrayApp app)
        {
            _screen = screen;
            _screenBounds = screenBounds;
            _settings = settings;
            _app = app;
            _color = settings.AnnotationColor;
            _widthLevel = Math.Max(0, Math.Min(2, settings.WidthLevel));
            _tool = CaptureTool.Select;

            AutoScaleMode = AutoScaleMode.None;
            BackColor = Color.Black;
            ClientSize = screenBounds.Size;
            DoubleBuffered = true;
            FormBorderStyle = FormBorderStyle.None;
            ImeMode = ImeMode.Disable;
            KeyPreview = true;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = screenBounds;
            Text = "DpSnip 截图";
            TopMost = true;

            SetStyle(ControlStyles.AllPaintingInWmPaint |
                     ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.Opaque |
                     ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);

            _uiScale = Native.GetUiScaleForPoint(Cursor.Position);
            RecreateFonts();
            RecreateCrosshairCursor();
            Cursor = CrosshairCursor;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Bounds = _screenBounds;
            Native.SetForegroundWindow(Handle);
            Activate();
            Invalidate();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _resourcesDisposed = true;
            CloseOcrForm();
            if (_screen != null)
            {
                _screen.Dispose();
            }

            try
            {
                Cursor = Cursors.Default;
            }
            catch
            {
            }

            if (_crosshairCursor != null)
            {
                _crosshairCursor.Dispose();
                _crosshairCursor = null;
            }

            DisposeFonts();
            base.OnFormClosed(e);
        }

        private void RecreateFonts()
        {
            DisposeFonts();
            _fontBadge = DrawUtil.CreateFont(11.5f * _uiScale, FontStyle.Bold);
            _fontTooltip = DrawUtil.CreateFont(12f * _uiScale, FontStyle.Regular);
            _fontButton = DrawUtil.CreateFont(12.8f * _uiScale, FontStyle.Bold);
            _fontIcon = DrawUtil.CreateFont(14.4f * _uiScale, FontStyle.Bold);
            _fontRecord = DrawUtil.CreateFont(10.8f * _uiScale, FontStyle.Bold);
            _fontPaletteTitle = DrawUtil.CreateFont(12.3f * _uiScale, FontStyle.Bold);
        }

        private void DisposeFonts()
        {
            Font[] fonts = new Font[]
            {
                _fontBadge, _fontTooltip, _fontButton, _fontIcon, _fontRecord, _fontPaletteTitle
            };
            for (int i = 0; i < fonts.Length; i++)
            {
                if (fonts[i] != null)
                {
                    fonts[i].Dispose();
                }
            }

            _fontBadge = null;
            _fontTooltip = null;
            _fontButton = null;
            _fontIcon = null;
            _fontRecord = null;
            _fontPaletteTitle = null;
        }

        private void UpdateUiScale()
        {
            if (!_hasSelection)
            {
                return;
            }

            Point center = new Point(
                _screenBounds.X + _selection.Left + _selection.Width / 2,
                _screenBounds.Y + _selection.Top + _selection.Height / 2);
            float scale = Native.GetUiScaleForPoint(center);
            if (Math.Abs(scale - _uiScale) > 0.02f)
            {
                _uiScale = scale;
                RecreateFonts();
                RecreateCrosshairCursor();
            }
        }

        private Cursor CrosshairCursor
        {
            get { return _crosshairCursor != null ? _crosshairCursor : Cursors.Cross; }
        }

        private void RecreateCrosshairCursor()
        {
            Cursor previous = _crosshairCursor;
            Cursor current = Cursor;
            try
            {
                int size = (int)Math.Round(32f * _uiScale);
                _crosshairCursor = Native.CreateWhiteCrosshairCursor(size);
            }
            catch
            {
                _crosshairCursor = null;
            }

            if (object.ReferenceEquals(current, previous))
            {
                Cursor = CrosshairCursor;
            }

            if (previous != null)
            {
                previous.Dispose();
            }
        }

        private float CurrentStrokeWidth
        {
            get { return WidthLevels[Math.Max(0, Math.Min(2, _widthLevel))] * _uiScale; }
        }

        private bool SelectionReady
        {
            get
            {
                return _hasSelection &&
                       _dragMode != DragMode.Creating &&
                       _selection.Width >= 3 &&
                       _selection.Height >= 3;
            }
        }

        private bool IsCaptureClosed
        {
            get { return _closing || _resourcesDisposed || IsDisposed || Disposing; }
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // Background is composed from the captured screen in OnPaint.
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (IsCaptureClosed)
            {
                return;
            }

            base.OnPaint(e);
            if (ClientRectangle.Width <= 0 || ClientRectangle.Height <= 0)
            {
                return;
            }

            Graphics g = e.Graphics;
            DrawBackgroundRegion(g);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            if (_hasSelection && _selection.Width > 0 && _selection.Height > 0)
            {
                DrawAnnotations(g);
                DrawSelectionBorder(g);
                DrawSizeBadge(g);

                if (SelectionReady)
                {
                    BuildToolbarLayout();
                    if (_tool == CaptureTool.Select && _dragMode != DragMode.Drawing)
                    {
                        DrawSelectionHandles(g);
                    }

                    DrawToolbar(g);
                    if (_paletteOpen)
                    {
                        DrawPalette(g);
                    }

                    DrawHoverTooltip(g);
                }
            }
        }

        private void DrawBackgroundRegion(Graphics g)
        {
            // Repaint only the invalidated region. This keeps dragging a selection
            // responsive even on 4K / multi-monitor desktops.
            RectangleF[] scans;
            using (Matrix matrix = new Matrix())
            {
                scans = g.Clip.GetRegionScans(matrix);
            }

            using (SolidBrush dim = new SolidBrush(Color.FromArgb(124, 0, 0, 0)))
            {
                for (int i = 0; i < scans.Length; i++)
                {
                    Rectangle clip = Rectangle.Round(scans[i]);
                    clip.Intersect(ClientRectangle);
                    if (clip.Width <= 0 || clip.Height <= 0)
                    {
                        continue;
                    }

                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImage(_screen, clip, clip.X, clip.Y, clip.Width, clip.Height, GraphicsUnit.Pixel);
                    g.CompositingMode = CompositingMode.SourceOver;

                    if (!_hasSelection || _selection.Width <= 0 || _selection.Height <= 0)
                    {
                        g.FillRectangle(dim, clip);
                        continue;
                    }

                    Rectangle visible = Rectangle.Intersect(clip, _selection);
                    if (visible.Width <= 0 || visible.Height <= 0)
                    {
                        g.FillRectangle(dim, clip);
                        continue;
                    }

                    // Dim only the part of this invalid region outside the selection.
                    // This avoids a second full image blit for the selected area.
                    FillDimBand(g, dim, Rectangle.FromLTRB(clip.Left, clip.Top, clip.Right, visible.Top));
                    FillDimBand(g, dim, Rectangle.FromLTRB(clip.Left, visible.Bottom, clip.Right, clip.Bottom));
                    FillDimBand(g, dim, Rectangle.FromLTRB(clip.Left, visible.Top, visible.Left, visible.Bottom));
                    FillDimBand(g, dim, Rectangle.FromLTRB(visible.Right, visible.Top, clip.Right, visible.Bottom));
                }
            }
        }

        private static void FillDimBand(Graphics g, Brush brush, Rectangle band)
        {
            if (band.Width > 0 && band.Height > 0)
            {
                g.FillRectangle(brush, band);
            }
        }

        private void DrawAnnotations(Graphics g)
        {
            GraphicsState state = g.Save();
            Rectangle clip = _selection;
            clip.Inflate(1, 1);
            g.SetClip(clip, CombineMode.Intersect);
            foreach (Annotation annotation in _annotations)
            {
                annotation.Render(g, _selection.Location);
            }

            if (_drawing != null)
            {
                _drawing.Render(g, _selection.Location);
            }

            g.Restore(state);
        }

        private void DrawSelectionBorder(Graphics g)
        {
            Rectangle r = _selection;
            if (r.Width <= 0 || r.Height <= 0)
            {
                return;
            }

            SmoothingMode oldSmoothing = g.SmoothingMode;
            PixelOffsetMode oldPixelOffset = g.PixelOffsetMode;
            g.SmoothingMode = SmoothingMode.None;
            g.PixelOffsetMode = PixelOffsetMode.None;
            try
            {
                // Paint four exact 1px strips so every edge has the same weight.
                using (SolidBrush brush = new SolidBrush(AppPalette.Accent))
                {
                    g.FillRectangle(brush, r.Left, r.Top, r.Width, 1);
                    g.FillRectangle(brush, r.Left, r.Bottom - 1, r.Width, 1);
                    g.FillRectangle(brush, r.Left, r.Top, 1, r.Height);
                    g.FillRectangle(brush, r.Right - 1, r.Top, 1, r.Height);
                }
            }
            finally
            {
                g.SmoothingMode = oldSmoothing;
                g.PixelOffsetMode = oldPixelOffset;
            }
        }

        private void DrawSizeBadge(Graphics g)
        {
            if (_selection.Width <= 0 || _selection.Height <= 0)
            {
                return;
            }

            string text = string.Format(CultureInfo.InvariantCulture, "{0} × {1}", _selection.Width, _selection.Height);
            float padX = 8f * _uiScale;
            float height = 23f * _uiScale;
            SizeF measured = g.MeasureString(text, _fontBadge);
            float width = measured.Width + padX * 2f;
            float x = _selection.Left;
            float y = _selection.Top - height - 7f * _uiScale;
            if (y < 3f * _uiScale)
            {
                y = _selection.Top + 7f * _uiScale;
            }

            if (x + width > _screen.Width - 3f * _uiScale)
            {
                x = _screen.Width - width - 3f * _uiScale;
            }

            if (x < 3f * _uiScale)
            {
                x = 3f * _uiScale;
            }

            RectangleF badge = new RectangleF(x, y, width, height);
            DrawUtil.FillRounded(g, Color.FromArgb(238, AppPalette.Accent), badge, 5f * _uiScale);
            DrawUtil.DrawText(g, text, _fontBadge, Color.White, badge,
                StringAlignment.Center, StringAlignment.Center, true);
        }

        private void DrawSelectionHandles(Graphics g)
        {
            Point[] centers = GetHandleCenters();
            float size = Math.Max(6f, 8f * _uiScale);
            bool narrow = _selection.Width < 26f * _uiScale || _selection.Height < 26f * _uiScale;

            using (SolidBrush fill = new SolidBrush(Color.White))
            using (Pen border = new Pen(AppPalette.Accent, Math.Max(1f, 1.5f * _uiScale)))
            using (SolidBrush shadow = new SolidBrush(Color.FromArgb(90, 0, 0, 0)))
            {
                for (int i = 0; i < centers.Length; i++)
                {
                    if (narrow && (i == 1 || i == 3 || i == 5 || i == 7))
                    {
                        continue;
                    }

                    RectangleF handle = new RectangleF(
                        centers[i].X - size / 2f,
                        centers[i].Y - size / 2f,
                        size,
                        size);
                    g.FillRectangle(shadow, handle.X + 1f, handle.Y + 1f, handle.Width, handle.Height);
                    g.FillRectangle(fill, handle);
                    g.DrawRectangle(border, handle.X, handle.Y, handle.Width, handle.Height);
                }
            }
        }

        private Point[] GetHandleCenters()
        {
            Rectangle r = _selection;
            int cx = r.Left + r.Width / 2;
            int cy = r.Top + r.Height / 2;
            return new Point[]
            {
                new Point(r.Left, r.Top),
                new Point(cx, r.Top),
                new Point(r.Right, r.Top),
                new Point(r.Right, cy),
                new Point(r.Right, r.Bottom),
                new Point(cx, r.Bottom),
                new Point(r.Left, r.Bottom),
                new Point(r.Left, cy)
            };
        }

        private void BuildToolbarLayout()
        {
            _barButtons = new List<ToolbarButton>();
            _dividerPositions = new List<float>();
            if (IsCaptureClosed || !SelectionReady)
            {
                _toolbarBounds = Rectangle.Empty;
                return;
            }

            float scale = _uiScale;
            float buttonSize = 36f * scale;
            float recordWidth = 42f * scale;
            float doneWidth = 67f * scale;
            float gap = 2f * scale;
            float groupGap = 9f * scale;
            float pad = 6f * scale;
            float height = 46f * scale;

            BarAction[] actions = new BarAction[]
            {
                BarAction.Select, BarAction.Rectangle, BarAction.Ellipse, BarAction.Pen, BarAction.Arrow,
                BarAction.Color, BarAction.Width,
                BarAction.Undo,
                BarAction.Ocr, BarAction.Gif, BarAction.Mp4,
                BarAction.Save, BarAction.Close, BarAction.Done
            };
            bool[] groupBefore = new bool[]
            {
                false, false, false, false, false,
                true, false,
                true,
                true, false, false,
                true, false, false
            };
            string[] tips = new string[]
            {
                "选择 / 调整选区   V",
                "矩形标注   R",
                "椭圆标注   E",
                "自由画笔   P",
                "箭头标注   A",
                "标注颜色",
                "线宽：" + WidthNames[_widthLevel] + "（点击切换）",
                "撤销   Ctrl+Z",
                "识别图中文字   O",
                "录制选定区域为 GIF 动图",
                "录制选定区域为 MP4 视频",
                "另存为   Ctrl+S",
                "关闭截图   Esc",
                "完成并复制   Enter"
            };

            float contentX = pad;
            for (int i = 0; i < actions.Length; i++)
            {
                if (groupBefore[i])
                {
                    contentX += groupGap;
                    _dividerPositions.Add(contentX - groupGap / 2f);
                }

                float width = actions[i] == BarAction.Done
                    ? doneWidth
                    : (actions[i] == BarAction.Gif || actions[i] == BarAction.Mp4 ? recordWidth : buttonSize);
                ToolbarButton button = new ToolbarButton();
                button.Action = actions[i];
                button.Tooltip = tips[i];
                button.Bounds = new RectangleF(contentX, 0f, width, buttonSize);
                button.Enabled = actions[i] != BarAction.Undo || _annotations.Count > 0;
                _barButtons.Add(button);
                contentX += width + gap;
            }

            float totalWidth = contentX - gap + pad;
            float totalHeight = height;

            float toolbarX = _selection.Left;
            float toolbarY = _selection.Bottom + 9f * scale;
            if (toolbarY + totalHeight > _screen.Height - 8f * scale)
            {
                toolbarY = _selection.Top - 9f * scale - totalHeight;
            }

            if (toolbarY < 8f * scale)
            {
                toolbarY = _selection.Bottom - 9f * scale - totalHeight;
                toolbarX = _selection.Right - 9f * scale - totalWidth;
            }

            if (toolbarX + totalWidth > _screen.Width - 8f * scale)
            {
                toolbarX = _screen.Width - totalWidth - 8f * scale;
            }

            if (toolbarX < 8f * scale)
            {
                toolbarX = 8f * scale;
            }

            if (toolbarY + totalHeight > _screen.Height - 8f * scale)
            {
                toolbarY = _screen.Height - totalHeight - 8f * scale;
            }

            if (toolbarY < 8f * scale)
            {
                toolbarY = 8f * scale;
            }

            _toolbarBounds = new Rectangle(
                (int)Math.Round(toolbarX),
                (int)Math.Round(toolbarY),
                (int)Math.Round(totalWidth),
                (int)Math.Round(totalHeight));

            float buttonY = _toolbarBounds.Top + (_toolbarBounds.Height - buttonSize) / 2f;
            for (int i = 0; i < _barButtons.Count; i++)
            {
                RectangleF b = _barButtons[i].Bounds;
                b.X += toolbarX;
                b.Y = buttonY;
                _barButtons[i].Bounds = b;
            }

            for (int i = 0; i < _dividerPositions.Count; i++)
            {
                _dividerPositions[i] += toolbarX;
            }

            if (_paletteOpen)
            {
                BuildPaletteLayout();
            }
        }

        private void DrawToolbar(Graphics g)
        {
            if (_toolbarBounds.Width <= 0 || _barButtons == null)
            {
                return;
            }

            float scale = _uiScale;
            RectangleF toolbar = _toolbarBounds;
            for (int i = 4; i >= 1; i--)
            {
                RectangleF shadow = toolbar;
                shadow.Offset(0f, i * 1.7f * scale);
                shadow.Inflate(i * 0.5f * scale, i * 0.5f * scale);
                DrawUtil.FillRounded(g, Color.FromArgb(16, 0, 0, 0), shadow, 11f * scale);
            }

            DrawUtil.FillRounded(g, Color.FromArgb(246, 31, 35, 44), toolbar, 11f * scale);
            DrawUtil.DrawRounded(g, Color.FromArgb(40, 255, 255, 255), 1f, toolbar, 11f * scale);
            DrawUtil.DrawRounded(g, Color.FromArgb(18, 0, 0, 0), 1f,
                new RectangleF(toolbar.X + 1f, toolbar.Y + 1f, toolbar.Width - 2f, toolbar.Height - 2f),
                10f * scale);

            for (int i = 0; i < _dividerPositions.Count; i++)
            {
                using (Pen divider = new Pen(Color.FromArgb(34, 255, 255, 255), 1f))
                {
                    float x = _dividerPositions[i];
                    g.DrawLine(divider, x, toolbar.Top + 12f * scale, x, toolbar.Bottom - 12f * scale);
                }
            }

            foreach (ToolbarButton button in _barButtons)
            {
                DrawToolbarButton(g, button);
            }
        }

        private void DrawToolbarButton(Graphics g, ToolbarButton button)
        {
            bool hover = _hoverAction.HasValue && _hoverAction.Value == button.Action;
            bool isActiveTool = IsActiveTool(button.Action);
            bool done = button.Action == BarAction.Done;
            RectangleF rect = button.Bounds;
            RectangleF background = new RectangleF(
                rect.X + 1f * _uiScale,
                rect.Y + 1f * _uiScale,
                rect.Width - 2f * _uiScale,
                rect.Height - 2f * _uiScale);

            Color backgroundFill = Color.Transparent;
            if (done)
            {
                backgroundFill = hover ? AppPalette.AccentHover : AppPalette.Accent;
            }
            else if (isActiveTool)
            {
                backgroundFill = hover ? AppPalette.AccentHover : AppPalette.Accent;
            }
            else if (hover)
            {
                backgroundFill = button.Action == BarAction.Close
                    ? Color.FromArgb(95, AppPalette.Danger)
                    : AppPalette.ToolbarHover;
            }

            if (backgroundFill.A > 0)
            {
                DrawUtil.FillRounded(g, backgroundFill, background, 7f * _uiScale);
            }

            Color iconColor;
            if (!button.Enabled)
            {
                iconColor = Color.FromArgb(85, 220, 226, 236);
            }
            else if (done || isActiveTool)
            {
                iconColor = Color.White;
            }
            else if (hover && button.Action == BarAction.Close)
            {
                iconColor = Color.FromArgb(255, 250, 250);
            }
            else
            {
                iconColor = AppPalette.Text;
            }

            float iconSize = 19f * _uiScale;
            float iconWidth = iconSize;
            float iconX;
            if (button.Action == BarAction.Done)
            {
                SizeF textSize = g.MeasureString("完成", _fontButton);
                float total = iconSize + 5f * _uiScale + textSize.Width;
                iconX = rect.X + (rect.Width - total) / 2f;
                RectangleF textRect = new RectangleF(
                    iconX + iconSize + 4f * _uiScale,
                    rect.Y,
                    textSize.Width + 2f * _uiScale,
                    rect.Height);
                DrawUtil.DrawText(g, "完成", _fontButton, iconColor, textRect,
                    StringAlignment.Near, StringAlignment.Center, true);
            }
            else if (button.Action == BarAction.Gif || button.Action == BarAction.Mp4)
            {
                iconWidth = rect.Width - 6f * _uiScale;
                iconX = rect.X + (rect.Width - iconWidth) / 2f;
            }
            else
            {
                iconX = rect.X + (rect.Width - iconWidth) / 2f;
            }

            RectangleF iconRect = new RectangleF(
                iconX,
                rect.Y + (rect.Height - iconSize) / 2f,
                iconWidth, iconSize);
            DrawIcon(g, button.Action, iconRect, iconColor, button.Enabled);
        }

        private bool IsActiveTool(BarAction action)
        {
            if (action == BarAction.Select && _tool == CaptureTool.Select) return true;
            if (action == BarAction.Rectangle && _tool == CaptureTool.Rectangle) return true;
            if (action == BarAction.Ellipse && _tool == CaptureTool.Ellipse) return true;
            if (action == BarAction.Pen && _tool == CaptureTool.Pen) return true;
            if (action == BarAction.Arrow && _tool == CaptureTool.Arrow) return true;
            return false;
        }

        private void DrawIcon(Graphics g, BarAction action, RectangleF r, Color color, bool enabled)
        {
            float unit = r.Width / 20f;
            using (Pen pen = new Pen(color, Math.Max(1f, 1.55f * unit)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                using (SolidBrush brush = new SolidBrush(color))
                {
                    switch (action)
                    {
                        case BarAction.Select:
                            {
                                PointF[] pointer = new PointF[]
                                {
                                    new PointF(r.X + 4.5f * unit, r.Y + 2.8f * unit),
                                    new PointF(r.X + 4.5f * unit, r.Y + 16.7f * unit),
                                    new PointF(r.X + 8.0f * unit, r.Y + 13.0f * unit),
                                    new PointF(r.X + 10.7f * unit, r.Y + 18.2f * unit),
                                    new PointF(r.X + 13.1f * unit, r.Y + 17.0f * unit),
                                    new PointF(r.X + 10.3f * unit, r.Y + 11.8f * unit),
                                    new PointF(r.X + 15.9f * unit, r.Y + 11.4f * unit)
                                };
                                g.FillPolygon(brush, pointer);
                                break;
                            }

                        case BarAction.Rectangle:
                            {
                                RectangleF box = new RectangleF(
                                    r.X + 3.2f * unit, r.Y + 5.0f * unit,
                                    13.6f * unit, 10.0f * unit);
                                DrawUtil.DrawRounded(g, pen, box, 1.2f * unit);
                                break;
                            }

                        case BarAction.Ellipse:
                            {
                                RectangleF ellipse = new RectangleF(
                                    r.X + 3.2f * unit, r.Y + 5.0f * unit,
                                    13.6f * unit, 10.0f * unit);
                                g.DrawEllipse(pen, ellipse);
                                break;
                            }

                        case BarAction.Pen:
                            {
                                PointF[] body = new PointF[]
                                {
                                    new PointF(r.X + 6.0f * unit, r.Y + 11.3f * unit),
                                    new PointF(r.X + 11.8f * unit, r.Y + 4.5f * unit),
                                    new PointF(r.X + 14.7f * unit, r.Y + 7.4f * unit),
                                    new PointF(r.X + 8.5f * unit, r.Y + 13.8f * unit)
                                };
                                g.FillPolygon(brush, body);
                                PointF[] nib = new PointF[]
                                {
                                    new PointF(r.X + 6.0f * unit, r.Y + 11.3f * unit),
                                    new PointF(r.X + 8.5f * unit, r.Y + 13.8f * unit),
                                    new PointF(r.X + 3.6f * unit, r.Y + 16.4f * unit)
                                };
                                g.FillPolygon(brush, nib);
                                g.DrawLine(pen,
                                    r.X + 7.0f * unit, r.Y + 12.6f * unit,
                                    r.X + 10.1f * unit, r.Y + 9.0f * unit);
                                break;
                            }

                        case BarAction.Arrow:
                            {
                                PointF start = new PointF(r.X + 3.6f * unit, r.Y + 16.0f * unit);
                                PointF tip = new PointF(r.X + 16.1f * unit, r.Y + 3.7f * unit);
                                g.DrawLine(pen, start, new PointF(r.X + 11.6f * unit, r.Y + 8.3f * unit));
                                PointF[] head = new PointF[]
                                {
                                    tip,
                                    new PointF(r.X + 8.1f * unit, r.Y + 8.6f * unit),
                                    new PointF(r.X + 12.5f * unit, r.Y + 12.8f * unit)
                                };
                                g.FillPolygon(brush, head);
                                break;
                            }

                        case BarAction.Color:
                            {
                                float d = 14.5f * unit;
                                RectangleF swatch = new RectangleF(
                                    r.X + (r.Width - d) / 2f,
                                    r.Y + (r.Height - d) / 2f,
                                    d, d);
                                using (SolidBrush selectedColor = new SolidBrush(_color))
                                {
                                    g.FillEllipse(selectedColor, swatch);
                                }
                                Color ringColor = _color.GetBrightness() > 0.82f
                                    ? Color.FromArgb(180, 28, 33, 43)
                                    : Color.FromArgb(245, 255, 255, 255);
                                using (Pen ring = new Pen(ringColor, Math.Max(1f, 1.4f * unit)))
                                {
                                    g.DrawEllipse(ring, swatch);
                                }
                                break;
                            }

                        case BarAction.Width:
                            {
                                for (int i = 0; i < 3; i++)
                                {
                                    float lineWidth = (1.05f + i * 1.05f) * unit;
                                    Color c = i == _widthLevel ? color : Color.FromArgb(62, color);
                                    using (Pen widthPen = new Pen(c, Math.Max(0.8f, lineWidth)))
                                    {
                                        widthPen.StartCap = LineCap.Round;
                                        widthPen.EndCap = LineCap.Round;
                                        float y = r.Y + (6.6f + i * 3.8f) * unit;
                                        g.DrawLine(widthPen, r.X + 4.5f * unit, y, r.X + 15.5f * unit, y);
                                    }
                                }
                                break;
                            }

                        case BarAction.Undo:
                            {
                                using (GraphicsPath path = new GraphicsPath())
                                {
                                    path.AddBezier(
                                        r.X + 16.2f * unit, r.Y + 13.6f * unit,
                                        r.X + 14.2f * unit, r.Y + 6.0f * unit,
                                        r.X + 8.0f * unit, r.Y + 3.9f * unit,
                                        r.X + 4.6f * unit, r.Y + 8.1f * unit);
                                    g.DrawPath(pen, path);
                                    g.DrawLines(pen, new PointF[]
                                    {
                                        new PointF(r.X + 4.6f * unit, r.Y + 8.1f * unit),
                                        new PointF(r.X + 9.0f * unit, r.Y + 7.4f * unit),
                                        new PointF(r.X + 7.5f * unit, r.Y + 12.1f * unit)
                                    });
                                }
                                break;
                            }

                        case BarAction.Ocr:
                            {
                                RectangleF textRect = new RectangleF(
                                    r.X + 2.0f * unit, r.Y + 1.7f * unit,
                                    r.Width - 4f * unit, r.Height - 3f * unit);
                                using (StringFormat format = new StringFormat())
                                {
                                    format.Alignment = StringAlignment.Center;
                                    format.LineAlignment = StringAlignment.Center;
                                    g.DrawString("识", _fontIcon, brush, textRect, format);
                                }
                                break;
                            }

                        case BarAction.Gif:
                            {
                                using (StringFormat format = new StringFormat())
                                {
                                    format.Alignment = StringAlignment.Center;
                                    format.LineAlignment = StringAlignment.Center;
                                    g.DrawString("GIF", _fontRecord, brush, r, format);
                                }
                                break;
                            }

                        case BarAction.Mp4:
                            {
                                using (StringFormat format = new StringFormat())
                                {
                                    format.Alignment = StringAlignment.Center;
                                    format.LineAlignment = StringAlignment.Center;
                                    g.DrawString("MP4", _fontRecord, brush, r, format);
                                }
                                break;
                            }

                        case BarAction.Save:
                            {
                                g.DrawLine(pen,
                                    r.X + 10.0f * unit, r.Y + 2.5f * unit,
                                    r.X + 10.0f * unit, r.Y + 12.4f * unit);
                                g.DrawLines(pen, new PointF[]
                                {
                                    new PointF(r.X + 5.8f * unit, r.Y + 8.4f * unit),
                                    new PointF(r.X + 10.0f * unit, r.Y + 12.6f * unit),
                                    new PointF(r.X + 14.2f * unit, r.Y + 8.4f * unit)
                                });
                                g.DrawLines(pen, new PointF[]
                                {
                                    new PointF(r.X + 4.1f * unit, r.Y + 13.2f * unit),
                                    new PointF(r.X + 4.1f * unit, r.Y + 17.0f * unit),
                                    new PointF(r.X + 15.9f * unit, r.Y + 17.0f * unit),
                                    new PointF(r.X + 15.9f * unit, r.Y + 13.2f * unit)
                                });
                                break;
                            }

                        case BarAction.Close:
                            {
                                g.DrawLine(pen,
                                    r.X + 5.4f * unit, r.Y + 5.4f * unit,
                                    r.X + 14.6f * unit, r.Y + 14.6f * unit);
                                g.DrawLine(pen,
                                    r.X + 14.6f * unit, r.Y + 5.4f * unit,
                                    r.X + 5.4f * unit, r.Y + 14.6f * unit);
                                break;
                            }

                        case BarAction.Done:
                            {
                                using (Pen check = new Pen(color, Math.Max(1.4f, 1.9f * unit)))
                                {
                                    check.StartCap = LineCap.Round;
                                    check.EndCap = LineCap.Round;
                                    check.LineJoin = LineJoin.Round;
                                    DrawUtil.DrawCheckMark(g, check, r);
                                }
                                break;
                            }
                    }
                }
            }
        }

        private void DrawHoverTooltip(Graphics g)
        {
            if (!_hoverAction.HasValue || _paletteOpen || _barButtons == null)
            {
                return;
            }

            ToolbarButton hovered = null;
            for (int i = 0; i < _barButtons.Count; i++)
            {
                if (_barButtons[i].Action == _hoverAction.Value)
                {
                    hovered = _barButtons[i];
                    break;
                }
            }

            if (hovered == null)
            {
                return;
            }

            string text = hovered.Tooltip;
            SizeF measure = g.MeasureString(text, _fontTooltip);
            float width = measure.Width + 18f * _uiScale;
            float height = 25f * _uiScale;
            float x = hovered.Bounds.X + hovered.Bounds.Width / 2f - width / 2f;
            float y = _toolbarBounds.Top - height - 8f * _uiScale;
            if (y < 5f * _uiScale)
            {
                y = _toolbarBounds.Bottom + 8f * _uiScale;
            }

            if (x < 5f * _uiScale)
            {
                x = 5f * _uiScale;
            }

            if (x + width > _screen.Width - 5f * _uiScale)
            {
                x = _screen.Width - width - 5f * _uiScale;
            }

            if (y + height > _screen.Height - 5f * _uiScale)
            {
                y = _screen.Height - height - 5f * _uiScale;
            }

            RectangleF bubble = new RectangleF(x, y, width, height);
            for (int i = 3; i >= 1; i--)
            {
                RectangleF shadow = bubble;
                shadow.Offset(0f, i * _uiScale);
                DrawUtil.FillRounded(g, Color.FromArgb(20, 0, 0, 0), shadow, 7f * _uiScale);
            }

            DrawUtil.FillRounded(g, Color.FromArgb(248, 22, 25, 33), bubble, 7f * _uiScale);
            DrawUtil.DrawRounded(g, Color.FromArgb(40, 255, 255, 255), 1f, bubble, 7f * _uiScale);
            DrawUtil.DrawText(g, text, _fontTooltip, Color.FromArgb(240, 238, 241, 247), bubble,
                StringAlignment.Center, StringAlignment.Center, true);
        }

        private void BuildPaletteLayout()
        {
            if (!_paletteOpen || _barButtons == null)
            {
                _paletteBounds = Rectangle.Empty;
                _paletteCells = null;
                return;
            }

            ToolbarButton colorButton = FindButton(BarAction.Color);
            if (colorButton == null)
            {
                _paletteOpen = false;
                return;
            }

            float scale = _uiScale;
            int columns = 6;
            int rows = 2;
            float cell = 27f * scale;
            float gap = 6f * scale;
            float pad = 11f * scale;
            float titleHeight = 22f * scale;
            float width = pad * 2f + columns * cell + (columns - 1) * gap;
            float height = pad * 2f + titleHeight + rows * cell + (rows - 1) * gap;

            float x = colorButton.Bounds.X + colorButton.Bounds.Width / 2f - width / 2f;
            float y = _toolbarBounds.Top - height - 8f * scale;
            if (y < 5f * scale)
            {
                y = _toolbarBounds.Bottom + 8f * scale;
            }

            if (x < 5f * scale)
            {
                x = 5f * scale;
            }

            if (x + width > _screen.Width - 5f * scale)
            {
                x = _screen.Width - width - 5f * scale;
            }

            if (y + height > _screen.Height - 5f * scale)
            {
                y = _screen.Height - height - 5f * scale;
            }

            if (y < 5f * scale)
            {
                y = 5f * scale;
            }

            _paletteBounds = new Rectangle((int)Math.Round(x), (int)Math.Round(y),
                (int)Math.Round(width), (int)Math.Round(height));
            _paletteCells = new List<PaletteCell>();

            for (int i = 0; i < columns * rows; i++)
            {
                float cx = _paletteBounds.X + pad + (i % columns) * (cell + gap);
                float cy = _paletteBounds.Y + pad + titleHeight + (i / columns) * (cell + gap);
                PaletteCell paletteCell = new PaletteCell();
                paletteCell.Bounds = new RectangleF(cx, cy, cell, cell);
                paletteCell.IsCustom = i == 11;
                if (i < AnnotationPalette.Length)
                {
                    paletteCell.Color = AnnotationPalette[i];
                }
                else
                {
                    paletteCell.Color = Color.Empty;
                }

                _paletteCells.Add(paletteCell);
            }
        }

        private void DrawPalette(Graphics g)
        {
            if (!_paletteOpen || _paletteCells == null)
            {
                return;
            }

            RectangleF panel = _paletteBounds;
            float scale = _uiScale;
            for (int i = 4; i >= 1; i--)
            {
                RectangleF shadow = panel;
                shadow.Offset(0f, i * 1.6f * scale);
                shadow.Inflate(i * 0.4f * scale, i * 0.4f * scale);
                DrawUtil.FillRounded(g, Color.FromArgb(18, 0, 0, 0), shadow, 10f * scale);
            }

            DrawUtil.FillRounded(g, Color.FromArgb(250, 33, 37, 46), panel, 10f * scale);
            DrawUtil.DrawRounded(g, Color.FromArgb(45, 255, 255, 255), 1f, panel, 10f * scale);
            DrawUtil.DrawText(g, "标注颜色", _fontPaletteTitle, AppPalette.Text,
                new RectangleF(panel.X + 12f * scale, panel.Y + 9f * scale,
                    panel.Width - 24f * scale, 20f * scale),
                StringAlignment.Near, StringAlignment.Center, true);

            for (int i = 0; i < _paletteCells.Count; i++)
            {
                PaletteCell cell = _paletteCells[i];
                float diameter = 19f * scale;
                RectangleF circle = new RectangleF(
                    cell.Bounds.X + (cell.Bounds.Width - diameter) / 2f,
                    cell.Bounds.Y + (cell.Bounds.Height - diameter) / 2f,
                    diameter, diameter);
                bool hover = _hoverPalette == i;
                bool selected = !cell.IsCustom && cell.Color.ToArgb() == _color.ToArgb();

                if (hover || selected)
                {
                    Color ringColor = Color.FromArgb(150, 255, 255, 255);
                    if (selected)
                    {
                        ringColor = !cell.IsCustom && cell.Color.GetBrightness() > 0.82f
                            ? Color.FromArgb(235, 36, 41, 50)
                            : Color.White;
                    }

                    using (Pen ring = new Pen(ringColor,
                        selected ? Math.Max(1.7f, 2f * scale) : 1f))
                    {
                        g.DrawEllipse(ring,
                            circle.X - 3f * scale, circle.Y - 3f * scale,
                            circle.Width + 6f * scale, circle.Height + 6f * scale);
                    }
                }

                if (cell.IsCustom)
                {
                    RectangleF inner = circle;
                    using (SolidBrush dark = new SolidBrush(Color.FromArgb(250, 26, 30, 38)))
                    {
                        g.FillEllipse(dark, inner);
                    }

                    Color[] wheel = new Color[]
                    {
                        Color.FromArgb(235, 64, 52),
                        Color.FromArgb(255, 179, 0),
                        Color.FromArgb(67, 176, 71),
                        Color.FromArgb(0, 172, 193),
                        Color.FromArgb(30, 136, 229),
                        Color.FromArgb(156, 65, 210)
                    };
                    for (int wedge = 0; wedge < wheel.Length; wedge++)
                    {
                        using (SolidBrush pie = new SolidBrush(wheel[wedge]))
                        {
                            g.FillPie(pie, inner.X, inner.Y, inner.Width, inner.Height,
                                -90f + wedge * 60f, 60f);
                        }
                    }

                    float center = 9f * scale;
                    using (SolidBrush dark = new SolidBrush(Color.FromArgb(250, 26, 30, 38)))
                    {
                        g.FillEllipse(dark,
                            inner.X + (inner.Width - center) / 2f,
                            inner.Y + (inner.Height - center) / 2f,
                            center, center);
                    }

                    using (Pen plus = new Pen(Color.White, Math.Max(1.2f, 1.7f * scale)))
                    {
                        plus.StartCap = LineCap.Round;
                        plus.EndCap = LineCap.Round;
                        float cx = inner.X + inner.Width / 2f;
                        float cy = inner.Y + inner.Height / 2f;
                        float arm = 3.2f * scale;
                        g.DrawLine(plus, cx - arm, cy, cx + arm, cy);
                        g.DrawLine(plus, cx, cy - arm, cx, cy + arm);
                    }
                }
                else
                {
                    using (SolidBrush fill = new SolidBrush(cell.Color))
                    {
                        g.FillEllipse(fill, circle);
                    }

                    Color outline = cell.Color.GetBrightness() > 0.8f
                        ? Color.FromArgb(75, 0, 0, 0)
                        : Color.FromArgb(45, 255, 255, 255);
                    using (Pen p = new Pen(outline, 1f))
                    {
                        g.DrawEllipse(p, circle);
                    }

                    if (selected)
                    {
                        using (Pen check = new Pen(
                            cell.Color.GetBrightness() > 0.62f
                                ? Color.FromArgb(185, 28, 31, 38)
                                : Color.White,
                            Math.Max(1.4f, 1.7f * scale)))
                        {
                            check.StartCap = LineCap.Round;
                            check.EndCap = LineCap.Round;
                            float left = circle.X + circle.Width * 0.30f;
                            float top = circle.Y + circle.Height * 0.50f;
                            g.DrawLines(check, new PointF[]
                            {
                                new PointF(left, top),
                                new PointF(circle.X + circle.Width * 0.44f, circle.Y + circle.Height * 0.65f),
                                new PointF(circle.X + circle.Width * 0.72f, circle.Y + circle.Height * 0.34f)
                            });
                        }
                    }
                }
            }
        }

        private ToolbarButton FindButton(BarAction action)
        {
            if (_barButtons == null)
            {
                return null;
            }

            for (int i = 0; i < _barButtons.Count; i++)
            {
                if (_barButtons[i].Action == action)
                {
                    return _barButtons[i];
                }
            }

            return null;
        }

        private ToolbarButton HitToolbar(Point point)
        {
            if (_barButtons == null || !SelectionReady)
            {
                return null;
            }

            for (int i = 0; i < _barButtons.Count; i++)
            {
                if (_barButtons[i].Bounds.Contains(point))
                {
                    return _barButtons[i];
                }
            }

            return null;
        }

        private int HitPalette(Point point)
        {
            if (!_paletteOpen || _paletteCells == null)
            {
                return -1;
            }

            for (int i = 0; i < _paletteCells.Count; i++)
            {
                if (_paletteCells[i].Bounds.Contains(point))
                {
                    return i;
                }
            }

            return -1;
        }

        private int HitResizeHandle(Point point)
        {
            if (!SelectionReady)
            {
                return -1;
            }

            Point[] centers = GetHandleCenters();
            float size = Math.Max(6f, 8f * _uiScale);
            float radius = size / 2f + 2.2f * _uiScale;
            for (int i = 0; i < centers.Length; i++)
            {
                float dx = point.X - centers[i].X;
                float dy = point.Y - centers[i].Y;
                if (Math.Abs(dx) <= radius && Math.Abs(dy) <= radius)
                {
                    return i;
                }
            }

            float edge = 5.5f * _uiScale;
            bool left = Math.Abs(point.X - _selection.Left) <= edge;
            bool right = Math.Abs(point.X - _selection.Right) <= edge;
            bool top = Math.Abs(point.Y - _selection.Top) <= edge;
            bool bottom = Math.Abs(point.Y - _selection.Bottom) <= edge;
            bool insideH = point.Y >= _selection.Top - edge && point.Y <= _selection.Bottom + edge;
            bool insideV = point.X >= _selection.Left - edge && point.X <= _selection.Right + edge;

            if (left && top) return 0;
            if (right && top) return 2;
            if (right && bottom) return 4;
            if (left && bottom) return 6;
            if (top && insideV) return 1;
            if (right && insideH) return 3;
            if (bottom && insideV) return 5;
            if (left && insideH) return 7;
            return -1;
        }

        private Rectangle GetSelectionDirtyBounds(Rectangle selection)
        {
            if (selection.Width <= 0 || selection.Height <= 0)
            {
                return Rectangle.Empty;
            }

            int scalePad = (int)Math.Ceiling(18f * _uiScale);
            Rectangle dirty = selection;
            dirty.Inflate(scalePad, scalePad);

            int badgeWidth = (int)Math.Ceiling(380f * _uiScale);
            int badgeHeight = (int)Math.Ceiling(120f * _uiScale);
            Rectangle badgeZone = new Rectangle(
                selection.Left - (int)Math.Ceiling(190f * _uiScale),
                selection.Top - (int)Math.Ceiling(55f * _uiScale),
                badgeWidth,
                badgeHeight);
            dirty = Rectangle.Union(dirty, badgeZone);
            dirty.Intersect(ClientRectangle);
            return dirty;
        }

        private Rectangle GetSizeBadgeDirtyBounds(Rectangle selection)
        {
            if (selection.Width <= 0 || selection.Height <= 0)
            {
                return Rectangle.Empty;
            }

            Rectangle zone = new Rectangle(
                selection.Left - (int)Math.Ceiling(230f * _uiScale),
                selection.Top - (int)Math.Ceiling(60f * _uiScale),
                (int)Math.Ceiling(500f * _uiScale),
                (int)Math.Ceiling(150f * _uiScale));
            zone.Intersect(ClientRectangle);
            return zone;
        }

        private static void AddClippedDirty(List<Rectangle> regions, Rectangle region, int padding)
        {
            if (region.Width <= 0 || region.Height <= 0)
            {
                return;
            }

            if (padding > 0)
            {
                region.Inflate(padding, padding);
            }

            if (region.Width > 0 && region.Height > 0)
            {
                regions.Add(region);
            }
        }

        private static void AddBorderDirty(List<Rectangle> regions, Rectangle selection, int thickness)
        {
            if (selection.Width <= 0 || selection.Height <= 0)
            {
                return;
            }

            AddClippedDirty(regions,
                Rectangle.FromLTRB(selection.Left - thickness, selection.Top - thickness,
                    selection.Right + thickness, selection.Top + thickness), 0);
            AddClippedDirty(regions,
                Rectangle.FromLTRB(selection.Left - thickness, selection.Bottom - thickness,
                    selection.Right + thickness, selection.Bottom + thickness), 0);
            AddClippedDirty(regions,
                Rectangle.FromLTRB(selection.Left - thickness, selection.Top,
                    selection.Left + thickness, selection.Bottom), 0);
            AddClippedDirty(regions,
                Rectangle.FromLTRB(selection.Right - thickness, selection.Top,
                    selection.Right + thickness, selection.Bottom), 0);
        }

        private void InvalidateCreationChange(Rectangle oldSelection, Rectangle newSelection)
        {
            List<Rectangle> regions = new List<Rectangle>();
            Rectangle screen = new Rectangle(0, 0, _screen.Width, _screen.Height);
            Rectangle oldSel = Rectangle.Intersect(oldSelection, screen);
            Rectangle newSel = Rectangle.Intersect(newSelection, screen);

            int borderPadding = (int)Math.Ceiling(7f * _uiScale);
            int borderThickness = (int)Math.Ceiling(4f * _uiScale);
            AddBorderDirty(regions, oldSel, borderThickness);
            AddBorderDirty(regions, newSel, borderThickness);
            Rectangle overlap = Rectangle.Intersect(oldSel, newSel);
            if (overlap.Width <= 0 || overlap.Height <= 0)
            {
                AddClippedDirty(regions, oldSel, borderPadding);
                AddClippedDirty(regions, newSel, borderPadding);
            }
            else
            {
                // Only pixels whose dim/bright state changed need repainting.
                AddClippedDirty(regions,
                    Rectangle.FromLTRB(oldSel.Left, oldSel.Top, oldSel.Right, overlap.Top), borderPadding);
                AddClippedDirty(regions,
                    Rectangle.FromLTRB(oldSel.Left, overlap.Bottom, oldSel.Right, oldSel.Bottom), borderPadding);
                AddClippedDirty(regions,
                    Rectangle.FromLTRB(oldSel.Left, overlap.Top, overlap.Left, overlap.Bottom), borderPadding);
                AddClippedDirty(regions,
                    Rectangle.FromLTRB(overlap.Right, overlap.Top, oldSel.Right, overlap.Bottom), borderPadding);

                AddClippedDirty(regions,
                    Rectangle.FromLTRB(newSel.Left, newSel.Top, newSel.Right, overlap.Top), borderPadding);
                AddClippedDirty(regions,
                    Rectangle.FromLTRB(newSel.Left, overlap.Bottom, newSel.Right, newSel.Bottom), borderPadding);
                AddClippedDirty(regions,
                    Rectangle.FromLTRB(newSel.Left, overlap.Top, overlap.Left, overlap.Bottom), borderPadding);
                AddClippedDirty(regions,
                    Rectangle.FromLTRB(overlap.Right, overlap.Top, newSel.Right, overlap.Bottom), borderPadding);
            }

            AddClippedDirty(regions, GetSizeBadgeDirtyBounds(oldSel), 0);
            AddClippedDirty(regions, GetSizeBadgeDirtyBounds(newSel), 0);

            if (regions.Count > 0)
            {
                InvalidateDirty(regions.ToArray());
            }
        }

        private Rectangle GetToolbarDirtyBounds(Rectangle toolbar)
        {
            if (toolbar.Width <= 0 || toolbar.Height <= 0)
            {
                return Rectangle.Empty;
            }

            int pad = (int)Math.Ceiling(18f * _uiScale);
            int horizontal = (int)Math.Ceiling(220f * _uiScale);
            int vertical = (int)Math.Ceiling(60f * _uiScale);

            Rectangle dirty = toolbar;
            dirty.Inflate(pad, pad);
            Rectangle tooltipZone = new Rectangle(
                toolbar.Left - horizontal,
                toolbar.Top - vertical,
                toolbar.Width + horizontal * 2,
                toolbar.Height + vertical * 2);
            dirty = Rectangle.Union(dirty, tooltipZone);
            dirty.Intersect(ClientRectangle);
            return dirty;
        }

        private Rectangle GetPaletteDirtyBounds()
        {
            if (!_paletteOpen || _paletteBounds.Width <= 0 || _paletteBounds.Height <= 0)
            {
                return Rectangle.Empty;
            }

            Rectangle dirty = _paletteBounds;
            dirty.Inflate((int)Math.Ceiling(18f * _uiScale), (int)Math.Ceiling(18f * _uiScale));
            dirty.Intersect(ClientRectangle);
            return dirty;
        }

        private Rectangle GetDrawingDirtyBounds()
        {
            if (_drawing == null || _selection.Width <= 0 || _selection.Height <= 0)
            {
                return Rectangle.Empty;
            }

            RectangleF bounds = _drawing.GetBounds();
            if (bounds.Width <= 0f || bounds.Height <= 0f)
            {
                return Rectangle.Empty;
            }

            Rectangle dirty = Rectangle.FromLTRB(
                (int)Math.Floor(bounds.Left + _selection.Left),
                (int)Math.Floor(bounds.Top + _selection.Top),
                (int)Math.Ceiling(bounds.Right + _selection.Left),
                (int)Math.Ceiling(bounds.Bottom + _selection.Top));

            Rectangle clip = _selection;
            clip.Inflate(2, 2);
            dirty.Intersect(clip);
            dirty.Intersect(ClientRectangle);
            return dirty;
        }

        private void InvalidateDirty(params Rectangle[] regions)
        {
            if (regions == null)
            {
                return;
            }

            Region dirty = null;
            try
            {
                for (int i = 0; i < regions.Length; i++)
                {
                    Rectangle region = regions[i];
                    if (region.Width <= 0 || region.Height <= 0)
                    {
                        continue;
                    }

                    if (dirty == null)
                    {
                        dirty = new Region(region);
                    }
                    else
                    {
                        dirty.Union(region);
                    }
                }

                if (dirty != null)
                {
                    Invalidate(dirty);
                }
            }
            finally
            {
                if (dirty != null)
                {
                    dirty.Dispose();
                }
            }
        }

        private void BeginCreate(Point location)
        {
            _paletteOpen = false;
            _hoverAction = null;
            _hasSelection = true;
            _dragMode = DragMode.Creating;
            _dragAnchor = ClampToScreen(location);
            _selection = Rectangle.Empty;
            _annotations.Clear();
            _redo.Clear();
            _dragStartSelection = Rectangle.Empty;
            _resizeHandle = -1;
            Invalidate();
        }

        private void BeginMove(Point location)
        {
            _dragMode = DragMode.Moving;
            _dragAnchor = location;
            _dragStartSelection = _selection;
            _hoverAction = null;
            Invalidate();
        }

        private void BeginResize(int handle, Point location)
        {
            _dragMode = DragMode.Resizing;
            _resizeHandle = handle;
            _dragAnchor = location;
            _dragStartSelection = _selection;
            _hoverAction = null;
            Invalidate();
        }

        private void BeginDraw(Point location)
        {
            _paletteOpen = false;
            Point local = new Point(location.X - _selection.Left, location.Y - _selection.Top);
            _drawing = new Annotation(_tool, _color, CurrentStrokeWidth);
            _drawing.Points.Add(local);
            if (_tool != CaptureTool.Pen)
            {
                _drawing.Points.Add(local);
            }

            _dragMode = DragMode.Drawing;
            _dragAnchor = location;
            _hoverAction = null;
            Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (IsCaptureClosed)
            {
                return;
            }

            base.OnMouseDown(e);

            if (e.Button == MouseButtons.Right)
            {
                if (_paletteOpen)
                {
                    _paletteOpen = false;
                    _paletteCells = null;
                    Invalidate();
                    return;
                }

                CloseCapture();
                return;
            }

            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            if (_paletteOpen)
            {
                if (_paletteBounds.Contains(e.Location))
                {
                    int paletteHit = HitPalette(e.Location);
                    if (paletteHit >= 0)
                    {
                        ChoosePaletteColor(paletteHit);
                    }

                    return;
                }

                _paletteOpen = false;
                _paletteCells = null;
                BuildToolbarLayout();
                Invalidate();
                return;
            }

            if (SelectionReady)
            {
                ToolbarButton button = HitToolbar(e.Location);
                if (button != null)
                {
                    ExecuteBarAction(button.Action);
                    return;
                }
            }

            // The second click of a double-click must not start a move or draw.
            // OnDoubleClick will copy the selection and close this window.
            if (e.Clicks >= 2 && SelectionReady && _selection.Contains(e.Location))
            {
                return;
            }

            if (!_hasSelection)
            {
                BeginCreate(e.Location);
                Capture = true;
                return;
            }

            if (_tool == CaptureTool.Select)
            {
                int handle = HitResizeHandle(e.Location);
                if (handle >= 0)
                {
                    BeginResize(handle, e.Location);
                    Capture = true;
                    return;
                }

                if (_selection.Contains(e.Location))
                {
                    BeginMove(e.Location);
                    Capture = true;
                    return;
                }

                BeginCreate(e.Location);
                Capture = true;
                return;
            }

            if (_selection.Contains(e.Location))
            {
                BeginDraw(e.Location);
                Capture = true;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (IsCaptureClosed)
            {
                return;
            }

            base.OnMouseMove(e);

            if (_dragMode == DragMode.Creating)
            {
                Rectangle oldSelection = _selection;
                Point current = ClampToScreen(e.Location);
                _selection = NormalizeRect(_dragAnchor, current);
                InvalidateCreationChange(oldSelection, _selection);
                UpdateCursor(e.Location);
                return;
            }

            if (_dragMode == DragMode.Moving)
            {
                Rectangle oldSelectionDirty = GetSelectionDirtyBounds(_selection);
                Rectangle oldToolbarDirty = GetToolbarDirtyBounds(_toolbarBounds);
                int newX = _dragStartSelection.X + (e.X - _dragAnchor.X);
                int newY = _dragStartSelection.Y + (e.Y - _dragAnchor.Y);
                newX = Math.Max(0, Math.Min(_screen.Width - _selection.Width, newX));
                newY = Math.Max(0, Math.Min(_screen.Height - _selection.Height, newY));
                _selection = new Rectangle(newX, newY, _selection.Width, _selection.Height);
                BuildToolbarLayout();
                InvalidateDirty(oldSelectionDirty, GetSelectionDirtyBounds(_selection),
                    oldToolbarDirty, GetToolbarDirtyBounds(_toolbarBounds));
                UpdateCursor(e.Location);
                return;
            }

            if (_dragMode == DragMode.Resizing)
            {
                Rectangle oldSelectionDirty = GetSelectionDirtyBounds(_selection);
                Rectangle oldToolbarDirty = GetToolbarDirtyBounds(_toolbarBounds);
                ResizeSelection(e.Location);
                BuildToolbarLayout();
                InvalidateDirty(oldSelectionDirty, GetSelectionDirtyBounds(_selection),
                    oldToolbarDirty, GetToolbarDirtyBounds(_toolbarBounds));
                UpdateCursor(e.Location);
                return;
            }

            if (_dragMode == DragMode.Drawing && _drawing != null)
            {
                Rectangle oldDirty = GetDrawingDirtyBounds();
                Point local = new Point(e.X - _selection.Left, e.Y - _selection.Top);
                if (_drawing.Kind == CaptureTool.Pen)
                {
                    PointF last = _drawing.Points[_drawing.Points.Count - 1];
                    float dx = local.X - last.X;
                    float dy = local.Y - last.Y;
                    if (dx * dx + dy * dy >= 1.6f * _uiScale * 1.6f * _uiScale)
                    {
                        _drawing.Points.Add(local);
                    }
                }
                else
                {
                    if (_drawing.Points.Count == 1)
                    {
                        _drawing.Points.Add(local);
                    }
                    else
                    {
                        _drawing.Points[_drawing.Points.Count - 1] = local;
                    }
                }

                InvalidateDirty(oldDirty, GetDrawingDirtyBounds());
                return;
            }

            BarAction? oldHover = _hoverAction;
            int oldPalette = _hoverPalette;

            _hoverAction = null;
            _hoverPalette = -1;
            if (_paletteOpen)
            {
                _hoverPalette = HitPalette(e.Location);
            }
            else
            {
                ToolbarButton button = HitToolbar(e.Location);
                if (button != null)
                {
                    _hoverAction = button.Action;
                }
            }

            UpdateCursor(e.Location);
            if (oldHover != _hoverAction || oldPalette != _hoverPalette)
            {
                if (_paletteOpen)
                {
                    InvalidateDirty(GetPaletteDirtyBounds());
                }
                else
                {
                    InvalidateDirty(GetToolbarDirtyBounds(_toolbarBounds));
                }
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (IsCaptureClosed)
            {
                return;
            }

            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left)
            {
                return;
            }

            if (_dragMode == DragMode.Creating)
            {
                Rectangle oldSelectionDirty = GetSelectionDirtyBounds(_selection);
                Rectangle oldToolbarDirty = GetToolbarDirtyBounds(_toolbarBounds);
                _selection = NormalizeRect(_dragAnchor, ClampToScreen(e.Location));
                if (_selection.Width >= 4 && _selection.Height >= 4)
                {
                    _hasSelection = true;
                    _selection.Intersect(new Rectangle(0, 0, _screen.Width, _screen.Height));
                    _tool = CaptureTool.Select;
                    _dragMode = DragMode.None;
                    Capture = false;
                    UpdateUiScale();
                    BuildToolbarLayout();
                }
                else
                {
                    _hasSelection = false;
                    _selection = Rectangle.Empty;
                    _annotations.Clear();
                    _redo.Clear();
                    _dragMode = DragMode.None;
                    Capture = false;
                }

                InvalidateDirty(oldSelectionDirty, GetSelectionDirtyBounds(_selection),
                    oldToolbarDirty, GetToolbarDirtyBounds(_toolbarBounds));
                UpdateCursor(e.Location);
                return;
            }

            if (_dragMode == DragMode.Moving || _dragMode == DragMode.Resizing)
            {
                Rectangle oldSelectionDirty = GetSelectionDirtyBounds(_selection);
                Rectangle oldToolbarDirty = GetToolbarDirtyBounds(_toolbarBounds);
                _dragMode = DragMode.None;
                Capture = false;
                UpdateUiScale();
                BuildToolbarLayout();
                InvalidateDirty(oldSelectionDirty, GetSelectionDirtyBounds(_selection),
                    oldToolbarDirty, GetToolbarDirtyBounds(_toolbarBounds));
                UpdateCursor(e.Location);
                return;
            }

            if (_dragMode == DragMode.Drawing)
            {
                Rectangle oldDrawingDirty = GetDrawingDirtyBounds();
                Rectangle oldToolbarDirty = GetToolbarDirtyBounds(_toolbarBounds);
                if (_drawing != null)
                {
                    bool accept = _drawing.Kind == CaptureTool.Pen
                        ? _drawing.Points.Count > 0
                        : _drawing.HasArea;
                    if (accept)
                    {
                        _annotations.Add(_drawing);
                        _redo.Clear();
                    }
                }

                _drawing = null;
                _dragMode = DragMode.None;
                Capture = false;
                BuildToolbarLayout();
                InvalidateDirty(oldDrawingDirty, GetDrawingDirtyBounds(),
                    oldToolbarDirty, GetToolbarDirtyBounds(_toolbarBounds));
                UpdateCursor(e.Location);
                return;
            }
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            if (IsCaptureClosed)
            {
                return;
            }

            base.OnDoubleClick(e);
            if (!SelectionReady || _paletteOpen)
            {
                return;
            }

            Point cursor = PointToClient(Cursor.Position);
            if (_selection.Contains(cursor) && HitToolbar(cursor) == null)
            {
                CopyAndClose();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (IsCaptureClosed)
            {
                return;
            }

            base.OnMouseLeave(e);
            Rectangle dirty = _paletteOpen
                ? GetPaletteDirtyBounds()
                : GetToolbarDirtyBounds(_toolbarBounds);
            _hoverAction = null;
            _hoverPalette = -1;
            InvalidateDirty(dirty);
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            if (IsCaptureClosed)
            {
                return;
            }

            base.OnMouseEnter(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);

            if (e.KeyCode == Keys.Escape)
            {
                e.SuppressKeyPress = true;
                e.Handled = true;
                if (_paletteOpen)
                {
                    _paletteOpen = false;
                    _paletteCells = null;
                    Invalidate();
                }
                else
                {
                    CloseCapture();
                }
                return;
            }

            if (!_hasSelection)
            {
                if (e.Control && e.KeyCode == Keys.A)
                {
                    SelectWholeScreen();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }

                return;
            }

            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                CopyAndClose();
                return;
            }

            if (e.Control && e.KeyCode == Keys.C)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                CopyAndClose();
                return;
            }

            if (e.Control && e.KeyCode == Keys.S)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                SaveAs();
                return;
            }

            if (e.Control && e.KeyCode == Keys.Z)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                Undo();
                return;
            }

            if (e.Control && e.KeyCode == Keys.Y)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                Redo();
                return;
            }

            if (e.Control && e.KeyCode == Keys.A)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                SelectWholeScreen();
                return;
            }

            if (!e.Control && !e.Alt)
            {
                if (e.KeyCode == Keys.V)
                {
                    SetTool(CaptureTool.Select);
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.R)
                {
                    SetTool(CaptureTool.Rectangle);
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.E)
                {
                    SetTool(CaptureTool.Ellipse);
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.P)
                {
                    SetTool(CaptureTool.Pen);
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.A)
                {
                    SetTool(CaptureTool.Arrow);
                    e.Handled = true;
                }
                else if (e.KeyCode == Keys.O)
                {
                    StartOcr();
                    e.Handled = true;
                }

                if (e.Handled)
                {
                    e.SuppressKeyPress = true;
                }
            }
        }

        private void UpdateCursor(Point location)
        {
            if (_paletteOpen && _paletteBounds.Contains(location))
            {
                Cursor = Cursors.Hand;
                return;
            }

            ToolbarButton button = HitToolbar(location);
            if (button != null)
            {
                Cursor = Cursors.Hand;
                return;
            }

            if (!_hasSelection)
            {
                Cursor = CrosshairCursor;
                return;
            }

            if (_tool == CaptureTool.Select)
            {
                int handle = HitResizeHandle(location);
                if (handle == 0 || handle == 4)
                {
                    Cursor = Cursors.SizeNWSE;
                    return;
                }

                if (handle == 2 || handle == 6)
                {
                    Cursor = Cursors.SizeNESW;
                    return;
                }

                if (handle == 1 || handle == 5)
                {
                    Cursor = Cursors.SizeNS;
                    return;
                }

                if (handle == 3 || handle == 7)
                {
                    Cursor = Cursors.SizeWE;
                    return;
                }

                if (_selection.Contains(location))
                {
                    Cursor = Cursors.SizeAll;
                    return;
                }

                Cursor = CrosshairCursor;
            }
            else
            {
                Cursor = _selection.Contains(location) ? CrosshairCursor : Cursors.Default;
            }
        }

        private void ResizeSelection(Point location)
        {
            Rectangle start = _dragStartSelection;
            int left = start.Left;
            int top = start.Top;
            int right = start.Right;
            int bottom = start.Bottom;
            int x = Math.Max(0, Math.Min(_screen.Width, location.X));
            int y = Math.Max(0, Math.Min(_screen.Height, location.Y));

            switch (_resizeHandle)
            {
                case 0:
                    left = x; top = y;
                    break;
                case 1:
                    top = y;
                    break;
                case 2:
                    right = x; top = y;
                    break;
                case 3:
                    right = x;
                    break;
                case 4:
                    right = x; bottom = y;
                    break;
                case 5:
                    bottom = y;
                    break;
                case 6:
                    left = x; bottom = y;
                    break;
                case 7:
                    left = x;
                    break;
            }

            if (right - left < 4)
            {
                if (_resizeHandle == 0 || _resizeHandle == 6 || _resizeHandle == 7)
                {
                    left = right - 4;
                }
                else
                {
                    right = left + 4;
                }
            }

            if (bottom - top < 4)
            {
                if (_resizeHandle == 0 || _resizeHandle == 1 || _resizeHandle == 2)
                {
                    top = bottom - 4;
                }
                else
                {
                    bottom = top + 4;
                }
            }

            left = Math.Max(0, left);
            top = Math.Max(0, top);
            right = Math.Min(_screen.Width, right);
            bottom = Math.Min(_screen.Height, bottom);
            _selection = Rectangle.FromLTRB(left, top, right, bottom);
        }

        private void SelectWholeScreen()
        {
            _paletteOpen = false;
            _hasSelection = true;
            _selection = new Rectangle(0, 0, _screen.Width, _screen.Height);
            _dragMode = DragMode.None;
            _annotations.Clear();
            _redo.Clear();
            _tool = CaptureTool.Select;
            UpdateUiScale();
            BuildToolbarLayout();
            Invalidate();
        }

        private Rectangle NormalizeRect(Point a, Point b)
        {
            int x1 = Math.Max(0, Math.Min(_screen.Width, a.X));
            int y1 = Math.Max(0, Math.Min(_screen.Height, a.Y));
            int x2 = Math.Max(0, Math.Min(_screen.Width, b.X));
            int y2 = Math.Max(0, Math.Min(_screen.Height, b.Y));
            int x = Math.Min(x1, x2);
            int y = Math.Min(y1, y2);
            return new Rectangle(x, y, Math.Abs(x1 - x2), Math.Abs(y1 - y2));
        }

        private Point ClampToScreen(Point point)
        {
            return new Point(
                Math.Max(0, Math.Min(_screen.Width - 1, point.X)),
                Math.Max(0, Math.Min(_screen.Height - 1, point.Y)));
        }

        private void ExecuteBarAction(BarAction action)
        {
            switch (action)
            {
                case BarAction.Select:
                    SetTool(CaptureTool.Select);
                    break;
                case BarAction.Rectangle:
                    SetTool(CaptureTool.Rectangle);
                    break;
                case BarAction.Ellipse:
                    SetTool(CaptureTool.Ellipse);
                    break;
                case BarAction.Pen:
                    SetTool(CaptureTool.Pen);
                    break;
                case BarAction.Arrow:
                    SetTool(CaptureTool.Arrow);
                    break;
                case BarAction.Color:
                    _paletteOpen = !_paletteOpen;
                    if (_paletteOpen)
                    {
                        _hoverAction = null;
                        BuildPaletteLayout();
                    }
                    else
                    {
                        _paletteCells = null;
                    }
                    Invalidate();
                    break;
                case BarAction.Width:
                    CycleWidth();
                    break;
                case BarAction.Undo:
                    Undo();
                    break;
                case BarAction.Ocr:
                    StartOcr();
                    break;
                case BarAction.Gif:
                    StartRecording(RecordingFormat.Gif);
                    break;
                case BarAction.Mp4:
                    StartRecording(RecordingFormat.Mp4);
                    break;
                case BarAction.Save:
                    SaveAs();
                    break;
                case BarAction.Close:
                    CloseCapture();
                    break;
                case BarAction.Done:
                    CopyAndClose();
                    break;
            }
        }

        private void SetTool(CaptureTool tool)
        {
            _tool = tool;
            _paletteOpen = false;
            _paletteCells = null;
            _hoverAction = null;
            Invalidate();
            UpdateCursor(PointToClient(Cursor.Position));
        }

        private void CycleWidth()
        {
            _widthLevel = (_widthLevel + 1) % WidthLevels.Length;
            _settings.WidthLevel = _widthLevel;
            _settings.Save();
            BuildToolbarLayout();
            Invalidate();
        }

        private void ChoosePaletteColor(int index)
        {
            if (_paletteCells == null || index < 0 || index >= _paletteCells.Count)
            {
                return;
            }

            PaletteCell cell = _paletteCells[index];
            if (cell.IsCustom)
            {
                _paletteOpen = false;
                _paletteCells = null;
                Invalidate();
                using (ColorDialog dialog = new ColorDialog())
                {
                    dialog.AllowFullOpen = true;
                    dialog.AnyColor = true;
                    dialog.FullOpen = true;
                    dialog.Color = _color;
                    bool oldTopMost = TopMost;
                    TopMost = false;
                    try
                    {
                        if (dialog.ShowDialog(this) == DialogResult.OK)
                        {
                            _color = Color.FromArgb(255, dialog.Color);
                            _settings.AnnotationColor = _color;
                            _settings.Save();
                        }
                    }
                    finally
                    {
                        TopMost = oldTopMost;
                        Activate();
                        Invalidate();
                    }
                }
                return;
            }

            _color = cell.Color;
            _settings.AnnotationColor = _color;
            _settings.Save();
            _paletteOpen = false;
            _paletteCells = null;
            Invalidate();
        }

        private void Undo()
        {
            if (_annotations.Count == 0)
            {
                return;
            }

            Annotation last = _annotations[_annotations.Count - 1];
            _annotations.RemoveAt(_annotations.Count - 1);
            _redo.Add(last);
            BuildToolbarLayout();
            Invalidate();
        }

        private void Redo()
        {
            if (_redo.Count == 0)
            {
                return;
            }

            Annotation last = _redo[_redo.Count - 1];
            _redo.RemoveAt(_redo.Count - 1);
            _annotations.Add(last);
            BuildToolbarLayout();
            Invalidate();
        }

        private Bitmap RenderResult()
        {
            Rectangle crop = _selection;
            crop.Intersect(new Rectangle(0, 0, _screen.Width, _screen.Height));
            if (crop.Width <= 0 || crop.Height <= 0)
            {
                throw new InvalidOperationException("截图选区无效。");
            }

            Bitmap result = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics g = Graphics.FromImage(result))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImage(_screen, new Rectangle(0, 0, crop.Width, crop.Height),
                        crop, GraphicsUnit.Pixel);
                    g.CompositingMode = CompositingMode.SourceOver;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;

                    GraphicsState state = g.Save();
                    g.SetClip(new Rectangle(0, 0, crop.Width, crop.Height));
                    foreach (Annotation annotation in _annotations)
                    {
                        annotation.Render(g, Point.Empty);
                    }
                    g.Restore(state);
                }

                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }

        private void CopyAndClose()
        {
            if (!SelectionReady)
            {
                return;
            }

            try
            {
                _settings.Save();
                using (Bitmap output = RenderResult())
                {
                    ClipboardHelper.PutImage(output);
                }

                string toastTitle = "已复制截图";
                if (_app != null)
                {
                    _app.ShowToast(toastTitle, "截图已复制到剪贴板，可直接粘贴。");
                }

                CloseCapture();
            }
            catch (Exception ex)
            {
                AppLog.WriteException("Copy", ex);
                ShowError("复制失败", "无法将截图写入剪贴板。\n\n" + ex.Message +
                    "\n\n请关闭可能占用剪贴板的程序后重试。");
            }
        }

        private void SaveAs()
        {
            if (!SelectionReady)
            {
                return;
            }

            _paletteOpen = false;
            _paletteCells = null;
            using (Bitmap output = RenderResult())
            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                string initial = _settings.LastSaveDirectory;
                if (string.IsNullOrEmpty(initial) || !Directory.Exists(initial))
                {
                    initial = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                }

                dialog.Title = "另存为";
                dialog.InitialDirectory = initial;
                dialog.Filter = "PNG 图片 (*.png)|*.png|JPEG 图片 (*.jpg;*.jpeg)|*.jpg;*.jpeg|BMP 图片 (*.bmp)|*.bmp";
                dialog.FilterIndex = 1;
                dialog.DefaultExt = "png";
                dialog.AddExtension = true;
                dialog.OverwritePrompt = true;
                dialog.RestoreDirectory = true;
                dialog.FileName = "截图_" + DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

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
                    Activate();
                }

                if (result != DialogResult.OK)
                {
                    Invalidate();
                    return;
                }

                string path = dialog.FileName;
                try
                {
                    SaveBitmap(output, path);
                    string directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory))
                    {
                        _settings.LastSaveDirectory = directory;
                        _settings.Save();
                    }

                    if (_app != null)
                    {
                        _app.ShowToast("已保存", Path.GetFileName(path) + "\n" + directory);
                    }

                    CloseCapture();
                }
                catch (Exception ex)
                {
                    AppLog.WriteException("Save", ex);
                    ShowError("保存失败", "无法保存截图：\n\n" + ex.Message);
                }
            }
        }

        private static void SaveBitmap(Bitmap bitmap, string path)
        {
            string extension = (Path.GetExtension(path) ?? string.Empty).ToLowerInvariant();
            if (extension == ".jpg" || extension == ".jpeg")
            {
                using (Bitmap flattened = new Bitmap(bitmap.Width, bitmap.Height, PixelFormat.Format24bppRgb))
                {
                    using (Graphics g = Graphics.FromImage(flattened))
                    {
                        g.Clear(Color.White);
                        g.DrawImage(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height));
                    }

                    ImageCodecInfo codec = null;
                    ImageCodecInfo[] codecs = ImageCodecInfo.GetImageEncoders();
                    for (int i = 0; i < codecs.Length; i++)
                    {
                        if (codecs[i].FormatID == ImageFormat.Jpeg.Guid)
                        {
                            codec = codecs[i];
                            break;
                        }
                    }

                    if (codec != null)
                    {
                        using (EncoderParameters parameters = new EncoderParameters(1))
                        using (EncoderParameter quality = new EncoderParameter(Encoder.Quality, 92L))
                        {
                            parameters.Param[0] = quality;
                            flattened.Save(path, codec, parameters);
                        }
                    }
                    else
                    {
                        flattened.Save(path, ImageFormat.Jpeg);
                    }
                }
            }
            else if (extension == ".bmp")
            {
                bitmap.Save(path, ImageFormat.Bmp);
            }
            else
            {
                bitmap.Save(path, ImageFormat.Png);
            }
        }

        private void StartRecording(RecordingFormat format)
        {
            if (!SelectionReady || IsCaptureClosed)
            {
                return;
            }

            if (_selection.Width < 16 || _selection.Height < 16)
            {
                _app.ShowToast("无法录制", "录制区域至少需要 16 × 16 像素。");
                return;
            }

            _paletteOpen = false;
            _paletteCells = null;
            Rectangle captureRect = new Rectangle(
                _screenBounds.X + _selection.X,
                _screenBounds.Y + _selection.Y,
                _selection.Width,
                _selection.Height);

            _app.StartRecording(captureRect, format);
            CloseCapture();
        }

        private void StartOcr()
        {
            if (!SelectionReady)
            {
                return;
            }

            if (_ocrForm != null && !_ocrForm.IsDisposed)
            {
                _ocrForm.BringToFront();
                _ocrForm.Activate();
                return;
            }

            _paletteOpen = false;
            _paletteCells = null;

            Rectangle cropScreen = new Rectangle(
                _screenBounds.X + _selection.X,
                _screenBounds.Y + _selection.Y,
                _selection.Width,
                _selection.Height);

            Bitmap crop = new Bitmap(_selection.Width, _selection.Height, PixelFormat.Format32bppArgb);
            try
            {
                using (Graphics g = Graphics.FromImage(crop))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImage(_screen, new Rectangle(0, 0, crop.Width, crop.Height),
                        _selection, GraphicsUnit.Pixel);
                }

            }
            catch
            {
                crop.Dispose();
                throw;
            }

            OcrForm form = new OcrForm(cropScreen, _uiScale);
            _ocrForm = form;
            form.FormClosed += delegate
            {
                if (object.ReferenceEquals(_ocrForm, form))
                {
                    _ocrForm = null;
                }
            };
            form.Show(this);
            form.Activate();
            Native.SetForegroundWindow(form.Handle);

            ThreadPool.QueueUserWorkItem(delegate(object state)
            {
                OcrOutcome outcome;
                try
                {
                    outcome = WindowsOcr.Recognize(crop, CultureInfo.CurrentUICulture.Name);
                }
                catch (Exception ex)
                {
                    AppLog.WriteException("OCR worker", ex);
                    outcome = new OcrOutcome();
                    outcome.Error = "文字识别失败：\n\n" + ex.Message;
                }
                finally
                {
                    crop.Dispose();
                }

                if (form.IsDisposed)
                {
                    return;
                }

                try
                {
                    form.BeginInvoke(new Action<OcrOutcome>(form.SetOutcome), new object[] { outcome });
                }
                catch (ObjectDisposedException)
                {
                }
                catch (InvalidOperationException)
                {
                }
                catch (Exception ex)
                {
                    AppLog.WriteException("OCR callback", ex);
                }
            });
        }

        private void CloseOcrForm()
        {
            if (_ocrForm != null && !_ocrForm.IsDisposed)
            {
                try
                {
                    _ocrForm.Close();
                }
                catch
                {
                }
            }

            _ocrForm = null;
        }

        private void CloseCapture()
        {
            if (_closing)
            {
                return;
            }

            _closing = true;
            CloseOcrForm();
            Close();
        }

        private void ShowError(string title, string message)
        {
            bool oldTopMost = TopMost;
            TopMost = false;
            try
            {
                MessageBox.Show(this, message, title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                if (!_closing && !IsDisposed)
                {
                    TopMost = oldTopMost;
                    Activate();
                }
            }
        }

        private sealed class ToolbarButton
        {
            public BarAction Action;
            public RectangleF Bounds;
            public string Tooltip;
            public bool Enabled;
        }

        private sealed class PaletteCell
        {
            public RectangleF Bounds;
            public Color Color;
            public bool IsCustom;
        }
    }
}
