using TMPro;
using UnityEngine;

namespace KotH.Client
{
    /// <summary>
    /// Frame rate readout, toggled with F3. Presentation only: it reads the
    /// clock and never touches the simulation. Collecting runs even while
    /// hidden, so the ten second window already holds valid numbers the
    /// moment it is switched on.
    /// </summary>
    public sealed class PerformanceOverlay : MonoBehaviour
    {
        [Header("Tasten")]
        [SerializeField] KeyCode toggleKey = KeyCode.F3;

        [Header("Messung")]
        [SerializeField] float windowSeconds = 10f;

        // Starting size of the ring buffer. It doubles on demand up to four
        // times this, so a fast machine does not silently end up with a
        // shorter window than it asked for.
        [SerializeField] int maxTrackedFps = 500;

        [Header("Anzeige")]
        [SerializeField] float refreshInterval = 0.25f;

        // The one percent low walks a ten second window, so it wanders
        // visibly when redrawn four times a second. Every few seconds says
        // the same thing and sits still while being read.
        [SerializeField] float lowRefreshInterval = 3f;

        [SerializeField] float fontSize = 18f;
        [SerializeField] float margin = 8f;

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

        Canvas canvas;
        TextMeshProUGUI label;
        float sinceRefresh;
        float sinceLowRefresh;
        float cachedLowFps;

        public float FrameTimeMs => count > 0 ? (float)(sumMs / count) : 0f;
        public float Fps => count > 0 ? 1000f / FrameTimeMs : 0f;
        public float OnePercentLowFps => count > 0 ? 1000f / WorstPercentMs() : 0f;
        public int SampleCount => count;

        void Awake()
        {
            windowMs = windowSeconds * 1000f;
            skipFrames = SkippedFrames;

            int capacity = Mathf.Max(64, Mathf.CeilToInt(windowSeconds * maxTrackedFps));
            maxCapacity = capacity * 4;
            frameMs = new float[capacity];
            bucketCount = new int[BucketCount + 1];
            bucketSum = new double[BucketCount + 1];

            BuildUi();
            canvas.enabled = false;
        }

        void Update()
        {
            // Unscaled: a changed Time.timeScale must not distort the reading.
            Push(Time.unscaledDeltaTime * 1000f);

            if (Input.GetKeyDown(toggleKey))
            {
                canvas.enabled = !canvas.enabled;
                sinceRefresh = refreshInterval;
            }

            if (!canvas.enabled)
                return;

            sinceLowRefresh += Time.unscaledDeltaTime;
            if (sinceLowRefresh >= lowRefreshInterval || cachedLowFps <= 0f)
            {
                sinceLowRefresh = 0f;
                cachedLowFps = OnePercentLowFps;
            }

            sinceRefresh += Time.unscaledDeltaTime;
            if (sinceRefresh < refreshInterval)
                return;

            sinceRefresh = 0f;
            Redraw();
        }

        void Redraw()
        {
            if (count == 0)
                return;

            // The mark tag paints a band behind the glyphs, so the readout
            // stays legible on sky and on grey rock without a second object.
            label.text = $"<mark=#000000A0>{Fps:F0} FPS | {FrameTimeMs:F1} ms" +
                         $" | 1% {cachedLowFps:F0} FPS</mark>";
        }

        /// <summary>
        /// Mean frame time of the worst one percent of the window, which is
        /// what hardware reviews call the one percent low. Walks the buckets
        /// from the slow end instead of sorting the window.
        /// </summary>
        float WorstPercentMs()
        {
            int wanted = Mathf.Max(1, count / 100);
            int taken = 0;
            double sum = 0d;

            for (int i = BucketCount; i >= 0 && taken < wanted; i--)
            {
                int inBucket = bucketCount[i];
                if (inBucket == 0)
                    continue;

                int take = Mathf.Min(inBucket, wanted - taken);

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
        /// window would quietly become shorter than windowSeconds.
        /// </summary>
        void Grow()
        {
            var bigger = new float[Mathf.Min(frameMs.Length * 2, maxCapacity)];
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

        void BuildUi()
        {
            var canvasGo = new GameObject("Overlay Canvas");
            canvasGo.transform.SetParent(transform, false);

            canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            // No GraphicRaycaster on purpose, or the overlay would swallow
            // clicks meant for the game. No CanvasScaler either: a debug
            // readout stays pixel exact instead of growing with resolution.
            var textGo = new GameObject("Readout");
            textGo.transform.SetParent(canvasGo.transform, false);

            label = textGo.AddComponent<TextMeshProUGUI>();
            label.fontSize = fontSize;
            label.color = Color.white;
            label.alignment = TextAlignmentOptions.TopLeft;
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.raycastTarget = false;

            RectTransform rect = label.rectTransform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(margin, -margin);
            rect.sizeDelta = new Vector2(420f, fontSize * 2f);
        }
    }
}
