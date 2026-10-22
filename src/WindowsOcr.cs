using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using Windows.Foundation;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace DpSnip
{
    internal sealed class OcrOutcome
    {
        public bool Success;
        public string Text;
        public string Error;
        public string LanguageName;
        public int LineCount;
        public int CharCount;
        public bool SuggestEnglishOcr;

        public bool HasText
        {
            get { return Success && !string.IsNullOrEmpty(Text); }
        }
    }

    internal static class WindowsOcr
    {
        private const int TargetMaxPixels = 8000000;

        private static readonly Regex CjkSpace = new Regex(
            @"(?<=[\u3400-\u9FFF\uF900-\uFAFF])\s+(?=[\u3400-\u9FFF\uF900-\uFAFF])",
            RegexOptions.Compiled);

        private static readonly Regex FullWidthPunctuationSpace = new Regex(
            @"\s*([，。！？：；、（）《》〈〉【】「」『』“”‘’…—])\s*",
            RegexOptions.Compiled);

        private static readonly Regex MultipleAsciiSpaces = new Regex(@"[ \t]{2,}", RegexOptions.Compiled);

        private static readonly Regex NumberPunctuationSpace = new Regex(
            @"(?<=\d)\s*([.,])\s*(?=\d)", RegexOptions.Compiled);

        private static readonly Regex SpaceBeforePercent = new Regex(@"\s+(?=%)", RegexOptions.Compiled);

        private static readonly Regex SpaceAfterSign = new Regex(@"(?<=[+\-])\s+(?=\d)", RegexOptions.Compiled);

        private static readonly Regex SpaceBeforeSign = new Regex(@"(?<=\d)\s+(?=[+\-])", RegexOptions.Compiled);

        private enum ImagePass
        {
            Color,
            Contrast,
            Binary
        }

        private sealed class EngineInfo
        {
            public OcrEngine Engine;
            public string Name;
            public string Tag;
        }

        private sealed class Candidate
        {
            public string Text;
            public int LineCount;
            public int CharCount;
            public int Score;
            public string LanguageName;
        }

        private sealed class LineBlock
        {
            public string Text;
            public double Left;
            public double Top;
            public double Right;
            public double Bottom;
        }

        private sealed class PreparedImage : IDisposable
        {
            public readonly Bitmap Bitmap;

            public PreparedImage(Bitmap bitmap)
            {
                Bitmap = bitmap;
            }

            public void Dispose()
            {
                if (Bitmap != null)
                {
                    Bitmap.Dispose();
                }
            }
        }

        public static OcrOutcome Recognize(Bitmap source, string preferredCulture)
        {
            OcrOutcome outcome = new OcrOutcome();
            if (source == null || source.Width < 2 || source.Height < 2)
            {
                outcome.Error = "选区太小，无法识别文字。";
                return outcome;
            }

            try
            {
                bool englishAvailable;
                List<EngineInfo> engines = CreateEngines(preferredCulture, out englishAvailable);
                if (engines.Count == 0)
                {
                    outcome.Error = "系统没有安装可用的 OCR 语言包。\n\n" +
                        "请打开“设置 → 时间和语言 → 语言”，添加中文（简体）或英语语言包，" +
                        "并在语言选项中安装“光学字符识别”功能后重试。";
                    return outcome;
                }

                Candidate best = null;
                using (PreparedImage primary = PrepareImage(source, ImagePass.Color))
                {
                    best = RunEngines(primary.Bitmap, engines);
                }

                // Small or low-contrast text sometimes needs a second, enhanced pass.
                if (best == null || best.Score < 6)
                {
                    using (PreparedImage contrast = PrepareImage(source, ImagePass.Contrast))
                    {
                        Candidate fallback = RunEngines(contrast.Bitmap, engines);
                        if (IsBetter(fallback, best))
                        {
                            best = fallback;
                        }
                    }

                    if (best == null || best.Score < 6)
                    {
                        using (PreparedImage binary = PrepareImage(source, ImagePass.Binary))
                        {
                            Candidate fallback = RunEngines(binary.Bitmap, engines);
                            if (IsBetter(fallback, best))
                            {
                                best = fallback;
                            }
                        }
                    }
                }

                if (best == null || string.IsNullOrEmpty(best.Text))
                {
                    outcome.Error = "没有识别到清晰的文字。\n\n" +
                        "已自动放大图像、增加边距并增强对比度。请尽量框选完整文字行，并在四周留一点背景。" +
                        (!englishAvailable
                            ? "\n\n当前系统只检测到中文 OCR。若截图以英文或代码为主，建议在 Windows " +
                              "“设置 → 时间和语言 → 语言”中给英语安装“光学字符识别”组件，英文识别会明显更准。"
                            : string.Empty);
                    return outcome;
                }

                outcome.Text = best.Text;
                outcome.LineCount = best.LineCount;
                outcome.CharCount = best.CharCount;
                outcome.LanguageName = best.LanguageName;
                outcome.SuggestEnglishOcr = !englishAvailable && ContainsLatin(best.Text);
                outcome.Success = true;
                return outcome;
            }
            catch (Exception ex)
            {
                AppLog.WriteException("OCR", ex);
                outcome.Success = false;
                outcome.Error = FriendlyError(ex);
                return outcome;
            }
        }

        private static bool IsBetter(Candidate candidate, Candidate current)
        {
            if (candidate == null || string.IsNullOrEmpty(candidate.Text))
            {
                return false;
            }

            if (current == null || string.IsNullOrEmpty(current.Text))
            {
                return true;
            }

            if (candidate.Score != current.Score)
            {
                return candidate.Score > current.Score;
            }

            return candidate.CharCount > current.CharCount;
        }

        private static List<EngineInfo> CreateEngines(string preferredCulture, out bool englishAvailable)
        {
            List<EngineInfo> result = new List<EngineInfo>();
            List<Language> available = new List<Language>();
            englishAvailable = false;
            try
            {
                foreach (Language language in OcrEngine.AvailableRecognizerLanguages)
                {
                    available.Add(language);
                    string tag = (language.LanguageTag ?? string.Empty).ToLowerInvariant();
                    if (tag.StartsWith("en", StringComparison.Ordinal))
                    {
                        englishAvailable = true;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.WriteException("OCR.AvailableLanguages", ex);
            }

            if (available.Count > 0)
            {
                List<Language> ordered = new List<Language>();
                string preferred = (preferredCulture ?? string.Empty).ToLowerInvariant();

                if (!string.IsNullOrEmpty(preferred))
                {
                    for (int i = 0; i < available.Count; i++)
                    {
                        string tag = (available[i].LanguageTag ?? string.Empty).ToLowerInvariant();
                        if (tag == preferred || tag.StartsWith(preferred, StringComparison.Ordinal) ||
                            preferred.StartsWith(tag, StringComparison.Ordinal))
                        {
                            AddLanguage(ordered, available[i]);
                        }
                    }
                }

                string[] prefixes = new string[] { "zh", "en" };
                for (int pass = 0; pass < prefixes.Length; pass++)
                {
                    for (int i = 0; i < available.Count; i++)
                    {
                        string tag = (available[i].LanguageTag ?? string.Empty).ToLowerInvariant();
                        if (tag.StartsWith(prefixes[pass], StringComparison.Ordinal))
                        {
                            AddLanguage(ordered, available[i]);
                        }
                    }
                }

                for (int i = 0; i < available.Count; i++)
                {
                    AddLanguage(ordered, available[i]);
                }

                int limit = Math.Min(4, ordered.Count);
                for (int i = 0; i < limit; i++)
                {
                    try
                    {
                        OcrEngine engine = OcrEngine.TryCreateFromLanguage(ordered[i]);
                        if (engine == null)
                        {
                            continue;
                        }

                        EngineInfo info = new EngineInfo();
                        info.Engine = engine;
                        info.Tag = ordered[i].LanguageTag;
                        info.Name = string.IsNullOrEmpty(ordered[i].DisplayName)
                            ? ordered[i].LanguageTag
                            : ordered[i].DisplayName;
                        result.Add(info);
                    }
                    catch
                    {
                    }
                }
            }

            if (result.Count == 0)
            {
                try
                {
                    OcrEngine profileEngine = OcrEngine.TryCreateFromUserProfileLanguages();
                    if (profileEngine != null)
                    {
                        EngineInfo info = new EngineInfo();
                        info.Engine = profileEngine;
                        info.Name = profileEngine.RecognizerLanguage != null
                            ? profileEngine.RecognizerLanguage.DisplayName
                            : "Windows OCR";
                        info.Tag = profileEngine.RecognizerLanguage != null
                            ? profileEngine.RecognizerLanguage.LanguageTag
                            : string.Empty;
                        result.Add(info);
                    }
                }
                catch
                {
                }
            }

            return result;
        }

        private static void AddLanguage(List<Language> list, Language language)
        {
            string tag = language.LanguageTag ?? string.Empty;
            for (int i = 0; i < list.Count; i++)
            {
                if (string.Equals(list[i].LanguageTag, tag, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            list.Add(language);
        }

        private static Candidate RunEngines(Bitmap bitmap, List<EngineInfo> engines)
        {
            SoftwareBitmap softwareBitmap = null;
            try
            {
                softwareBitmap = CreateSoftwareBitmap(bitmap);
                Candidate best = null;
                for (int i = 0; i < engines.Count; i++)
                {
                    OcrResult result;
                    try
                    {
                        result = WaitFor(
                            engines[i].Engine.RecognizeAsync(softwareBitmap), 45000, "文字识别");
                    }
                    catch (TimeoutException)
                    {
                        continue;
                    }
                    catch (Exception ex)
                    {
                        AppLog.WriteException("OCR engine " + engines[i].Tag, ex);
                        continue;
                    }

                    int lineCount;
                    string text = NormalizeLines(result, out lineCount);
                    if (string.IsNullOrEmpty(text))
                    {
                        continue;
                    }

                    Candidate candidate = new Candidate();
                    candidate.Text = text;
                    candidate.LineCount = lineCount;
                    candidate.CharCount = text.Replace("\r", "").Replace("\n", "").Length;
                    candidate.Score = ScoreText(text);
                    candidate.LanguageName = engines[i].Name;
                    if (IsBetter(candidate, best))
                    {
                        best = candidate;
                    }
                }

                return best;
            }
            finally
            {
                CloseWinRt(softwareBitmap);
            }
        }

        private static int ScoreText(string text)
        {
            int score = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsWhiteSpace(c))
                {
                    continue;
                }

                if (IsCjk(c))
                {
                    score += 3;
                }
                else if (char.IsLetterOrDigit(c))
                {
                    score += 2;
                }
                else
                {
                    score += 1;
                }
            }

            return score;
        }

        private static bool IsCjk(char c)
        {
            return (c >= '\u3400' && c <= '\u9FFF') ||
                   (c >= '\uF900' && c <= '\uFAFF');
        }

        private static PreparedImage PrepareImage(Bitmap source, ImagePass pass)
        {
            int maxDimension = 10000;
            try
            {
                uint value = OcrEngine.MaxImageDimension;
                if (value > 0 && value < 200000)
                {
                    maxDimension = (int)value;
                }
            }
            catch
            {
            }

            float scale = ChooseScale(source.Width, source.Height);
            while (scale > 1f &&
                   (double)(source.Width * scale) * (source.Height * scale) > TargetMaxPixels)
            {
                scale -= 0.5f;
            }

            int pad = scale >= 4f ? 32 : (scale >= 2f ? 20 : 12);
            int maxContent = Math.Max(16, maxDimension - pad * 2);
            if (source.Width * scale > maxContent || source.Height * scale > maxContent)
            {
                float fit = Math.Min(
                    (float)maxContent / source.Width,
                    (float)maxContent / source.Height);
                if (fit < scale)
                {
                    scale = fit;
                }
            }

            if (scale < 0.05f)
            {
                scale = 0.05f;
            }

            int scaledWidth = Math.Max(2, (int)Math.Round(source.Width * scale));
            int scaledHeight = Math.Max(2, (int)Math.Round(source.Height * scale));
            pad = Math.Min(pad, Math.Max(4, (maxDimension - Math.Max(scaledWidth, scaledHeight)) / 2));

            Bitmap content = new Bitmap(scaledWidth, scaledHeight, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(content))
            {
                g.Clear(Color.White);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.DrawImage(source, new Rectangle(0, 0, scaledWidth, scaledHeight));
            }

            Bitmap processed = content;
            if (pass != ImagePass.Color)
            {
                processed = Enhance(content, pass);
            }

            Bitmap output = new Bitmap(
                processed.Width + pad * 2,
                processed.Height + pad * 2,
                PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(output))
            {
                g.Clear(Color.White);
                g.DrawImageUnscaled(processed, pad, pad);
            }

            if (!object.ReferenceEquals(processed, content))
            {
                processed.Dispose();
            }

            content.Dispose();
            return new PreparedImage(output);
        }

        private static float ChooseScale(int width, int height)
        {
            int maxSide = Math.Max(width, height);
            if (height <= 80 && width <= 1200)
            {
                return 8f;
            }

            if (maxSide <= 700)
            {
                return 4f;
            }

            if (maxSide <= 1500)
            {
                return 2f;
            }

            return 1f;
        }

        private static Bitmap Enhance(Bitmap source, ImagePass pass)
        {
            int width = source.Width;
            int height = source.Height;
            BitmapData sourceData = null;
            byte[] sourceBytes = null;
            try
            {
                sourceData = source.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppArgb);
                sourceBytes = new byte[sourceData.Stride * height];
                Marshal.Copy(sourceData.Scan0, sourceBytes, 0, sourceBytes.Length);
            }
            finally
            {
                if (sourceData != null)
                {
                    source.UnlockBits(sourceData);
                }
            }

            byte[] gray = new byte[width * height];
            int[] histogram = new int[256];
            long borderSum = 0;
            int borderCount = 0;
            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * (sourceBytes.Length / height);
                for (int x = 0; x < width; x++)
                {
                    int offset = rowOffset + x * 4;
                    int b = sourceBytes[offset];
                    int g = sourceBytes[offset + 1];
                    int r = sourceBytes[offset + 2];
                    int value = (r * 299 + g * 587 + b * 114) / 1000;
                    gray[y * width + x] = (byte)value;
                    histogram[value]++;
                    if (x < 2 || y < 2 || x >= width - 2 || y >= height - 2)
                    {
                        borderSum += value;
                        borderCount++;
                    }
                }
            }

            bool darkBackground = borderCount > 0 && borderSum < 115L * borderCount;
            int threshold = OtsuThreshold(histogram, width * height);
            int low;
            int high;
            FindPercentiles(histogram, width * height, out low, out high);
            if (high - low < 32)
            {
                low = 0;
                high = 255;
            }

            Bitmap output = new Bitmap(width, height, PixelFormat.Format24bppRgb);
            BitmapData outputData = null;
            try
            {
                outputData = output.LockBits(
                    new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format24bppRgb);
                byte[] row = new byte[outputData.Stride];
                for (int y = 0; y < height; y++)
                {
                    Array.Clear(row, 0, row.Length);
                    for (int x = 0; x < width; x++)
                    {
                        int value = gray[y * width + x];
                        if (darkBackground)
                        {
                            value = 255 - value;
                        }

                        int finalValue;
                        if (pass == ImagePass.Binary)
                        {
                            int limit = darkBackground ? 255 - threshold : threshold;
                            finalValue = value >= limit ? 255 : 0;
                        }
                        else
                        {
                            int sourceLow = darkBackground ? 255 - high : low;
                            int sourceHigh = darkBackground ? 255 - low : high;
                            if (sourceHigh <= sourceLow)
                            {
                                finalValue = value;
                            }
                            else
                            {
                                finalValue = (value - sourceLow) * 255 / (sourceHigh - sourceLow);
                                finalValue = Math.Max(0, Math.Min(255, finalValue));
                            }
                        }

                        int offset = x * 3;
                        row[offset] = (byte)finalValue;
                        row[offset + 1] = (byte)finalValue;
                        row[offset + 2] = (byte)finalValue;
                    }

                    Marshal.Copy(row, 0, IntPtr.Add(outputData.Scan0, y * outputData.Stride), row.Length);
                }
            }
            finally
            {
                if (outputData != null)
                {
                    output.UnlockBits(outputData);
                }
            }

            return output;
        }

        private static int OtsuThreshold(int[] histogram, int total)
        {
            double sum = 0;
            for (int i = 0; i < histogram.Length; i++)
            {
                sum += (double)i * histogram[i];
            }

            double sumBackground = 0;
            int background = 0;
            double best = -1;
            int threshold = 127;
            for (int i = 0; i < histogram.Length; i++)
            {
                background += histogram[i];
                if (background == 0)
                {
                    continue;
                }

                int foreground = total - background;
                if (foreground <= 0)
                {
                    break;
                }

                sumBackground += (double)i * histogram[i];
                double meanBackground = sumBackground / background;
                double meanForeground = (sum - sumBackground) / foreground;
                double difference = meanBackground - meanForeground;
                double between = (double)background * foreground * difference * difference;
                if (between > best)
                {
                    best = between;
                    threshold = i;
                }
            }

            return threshold;
        }

        private static void FindPercentiles(int[] histogram, int total, out int low, out int high)
        {
            int lowTarget = Math.Max(1, total / 50);
            int highTarget = Math.Max(1, total - total / 50);
            int accumulated = 0;
            low = 0;
            high = 255;
            for (int i = 0; i < histogram.Length; i++)
            {
                accumulated += histogram[i];
                if (accumulated >= lowTarget)
                {
                    low = i;
                    break;
                }
            }

            accumulated = 0;
            for (int i = 0; i < histogram.Length; i++)
            {
                accumulated += histogram[i];
                if (accumulated >= highTarget)
                {
                    high = i;
                    break;
                }
            }
        }

        private static SoftwareBitmap CreateSoftwareBitmap(Bitmap bitmap)
        {
            byte[] pngBytes = EncodePng(bitmap);
            InMemoryRandomAccessStream stream = null;
            DataWriter writer = null;
            BitmapDecoder decoder = null;
            try
            {
                stream = new InMemoryRandomAccessStream();
                writer = new DataWriter(stream);
                writer.WriteBytes(pngBytes);
                DataWriterStoreOperation storeOperation = writer.StoreAsync();
                WaitStore(storeOperation);
                storeOperation.GetResults();
                try
                {
                    writer.DetachStream();
                }
                catch
                {
                }

                stream.Seek(0);
                decoder = WaitFor(BitmapDecoder.CreateAsync(stream), 20000, "读取截图");
                SoftwareBitmap softwareBitmap = WaitFor(
                    decoder.GetSoftwareBitmapAsync(), 20000, "准备图像");
                return softwareBitmap;
            }
            finally
            {
                CloseWinRt(decoder);
                CloseWinRt(writer);
                CloseWinRt(stream);
            }
        }

        private static byte[] EncodePng(Bitmap bitmap)
        {
            using (MemoryStream stream = new MemoryStream())
            {
                bitmap.Save(stream, ImageFormat.Png);
                return stream.ToArray();
            }
        }

        private static void WaitStore(DataWriterStoreOperation operation)
        {
            long waited = 0;
            while (operation.Status == AsyncStatus.Started)
            {
                if (waited > 20000)
                {
                    throw new TimeoutException("写入识别图像超时。");
                }

                Thread.Sleep(10);
                waited += 10;
            }

            if (operation.Status == AsyncStatus.Error)
            {
                Exception error = operation.ErrorCode;
                throw new InvalidOperationException(
                    "写入识别图像失败。" + (error == null ? string.Empty : " " + error.Message), error);
            }

            if (operation.Status == AsyncStatus.Canceled)
            {
                throw new OperationCanceledException("写入识别图像已取消。");
            }
        }

        private static T WaitFor<T>(IAsyncOperation<T> operation, int timeoutMilliseconds, string action)
        {
            long waited = 0;
            while (operation.Status == AsyncStatus.Started)
            {
                if (waited > timeoutMilliseconds)
                {
                    throw new TimeoutException(action + "超时，请尝试更小的截图区域。");
                }

                Thread.Sleep(10);
                waited += 10;
            }

            if (operation.Status == AsyncStatus.Error)
            {
                Exception error = operation.ErrorCode;
                throw new InvalidOperationException(
                    action + "失败。" + (error == null ? string.Empty : " " + error.Message), error);
            }

            if (operation.Status == AsyncStatus.Canceled)
            {
                throw new OperationCanceledException(action + "已取消。");
            }

            return operation.GetResults();
        }

        private static string NormalizeLines(OcrResult result, out int lineCount)
        {
            lineCount = 0;
            if (result == null)
            {
                return string.Empty;
            }

            List<LineBlock> blocks = new List<LineBlock>();
            try
            {
                foreach (OcrLine line in result.Lines)
                {
                    string text = NormalizeLineText(line.Text);
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    LineBlock block = new LineBlock();
                    block.Text = text;
                    block.Left = double.MaxValue;
                    block.Top = double.MaxValue;
                    block.Right = double.MinValue;
                    block.Bottom = double.MinValue;
                    foreach (OcrWord word in line.Words)
                    {
                        Rect r = word.BoundingRect;
                        if (r.Width <= 0 || r.Height <= 0)
                        {
                            continue;
                        }

                        block.Left = Math.Min(block.Left, r.X);
                        block.Top = Math.Min(block.Top, r.Y);
                        block.Right = Math.Max(block.Right, r.X + r.Width);
                        block.Bottom = Math.Max(block.Bottom, r.Y + r.Height);
                    }

                    blocks.Add(block);
                }
            }
            catch
            {
            }

            if (blocks.Count == 0)
            {
                string raw = NormalizeLineText(result.Text);
                if (raw.Length > 0)
                {
                    lineCount = 1;
                    return raw;
                }

                return string.Empty;
            }

            StringBuilder builder = new StringBuilder();
            LineBlock previous = null;
            for (int i = 0; i < blocks.Count; i++)
            {
                LineBlock block = blocks[i];
                if (previous != null && IsSameVisualLine(previous, block))
                {
                    bool insertSpace = !EndsWithCjk(previous.Text) || !StartsWithCjk(block.Text);
                    if (insertSpace)
                    {
                        builder.Append(' ');
                    }

                    builder.Append(block.Text);
                    previous.Text = insertSpace
                        ? previous.Text + " " + block.Text
                        : previous.Text + block.Text;
                    previous.Left = Math.Min(previous.Left, block.Left);
                    previous.Top = Math.Min(previous.Top, block.Top);
                    previous.Right = Math.Max(previous.Right, block.Right);
                    previous.Bottom = Math.Max(previous.Bottom, block.Bottom);
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.AppendLine();
                    lineCount++;
                }

                builder.Append(block.Text);
                previous = block;
            }

            if (builder.Length > 0)
            {
                lineCount++;
            }

            return builder.ToString();
        }

        private static bool IsSameVisualLine(LineBlock a, LineBlock b)
        {
            if (a.Left == double.MaxValue || b.Left == double.MaxValue ||
                a.Bottom <= a.Top || b.Bottom <= b.Top)
            {
                return false;
            }

            double overlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
            double minHeight = Math.Min(a.Bottom - a.Top, b.Bottom - b.Top);
            if (overlap < minHeight * 0.5)
            {
                return false;
            }

            double gap = Math.Max(a.Left, b.Left) - Math.Min(a.Right, b.Right);
            double maxHeight = Math.Max(a.Bottom - a.Top, b.Bottom - b.Top);
            return gap <= Math.Max(8.0, maxHeight * 1.5);
        }

        private static bool EndsWithCjk(string text)
        {
            return !string.IsNullOrEmpty(text) && IsCjk(text[text.Length - 1]);
        }

        private static bool StartsWithCjk(string text)
        {
            return !string.IsNullOrEmpty(text) && IsCjk(text[0]);
        }

        private static bool ContainsLatin(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            int count = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                {
                    count++;
                    if (count >= 4)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static string NormalizeLineText(string source)
        {
            if (string.IsNullOrEmpty(source))
            {
                return string.Empty;
            }

            string text = source;
            text = CjkSpace.Replace(text, string.Empty);
            text = FullWidthPunctuationSpace.Replace(text, "$1");
            text = text.Replace("．", ".").Replace("％", "%").Replace("＋", "+").Replace("－", "-");
            text = NumberPunctuationSpace.Replace(text, "$1");
            text = SpaceBeforePercent.Replace(text, string.Empty);
            text = SpaceAfterSign.Replace(text, string.Empty);
            text = SpaceBeforeSign.Replace(text, string.Empty);
            text = MultipleAsciiSpaces.Replace(text, " ");
            return text.Trim();
        }

        private static void CloseWinRt(object value)
        {
            if (value == null)
            {
                return;
            }

            try
            {
                IDisposable disposable = value as IDisposable;
                if (disposable != null)
                {
                    disposable.Dispose();
                }
            }
            catch
            {
            }
        }

        private static string FriendlyError(Exception exception)
        {
            if (exception is TimeoutException)
            {
                return "识别超时。请尝试缩小选区后再试。";
            }

            if (exception is OutOfMemoryException)
            {
                return "选区太大，内存不足。请缩小选区后再试。";
            }

            string message = exception == null ? string.Empty : (exception.Message ?? string.Empty);
            if (message.IndexOf("language", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("语言", StringComparison.OrdinalIgnoreCase) >= 0 ||
                message.IndexOf("0x80070002", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "系统文字识别语言包不可用。\n\n" +
                    "请检查“设置 → 时间和语言 → 语言”中的 OCR 语言功能是否已安装。\n\n" +
                    "技术信息：" + message;
            }

            return "文字识别失败。" +
                (string.IsNullOrEmpty(message) ? "请稍后重试。" : "\n\n技术信息：" + message);
        }
    }
}
