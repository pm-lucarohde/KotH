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
    /// Der gesamte veränderliche Zustand steckt in wenigen Feldern, damit er
    /// sich später für Reconciliation sichern und zurückspielen lässt.
    /// </summary>
    [RequireComponent(typeof(CharacterController))]
    public sealed class PlayerMotor : MonoBehaviour
    {
        [Header("Tempo (m/s)")]
        [SerializeField] float walkSpeed = 5.175f;
        [SerializeField] float sprintSpeed = 8.625f;
        [SerializeField] float crouchSpeed = 2.1f;

        [Header("Trägheit (m/s²)")]
        [SerializeField] float groundAcceleration = 50f;
        [SerializeField] float groundDeceleration = 35f;

        // 15 % der Bodenbeschleunigung. Reicht zum Nachkorrigieren im Sprung,
        // aber bei normaler Flugzeit nicht, um die Richtung umzukehren.
        [SerializeField] float airAcceleration = 7.5f;

        [Header("Sprung und Schwerkraft")]
        [SerializeField] float jumpHeight = 1.5f;
        [SerializeField] float doubleJumpHeight = 1f;
        [SerializeField] float doubleTapWindow = 0.25f;

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

        [Header("Diagnose")]
        [Tooltip("Protokolliert die erreichte Sprunghöhe. Nur zum Abnehmen gedacht.")]
        [SerializeField] bool logJumpStats = true;

        CharacterController controller;

        // --- veränderlicher Zustand ---
        Vector3 velocity;
        bool isCrouching;
        bool grounded;
        bool doubleJumpUsed;      // pro Flug nur einmal
        bool hasPendingAirPress;  // erster Luft-Tipp wartet auf seinen Partner
        float timeSinceAirPress;

        // --- nur für die Kamera-Interpolation, keine Simulation ---
        Vector3 eyePosition;
        Vector3 previousEyePosition;

        // --- nur für die Diagnose ---
        float jumpStartY;
        bool trackingApex;

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
            if (grounded)
            {
                if (velocity.y < 0f)
                    velocity.y = -2f; // drückt die Figur an den Boden, hält sie an Hängen

                doubleJumpUsed = false;
                hasPendingAirPress = false;
            }
            else if (hasPendingAirPress)
            {
                timeSinceAirPress += dt;
                if (timeSinceAirPress > doubleTapWindow)
                    hasPendingAirPress = false;
            }

            UpdateCrouch(input.Crouch, dt);
            UpdateHorizontal(input, dt);
            ProcessJumpPresses(input.JumpPresses);

            velocity.y += gravity * dt;

            controller.Move(velocity * dt);

            ReportApex();
            eyePosition = CurrentEyeWorldPosition();
        }

        void UpdateHorizontal(in MoveInput input, float dt)
        {
            // Ohne Begrenzung wäre diagonales Laufen um Faktor 1,41 schneller.
            Vector2 move = Vector2.ClampMagnitude(input.Move, 1f);
            bool hasInput = move.sqrMagnitude > 0.0001f;

            Vector3 wish = transform.right * move.x + transform.forward * move.y;

            float speed = isCrouching ? crouchSpeed
                        : input.Sprint ? sprintSpeed
                        : walkSpeed;

            // Ohne Eingabe in der Luft: Rate 0, der Schwung bleibt erhalten.
            // Alles andere wäre eine versteckte Luftbremse beim Loslassen.
            float rate = grounded
                ? (hasInput ? groundAcceleration : groundDeceleration)
                : (hasInput ? airAcceleration : 0f);

            Vector3 current = new Vector3(velocity.x, 0f, velocity.z);
            Vector3 next = Vector3.MoveTowards(current, wish * speed, rate * dt);

            velocity.x = next.x;
            velocity.z = next.z;
        }

        /// <summary>
        /// Boden- und Doppelsprung. Der Zähler kann größer als 1 sein, wenn
        /// innerhalb eines Ticks (25 ms) mehrfach gedrückt wurde.
        /// </summary>
        void ProcessJumpPresses(int presses)
        {
            for (int i = 0; i < presses; i++)
            {
                if (grounded && !isCrouching)
                {
                    velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
                    grounded = false;
                    doubleJumpUsed = false;

                    // Der Absprung zählt ausdrücklich NICHT als erster Tipp
                    // der Doppelsprung-Geste.
                    hasPendingAirPress = false;

                    BeginApexTracking();
                    continue;
                }

                // Am Boden geduckt: kein Sprung, und der Druck darf auch nicht
                // als erster Tipp der Luft-Geste zählen.
                if (grounded)
                    continue;

                if (!hasPendingAirPress)
                {
                    // Erster Tipp in der Luft: nur vormerken.
                    hasPendingAirPress = true;
                    timeSinceAirPress = 0f;
                    continue;
                }

                // Zweiter Tipp innerhalb des Fensters. velocity.y >= 0 deckt
                // beide erlaubten Fälle ab: im Steigflug oder exakt am
                // Scheitel. Im Sinkflug ist die Bedingung dauerhaft falsch —
                // und wird es ohne Bodenkontakt auch nicht mehr wahr.
                if (!doubleJumpUsed && velocity.y >= 0f)
                {
                    velocity.y = Mathf.Sqrt(doubleJumpHeight * -2f * gravity);
                    doubleJumpUsed = true;
                }

                hasPendingAirPress = false;
            }
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

        void BeginApexTracking()
        {
            if (!logJumpStats)
                return;

            jumpStartY = transform.position.y;
            trackingApex = true;
        }

        /// <summary>
        /// Meldet die Scheitelhöhe, sobald die Vertikalgeschwindigkeit das
        /// Vorzeichen wechselt. Reine Diagnose, fliegt nach der Abnahme raus.
        /// </summary>
        void ReportApex()
        {
            if (!trackingApex || velocity.y >= 0f)
                return;

            trackingApex = false;
            Debug.Log($"Sprunghöhe: {transform.position.y - jumpStartY:0.00} m"
                    + (doubleJumpUsed ? " (mit Doppelsprung)" : ""));
        }

        Vector3 EyeLocalPosition(float eyeHeightAboveFeet)
            => new Vector3(0f, eyeHeightAboveFeet - standHeight * 0.5f, 0f);

        Vector3 CurrentEyeWorldPosition()
            => cameraMount != null ? cameraMount.position : transform.position;
    }
}
