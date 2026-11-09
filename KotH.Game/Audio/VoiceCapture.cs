using System;
using System.Runtime.InteropServices;
using System.Text;

namespace KotH.Client.Audio
{
    /// <summary>The microphone through OpenAL Soft's capture (the Settings' input device, empty for the
    /// system's): 48 kHz mono 16 bit, read in frames of 20 ms. Opened on first use, recording only while
    /// started; what piled up before a start is dropped. A failure is said once and leaves it silent.</summary>
    public sealed unsafe class VoiceCapture : IDisposable
    {
        public const int Rate = 48000, Frame = Rate / 50;
        const int MonoShort = 0x1101, CaptureSamples = 0x312;

        nint device;
        string openedFor;
        bool running, failed;

        /// <summary>Why it is silent, null while it works.</summary>
        public string Failure { get; private set; }

        /// <summary>Starts recording on the device (opening it, or another one if the setting changed).</summary>
        public bool Start(string deviceName)
        {
            deviceName ??= "";
            if (device != 0 && openedFor != deviceName)
                Close();
            if (device == 0 && !Open(deviceName))
                return false;
            if (!running)
            {
                Drop();
                Al.CaptureStart(device);
                running = true;
            }
            return true;
        }

        public void Stop()
        {
            if (!running)
                return;
            Al.CaptureStop(device);
            running = false;
        }

        /// <summary>The next 20 ms the microphone heard, if there are that many.</summary>
        public bool TryRead(Span<short> frame)
        {
            if (!running || Available() < Frame)
                return false;
            fixed (short* p = frame)
                Al.CaptureSamples(device, p, Frame);
            return true;
        }

        int Available()
        {
            int n = 0;
            Al.GetIntegerv(device, CaptureSamples, 1, &n);
            return n;
        }

        // Whatever waited from before the start: a press must not send old sound.
        void Drop()
        {
            Span<short> scrap = stackalloc short[Frame];
            for (int n = Available(); n >= Frame; n -= Frame)
                fixed (short* p = scrap)
                    Al.CaptureSamples(device, p, Frame);
        }

        bool Open(string deviceName)
        {
            if (failed)
                return false;
            try
            {
                byte[] name = deviceName.Length > 0 ? Encoding.UTF8.GetBytes(deviceName + "\0") : null;
                fixed (byte* n = name)
                    device = Al.CaptureOpenDevice(n, Rate, MonoShort, Rate / 2);
                if (device == 0)
                    return Fail($"OpenAL opened no microphone ({(deviceName.Length > 0 ? deviceName : "system default")})");
                openedFor = deviceName;
                return true;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
                return Fail($"no OpenAL capture: {e.Message}");
            }
        }

        bool Fail(string why)
        {
            failed = true;
            Failure = why;
            Console.WriteLine($"[voice] {why}");
            return false;
        }

        void Close()
        {
            if (device == 0)
                return;
            Stop();
            Al.CaptureCloseDevice(device);
            device = 0;
            openedFor = null;
        }

        public void Dispose() => Close();

        static class Al
        {
            static readonly bool Windows = OperatingSystem.IsWindows();

            public static nint CaptureOpenDevice(byte* name, int rate, int format, int samples)
                => Windows ? W.alcCaptureOpenDevice(name, (uint)rate, format, samples) : L.alcCaptureOpenDevice(name, (uint)rate, format, samples);
            public static void CaptureStart(nint d) { if (Windows) W.alcCaptureStart(d); else L.alcCaptureStart(d); }
            public static void CaptureStop(nint d) { if (Windows) W.alcCaptureStop(d); else L.alcCaptureStop(d); }
            public static void CaptureSamples(nint d, void* into, int n) { if (Windows) W.alcCaptureSamples(d, into, n); else L.alcCaptureSamples(d, into, n); }
            public static void GetIntegerv(nint d, int param, int size, int* into) { if (Windows) W.alcGetIntegerv(d, param, size, into); else L.alcGetIntegerv(d, param, size, into); }
            public static void CaptureCloseDevice(nint d) { if (Windows) W.alcCaptureCloseDevice(d); else L.alcCaptureCloseDevice(d); }

            static class L
            {
                const string Lib = "libopenal.so.1";
                [DllImport(Lib)] public static extern nint alcCaptureOpenDevice(byte* name, uint rate, int format, int samples);
                [DllImport(Lib)] public static extern void alcCaptureStart(nint device);
                [DllImport(Lib)] public static extern void alcCaptureStop(nint device);
                [DllImport(Lib)] public static extern void alcCaptureSamples(nint device, void* buffer, int samples);
                [DllImport(Lib)] public static extern void alcGetIntegerv(nint device, int param, int size, int* data);
                [DllImport(Lib)] public static extern byte alcCaptureCloseDevice(nint device);
            }

            static class W
            {
                const string Lib = "OpenAL32";
                [DllImport(Lib)] public static extern nint alcCaptureOpenDevice(byte* name, uint rate, int format, int samples);
                [DllImport(Lib)] public static extern void alcCaptureStart(nint device);
                [DllImport(Lib)] public static extern void alcCaptureStop(nint device);
                [DllImport(Lib)] public static extern void alcCaptureSamples(nint device, void* buffer, int samples);
                [DllImport(Lib)] public static extern void alcGetIntegerv(nint device, int param, int size, int* data);
                [DllImport(Lib)] public static extern byte alcCaptureCloseDevice(nint device);
            }
        }
    }
}
