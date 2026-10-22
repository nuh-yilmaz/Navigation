using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

/// <summary>
/// Player movement and look, driven by Unity's Input System (mouse/keyboard for
/// look via OnLook, or a joystick's Rx/Ry axes read directly for gamepad look).
/// Also exposes IsInteractTriggered: the single shared "Interact button pressed
/// this frame" signal that Gaze and MoveObjectController both read, so there's
/// exactly one place (the Input Actions asset feeding OnInteract) that defines
/// what "interact" means.
/// Requires CharacterController and PlayerInput components.
/// </summary>
[RequireComponent(typeof(CharacterController))]
[RequireComponent(typeof(PlayerInput))]
public class PlayerController : MonoBehaviour
{
    [Header("Movement Settings")]
    [Tooltip("Player movement speed (units/second).")]
    public float moveSpeed = 2.5f;

    [Tooltip("Gravity force. Should be negative.")]
    public float gravity = -9.81f;

    [Header("Look Settings")]
    [Tooltip("Camera look sensitivity.")]
    public float lookSensitivity = 0.5f;

    public CharacterController m_characterController;

    private Vector2 moveInput;
    private Vector2 lookInput;
    private float verticalVelocity;
    private float pitchRotation;
    private float yawRotation;

    [Header("Interact Input")]
    [Tooltip("Also read the joystick button and key directly, as well as through PlayerInput. Leave ON unless you are certain the Interact action is wired correctly - it costs nothing and removes a whole class of setup failure.")]
    [SerializeField] private bool alsoPollDevicesDirectly = true;

    [Tooltip("Joystick control name for the response button. 'button2' matches the Current Designs response device.")]
    [SerializeField] private string interactJoystickButton = "button2";

    [Tooltip("Keyboard key that also counts as Interact, for testing without the scanner hardware.")]
    [SerializeField] private Key interactKey = Key.T;

    [Tooltip("Logs every Interact press and where it came from.")]
    [SerializeField] private bool debugInput = false;

    private static int lastInteractFrame = -1;
    private static bool interactConsumed = true;

    /// <summary>
    /// True if Interact fired on this frame or the previous one.
    ///
    /// The two-frame window is deliberate. A press can arrive from the Input
    /// System callback (before Update) or from direct polling inside this
    /// component's own Update, and Unity gives no guarantee that this component's
    /// Update runs before a reader's. A one-frame flag would therefore be missed
    /// whenever the reader happened to run first. Readers that must act exactly
    /// once should call ConsumeInteract() instead of reading this.
    /// </summary>
    public static bool IsInteractTriggered =>
        lastInteractFrame >= 0 && Time.frameCount - lastInteractFrame <= 1;

    /// <summary>
    /// Returns true once per press, then false until the next press. Use this
    /// wherever a press must trigger exactly one action.
    /// </summary>
    public static bool ConsumeInteract()
    {
        if (interactConsumed || !IsInteractTriggered) return false;

        interactConsumed = true;
        return true;
    }

    private static void RegisterInteract(string source, bool log)
    {
        lastInteractFrame = Time.frameCount;
        interactConsumed = false;

        if (log) Debug.Log($"PlayerController: Interact pressed via {source} (frame {Time.frameCount}).");
    }

    private bool movementEnabled = true;

    /// <summary>
    /// When false the participant cannot move or look, and the mouse cursor is
    /// released. Used between trials, during breaks, and while the setup screens
    /// are showing.
    ///
    /// The cursor MUST follow this flag: a locked cursor cannot click IMGUI
    /// controls, so locking it while the participant-ID or checklist screen is up
    /// makes those screens impossible to use.
    /// </summary>
    public bool MovementEnabled
    {
        get => movementEnabled;
        set
        {
            movementEnabled = value;
            ApplyCursorState();
        }
    }

    private void ApplyCursorState()
    {
        Cursor.lockState = movementEnabled ? CursorLockMode.Locked : CursorLockMode.None;
        Cursor.visible = !movementEnabled;
    }

    private void Awake()
    {
        m_characterController = GetComponent<CharacterController>();
    }

    private void Start()
    {
        // Start released. ExperimentController locks it when a trial begins by
        // setting MovementEnabled. Locking here would break the setup screens.
        MovementEnabled = false;
    }

    /// <summary>Called by PlayerInput when movement input is received (e.g., Left Stick / WASD).</summary>
    public void OnMove(InputValue val) => moveInput = val.Get<Vector2>();

    /// <summary>Called by PlayerInput when look input is received (e.g., Mouse Delta).</summary>
    public void OnLook(InputValue val) => lookInput = val.Get<Vector2>();

    /// <summary>Called by PlayerInput when the Interact action (button2 / E / etc.) fires.</summary>
    public void OnInteract(InputValue val)
    {
        if (val.isPressed) RegisterInteract("PlayerInput action", debugInput);
    }

    /// <summary>
    /// Reads the response button straight off the device, in addition to the
    /// PlayerInput action. This is the safety net: if the Interact action is not
    /// bound, or PlayerInput's Behavior is not set to Send Messages, OnInteract
    /// is simply never called and the experiment silently cannot be completed.
    /// Polling here means the button works either way.
    /// </summary>
    private void PollInteractDevices()
    {
        if (!alsoPollDevicesDirectly) return;

        if (Keyboard.current != null
            && Keyboard.current[interactKey].wasPressedThisFrame)
        {
            RegisterInteract($"keyboard {interactKey}", debugInput);
            return;
        }

        if (Joystick.current != null && !string.IsNullOrEmpty(interactJoystickButton))
        {
            var button = Joystick.current.GetChildControl<ButtonControl>(interactJoystickButton);
            if (button != null && button.wasPressedThisFrame)
            {
                RegisterInteract($"joystick {interactJoystickButton}", debugInput);
                return;
            }
        }

        if (Gamepad.current != null && Gamepad.current.buttonWest.wasPressedThisFrame)
        {
            RegisterInteract("gamepad buttonWest", debugInput);
        }
    }

    private void Update()
    {
        PollInteractDevices();

        if (!MovementEnabled)
        {
            // Still consume look input so a stick held during the freeze doesn't
            // snap the view the instant the trial starts.
            lookInput = Vector2.zero;
            moveInput = Vector2.zero;
            return;
        }

        HandleJoystickLook();
        ApplyLook();
        ApplyMovementAndGravity();
    }

    /// <summary>
    /// Moves the player to a fixed start pose between trials. CharacterController
    /// must be disabled first — it overrides direct transform writes otherwise,
    /// and the teleport silently fails.
    /// </summary>
    public void TeleportTo(Vector3 position, Quaternion rotation)
    {
        bool wasEnabled = m_characterController.enabled;
        m_characterController.enabled = false;

        transform.SetPositionAndRotation(position, rotation);

        yawRotation = rotation.eulerAngles.y;
        pitchRotation = 0f;
        verticalVelocity = 0f;

        Camera cam = GetComponentInChildren<Camera>();
        if (cam != null) cam.transform.localEulerAngles = Vector3.zero;

        m_characterController.enabled = wasEnabled;
    }

    private void HandleJoystickLook()
    {
        if (Joystick.current == null) return;

        AxisControl rx = Joystick.current.GetChildControl<AxisControl>("rx");
        AxisControl ry = Joystick.current.GetChildControl<AxisControl>("ry");
        if (rx == null || ry == null) return;

        const float deadzone = 0.15f;
        float rawRx = rx.ReadValue();
        float rawRy = ry.ReadValue();

        lookInput = new Vector2(
            Mathf.Abs(rawRx) > deadzone ? rawRx : 0f,
            Mathf.Abs(rawRy) > deadzone ? rawRy : 0f
        );
    }

    private void ApplyLook()
    {
        yawRotation += lookInput.x * lookSensitivity;
        transform.localRotation = Quaternion.Euler(0f, yawRotation, 0f);

        Camera cam = GetComponentInChildren<Camera>();
        if (cam != null)
        {
            pitchRotation = Mathf.Clamp(pitchRotation - lookInput.y * lookSensitivity, -80f, 80f);
            cam.transform.localEulerAngles = new Vector3(pitchRotation, 0f, 0f);
        }

        // Mouse-delta look doesn't hold a value between frames like a joystick
        // axis does, so it needs to be cleared each frame to avoid drifting.
        if (Joystick.current == null)
        {
            lookInput = Vector2.zero;
        }
    }

    private void ApplyMovementAndGravity()
    {
        if (m_characterController.isGrounded && verticalVelocity < 0f)
        {
            verticalVelocity = -2f;
        }
        verticalVelocity += gravity * Time.deltaTime;

        Vector3 forward = transform.forward * moveInput.y;
        Vector3 right = transform.right * moveInput.x;
        Vector3 moveDir = (forward + right).normalized;

        Vector3 velocity = moveDir * moveSpeed + Vector3.up * verticalVelocity;
        m_characterController.Move(velocity * Time.deltaTime);
    }
}