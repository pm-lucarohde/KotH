using KotH.Shared.Player;
using UnityEngine;

namespace KotH.Client.Player
{
    /// <summary>
    /// Übersetzt Tastatur und Maus in ein <see cref="MoveInput"/> und reicht es
    /// einmal pro Tick an den <see cref="PlayerMotor"/>.
    ///
    /// Diese Klasse ist die einzige im Projekt, die <c>Input.*</c> anfasst. Beim
    /// Umstieg auf das neue Input System oder beim Ersetzen durch Netzwerkpakete
    /// ändert sich genau eine Datei.
    /// </summary>
    [RequireComponent(typeof(PlayerMotor))]
    public sealed class PlayerInputReader : MonoBehaviour
    {
        [Header("Maus")]
        [SerializeField] float mouseSensitivity = 2f;
        [SerializeField] float minPitch = -89f;
        [SerializeField] float maxPitch = 89f;

        [Header("Tasten")]
        [SerializeField] KeyCode sprintKey = KeyCode.LeftShift;
        [SerializeField] KeyCode crouchKey = KeyCode.LeftControl;

        PlayerMotor motor;

        float yaw;
        float pitch;
        byte jumpPresses;

        public float Yaw => yaw;
        public float Pitch => pitch;

        void Awake()
        {
            motor = GetComponent<PlayerMotor>();
            yaw = transform.eulerAngles.y;
            SetCursorLocked(true);
        }

        void OnDisable() => SetCursorLocked(false);

        // Update läuft pro Bild, FixedUpdate pro Tick. Bei 144 fps und 40 Hz
        // kommen auf einen Tick rund dreieinhalb Bilder — alles, was pro Bild
        // anfällt, muss deshalb hier eingesammelt werden.
        void Update()
        {
            if (Input.GetKeyDown(KeyCode.Escape))
                SetCursorLocked(false);
            else if (Input.GetMouseButtonDown(0) && Cursor.lockState != CursorLockMode.Locked)
                SetCursorLocked(true);

            if (Cursor.lockState == CursorLockMode.Locked)
            {
                // Die Mausachsen liefern bereits ein Delta seit dem letzten Bild.
                // Deshalb KEIN Time.deltaTime — das ist der häufigste Fehler in
                // Unity-Maussteuerungen und macht den Blick framerate-abhängig.
                yaw += Input.GetAxisRaw("Mouse X") * mouseSensitivity;
                pitch = Mathf.Clamp(
                    pitch - Input.GetAxisRaw("Mouse Y") * mouseSensitivity,
                    minPitch, maxPitch);
            }

            // GetButtonDown ist nur in einem einzigen Bild wahr. Läuft in genau
            // diesem Bild kein FixedUpdate, wäre der Druck sonst verloren.
            // Gezählt statt gemerkt, damit auch zwei Drücke innerhalb eines
            // Ticks (25 ms) beide ankommen — der Doppelsprung hängt daran.
            if (Input.GetButtonDown("Jump") && jumpPresses < byte.MaxValue)
                jumpPresses++;
        }

        void FixedUpdate()
        {
            var input = new MoveInput
            {
                Move = new Vector2(
                    Input.GetAxisRaw("Horizontal"),
                    Input.GetAxisRaw("Vertical")),
                Yaw = yaw,
                Pitch = pitch,
                JumpPresses = jumpPresses,
                // Dauerzustände dürfen hier direkt abgefragt werden: GetKey ist
                // über mehrere Bilder stabil, anders als GetKeyDown.
                Sprint = Input.GetKey(sprintKey),
                Crouch = Input.GetKey(crouchKey),
            };

            jumpPresses = 0;

            motor.Tick(input, Time.fixedDeltaTime);
        }

        static void SetCursorLocked(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }
    }
}
