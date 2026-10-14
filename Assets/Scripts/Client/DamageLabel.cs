using KotH.Shared.Combat;
using KotH.Shared.Player;
using TMPro;
using UnityEngine;

namespace KotH.Client
{
    /// <summary>
    /// Shows a body's damage percent in the world above its head. Presentation
    /// only: it reads the value and never writes one. It interpolates its own
    /// position instead of following the rendered capsule, whose LateUpdate
    /// runs in no defined order relative to this one.
    /// </summary>
    public sealed class DamageLabel : MonoBehaviour
    {
        [Header("Ziel")]
        [SerializeField] DamagePercent target;
        [SerializeField] PlayerMotor motor;

        [Header("Anzeige")]
        [Tooltip("Abstand über dem Scheitel, in Metern.")]
        [SerializeField] float offset = 0.35f;

        [Tooltip("Schriftgröße in Weltmaßen.")]
        [SerializeField] float fontSize = 1.2f;

        [SerializeField] Color color = Color.white;

        TextMeshPro label;
        float shown = float.NaN;

        /// <summary>The text object, so a probe can measure the font.</summary>
        public TextMeshPro Label => label;

        void Awake()
        {
            label = gameObject.AddComponent<TextMeshPro>();
            label.fontSize = fontSize;
            label.color = color;
            label.alignment = TextAlignmentOptions.Center;

            // No wrapping and a rect wide enough for "1000,0 %", otherwise
            // the line breaks as the number grows a digit.
            label.textWrappingMode = TextWrappingModes.NoWrap;
            label.rectTransform.sizeDelta = new Vector2(4f, 1f);

            // Explicit == null, Unity's fake null breaks the shorthand.
            if (target == null || motor == null)
                Debug.LogWarning("DamageLabel ohne Ziel bleibt leer.", this);
        }

        void LateUpdate()
        {
            if (target == null || motor == null)
                return;

            // The same formula the visual job uses, rather than reading the
            // interpolated capsule: that way no execution order matters.
            Vector3 center = Vector3.Lerp(
                motor.PreviousBodyCenter, motor.BodyCenter, TickInterpolation.Alpha);

            transform.position =
                center + Vector3.up * (motor.Height * 0.5f + offset);

            Camera cam = Camera.main;
            if (cam != null)
                transform.rotation = Quaternion.LookRotation(
                    transform.position - cam.transform.position, Vector3.up);

            // Rewritten only when the number moved, so a still dummy costs
            // nothing per frame.
            float value = target.Percent;
            if (value != shown)
            {
                shown = value;
                label.text = value.ToString("F1") + " %";
            }
        }
    }
}
