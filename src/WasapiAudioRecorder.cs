using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DpSnip
{
    internal sealed class WasapiAudioRecorder : IDisposable
    {
        private const int AudClntShareModeShared = 0;
        private const int AudClntStreamFlagsLoopback = 0x00020000;
        private const uint AudClntBufferFlagsSilent = 0x00000002;

        private readonly string _outputPath;
        private readonly ManualResetEvent _stopEvent = new ManualResetEvent(false);
        private readonly ManualResetEvent _readyEvent = new ManualResetEvent(false);
        private readonly ManualResetEvent _finishedEvent = new ManualResetEvent(false);

        private Thread _thread;
        private volatile bool _active;
        private volatile bool _hasData;
        private volatile Exception _error;
        private long _dataBytes;

        public WasapiAudioRecorder(string outputPath)
        {
            _outputPath = outputPath;
        }

        public string OutputPath
        {
            get { return _outputPath; }
        }

        public bool IsActive
        {
            get { return _active; }
        }

        public bool HasData
        {
            get { return _hasData; }
        }

        public Exception Error
        {
            get { return _error; }
        }

        public void Start()
        {
            if (_thread != null)
            {
                return;
            }

            _thread = new Thread(CaptureThread);
            _thread.IsBackground = true;
            _thread.Name = "DpSnipSystemAudio";
            _thread.Start();
            _readyEvent.WaitOne(2000);
        }

        public void RequestStop()
        {
            try
            {
                _stopEvent.Set();
            }
            catch
            {
            }
        }

        public bool WaitForFinish(int milliseconds)
        {
            try
            {
                return _finishedEvent.WaitOne(milliseconds);
            }
            catch
            {
                return true;
            }
        }

        private void CaptureThread()
        {
            bool comInitialized = false;
            IntPtr formatPtr = IntPtr.Zero;
            IMMDeviceEnumerator enumerator = null;
            IMMDevice device = null;
            IAudioClient audioClient = null;
            IAudioCaptureClient captureClient = null;
            FileStream stream = null;
            BinaryWriter writer = null;
            try
            {
                int initResult = CoInitializeEx(IntPtr.Zero, 0);
                comInitialized = initResult >= 0;

                enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                int hr = enumerator.GetDefaultAudioEndpoint(0, 0, out device);
                Marshal.ThrowExceptionForHR(hr);

                Guid audioClientId = typeof(IAudioClient).GUID;
                object audioObject;
                hr = device.Activate(ref audioClientId, 23, IntPtr.Zero, out audioObject);
                Marshal.ThrowExceptionForHR(hr);
                audioClient = (IAudioClient)audioObject;

                hr = audioClient.GetMixFormat(out formatPtr);
                Marshal.ThrowExceptionForHR(hr);
                WAVEFORMATEX format = (WAVEFORMATEX)Marshal.PtrToStructure(
                    formatPtr, typeof(WAVEFORMATEX));
                int formatSize = 18 + format.cbSize;
                if (formatSize < 18 || formatSize > 4096 || format.nBlockAlign <= 0 ||
                    format.nSamplesPerSec == 0)
                {
                    throw new InvalidOperationException("系统音频格式无效。");
                }

                byte[] formatBytes = new byte[formatSize];
                Marshal.Copy(formatPtr, formatBytes, 0, formatSize);

                hr = audioClient.Initialize(
                    AudClntShareModeShared,
                    AudClntStreamFlagsLoopback,
                    10000000,
                    0,
                    formatPtr,
                    IntPtr.Zero);
                Marshal.ThrowExceptionForHR(hr);

                Guid captureId = typeof(IAudioCaptureClient).GUID;
                object captureObject;
                hr = audioClient.GetService(ref captureId, out captureObject);
                Marshal.ThrowExceptionForHR(hr);
                captureClient = (IAudioCaptureClient)captureObject;

                if (_stopEvent.WaitOne(0))
                {
                    return;
                }

                stream = new FileStream(
                    _outputPath, FileMode.Create, FileAccess.Write, FileShare.Read, 65536);
                writer = new BinaryWriter(stream, Encoding.ASCII);
                WriteWavHeader(writer, stream, formatBytes);
                long dataStart = stream.Position;

                hr = audioClient.Start();
                Marshal.ThrowExceptionForHR(hr);
                _active = true;
                _readyEvent.Set();

                byte[] packetBuffer = new byte[65536];
                while (!_stopEvent.WaitOne(0))
                {
                    DrainPackets(captureClient, writer, ref packetBuffer, format.nBlockAlign);
                    Thread.Sleep(8);
                }

                // Give the endpoint a moment to deliver the final loopback packets.
                Stopwatch drainClock = Stopwatch.StartNew();
                while (drainClock.ElapsedMilliseconds < 80)
                {
                    DrainPackets(captureClient, writer, ref packetBuffer, format.nBlockAlign);
                    Thread.Sleep(5);
                }

                try
                {
                    audioClient.Stop();
                }
                catch
                {
                }

                long dataLength = stream.Position - dataStart;
                _dataBytes = dataLength;
                _hasData = dataLength > 0;

                writer.Flush();
                stream.Position = 4;
                writer.Write((uint)Math.Min(uint.MaxValue, (ulong)(stream.Length - 8)));
                stream.Position = 20 + formatBytes.Length + 4;
                writer.Write((uint)Math.Min(uint.MaxValue, (ulong)dataLength));
                writer.Flush();
                stream.Flush();
            }
            catch (Exception ex)
            {
                _error = ex;
                AppLog.WriteException("WasapiAudioRecorder", ex);
            }
            finally
            {
                _active = false;
                try
                {
                    if (writer != null)
                    {
                        writer.Dispose();
                    }
                }
                catch
                {
                }

                try
                {
                    if (stream != null)
                    {
                        stream.Dispose();
                    }
                }
                catch
                {
                }

                if (formatPtr != IntPtr.Zero)
                {
                    CoTaskMemFree(formatPtr);
                }

                if (captureClient != null)
                {
                    Marshal.ReleaseComObject(captureClient);
                }

                if (audioClient != null)
                {
                    Marshal.ReleaseComObject(audioClient);
                }

                if (device != null)
                {
                    Marshal.ReleaseComObject(device);
                }

                if (enumerator != null)
                {
                    Marshal.ReleaseComObject(enumerator);
                }

                if (comInitialized)
                {
                    CoUninitialize();
                }

                _readyEvent.Set();
                _finishedEvent.Set();
            }
        }

        private static void WriteWavHeader(BinaryWriter writer, Stream stream, byte[] formatBytes)
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write((uint)0);
            writer.Write(Encoding.ASCII.GetBytes("WAVE"));
            writer.Write(Encoding.ASCII.GetBytes("fmt "));
            writer.Write((uint)formatBytes.Length);
            writer.Write(formatBytes);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write((uint)0);
            writer.Flush();
        }

        private void DrainPackets(
            IAudioCaptureClient captureClient,
            BinaryWriter writer,
            ref byte[] packetBuffer,
            int blockAlign)
        {
            while (true)
            {
                uint packetFrames;
                int hr = captureClient.GetNextPacketSize(out packetFrames);
                if (hr < 0 || packetFrames == 0)
                {
                    return;
                }

                IntPtr data;
                uint frames;
                uint flags;
                long devicePosition;
                long qpcPosition;
                hr = captureClient.GetBuffer(
                    out data, out frames, out flags, out devicePosition, out qpcPosition);
                if (hr < 0)
                {
                    Marshal.ThrowExceptionForHR(hr);
                }

                if (frames > 0)
                {
                    long bytesLong = (long)frames * blockAlign;
                    if (bytesLong > packetBuffer.Length)
                    {
                        packetBuffer = new byte[bytesLong];
                    }

                    int bytes = (int)bytesLong;
                    if ((flags & AudClntBufferFlagsSilent) != 0 || data == IntPtr.Zero)
                    {
                        Array.Clear(packetBuffer, 0, bytes);
                    }
                    else
                    {
                        Marshal.Copy(data, packetBuffer, 0, bytes);
                    }

                    writer.Write(packetBuffer, 0, bytes);
                    _dataBytes += bytes;
                }

                captureClient.ReleaseBuffer(frames);
            }
        }

        public void Dispose()
        {
            RequestStop();
            if (_finishedEvent.WaitOne(1500))
            {
                _stopEvent.Dispose();
                _readyEvent.Dispose();
                _finishedEvent.Dispose();
            }
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [ComImport]
        [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig]
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);

            [PreserveSig]
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        }

        [ComImport]
        [Guid("D666063F-1587-4E43-81F1-B948E807363F")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig]
            int Activate(
                ref Guid iid,
                int clsCtx,
                IntPtr activationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        }

        [ComImport]
        [Guid("1CB9AD4C-DBFA-4C32-B178-C2F568A703B2")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioClient
        {
            [PreserveSig]
            int Initialize(int shareMode, int streamFlags, long bufferDuration,
                long periodicity, IntPtr format, IntPtr sessionGuid);

            [PreserveSig]
            int GetBufferSize(out uint frames);

            [PreserveSig]
            int GetStreamLatency(out long latency);

            [PreserveSig]
            int GetCurrentPadding(out uint frames);

            [PreserveSig]
            int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closestMatch);

            [PreserveSig]
            int GetMixFormat(out IntPtr format);

            [PreserveSig]
            int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);

            [PreserveSig]
            int Start();

            [PreserveSig]
            int Stop();

            [PreserveSig]
            int Reset();

            [PreserveSig]
            int SetEventHandle(IntPtr handle);

            [PreserveSig]
            int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport]
        [Guid("C8ADBD64-E71E-48A0-A4DE-185C395CD317")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioCaptureClient
        {
            [PreserveSig]
            int GetBuffer(out IntPtr data, out uint frames, out uint flags,
                out long devicePosition, out long qpcPosition);

            [PreserveSig]
            int ReleaseBuffer(uint frames);

            [PreserveSig]
            int GetNextPacketSize(out uint frames);
        }

        [ComImport]
        [Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject
        {
        }

        [DllImport("ole32.dll")]
        private static extern int CoInitializeEx(IntPtr reserved, uint coInit);

        [DllImport("ole32.dll")]
        private static extern void CoUninitialize();

        [DllImport("ole32.dll")]
        private static extern void CoTaskMemFree(IntPtr pointer);
    }
}
