using UnityEngine;

namespace KotH.Shared.Player
{
    /// <summary>
    /// Bewegt die Spielerfigur. Die einzige Stelle im Projekt, die den
    /// <see cref="CharacterController"/> anfasst.
    ///
    /// Liest selbst keine Eingabe und kennt keine Kamera — beides gäbe es auf
    /// einem dedizierten Server nicht. Stattdessen bekommt der Motor pro Tick
    /// ein fertiges <see cref="MoveInput"/> gereicht.
    ///
    /// Der gesamte veränderliche Zustand steckt in drei Feldern (velocity,
    /// isCrouching, grounded), damit er sich später für Reconciliation sichern
    /// und zurückspielen lässt.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class PlayerMotor : MonoBehaviour
    {
        [Header("Tempo (m/s)")]
        [SerializeField] float walkSpeed = 4.5f;
        [SerializeField] float sprintSpeed = 7.5f;
        [SerializeField] float crouchSpeed = 2f;

        [Header("Sprung und Schwerkraft")]
        [SerializeField] float jumpHeight = 1.1f;

        // Bewusst rund die doppelte Erdbeschleunigung. Mit -9.81 fühlt sich ein
        // Sprung schwebend an; das machen praktisch alle Shooter so. Die
        // Projekteinstellung bleibt unberührt, die gilt weiter für Rigidbodies.
        [SerializeField] float gravity = -20f;

        [Header("Körpermaße (m)")]
        [SerializeField] float standHeight = 2f;
        [SerializeField] float crouchHeight = 1.2f;
        [SerializeField] float standEyeHeight = 1.7f;
        [SerializeField] float crouchEyeHeight = 1f;
        [SerializeField] float eyeMoveSpeed = 6f;
        [SerializeField] LayerMask headroomMask = ~0;

        [Header("Verknüpfung")]
        [SerializeField] Transform cameraMount;

        CharacterController controller;

        // --- veränderlicher Zustand ---
        Vector3 velocity;
        bool isCrouching;
        bool grounded;

        // --- nur für die Kamera-Interpolation, keine Simulation ---
        Vector3 eyePosition;
        Vector3 previousEyePosition;

        // Vorab angelegt, damit die Kopffreiheitsprüfung pro Tick nichts alloziert.
        readonly Collider[] headroomHits = new Collider[8];

        public Vector3 EyePosition => eyePosition;
        public Vector3 PreviousEyePosition => previousEyePosition;
        public bool IsGrounded => grounded;
        public bool IsCrouching => isCrouching;

        void Awake()
        {
            // GetComponent gehört hierher — nicht in Tick und nicht in Update.
            controller = GetComponent<CharacterController>();

            ApplyHeight(standHeight);
            if (cameraMount != null)
                cameraMount.localPosition = EyeLocalPosition(standEyeHeight);

            eyePosition = previousEyePosition = CurrentEyeWorldPosition();
        }

        /// <summary>
        /// Ein Simulationsschritt. Wird mit festem dt aufgerufen (40 Hz), damit
        /// Client und Server später denselben Schritt rechnen können.
        /// </summary>
        public void Tick(in MoveInput input, float dt)
        {
            previousEyePosition = eyePosition;

            // Yaw dreht den Körper. Pitch gehört allein der Kamera, sonst würde
            // sich die Figur beim Hochschauen nach hinten kippen.
            transform.rotation = Quaternion.Euler(0f, input.Yaw, 0f);

            // isGrounded spiegelt das Ergebnis des letzten Move wider.
            grounded = controller.isGrounded;
            if (grounded && velocity.y < 0f)
                velocity.y = -2f; // drückt die Figur an den Boden, hält sie an Hängen

            UpdateCrouch(input.Crouch, dt);

            // Ohne Begrenzung wäre diagonales Laufen um Faktor 1,41 schneller.
            Vector2 move = Vector2.ClampMagnitude(input.Move, 1f);
            Vector3 wish = transform.right * move.x + transform.forward * move.y;

            float speed = isCrouching ? crouchSpeed
                        : input.Sprint ? sprintSpeed
                        : walkSpeed;

            velocity.x = wish.x * speed;
            velocity.z = wish.z * speed;

            // Über die Höhe statt über einen Impuls: so steht im Inspector eine
            // Zahl, die man nachmessen kann.
            if (input.Jump && grounded && !isCrouching)
                velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);

            velocity.y += gravity * dt;

            controller.Move(velocity * dt);

            eyePosition = CurrentEyeWorldPosition();
        }

        void UpdateCrouch(bool wantsCrouch, float dt)
        {
            if (wantsCrouch && !isCrouching)
            {
                isCrouching = true;
                ApplyHeight(crouchHeight);
            }
            else if (!wantsCrouch && isCrouching && HasHeadroom())
            {
                isCrouching = false;
                ApplyHeight(standHeight);
            }

            if (cameraMount == null)
                return;

            Vector3 target = EyeLocalPosition(isCrouching ? crouchEyeHeight : standEyeHeight);
            cameraMount.localPosition =
                Vector3.MoveTowards(cameraMount.localPosition, target, eyeMoveSpeed * dt);
        }

        /// <summary>
        /// Setzt die Höhe und verschiebt den Mittelpunkt so, dass die Füße
        /// stehen bleiben. Ohne die Korrektur würde die Figur beim Ducken
        /// einsinken oder in den Boden rutschen.
        /// </summary>
        void ApplyHeight(float height)
        {
            controller.height = height;
            controller.center = new Vector3(0f, (height - standHeight) * 0.5f, 0f);
        }

        /// <summary>
        /// Passt die stehende Kapsel an die jetzige Position, ohne dass etwas im
        /// Weg ist? Der eigene Controller zählt dabei nicht mit.
        /// </summary>
        bool HasHeadroom()
        {
            float radius = controller.radius;
            Vector3 feet = transform.position
                         + Vector3.up * (controller.center.y - controller.height * 0.5f);

            Vector3 bottom = feet + Vector3.up * radius;
            Vector3 top = feet + Vector3.up * (standHeight - radius);

            // Radius leicht verkleinert, sonst streift die Prüfung Wände,
            // an denen man gerade entlangläuft.
            int count = Physics.OverlapCapsuleNonAlloc(
                bottom, top, radius * 0.95f, headroomHits,
                headroomMask, QueryTriggerInteraction.Ignore);

            for (int i = 0; i < count; i++)
            {
                if (headroomHits[i] != controller)
                    return false;
            }

            return true;
        }

        Vector3 EyeLocalPosition(float eyeHeightAboveFeet)
            => new Vector3(0f, eyeHeightAboveFeet - standHeight * 0.5f, 0f);

        Vector3 CurrentEyeWorldPosition()
            => cameraMount != null ? cameraMount.position : transform.position;
    }
}
