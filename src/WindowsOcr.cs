using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
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

        public bool HasText
        {
            get { return Success && !string.IsNullOrEmpty(Text); }
        }
    }

    internal static class WindowsOcr
    {
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

        public static OcrOutcome Recognize(Bitmap source, string preferredCulture)
        {
            OcrOutcome outcome = new OcrOutcome();
            if (source == null || source.Width < 2 || source.Height < 2)
            {
                outcome.Error = "选区太小，无法识别文字。";
                return outcome;
            }

            Bitmap prepared = null;
            try
            {
                Language selectedLanguage = null;
                OcrEngine engine = CreateEngine(preferredCulture, ref selectedLanguage);
                if (engine == null)
                {
                    outcome.Error = "系统没有安装可用的 OCR 语言包。\n\n" +
                        "请打开“设置 → 时间和语言 → 语言”，添加中文（简体）或英语语言包，" +
                        "并在语言选项中安装“光学字符识别”功能后重试。";
                    return outcome;
                }

                if (selectedLanguage != null)
                {
                    outcome.LanguageName = selectedLanguage.DisplayName;
                }
                else if (engine.RecognizerLanguage != null)
                {
                    outcome.LanguageName = engine.RecognizerLanguage.DisplayName;
                }
                else
                {
                    outcome.LanguageName = "Windows OCR";
                }

                prepared = PrepareImage(source);
                byte[] pngBytes = EncodePng(prepared);

                InMemoryRandomAccessStream stream = null;
                DataWriter writer = null;
                SoftwareBitmap softwareBitmap = null;
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
                    BitmapDecoder decoder = WaitFor(
                        BitmapDecoder.CreateAsync(stream), 20000, "读取截图");
                    softwareBitmap = WaitFor(
                        decoder.GetSoftwareBitmapAsync(), 20000, "准备图像");
                    OcrResult result = WaitFor(
                        engine.RecognizeAsync(softwareBitmap), 60000, "文字识别");

                    string normalized = NormalizeLines(result, out outcome.LineCount);
                    outcome.Text = normalized;
                    outcome.CharCount = normalized == null ? 0 : normalized.Replace("\r", "").Replace("\n", "").Length;
                    outcome.Success = !string.IsNullOrEmpty(normalized);
                    if (!outcome.Success)
                    {
                        outcome.Error = "没有识别到清晰的文字。\n\n" +
                            "请尽量框选紧凑的文字区域；如果文字太小，可以放大页面后再截图识别。";
                    }

                    return outcome;
                }
                finally
                {
                    CloseWinRt(softwareBitmap);
                    CloseWinRt(writer);
                    CloseWinRt(stream);
                }
            }
            catch (Exception ex)
            {
                AppLog.WriteException("OCR", ex);
                outcome.Success = false;
                outcome.Error = FriendlyError(ex);
                return outcome;
            }
            finally
            {
                if (prepared != null && !object.ReferenceEquals(prepared, source))
                {
                    prepared.Dispose();
                }
            }
        }

        private static OcrEngine CreateEngine(string preferredCulture, ref Language selected)
        {
            List<Language> available = new List<Language>();
            try
            {
                foreach (Language language in OcrEngine.AvailableRecognizerLanguages)
                {
                    available.Add(language);
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
                            ordered.Add(available[i]);
                        }
                    }
                }

                for (int pass = 0; pass < 2; pass++)
                {
                    string prefix = pass == 0 ? "zh" : "en";
                    for (int i = 0; i < available.Count; i++)
                    {
                        string tag = (available[i].LanguageTag ?? string.Empty).ToLowerInvariant();
                        if (tag.StartsWith(prefix, StringComparison.Ordinal) && !ordered.Contains(available[i]))
                        {
                            ordered.Add(available[i]);
                        }
                    }
                }

                for (int i = 0; i < available.Count; i++)
                {
                    if (!ordered.Contains(available[i]))
                    {
                        ordered.Add(available[i]);
                    }
                }

                for (int i = 0; i < ordered.Count; i++)
                {
                    try
                    {
                        OcrEngine engine = OcrEngine.TryCreateFromLanguage(ordered[i]);
                        if (engine != null)
                        {
                            selected = ordered[i];
                            return engine;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            try
            {
                OcrEngine profileEngine = OcrEngine.TryCreateFromUserProfileLanguages();
                if (profileEngine != null)
                {
                    selected = null;
                    return profileEngine;
                }
            }
            catch
            {
            }

            return null;
        }

        private static Bitmap PrepareImage(Bitmap source)
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

            int width = source.Width;
            int height = source.Height;
            float scale = 1f;

            if (width > maxDimension || height > maxDimension)
            {
                scale = Math.Min((float)maxDimension / width, (float)maxDimension / height);
            }

            if (scale >= 0.999f)
            {
                return source;
            }

            int newWidth = Math.Max(2, (int)Math.Round(width * scale));
            int newHeight = Math.Max(2, (int)Math.Round(height * scale));
            Bitmap resized = new Bitmap(newWidth, newHeight, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(resized))
            {
                g.Clear(Color.White);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(source, new Rectangle(0, 0, newWidth, newHeight));
            }

            return resized;
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

            StringBuilder builder = new StringBuilder();
            try
            {
                foreach (OcrLine line in result.Lines)
                {
                    string text = NormalizeLineText(line.Text);
                    if (text.Length == 0)
                    {
                        continue;
                    }

                    if (builder.Length > 0)
                    {
                        builder.AppendLine();
                    }

                    builder.Append(text);
                    lineCount++;
                }
            }
            catch
            {
            }

            if (builder.Length == 0)
            {
                string raw = NormalizeLineText(result.Text);
                if (raw.Length > 0)
                {
                    builder.Append(raw);
                    lineCount = 1;
                }
            }

            return builder.ToString();
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

