using UnityEngine;
using UnityEngine.UI;

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

        // Caps the ring buffer. Above this rate the window shortens instead
        // of the buffer growing without bound.
        [SerializeField] int maxTrackedFps = 500;

        [Header("Anzeige")]
        [SerializeField] float refreshInterval = 0.25f;
        [SerializeField] int fontSize = 16;
        [SerializeField] float margin = 8f;

        // The first frames after a scene load take hundreds of milliseconds
        // and would dominate the one percent low for a whole window.
        const int SkippedFrames = 5;

        // 0.1 ms per bucket over 0 to 100 ms, plus one bucket for anything
        // slower. That is the resolution of the one percent low.
        const float BucketMs = 0.1f;
        const int BucketCount = 1000;

        float[] frameMs;
        int[] bucketCount;
        double[] bucketSum;

        int head;        // oldest sample
        int count;
        double sumMs;
        float windowMs;
        int skipFrames;

        Canvas canvas;
        Text label;
        float sinceRefresh;

        public float FrameTimeMs => count > 0 ? (float)(sumMs / count) : 0f;
        public float Fps => count > 0 ? 1000f / FrameTimeMs : 0f;
        public float OnePercentLowFps => count > 0 ? 1000f / WorstPercentMs() : 0f;
        public int SampleCount => count;

        void Awake()
        {
            windowMs = windowSeconds * 1000f;
            skipFrames = SkippedFrames;

            int capacity = Mathf.Max(64, Mathf.CeilToInt(windowSeconds * maxTrackedFps));
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

            label.text = $"{Fps:F0} FPS | {FrameTimeMs:F1} ms" +
                         $" | 1% {OnePercentLowFps:F0} FPS";
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
                Pop();

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

            label = textGo.AddComponent<Text>();
            label.font = BuiltinFont();
            label.fontSize = fontSize;
            label.color = Color.white;
            label.alignment = TextAnchor.UpperLeft;
            label.horizontalOverflow = HorizontalWrapMode.Overflow;
            label.verticalOverflow = VerticalWrapMode.Overflow;
            label.raycastTarget = false;

            // Readable against both the sky and the grey rock.
            var shadow = textGo.AddComponent<Shadow>();
            shadow.effectColor = new Color(0f, 0f, 0f, 0.85f);
            shadow.effectDistance = new Vector2(1f, -1f);

            RectTransform rect = label.rectTransform;
            rect.anchorMin = new Vector2(0f, 1f);
            rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(margin, -margin);
            rect.sizeDelta = new Vector2(400f, fontSize * 2f);
        }

        /// <summary>
        /// The font Unity ships inside the editor, so the overlay needs no
        /// font asset in the project. Called LegacyRuntime.ttf since 2022,
        /// Arial.ttf before that.
        /// </summary>
        static Font BuiltinFont()
        {
            Font font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

            // Explicit == null: Unity's fake null makes ?? and is null lie.
            if (font == null)
                font = Resources.GetBuiltinResource<Font>("Arial.ttf");

            return font;
        }
    }
}
