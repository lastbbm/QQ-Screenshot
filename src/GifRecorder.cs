using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DpSnip
{
    internal sealed class GifRecorder : IDisposable
    {
        private const int TransparentIndex = 255;

        private readonly int _width;
        private readonly int _height;
        private readonly int _delayCentiseconds;
        private readonly FileStream _stream;
        private readonly BinaryWriter _writer;

        private readonly byte[] _palette = new byte[256 * 3];
        private int _paletteCount;
        private short[] _colorCache;
        private int[] _previousPixels;
        private bool _headerWritten;
        private bool _firstFrame = true;
        private bool _finished;

        public GifRecorder(string path, int width, int height, int delayCentiseconds)
        {
            if (width < 1 || height < 1)
            {
                throw new ArgumentException("GIF 尺寸无效。");
            }

            _width = width;
            _height = height;
            _delayCentiseconds = Math.Max(2, delayCentiseconds);
            _stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            _writer = new BinaryWriter(_stream, Encoding.ASCII);
            byte[] header = Encoding.ASCII.GetBytes("GIF89a");
            _writer.Write(header);
        }

        public int Width
        {
            get { return _width; }
        }

        public int Height
        {
            get { return _height; }
        }

        public int FrameCount
        {
            get { return _frameCount; }
        }

        private int _frameCount;

        public void AddFrame(Bitmap frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException("frame");
            }

            if (_finished)
            {
                throw new InvalidOperationException("GIF 录制已经结束。");
            }

            if (frame.Width != _width || frame.Height != _height)
            {
                throw new ArgumentException("帧尺寸与 GIF 尺寸不一致。");
            }

            int[] pixels = ReadPixels(frame);
            if (!_headerWritten)
            {
                BuildPalette(pixels);
                WriteLogicalScreen();
                WriteLoopExtension();
                _headerWritten = true;
            }

            bool transparentFrame = !_firstFrame;
            WriteGraphicControlExtension(transparentFrame);
            WriteImageDescriptor();
            byte[] indices = BuildIndices(pixels, transparentFrame);
            WriteLzwData(indices);

            _previousPixels = pixels;
            _firstFrame = false;
            _frameCount++;
        }

        public void Finish()
        {
            if (_finished)
            {
                return;
            }

            _finished = true;
            try
            {
                if (_headerWritten)
                {
                    _writer.Write((byte)0x3B);
                }

                _writer.Flush();
            }
            finally
            {
                try
                {
                    _writer.Dispose();
                }
                catch
                {
                }

                try
                {
                    _stream.Dispose();
                }
                catch
                {
                }
            }
        }

        public void Dispose()
        {
            Finish();
        }

        private void WriteLogicalScreen()
        {
            WriteUInt16(_width);
            WriteUInt16(_height);
            _writer.Write((byte)0xF7); // global color table, 256 entries
            _writer.Write((byte)0);
            _writer.Write((byte)0);
            FillPaletteTable();
            _writer.Write(_palette);
        }

        private void WriteLoopExtension()
        {
            _writer.Write((byte)0x21);
            _writer.Write((byte)0xFF);
            _writer.Write((byte)0x0B);
            _writer.Write(Encoding.ASCII.GetBytes("NETSCAPE2.0"));
            _writer.Write((byte)0x03);
            _writer.Write((byte)0x01);
            WriteUInt16(0);
            _writer.Write((byte)0x00);
        }

        private void WriteGraphicControlExtension(bool transparent)
        {
            _writer.Write((byte)0x21);
            _writer.Write((byte)0xF9);
            _writer.Write((byte)0x04);
            int packed = (1 << 2) | (transparent ? 1 : 0); // disposal = keep previous frame
            _writer.Write((byte)packed);
            WriteUInt16(_delayCentiseconds);
            _writer.Write((byte)TransparentIndex);
            _writer.Write((byte)0x00);
        }

        private void WriteImageDescriptor()
        {
            _writer.Write((byte)0x2C);
            WriteUInt16(0);
            WriteUInt16(0);
            WriteUInt16(_width);
            WriteUInt16(_height);
            _writer.Write((byte)0x00);
        }

        private void WriteUInt16(int value)
        {
            _writer.Write((byte)(value & 0xFF));
            _writer.Write((byte)((value >> 8) & 0xFF));
        }

        private void FillPaletteTable()
        {
            for (int i = 0; i < 256; i++)
            {
                if (i < _paletteCount)
                {
                    _palette[i * 3] = _palette[i * 3];
                }
                else
                {
                    _palette[i * 3] = 0;
                    _palette[i * 3 + 1] = 0;
                    _palette[i * 3 + 2] = 0;
                }
            }

            // Index 255 is reserved for transparency.
            _palette[TransparentIndex * 3] = 0;
            _palette[TransparentIndex * 3 + 1] = 0;
            _palette[TransparentIndex * 3 + 2] = 0;
        }

        private void WriteLzwData(byte[] indices)
        {
            _writer.Write((byte)8);

            GifBitWriter bits = new GifBitWriter();
            const int clearCode = 256;
            const int endCode = 257;
            int codeSize = 9;
            int nextCode = 258;
            Dictionary<int, int> dictionary = new Dictionary<int, int>(4096);
            bits.Write(clearCode, codeSize);

            int prefix = indices[0];
            for (int i = 1; i < indices.Length; i++)
            {
                int current = indices[i];
                int key = (prefix << 8) | current;
                int code;
                if (dictionary.TryGetValue(key, out code))
                {
                    prefix = code;
                    continue;
                }

                bits.Write(prefix, codeSize);
                if (nextCode < 4096)
                {
                    dictionary[key] = nextCode++;
                    if (nextCode > (1 << codeSize) && codeSize < 12)
                    {
                        codeSize++;
                    }
                }
                else
                {
                    bits.Write(clearCode, codeSize);
                    dictionary.Clear();
                    nextCode = 258;
                    codeSize = 9;
                }

                prefix = current;
            }

            bits.Write(prefix, codeSize);
            bits.Write(endCode, codeSize);
            byte[] data = bits.Finish();

            int offset = 0;
            while (offset < data.Length)
            {
                int count = Math.Min(255, data.Length - offset);
                _writer.Write((byte)count);
                _writer.Write(data, offset, count);
                offset += count;
            }

            _writer.Write((byte)0x00);
        }

        private int[] ReadPixels(Bitmap frame)
        {
            int[] pixels = new int[_width * _height];
            BitmapData data = frame.LockBits(
                new Rectangle(0, 0, _width, _height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                if (data.Stride == _width * 4)
                {
                    Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
                }
                else
                {
                    for (int y = 0; y < _height; y++)
                    {
                        IntPtr row = new IntPtr(data.Scan0.ToInt64() + (long)y * data.Stride);
                        Marshal.Copy(row, pixels, y * _width, _width);
                    }
                }
            }
            finally
            {
                frame.UnlockBits(data);
            }

            return pixels;
        }

        private byte[] BuildIndices(int[] pixels, bool transparentFrame)
        {
            byte[] indices = new byte[pixels.Length];
            if (!transparentFrame)
            {
                for (int i = 0; i < pixels.Length; i++)
                {
                    int pixel = pixels[i];
                    indices[i] = MapColor(
                        (byte)((pixel >> 16) & 0xFF),
                        (byte)((pixel >> 8) & 0xFF),
                        (byte)(pixel & 0xFF));
                }

                return indices;
            }

            for (int i = 0; i < pixels.Length; i++)
            {
                int pixel = pixels[i];
                if ((pixel & 0x00FFFFFF) == (_previousPixels[i] & 0x00FFFFFF))
                {
                    indices[i] = TransparentIndex;
                    continue;
                }

                indices[i] = MapColor(
                    (byte)((pixel >> 16) & 0xFF),
                    (byte)((pixel >> 8) & 0xFF),
                    (byte)(pixel & 0xFF));
            }

            return indices;
        }

        private byte MapColor(byte red, byte green, byte blue)
        {
            int key = ((red >> 3) << 10) | ((green >> 3) << 5) | (blue >> 3);
            short cached = _colorCache[key];
            if (cached >= 0)
            {
                return (byte)cached;
            }

            int r = (red >> 3) << 3;
            int g = (green >> 3) << 3;
            int b = (blue >> 3) << 3;
            int best = 0;
            int bestDistance = int.MaxValue;
            for (int i = 0; i < _paletteCount; i++)
            {
                int dr = r - _palette[i * 3];
                int dg = g - _palette[i * 3 + 1];
                int db = b - _palette[i * 3 + 2];
                int distance = dr * dr + dg * dg + db * db;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                    if (distance == 0)
                    {
                        break;
                    }
                }
            }

            _colorCache[key] = (short)best;
            return (byte)best;
        }

        private void BuildPalette(int[] pixels)
        {
            int[] counts = new int[32768];
            long[] sumR = new long[32768];
            long[] sumG = new long[32768];
            long[] sumB = new long[32768];

            int step = pixels.Length > 1200000 ? 2 : 1;
            for (int i = 0; i < pixels.Length; i += step)
            {
                int pixel = pixels[i];
                int r = (pixel >> 16) & 0xFF;
                int g = (pixel >> 8) & 0xFF;
                int b = pixel & 0xFF;
                int key = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
                counts[key]++;
                sumR[key] += r;
                sumG[key] += g;
                sumB[key] += b;
            }

            List<ColorEntry> entries = new List<ColorEntry>();
            for (int key = 0; key < counts.Length; key++)
            {
                if (counts[key] == 0)
                {
                    continue;
                }

                ColorEntry entry = new ColorEntry();
                entry.R = (byte)(sumR[key] / counts[key]);
                entry.G = (byte)(sumG[key] / counts[key]);
                entry.B = (byte)(sumB[key] / counts[key]);
                entry.Count = counts[key];
                entries.Add(entry);
            }

            if (entries.Count == 0)
            {
                entries.Add(MakeColorEntry(0, 0, 0, 1));
            }

            ColorEntry[] colors = entries.ToArray();
            List<ColorBox> boxes = new List<ColorBox>();
            boxes.Add(CreateBox(colors, 0, colors.Length));

            while (boxes.Count < 255)
            {
                int bestIndex = -1;
                long bestScore = -1;
                for (int i = 0; i < boxes.Count; i++)
                {
                    ColorBox candidate = boxes[i];
                    if (candidate.Length <= 1 || candidate.MaxRange <= 0)
                    {
                        continue;
                    }

                    long score = candidate.Population * (long)(candidate.MaxRange + 1);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestIndex = i;
                    }
                }

                if (bestIndex < 0)
                {
                    break;
                }

                ColorBox box = boxes[bestIndex];
                int channel = box.MaxChannel;
                Array.Sort(colors, box.Start, box.Length, new ColorEntryComparer(channel));

                long half = box.Population / 2;
                long accumulated = 0;
                int cut = box.Start + 1;
                for (int i = box.Start; i < box.Start + box.Length - 1; i++)
                {
                    accumulated += colors[i].Count;
                    if (accumulated >= half)
                    {
                        cut = i + 1;
                        break;
                    }
                }

                if (cut <= box.Start)
                {
                    cut = box.Start + 1;
                }

                if (cut >= box.Start + box.Length)
                {
                    cut = box.Start + box.Length - 1;
                }

                int leftLength = cut - box.Start;
                int rightLength = box.Length - leftLength;
                boxes.RemoveAt(bestIndex);
                boxes.Add(CreateBox(colors, box.Start, leftLength));
                boxes.Add(CreateBox(colors, cut, rightLength));
            }

            _paletteCount = Math.Min(255, boxes.Count);
            for (int i = 0; i < _paletteCount; i++)
            {
                ColorBox box = boxes[i];
                _palette[i * 3] = (byte)(box.SumR / box.Population);
                _palette[i * 3 + 1] = (byte)(box.SumG / box.Population);
                _palette[i * 3 + 2] = (byte)(box.SumB / box.Population);
            }

            if (_paletteCount == 0)
            {
                _palette[0] = 0;
                _palette[1] = 0;
                _palette[2] = 0;
                _paletteCount = 1;
            }

            _colorCache = new short[32768];
            for (int i = 0; i < _colorCache.Length; i++)
            {
                _colorCache[i] = -1;
            }
        }

        private static ColorEntry MakeColorEntry(byte r, byte g, byte b, int count)
        {
            ColorEntry entry = new ColorEntry();
            entry.R = r;
            entry.G = g;
            entry.B = b;
            entry.Count = count;
            return entry;
        }

        private static ColorBox CreateBox(ColorEntry[] colors, int start, int length)
        {
            ColorBox box = new ColorBox();
            box.Start = start;
            box.Length = length;
            box.MinR = 255;
            box.MinG = 255;
            box.MinB = 255;
            for (int i = start; i < start + length; i++)
            {
                ColorEntry entry = colors[i];
                box.Population += entry.Count;
                box.SumR += (long)entry.R * entry.Count;
                box.SumG += (long)entry.G * entry.Count;
                box.SumB += (long)entry.B * entry.Count;
                if (entry.R < box.MinR) box.MinR = entry.R;
                if (entry.G < box.MinG) box.MinG = entry.G;
                if (entry.B < box.MinB) box.MinB = entry.B;
                if (entry.R > box.MaxR) box.MaxR = entry.R;
                if (entry.G > box.MaxG) box.MaxG = entry.G;
                if (entry.B > box.MaxB) box.MaxB = entry.B;
            }

            return box;
        }

        private struct ColorEntry
        {
            public byte R;
            public byte G;
            public byte B;
            public int Count;
        }

        private sealed class ColorBox
        {
            public int Start;
            public int Length;
            public long Population;
            public long SumR;
            public long SumG;
            public long SumB;
            public int MinR = 255;
            public int MinG = 255;
            public int MinB = 255;
            public int MaxR;
            public int MaxG;
            public int MaxB;

            public int MaxRange
            {
                get
                {
                    int rr = MaxR - MinR;
                    int gr = MaxG - MinG;
                    int br = MaxB - MinB;
                    return Math.Max(rr, Math.Max(gr, br));
                }
            }

            public int MaxChannel
            {
                get
                {
                    int rr = MaxR - MinR;
                    int gr = MaxG - MinG;
                    int br = MaxB - MinB;
                    if (rr >= gr && rr >= br) return 0;
                    if (gr >= br) return 1;
                    return 2;
                }
            }
        }

        private sealed class ColorEntryComparer : IComparer<ColorEntry>
        {
            private readonly int _channel;

            public ColorEntryComparer(int channel)
            {
                _channel = channel;
            }

            public int Compare(ColorEntry a, ColorEntry b)
            {
                if (_channel == 0)
                {
                    return a.R.CompareTo(b.R);
                }

                if (_channel == 1)
                {
                    return a.G.CompareTo(b.G);
                }

                return a.B.CompareTo(b.B);
            }
        }

        private sealed class GifBitWriter
        {
            private readonly MemoryStream _data = new MemoryStream();
            private int _bitBuffer;
            private int _bitCount;

            public void Write(int code, int bitCount)
            {
                _bitBuffer |= (code & ((1 << bitCount) - 1)) << _bitCount;
                _bitCount += bitCount;
                while (_bitCount >= 8)
                {
                    _data.WriteByte((byte)(_bitBuffer & 0xFF));
                    _bitBuffer >>= 8;
                    _bitCount -= 8;
                }
            }

            public byte[] Finish()
            {
                if (_bitCount > 0)
                {
                    _data.WriteByte((byte)(_bitBuffer & 0xFF));
                    _bitBuffer = 0;
                    _bitCount = 0;
                }

                return _data.ToArray();
            }
        }
    }
}
