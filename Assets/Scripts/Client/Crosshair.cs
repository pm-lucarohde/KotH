using UnityEngine;
using UnityEngine.UI;

namespace KotH.Client
{
    /// <summary>
    /// A dot with four diagonal arms, drawn in code so it needs no sprite
    /// asset. Presentation only: it reads nothing and is never read. Sizes
    /// are in pixels and stay in pixels, like the performance readout --
    /// an aiming mark that grows with the window is a different mark.
    /// </summary>
    public sealed class Crosshair : MonoBehaviour
    {
        [Header("Farben")]
        [SerializeField] Color color = Color.white;

        // A dark backing behind every part, so the mark stays readable on
        // sky and on pale rock without a second mechanism.
        [SerializeField] Color outlineColor = new Color(0f, 0f, 0f, 0.65f);
        [SerializeField] float outline = 1f;

        [Header("Maße (Pixel)")]
        [SerializeField] float dotSize = 3f;
        [SerializeField] float armLength = 7f;
        [SerializeField] float armThickness = 2f;

        // Distance from the centre to where an arm starts.
        [SerializeField] float gap = 4f;

        // 32 px is far more than the mark is ever drawn at, so the disc
        // stays smooth however large the dot is set.
        const int DiscPixels = 32;

        static readonly float[] Angles = { 45f, 135f, 225f, 315f };

        void Awake()
        {
            var canvasGo = new GameObject("Crosshair Canvas");
            canvasGo.transform.SetParent(transform, false);

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;

            // Below the performance readout, which sits at 100.
            canvas.sortingOrder = 90;

            Sprite disc = MakeDisc();

            AddPart(canvasGo.transform, disc, Vector2.zero, 0f,
                    new Vector2(dotSize, dotSize));

            for (int i = 0; i < Angles.Length; i++)
            {
                float rad = Angles[i] * Mathf.Deg2Rad;
                Vector2 dir = new Vector2(Mathf.Cos(rad), Mathf.Sin(rad));

                AddPart(canvasGo.transform, null,
                        dir * (gap + armLength * 0.5f), Angles[i],
                        new Vector2(armLength, armThickness));
            }
        }

        /// <summary>
        /// One part of the mark: a dark rectangle a hair larger, and the
        /// bright one on top of it. Added in that order, because a Canvas
        /// draws its children back to front.
        /// </summary>
        void AddPart(Transform parent, Sprite sprite, Vector2 position,
                     float angle, Vector2 size)
        {
            if (outline > 0f)
                AddImage(parent, sprite, position, angle,
                         size + new Vector2(outline, outline) * 2f, outlineColor);

            AddImage(parent, sprite, position, angle, size, color);
        }

        void AddImage(Transform parent, Sprite sprite, Vector2 position,
                      float angle, Vector2 size, Color tint)
        {
            var go = new GameObject("Part");
            go.transform.SetParent(parent, false);

            var image = go.AddComponent<Image>();
            image.sprite = sprite;
            image.color = tint;

            // No raycast target anywhere: the mark must never swallow a click.
            image.raycastTarget = false;

            RectTransform rect = image.rectTransform;
            rect.anchorMin = rect.anchorMax = rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            rect.localRotation = Quaternion.Euler(0f, 0f, angle);
        }

        /// <summary>
        /// A filled circle with one pixel of soft edge, built once. Without
        /// the soft edge the dot reads as a tiny square at small sizes.
        /// </summary>
        static Sprite MakeDisc()
        {
            var tex = new Texture2D(DiscPixels, DiscPixels,
                                    TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode = TextureWrapMode.Clamp;

            float r = DiscPixels * 0.5f;

            for (int y = 0; y < DiscPixels; y++)
            {
                for (int x = 0; x < DiscPixels; x++)
                {
                    float dx = x + 0.5f - r;
                    float dy = y + 0.5f - r;
                    float alpha = Mathf.Clamp01(r - Mathf.Sqrt(dx * dx + dy * dy));
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }

            tex.Apply();

            return Sprite.Create(tex, new Rect(0f, 0f, DiscPixels, DiscPixels),
                                 new Vector2(0.5f, 0.5f));
        }
    }
}
