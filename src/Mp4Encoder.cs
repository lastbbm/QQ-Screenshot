using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using Windows.Foundation;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace DpSnip
{
    internal static class Mp4Encoder
    {
        public static void EncodeImageSequence(
            IList<string> imagePaths,
            string outputPath,
            int width,
            int height,
            int frameRate,
            int frameDurationMilliseconds)
        {
            if (imagePaths == null || imagePaths.Count == 0)
            {
                throw new InvalidOperationException("没有可编码的录制帧。");
            }

            if (width <= 0 || height <= 0)
            {
                throw new ArgumentException("MP4 尺寸无效。");
            }

            int safeFrameRate = Math.Max(1, Math.Min(30, frameRate));
            int safeDuration = Math.Max(20, frameDurationMilliseconds);
            MediaComposition composition = new MediaComposition();
            for (int i = 0; i < imagePaths.Count; i++)
            {
                StorageFile imageFile = Wait(StorageFile.GetFileFromPathAsync(imagePaths[i]));
                MediaClip clip = Wait(MediaClip.CreateFromImageFileAsync(
                    imageFile,
                    TimeSpan.FromMilliseconds(safeDuration)));
                composition.Clips.Add(clip);
            }

            string directory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                throw new DirectoryNotFoundException("MP4 输出目录不存在。");
            }

            StorageFolder folder = Wait(StorageFolder.GetFolderFromPathAsync(directory));
            StorageFile output = Wait(folder.CreateFileAsync(
                Path.GetFileName(outputPath),
                CreationCollisionOption.ReplaceExisting));

            MediaEncodingProfile profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
            profile.Video.Width = (uint)width;
            profile.Video.Height = (uint)height;
            profile.Video.FrameRate.Numerator = (uint)safeFrameRate;
            profile.Video.FrameRate.Denominator = 1;
            profile.Video.Bitrate = (uint)Math.Max(1000000, Math.Min(12000000, width * height * 3));

            IAsyncOperationWithProgress<TranscodeFailureReason, double> operation =
                composition.RenderToFileAsync(output, MediaTrimmingPreference.Fast, profile);

            long waited = 0;
            while (operation.Status == AsyncStatus.Started)
            {
                if (waited > 180000)
                {
                    throw new TimeoutException("MP4 编码超时。");
                }

                Thread.Sleep(20);
                waited += 20;
            }

            if (operation.Status == AsyncStatus.Error)
            {
                Exception error = operation.ErrorCode;
                throw new InvalidOperationException(
                    "MP4 编码失败。" + (error == null ? string.Empty : " " + error.Message), error);
            }

            if (operation.Status == AsyncStatus.Canceled)
            {
                throw new OperationCanceledException("MP4 编码已取消。");
            }

            TranscodeFailureReason reason = operation.GetResults();
            if (reason != TranscodeFailureReason.None)
            {
                throw new InvalidOperationException("MP4 编码失败：" + reason.ToString());
            }

            if (!File.Exists(outputPath) || new FileInfo(outputPath).Length <= 0)
            {
                throw new IOException("MP4 文件生成失败。");
            }
        }

        private static T Wait<T>(IAsyncOperation<T> operation)
        {
            while (operation.Status == AsyncStatus.Started)
            {
                Thread.Sleep(10);
            }

            if (operation.Status == AsyncStatus.Error)
            {
                Exception error = operation.ErrorCode;
                throw new InvalidOperationException(
                    "读取录制帧失败。" + (error == null ? string.Empty : " " + error.Message), error);
            }

            if (operation.Status == AsyncStatus.Canceled)
            {
                throw new OperationCanceledException("读取录制帧已取消。");
            }

            return operation.GetResults();
        }
    }
}
