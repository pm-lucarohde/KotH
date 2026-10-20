using System;
using Stride.Core;
using Stride.Core.Mathematics;
using Stride.Engine;
using Stride.Graphics;
using Stride.Input;

namespace KotH.Client.Hud
{
    /// <summary>
    /// Frame rate readout, toggled with F3. Presentation only: it reads the
    /// clock and never touches the simulation. Collecting runs even while
    /// hidden, so the ten second window already holds valid numbers the
    /// moment it is switched on.
    /// </summary>
    [DataContract("KotH.PerformanceOverlay")]
    public sealed class PerformanceOverlay : SyncScript, IHudElement
    {
        [Display("Umschalten", "Tasten")] public Keys ToggleKey = Keys.F3;

        [Display("Fenster (s)", "Messung")] public float WindowSeconds = 10f;

        // Starting size of the ring buffer. It doubles on demand up to four
        // times this, so a fast machine does not silently end up with a
        // shorter window than it asked for.
        [Display("Erwartete Höchst-FPS", "Messung")] public int MaxTrackedFps = 500;

        [Display("Auffrischen (s)", "Anzeige")] public float RefreshInterval = 0.25f;

        // The one percent low walks a ten second window, so it wanders
        // visibly when redrawn four times a second. Every few seconds says
        // the same thing and sits still while being read.
        [Display("1%-Low auffrischen (s)", "Anzeige")] public float LowRefreshInterval = 3f;

        [Display("Schriftgröße", "Anzeige")] public float FontSize = 18f;
        [Display("Rand", "Anzeige")] public float Margin = 8f;
        [Display("Schrift", "Anzeige")] public SpriteFont Font;

        // The first frames after a scene load take hundreds of milliseconds
        // and would dominate the one percent low for a whole window.
        const int SkippedFrames = 5;

        // 0.1 ms per bucket over 0 to 100 ms, plus one bucket for anything
        // slower. That is the resolution of the one percent low.
        const float BucketMs = 0.1f;
        const int BucketCount = 1000;

        float[] frameMs;
        int maxCapacity;
        int[] bucketCount;
        double[] bucketSum;

        int head;        // oldest sample
        int count;
        double sumMs;
        float windowMs;
        int skipFrames;

        // The dark band of Unity's mark tag: legible on sky and on grey
        // rock alike.
        static readonly Color4 Band = Color4.PremultiplyAlpha(new Color(0, 0, 0, 0xA0).ToColor4());

        HudRenderer hud;
        string text = "";
        bool visible;
        float sinceRefresh;
        float sinceLowRefresh;
        float cachedLowFps;

        public PerformanceOverlay()
        {
            // Last of all scripts: the readout sees the frame as finished.
            Priority = 2000;
        }

        [DataMemberIgnore] public float FrameTimeMs => count > 0 ? (float)(sumMs / count) : 0f;
        [DataMemberIgnore] public float Fps => count > 0 ? 1000f / FrameTimeMs : 0f;
        [DataMemberIgnore] public float OnePercentLowFps => count > 0 ? 1000f / WorstPercentMs() : 0f;
        [DataMemberIgnore] public int SampleCount => count;

        /// <summary>Networked: a second line with ping and rollbacks.</summary>
        [DataMemberIgnore] public Func<string> Network { get; set; }

        /// <summary>Whether the readout is drawn; collecting never stops.</summary>
        [DataMemberIgnore]
        public bool Visible
        {
            get => visible;
            set
            {
                if (visible == value)
                    return;
                visible = value;
                sinceRefresh = RefreshInterval;
            }
        }

        public override void Start()
        {
            windowMs = WindowSeconds * 1000f;
            skipFrames = SkippedFrames;

            int capacity = Math.Max(64, (int)MathF.Ceiling(WindowSeconds * MaxTrackedFps));
            maxCapacity = capacity * 4;
            frameMs = new float[capacity];
            bucketCount = new int[BucketCount + 1];
            bucketSum = new double[BucketCount + 1];

            hud = HudRenderer.Of(this);
            hud.Add(this);
        }

        public override void Cancel()
        {
            hud?.Remove(this);
        }

        public override void Update()
        {
            float delta = (float)Game.UpdateTime.Elapsed.TotalSeconds;
            Push(delta * 1000f);

            if (KeyPresses.Pressed(Input, ToggleKey))
                Visible = !Visible;

            if (!Visible)
                return;

            sinceLowRefresh += delta;
            if (sinceLowRefresh >= LowRefreshInterval || cachedLowFps <= 0f)
            {
                sinceLowRefresh = 0f;
                cachedLowFps = OnePercentLowFps;
            }

            sinceRefresh += delta;
            if (sinceRefresh < RefreshInterval)
                return;

            sinceRefresh = 0f;
            Redraw();
        }

        void Redraw()
        {
            if (count == 0)
                return;

            text = string.Format(HudRenderer.Numbers, "{0:F0} FPS | {1:F1} ms | 1% {2:F0} FPS",
                                 Fps, FrameTimeMs, cachedLowFps);
        }

        public void Draw(SpriteBatch batch, Vector2 screen)
        {
            if (!visible || Font == null || text.Length == 0)
                return;

            var at = new Vector2(Margin, Margin);
            Vector2 size = Font.MeasureString(text, FontSize);
            batch.Draw(hud.White, new RectangleF(at.X, at.Y, size.X, size.Y), Band);
            batch.DrawString(Font, text, FontSize, at, Color4.White);

            if (Network?.Invoke() is { } line)
            {
                at.Y += size.Y;
                size = Font.MeasureString(line, FontSize);
                batch.Draw(hud.White, new RectangleF(at.X, at.Y, size.X, size.Y), Band);
                batch.DrawString(Font, line, FontSize, at, Color4.White);
            }
        }

        /// <summary>
        /// Mean frame time of the worst one percent of the window, which is
        /// what hardware reviews call the one percent low. Walks the buckets
        /// from the slow end instead of sorting the window.
        /// </summary>
        float WorstPercentMs()
        {
            int wanted = Math.Max(1, count / 100);
            int taken = 0;
            double sum = 0d;

            for (int i = BucketCount; i >= 0 && taken < wanted; i--)
            {
                int inBucket = bucketCount[i];
                if (inBucket == 0)
                    continue;

                int take = Math.Min(inBucket, wanted - taken);

                // A whole bucket contributes its exact sum. Only the bucket
                // the cut falls into is shared out proportionally.
                sum += take == inBucket
                    ? bucketSum[i]
                    : bucketSum[i] * take / inBucket;

                taken += take;
            }

            return taken > 0 ? (float)(sum / taken) : (float)(sumMs / count);
        }

        void Push(float ms)
        {
            if (skipFrames > 0)
            {
                skipFrames--;
                return;
            }

            if (count == frameMs.Length)
            {
                if (frameMs.Length < maxCapacity)
                    Grow();
                else
                    Pop();
            }

            int slot = (head + count) % frameMs.Length;
            frameMs[slot] = ms;
            count++;
            sumMs += ms;
            Histogram(ms, 1);

            // One sample always stays, so a frame longer than the whole
            // window cannot empty the buffer and divide by zero later.
            while (count > 1 && sumMs > windowMs)
                Pop();
        }

        void Pop()
        {
            float ms = frameMs[head];
            head = (head + 1) % frameMs.Length;
            count--;
            sumMs -= ms;
            Histogram(ms, -1);
        }

        /// <summary>
        /// Doubles the ring buffer when the frame rate outruns it. Allocates
        /// at most twice in a session and never per frame; without it the
        /// window would quietly become shorter than WindowSeconds.
        /// </summary>
        void Grow()
        {
            var bigger = new float[Math.Min(frameMs.Length * 2, maxCapacity)];
            for (int i = 0; i < count; i++)
                bigger[i] = frameMs[(head + i) % frameMs.Length];

            frameMs = bigger;
            head = 0;
        }

        void Histogram(float ms, int sign)
        {
            int i = BucketIndex(ms);
            bucketCount[i] += sign;
            bucketSum[i] += sign * ms;
        }

        static int BucketIndex(float ms)
        {
            if (ms <= 0f)
                return 0;

            int i = (int)(ms / BucketMs);
            return i < BucketCount ? i : BucketCount;
        }
    }
}
