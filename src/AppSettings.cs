using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DpSnip
{
    internal sealed class AppSettings
    {
        private readonly string _path;
        private readonly Dictionary<string, string> _values;

        public Color AnnotationColor;
        public int WidthLevel;
        public string LastSaveDirectory;

        public AppSettings()
        {
            _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DpSnip");
            _path = Path.Combine(baseDir, "settings.ini");

            AnnotationColor = Color.FromArgb(229, 57, 53);
            WidthLevel = 1;
            LastSaveDirectory = SafeMyPictures();

            Load();
        }

        public string SettingsPath
        {
            get { return _path; }
        }

        private static string SafeMyPictures()
        {
            try
            {
                string p = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                if (!string.IsNullOrEmpty(p) && Directory.Exists(p))
                {
                    return p;
                }
            }
            catch
            {
            }

            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path))
                {
                    return;
                }

                foreach (string line in File.ReadAllLines(_path, Encoding.UTF8))
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith("#", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    int equals = trimmed.IndexOf('=');
                    if (equals <= 0)
                    {
                        continue;
                    }

                    string key = trimmed.Substring(0, equals).Trim();
                    string value = trimmed.Substring(equals + 1).Trim();
                    _values[key] = value;
                }

                string colorText;
                if (_values.TryGetValue("color", out colorText))
                {
                    Color parsed = DrawUtil.ColorFromHex(colorText);
                    if (parsed != Color.Empty)
                    {
                        AnnotationColor = parsed;
                    }
                }

                string widthText;
                int level;
                if (_values.TryGetValue("width", out widthText) &&
                    int.TryParse(widthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out level))
                {
                    WidthLevel = Math.Max(0, Math.Min(2, level));
                }

                string saveDir;
                if (_values.TryGetValue("save_dir", out saveDir) &&
                    !string.IsNullOrEmpty(saveDir) && Directory.Exists(saveDir))
                {
                    LastSaveDirectory = saveDir;
                }
            }
            catch
            {
            }
        }

        public void Save()
        {
            try
            {
                string dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                StringBuilder builder = new StringBuilder();
                builder.AppendLine("# DpSnip settings");
                builder.AppendLine("color=" + DrawUtil.ToHex(AnnotationColor));
                builder.AppendLine("width=" + WidthLevel.ToString(CultureInfo.InvariantCulture));
                builder.AppendLine("save_dir=" + (LastSaveDirectory ?? string.Empty));
                File.WriteAllText(_path, builder.ToString(), new UTF8Encoding(false));
            }
            catch
            {
            }
        }
    }

    internal static class StartupManager
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "DpSnip";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null)
                    {
                        return false;
                    }

                    string value = key.GetValue(ValueName) as string;
                    if (string.IsNullOrEmpty(value))
                    {
                        return false;
                    }

                    string exe = Application.ExecutablePath;
                    return value.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch
            {
                return false;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (key == null)
                {
                    throw new InvalidOperationException("无法打开当前用户的启动项注册表。");
                }

                if (enabled)
                {
                    string command = "\"" + Application.ExecutablePath + "\" --autostart";
                    key.SetValue(ValueName, command, RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(ValueName, false);
                }
            }
        }
    }

    internal static class AppLog
    {
        private static readonly object Sync = new object();

        public static string LogPath
        {
            get
            {
                return Path.Combine(
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DpSnip"),
                    "DpSnip.log");
            }
        }

        public static void Write(string message)
        {
            try
            {
                lock (Sync)
                {
                    string path = LogPath;
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.AppendAllText(path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) +
                        "  " + message + Environment.NewLine,
                        new UTF8Encoding(false));
                }
            }
            catch
            {
            }
        }

        public static void WriteException(string context, Exception exception)
        {
            if (exception == null)
            {
                return;
            }

            Write(context + ": " + exception.GetType().Name + ": " + exception.Message +
                  Environment.NewLine + exception.StackTrace);
        }
    }

    internal static class ClipboardHelper
    {
        public static void PutImage(Bitmap image)
        {
            Exception last = null;
            for (int attempt = 0; attempt < 6; attempt++)
            {
                try
                {
                    DataObject data = new DataObject();
                    data.SetImage(image);
                    using (MemoryStream png = new MemoryStream())
                    {
                        image.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                        png.Position = 0;
                        data.SetData("PNG", false, png);
                    }

                    Clipboard.SetDataObject(data, true, 10, 60);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    System.Threading.Thread.Sleep(50);
                    Application.DoEvents();
                }
            }

            throw new InvalidOperationException("无法写入剪贴板。请检查是否有其他程序正在占用剪贴板。", last);
        }
    }
}

