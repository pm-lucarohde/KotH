using KotH.Shared.Player;
using UnityEngine;

namespace KotH.Client.Player
{
    /// <summary>
    /// Setzt die Kamera auf den Augenpunkt des Spielers.
    ///
    /// Bewusst KEIN Kind des Spielers: der Motor bewegt die Figur in 40
    /// Sprüngen pro Sekunde, ein mitbewegtes Kind würde genau diese 40 Sprünge
    /// zeigen — sichtbares Ruckeln, egal wie hoch die Bildrate ist. Stattdessen
    /// wird hier zwischen den letzten beiden Tick-Positionen interpoliert.
    ///
    /// LateUpdate, damit die Kamera nach aller Bewegung des Bildes läuft.
    /// </summary>
    public sealed class FirstPersonCamera : MonoBehaviour
    {
        [SerializeField] PlayerMotor motor;
        [SerializeField] PlayerInputReader input;

        void LateUpdate()
        {
            if (motor == null || input == null)
                return;

            // Wie weit ist der aktuelle Tick fortgeschritten? Time.fixedTime ist
            // der Zeitpunkt des letzten FixedUpdate.
            float alpha = Mathf.Clamp01(
                (Time.time - Time.fixedTime) / Time.fixedDeltaTime);

            transform.SetPositionAndRotation(
                Vector3.Lerp(motor.PreviousEyePosition, motor.EyePosition, alpha),
                Quaternion.Euler(input.Pitch, input.Yaw, 0f));
        }
    }
}
