using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DpSnip
{
    internal static class Native
    {
        public const int WM_HOTKEY = 0x0312;
        public const int WM_APP_ACTIVATE = 0x8000 + 0x51;

        public const uint MOD_ALT = 0x0001;
        public const uint MOD_CONTROL = 0x0002;
        public const uint MOD_NOREPEAT = 0x4000;

        public const int SM_XVIRTUALSCREEN = 76;
        public const int SM_YVIRTUALSCREEN = 77;
        public const int SM_CXVIRTUALSCREEN = 78;
        public const int SM_CYVIRTUALSCREEN = 79;

        private const int CURSOR_SHOWING = 0x00000001;
        private const uint DI_NORMAL = 0x00000003;
        private const uint MONITOR_DEFAULTTONEAREST = 2;
        private const int MDT_EFFECTIVE_DPI = 0;

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;

            public POINT(int x, int y)
            {
                X = x;
                Y = y;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CURSORINFO
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public POINT ptScreenPos;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ICONINFO
        {
            [MarshalAs(UnmanagedType.Bool)]
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr dpiAwarenessContext);

        [DllImport("user32.dll")]
        public static extern bool ReleaseCapture();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetCursorInfo(ref CURSORINFO pci);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO piconinfo);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DrawIconEx(
            IntPtr hdc,
            int xLeft,
            int yTop,
            IntPtr hIcon,
            int cxWidth,
            int cyWidth,
            uint istepIfAniCur,
            IntPtr hbrFlickerFreeDraw,
            uint diFlags);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        public static Cursor CreateWhiteCrosshairCursor(int requestedSize)
        {
            int size = Math.Max(24, Math.Min(64, requestedSize));
            int thickness = size >= 44 ? 2 : 1;
            int center = size / 2;
            int lineStart = center - thickness / 2;
            int stride = ((size + 31) / 32) * 4;
            byte[] xor = new byte[stride * size];
            byte[] mask = new byte[stride * size];

            for (int i = 0; i < mask.Length; i++)
            {
                mask[i] = 0xFF;
            }

            for (int y = 0; y < size; y++)
            {
                int row = size - 1 - y;
                for (int x = 0; x < size; x++)
                {
                    bool vertical = x >= lineStart && x < lineStart + thickness &&
                                    y >= 1 && y <= size - 2;
                    bool horizontal = y >= lineStart && y < lineStart + thickness &&
                                      x >= 1 && x <= size - 2;
                    if (!vertical && !horizontal)
                    {
                        continue;
                    }

                    int offset = row * stride + (x / 8);
                    byte bit = (byte)(1 << (7 - (x % 8)));
                    // Monochrome cursor ROP: (screen AND mask) XOR xor.
                    // mask=0, xor=1 forces a pure white pixel on any background.
                    xor[offset] |= bit;
                    mask[offset] &= (byte)~bit;
                }
            }

            using (MemoryStream stream = new MemoryStream())
            using (BinaryWriter writer = new BinaryWriter(stream))
            {
                writer.Write((ushort)0);
                writer.Write((ushort)2);
                writer.Write((ushort)1);
                writer.Write((byte)size);
                writer.Write((byte)size);
                writer.Write((byte)2);
                writer.Write((byte)0);
                writer.Write((ushort)center);
                writer.Write((ushort)center);
                writer.Write((uint)(40 + 8 + xor.Length + mask.Length));
                writer.Write((uint)22);

                writer.Write((uint)40);
                writer.Write(size);
                writer.Write(size * 2);
                writer.Write((ushort)1);
                writer.Write((ushort)1);
                writer.Write((uint)0);
                writer.Write((uint)(xor.Length + mask.Length));
                writer.Write(0);
                writer.Write(0);
                writer.Write((uint)2);
                writer.Write((uint)0);
                writer.Write(new byte[] { 0, 0, 0, 0, 255, 255, 255, 0 });
                writer.Write(xor);
                writer.Write(mask);
                writer.Flush();
                stream.Position = 0;
                return new Cursor(stream);
            }
        }

        public static bool DrawCursor(Graphics graphics, int offsetX, int offsetY)
        {
            if (graphics == null)
            {
                return false;
            }

            try
            {
                CURSORINFO cursor = new CURSORINFO();
                cursor.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
                if (!GetCursorInfo(ref cursor) ||
                    (cursor.flags & CURSOR_SHOWING) == 0 ||
                    cursor.hCursor == IntPtr.Zero)
                {
                    return false;
                }

                ICONINFO info;
                if (!GetIconInfo(cursor.hCursor, out info))
                {
                    return false;
                }

                try
                {
                    int x = cursor.ptScreenPos.X - info.xHotspot - offsetX;
                    int y = cursor.ptScreenPos.Y - info.yHotspot - offsetY;
                    IntPtr hdc = graphics.GetHdc();
                    try
                    {
                        return DrawIconEx(
                            hdc, x, y, cursor.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
                    }
                    finally
                    {
                        graphics.ReleaseHdc(hdc);
                    }
                }
                finally
                {
                    if (info.hbmMask != IntPtr.Zero)
                    {
                        DeleteObject(info.hbmMask);
                    }

                    if (info.hbmColor != IntPtr.Zero)
                    {
                        DeleteObject(info.hbmColor);
                    }
                }
            }
            catch
            {
                return false;
            }
        }

        public static void TryEnablePerMonitorDpiAwareness()
        {
            // -4 = DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2
            try
            {
                if (SetProcessDpiAwarenessContext(new IntPtr(-4)))
                {
                    return;
                }
            }
            catch
            {
            }

            try
            {
                SetProcessDPIAware();
            }
            catch
            {
            }
        }

        public static Rectangle GetVirtualScreenBounds()
        {
            int x = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int height = GetSystemMetrics(SM_CYVIRTUALSCREEN);

            if (width <= 0 || height <= 0)
            {
                Rectangle fallback = SystemInformation.VirtualScreen;
                return fallback;
            }

            return new Rectangle(x, y, width, height);
        }

        public static Point GetCursorPosition()
        {
            POINT p;
            if (GetCursorPos(out p))
            {
                return new Point(p.X, p.Y);
            }

            return Cursor.Position;
        }

        public static Rectangle GetMonitorWorkArea(Point screenPoint)
        {
            try
            {
                POINT p = new POINT(screenPoint.X, screenPoint.Y);
                IntPtr monitor = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
                MONITORINFO info = new MONITORINFO();
                info.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                if (monitor != IntPtr.Zero && GetMonitorInfo(monitor, ref info))
                {
                    RECT r = info.rcWork;
                    return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                }
            }
            catch
            {
            }

            return Screen.FromPoint(screenPoint).WorkingArea;
        }

        public static float GetUiScaleForPoint(Point screenPoint)
        {
            try
            {
                POINT p = new POINT(screenPoint.X, screenPoint.Y);
                IntPtr monitor = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
                uint dx;
                uint dy;
                if (monitor != IntPtr.Zero && GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out dx, out dy) == 0 && dx > 0)
                {
                    float scale = dx / 96f;
                    if (scale < 1f)
                    {
                        return 1f;
                    }

                    if (scale > 2f)
                    {
                        return 2f;
                    }

                    return scale;
                }
            }
            catch
            {
            }

            return 1f;
        }
    }
}
