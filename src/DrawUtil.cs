using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace DpSnip
{
    internal static class AppPalette
    {
        public static readonly Color Accent = Color.FromArgb(45, 127, 249);
        public static readonly Color AccentHover = Color.FromArgb(72, 147, 255);
        public static readonly Color Toolbar = Color.FromArgb(34, 38, 47);
        public static readonly Color ToolbarHover = Color.FromArgb(22, 255, 255, 255);
        public static readonly Color ToolbarBorder = Color.FromArgb(45, 255, 255, 255);
        public static readonly Color Text = Color.FromArgb(238, 241, 247);
        public static readonly Color MutedText = Color.FromArgb(158, 166, 181);
        public static readonly Color Danger = Color.FromArgb(224, 77, 84);
        public static readonly Color Surface = Color.FromArgb(250, 251, 253);
        public static readonly Color CardBorder = Color.FromArgb(223, 228, 237);
        public static readonly Color Ink = Color.FromArgb(32, 38, 51);
        public static readonly Color SubInk = Color.FromArgb(113, 122, 139);
    }

    internal static class DrawUtil
    {
        public static GraphicsPath RoundedRect(RectangleF rect, float radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (radius <= 0.1f)
            {
                path.AddRectangle(rect);
                path.CloseFigure();
                return path;
            }

            float diameter = radius * 2f;
            if (diameter > rect.Width)
            {
                diameter = rect.Width;
            }

            if (diameter > rect.Height)
            {
                diameter = rect.Height;
            }

            RectangleF arc = new RectangleF(rect.X, rect.Y, diameter, diameter);
            path.AddArc(arc, 180f, 90f);
            arc.X = rect.Right - diameter;
            path.AddArc(arc, 270f, 90f);
            arc.Y = rect.Bottom - diameter;
            path.AddArc(arc, 0f, 90f);
            arc.X = rect.Left;
            path.AddArc(arc, 90f, 90f);
            path.CloseFigure();
            return path;
        }

        public static void FillRounded(Graphics g, Brush brush, RectangleF rect, float radius)
        {
            using (GraphicsPath path = RoundedRect(rect, radius))
            {
                g.FillPath(brush, path);
            }
        }

        public static void DrawRounded(Graphics g, Pen pen, RectangleF rect, float radius)
        {
            using (GraphicsPath path = RoundedRect(rect, radius))
            {
                g.DrawPath(pen, path);
            }
        }

        public static void FillRounded(Graphics g, Color color, RectangleF rect, float radius)
        {
            using (SolidBrush b = new SolidBrush(color))
            {
                FillRounded(g, b, rect, radius);
            }
        }

        public static void DrawRounded(Graphics g, Color color, float width, RectangleF rect, float radius)
        {
            using (Pen p = new Pen(color, width))
            {
                p.Alignment = PenAlignment.Center;
                DrawRounded(g, p, rect, radius);
            }
        }

        public static Font CreateFont(float pixelSize, FontStyle style)
        {
            if (pixelSize < 1f)
            {
                pixelSize = 1f;
            }

            string[] families = new string[] { "Microsoft YaHei UI", "Microsoft YaHei", "Segoe UI", "Tahoma" };
            for (int i = 0; i < families.Length; i++)
            {
                try
                {
                    Font f = new Font(families[i], pixelSize, style, GraphicsUnit.Pixel);
                    if (string.Equals(f.Name, families[i], StringComparison.OrdinalIgnoreCase))
                    {
                        return f;
                    }

                    f.Dispose();
                }
                catch
                {
                }
            }

            return new Font(FontFamily.GenericSansSerif, pixelSize, style, GraphicsUnit.Pixel);
        }

        public static void DrawText(Graphics g, string text, Font font, Color color, RectangleF rect,
            StringAlignment horizontal, StringAlignment vertical, bool noWrap)
        {
            using (SolidBrush brush = new SolidBrush(color))
            using (StringFormat format = new StringFormat(StringFormat.GenericTypographic))
            {
                format.Alignment = horizontal;
                format.LineAlignment = vertical;
                format.Trimming = StringTrimming.EllipsisCharacter;
                if (noWrap)
                {
                    format.FormatFlags |= StringFormatFlags.NoWrap;
                }

                format.FormatFlags |= StringFormatFlags.NoClip;
                g.DrawString(text, font, brush, rect, format);
            }
        }

        public static Color Blend(Color a, Color b, float amount)
        {
            if (amount < 0f) amount = 0f;
            if (amount > 1f) amount = 1f;
            return Color.FromArgb(
                (int)(a.A + (b.A - a.A) * amount),
                (int)(a.R + (b.R - a.R) * amount),
                (int)(a.G + (b.G - a.G) * amount),
                (int)(a.B + (b.B - a.B) * amount));
        }

        public static Color WithAlpha(Color color, int alpha)
        {
            if (alpha < 0) alpha = 0;
            if (alpha > 255) alpha = 255;
            return Color.FromArgb(alpha, color.R, color.G, color.B);
        }

        public static Color ColorFromHex(string hex)
        {
            try
            {
                if (!string.IsNullOrEmpty(hex))
                {
                    string clean = hex.Trim().TrimStart('#');
                    if (clean.Length == 6)
                    {
                        return Color.FromArgb(
                            255,
                            Convert.ToInt32(clean.Substring(0, 2), 16),
                            Convert.ToInt32(clean.Substring(2, 2), 16),
                            Convert.ToInt32(clean.Substring(4, 2), 16));
                    }

                    if (clean.Length == 8)
                    {
                        return Color.FromArgb(
                            Convert.ToInt32(clean.Substring(0, 2), 16),
                            Convert.ToInt32(clean.Substring(2, 2), 16),
                            Convert.ToInt32(clean.Substring(4, 2), 16),
                            Convert.ToInt32(clean.Substring(6, 2), 16));
                    }
                }
            }
            catch
            {
            }

            return Color.Empty;
        }

        public static string ToHex(Color color)
        {
            return string.Format("{0:X2}{1:X2}{2:X2}", color.R, color.G, color.B);
        }

        public static Region RoundedRegion(Rectangle bounds, int radius)
        {
            using (GraphicsPath path = RoundedRect(bounds, radius))
            {
                return new Region(path);
            }
        }

        public static void DrawCheckMark(Graphics g, Pen pen, RectangleF rect)
        {
            float x = rect.X;
            float y = rect.Y;
            float w = rect.Width;
            float h = rect.Height;
            g.DrawLines(pen, new PointF[]
            {
                new PointF(x + w * 0.18f, y + h * 0.53f),
                new PointF(x + w * 0.42f, y + h * 0.76f),
                new PointF(x + w * 0.84f, y + h * 0.25f)
            });
        }
    }

    internal static class AppIcon
    {
        public static Icon CreateTrayIcon()
        {
            using (Bitmap bitmap = Render(64))
            {
                IntPtr hIcon = bitmap.GetHicon();
                try
                {
                    using (Icon temporary = Icon.FromHandle(hIcon))
                    {
                        return (Icon)temporary.Clone();
                    }
                }
                finally
                {
                    Native.DestroyIcon(hIcon);
                }
            }
        }

        public static Bitmap Render(int size)
        {
            if (size < 8) size = 8;
            Bitmap bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(bitmap))
            using (LinearGradientBrush gradient = new LinearGradientBrush(
                new Rectangle(0, 0, size, size),
                Color.FromArgb(83, 151, 255),
                Color.FromArgb(33, 91, 224),
                52f))
            using (Pen bracket = new Pen(Color.FromArgb(250, 252, 255), Math.Max(1.1f, size * 0.075f)))
            using (SolidBrush dot = new SolidBrush(Color.FromArgb(255, 108, 112)))
            using (Pen gloss = new Pen(Color.FromArgb(50, 255, 255, 255), Math.Max(1f, size * 0.035f)))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.Clear(Color.Transparent);

                float inset = Math.Max(1f, size * 0.025f);
                RectangleF tile = new RectangleF(inset, inset, size - inset * 2f, size - inset * 2f);
                float radius = size * 0.225f;
                using (GraphicsPath path = DrawUtil.RoundedRect(tile, radius))
                {
                    g.FillPath(gradient, path);
                    using (Pen edge = new Pen(Color.FromArgb(70, 255, 255, 255), Math.Max(0.8f, size * 0.022f)))
                    {
                        g.DrawPath(edge, path);
                    }
                }

                bracket.StartCap = LineCap.Round;
                bracket.EndCap = LineCap.Round;
                bracket.LineJoin = LineJoin.Round;

                float left = size * 0.255f;
                float top = size * 0.265f;
                float right = size * 0.745f;
                float bottom = size * 0.735f;
                float arm = size * 0.135f;

                g.DrawLines(bracket, new PointF[]
                {
                    new PointF(left, top + arm), new PointF(left, top), new PointF(left + arm, top)
                });
                g.DrawLines(bracket, new PointF[]
                {
                    new PointF(right - arm, top), new PointF(right, top), new PointF(right, top + arm)
                });
                g.DrawLines(bracket, new PointF[]
                {
                    new PointF(left, bottom - arm), new PointF(left, bottom), new PointF(left + arm, bottom)
                });
                g.DrawLines(bracket, new PointF[]
                {
                    new PointF(right - arm, bottom), new PointF(right, bottom), new PointF(right, bottom - arm)
                });

                float dotSize = size * 0.195f;
                g.FillEllipse(dot, size / 2f - dotSize / 2f, size / 2f - dotSize / 2f, dotSize, dotSize);

                using (Pen dotRing = new Pen(Color.FromArgb(90, 255, 255, 255), Math.Max(0.8f, size * 0.018f)))
                {
                    float ring = dotSize + size * 0.07f;
                    g.DrawEllipse(dotRing, size / 2f - ring / 2f, size / 2f - ring / 2f, ring, ring);
                }

                g.DrawArc(gloss, inset + size * 0.09f, inset + size * 0.07f,
                    size * 0.8f, size * 0.8f, 198f, 144f);
            }

            return bitmap;
        }
    }
}
