using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace KotH.Client.Settings
{
    /// <summary>The sound devices OpenAL knows (Stride's audio runs on OpenAL Soft): outputs and
    /// microphones by name, the empty name first for the system's choice. Read once; an output
    /// takes effect at the next start (AudioSystem.RequestedAudioDevice), a microphone waits for voice chat.</summary>
    public static class AudioDevices
    {
        const int AllDevices = 0x1013, Devices = 0x1005, CaptureDevices = 0x310;

        [DllImport("libopenal.so.1", EntryPoint = "alcGetString")]
        static extern nint LinuxString(nint device, int param);

        [DllImport("OpenAL32", EntryPoint = "alcGetString")]
        static extern nint WindowsString(nint device, int param);

        static List<string> outputs, inputs;

        public static IReadOnlyList<string> Outputs() => outputs ??= List(AllDevices, Devices);

        public static IReadOnlyList<string> Inputs() => inputs ??= List(CaptureDevices, CaptureDevices);

        /// <summary>A device as the page writes it; OpenAL Soft puts "OpenAL Soft on " before each name.</summary>
        public static string Text(string name) => string.IsNullOrEmpty(name) ? "System default"
            : name.StartsWith("OpenAL Soft on ") ? name[15..] : name;

        // OpenAL's list: names one after another, each ended by a NUL, the list by a second.
        static List<string> List(int param, int fallback)
        {
            var names = new List<string> { "" };
            try
            {
                nint text = Get(param);
                if (text == 0)
                    text = Get(fallback);
                while (text != 0 && Marshal.ReadByte(text) != 0)
                {
                    string name = Marshal.PtrToStringUTF8(text) ?? "";
                    names.Add(name);
                    text += System.Text.Encoding.UTF8.GetByteCount(name) + 1;
                }
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
            {
            }
            return names;
        }

        static nint Get(int param) => OperatingSystem.IsWindows() ? WindowsString(0, param) : LinuxString(0, param);
    }
}
