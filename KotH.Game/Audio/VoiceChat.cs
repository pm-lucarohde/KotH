using System;
using System.Collections.Generic;
using System.Globalization;
using Concentus;
using Concentus.Enums;
using KotH.Client.Net;
using KotH.Client.Player;
using KotH.Client.Settings;
using KotH.Client.Steam;
using KotH.Shared.Player;
using KotH.Shared.Simulation;
using Stride.Audio;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace KotH.Client.Audio
{
    /// <summary>Voice in a Steam round: while the push-to-talk key is held (and 200 ms after) the microphone
    /// goes out Opus coded, 40 ms a packet, straight to every player within 33 m. Each speaker heard plays
    /// from where its figure stands: full up to 4 m, softer with the distance, silent from 30 m (Luca).
    /// Muted players are never played. Presentation only; nothing here reaches the simulation.</summary>
    [DataContract("KotH.VoiceChat")]
    public sealed class VoiceChat : SyncScript
    {
        public const float FullUpTo = 4f, SilentFrom = 30f, SendWithin = 33f;
        const double TailMs = 200.0, SpeakingMs = 300.0, ForgetMs = 30000.0;
        const int FramesPerPacket = 2, MaxFrameBytes = 250, Bitrate = 24000;
        const byte Talk = 1, End = 2;
        const ulong Loopback = 1;   // no Steam ID is this small

        public enum Mic { Off, Talking, Missing, NoRound }

        [DataMemberIgnore] public SimulationDriver Driver { get; set; }

        /// <summary>The own microphone: off, going out, held without one that works, or outside a Steam round.</summary>
        public static Mic State { get; private set; }

        /// <summary>Whether the player with this NetId was heard in the last 300 ms.</summary>
        public static bool IsSpeaking(int netId) => netId != 0 && speaking.Contains(netId);

        static readonly HashSet<int> speaking = new HashSet<int>();

        sealed class Speaker
        {
            public int NetId;
            public IOpusDecoder Decoder;
            public SoundInstance Instance;
            public VoiceSource Source;
            public ushort Next;
            public bool Started;
            public double LastPacket;
        }

        readonly VoiceCapture capture = new VoiceCapture();
        readonly Dictionary<ulong, Speaker> speakers = new Dictionary<ulong, Speaker>();
        readonly List<ulong> gone = new List<ulong>();
        readonly short[] pcm = new short[VoiceCapture.Frame];
        readonly byte[] packet = new byte[4 + FramesPerPacket * (1 + MaxFrameBytes)];
        readonly AudioEmitter emitter = new AudioEmitter();
        // Test aids: KOTH_VOICE_LOOPBACK=metres plays the own voice back from that far ahead, _HOLD=1 holds the key.
        readonly float loopback = Metres(Environment.GetEnvironmentVariable("KOTH_VOICE_LOOPBACK"));
        readonly bool hold = Environment.GetEnvironmentVariable("KOTH_VOICE_HOLD") == "1";
        IOpusEncoder encoder;
        PlayerCamera camera;
        Comms comms;
        NetClient net;
        SteamMesh.VoiceHandler heard;
        int packetLength, packetFrames;
        ushort sequence;
        double talkUntil, now;
        bool talking;
        Vector3? loopPoint;

        static VoiceChat() => OpusCodecFactory.AttemptToUseNativeLibrary = false;

        public VoiceChat()
        {
            Priority = 1050;   // after the camera (1000): the ear is where this frame is drawn from
        }

        public override void Update()
        {
            now = Game.UpdateTime.Total.TotalMilliseconds;
            net = Services.GetService<NetClient>();
            Comms current = net?.Comms;
            if (current != comms)
            {
                Silence();
                comms = current;
                if (comms != null)
                    comms.VoiceHeard = heard ??= Heard;
            }
            camera ??= FindCamera();
            if (comms == null && float.IsNaN(loopback) || camera?.Motor == null)
            {
                State = Held() ? Mic.NoRound : Mic.Off;   // the key says why it is silent
                return;
            }
            Send();
            Place();
        }

        PlayerCamera FindCamera()
        {
            foreach (Entity e in Entity.Scene.Entities)
                if (e.Get<PlayerCamera>() is { } c)
                    return c;
            return null;
        }

        // The own voice: recorded while the key is held and a little after, out in packets of 40 ms.
        void Send()
        {
            bool held = Held();
            if (held)
                talkUntil = now + TailMs;
            bool wanted = now < talkUntil;
            if (wanted && !talking)
                talking = capture.Start(ClientSettings.Applied.InputDevice);
            State = talking ? Mic.Talking : held ? Mic.Missing : Mic.Off;
            if (!talking)
                return;
            encoder ??= NewEncoder();
            while (capture.TryRead(pcm))
            {
                if (packetFrames == 0)
                    Head(Talk);
                int length = encoder.Encode(pcm, VoiceCapture.Frame, packet.AsSpan(packetLength + 1), MaxFrameBytes);
                packet[packetLength] = (byte)length;
                packetLength += 1 + length;
                packetFrames++;
                sequence++;
                if (packetFrames == FramesPerPacket)
                    Out();
            }
            if (!wanted)
            {
                if (packetFrames > 0)
                    Out();
                Head(End);
                Out();
                capture.Stop();
                talking = false;
                State = Mic.Off;
            }
        }

        bool Held() => Driver is { InMenu: false, Typing: false }
                       && (hold || Input.IsKeyDown(ClientSettings.Applied.Key(Binding.PushToTalk)));

        // Kind, the sequence number of the first frame, then the frame count (set in Out).
        void Head(byte kind)
        {
            packet[0] = kind;
            packet[1] = (byte)sequence;
            packet[2] = (byte)(sequence >> 8);
            packetLength = 4;
        }

        static IOpusEncoder NewEncoder()
        {
            IOpusEncoder e = OpusCodecFactory.CreateEncoder(VoiceCapture.Rate, 1, OpusApplication.OPUS_APPLICATION_VOIP);
            e.Bitrate = Bitrate;
            e.Complexity = 5;
            e.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
            return e;
        }

        // To every member whose figure is within reach of the own one, as this world has them.
        void Out()
        {
            packet[3] = (byte)packetFrames;
            packetFrames = 0;
            var data = new ReadOnlySpan<byte>(packet, 0, packetLength);
            if (!float.IsNaN(loopback))
                Heard(Loopback, data);
            if (comms == null)
                return;
            Vector3 own = camera.Motor.Position;
            foreach (ulong member in comms.Members)
                if (Body(comms.NetIdOf(member)) is { } body && Vector3.Distance(body.Position, own) <= SendWithin)
                    comms.Mesh.SendVoice(member, data);
        }

        PlayerMotor Body(int netId) => netId != 0 ? net?.Session.BodyOf(netId)?.Motor : null;

        // A packet from a member: its frames decoded in order, a lost one guessed (Opus concealment).
        void Heard(ulong from, ReadOnlySpan<byte> data)
        {
            if (data.Length < 4 || comms != null && comms.IsMuted(from) || SpeakerOf(from) is not { } s)
                return;
            s.LastPacket = now;
            if (data[0] == End)
            {
                s.Started = false;
                return;
            }
            ushort first = (ushort)(data[1] | data[2] << 8);
            int at = 4;
            for (int i = 0; i < data[3] && at < data.Length; i++)
            {
                int length = data[at];
                if (at + 1 + length > data.Length)
                    return;
                var frame = data.Slice(at + 1, length);
                at += 1 + length;
                ushort seq = (ushort)(first + i);
                int ahead = (ushort)(seq - s.Next);
                if (s.Started && ahead >= 0x8000)
                    continue;   // late: its place was already guessed
                for (int lost = s.Started ? Math.Min(ahead, 3) : 0; lost > 0; lost--)
                    Decode(s, ReadOnlySpan<byte>.Empty);
                Decode(s, frame);
                s.Next = (ushort)(seq + 1);
                s.Started = true;
            }
        }

        void Decode(Speaker s, ReadOnlySpan<byte> frame)
        {
            try
            {
                int n = s.Decoder.Decode(frame, pcm, VoiceCapture.Frame, false);
                if (n > 0)
                    s.Source.Write(pcm.AsSpan(0, n));
            }
            catch (Exception e)
            {
                // A broken packet is silence, never a stop; counted, the first one said.
                if (brokenFrames++ == 0)
                    StartLog.Warn($"voice frame not decoded: {e.GetType().Name}: {e.Message}");
            }
        }

        int brokenFrames;   // since the start

        Speaker SpeakerOf(ulong from)
        {
            if (speakers.TryGetValue(from, out Speaker s))
                return s;
            if (Audio.AudioEngine is not { State: not AudioEngineState.Invalidated } engine)
                return null;
            SoundInstance instance = VoiceSource.Create(engine, engine.DefaultListener, out VoiceSource source);
            if (instance == null)
                return null;
            instance.Volume = 0f;
            Console.WriteLine($"[voice] hearing {(from == Loopback ? "the own loopback" : from.ToString(CultureInfo.InvariantCulture))}");
            s = new Speaker { Decoder = OpusCodecFactory.CreateDecoder(VoiceCapture.Rate, 1), Instance = instance, Source = source };
            speakers[from] = s;
            return s;
        }

        // Every speaker where its figure stands, as loud as its distance allows; long quiet ones go.
        void Place()
        {
            Vector3 own = camera.Motor.Position;
            float gain = PlayerSettings.Gain(ClientSettings.Applied.Voice);
            gone.Clear();
            speaking.Clear();
            foreach ((ulong member, Speaker s) in speakers)
            {
                if (now - s.LastPacket > ForgetMs)
                {
                    gone.Add(member);
                    continue;
                }
                s.NetId = member == Loopback ? 0 : comms.NetIdOf(member);
                if (s.Started && now - s.LastPacket < SpeakingMs)
                    speaking.Add(s.NetId);
                Vector3? at = member == Loopback ? loopPoint ??= own + Ahead() * loopback : Body(s.NetId)?.BodyCenter;
                if (at is not { } point || comms != null && comms.IsMuted(member))
                {
                    s.Instance.Volume = 0f;
                    continue;
                }
                s.Instance.Volume = Loudness(Vector3.Distance(point, own)) * gain;
                // A metre from the ear in its direction: OpenAL's own fall-off stays out of it.
                Vector3 d = SoundEffects.FromEar(camera, point);
                float length = d.Length();
                emitter.Position = length > 1e-3f ? d / length : Vector3.UnitZ;
                s.Instance.Apply3D(emitter);
            }
            foreach (ulong member in gone)
                Forget(member);
        }

        // Level, where the view looks: the loopback's speaker stands there.
        Vector3 Ahead()
        {
            Vector3 f = camera.Entity.Transform.WorldMatrix.Forward;
            f.Y = 0f;
            return f.LengthSquared() > 1e-6f ? Vector3.Normalize(f) : -Vector3.UnitZ;
        }

        /// <summary>How loud a speaker is at a distance: 1 up to 4 m, then softly (in log distance) to 0 at 30 m.</summary>
        public static float Loudness(float distance)
        {
            if (distance <= FullUpTo)
                return 1f;
            if (distance >= SilentFrom)
                return 0f;
            float t = MathF.Log(distance / FullUpTo) / MathF.Log(SilentFrom / FullUpTo);
            return 1f - t * t * (3f - 2f * t);
        }

        static float Metres(string text)
            => float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float m) ? m : float.NaN;

        public override void Cancel()
        {
            Silence();
            capture.Dispose();
        }

        void Forget(ulong member)
        {
            if (!speakers.Remove(member, out Speaker s))
                return;
            s.Instance.Stop();
            s.Instance.Dispose();
        }

        // Out of a round (or into another): every speaker goes, the microphone stops.
        void Silence()
        {
            gone.Clear();
            gone.AddRange(speakers.Keys);
            foreach (ulong member in gone)
                Forget(member);
            capture.Stop();
            talking = false;
            talkUntil = 0.0;
            packetFrames = 0;
            loopPoint = null;
            speaking.Clear();
            State = Mic.Off;
            if (comms != null)
                comms.VoiceHeard = null;
        }
    }
}
