using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// Detects which target the participant is looking at, and turns a button press
/// plus a sustained gaze into a recognition event.
///
/// Timing note: the gaze hold is measured from real timestamps taken at press and
/// at completion, not by accumulating Time.deltaTime. Accumulation is only
/// accurate to a frame and drifts with frame rate; since the hold duration is an
/// experimental parameter, the TRUE elapsed time is measured and logged rather
/// than assuming it was exactly gazeDuration.
/// </summary>
public class Gaze : MonoBehaviour
{
    [Header("References")]
    [SerializeField] private Camera playerCamera;
    [SerializeField] private TrialManager trialManager;

    [Header("Gaze Raycast")]
    [Tooltip("Layers the ray can hit at all — include the target layer AND anything that should occlude a target, e.g. walls.")]
    [SerializeField] private LayerMask raycastLayerMask;
    [SerializeField] private float maxGazeDistance = 2f;

    [Header("Target Layer")]
    [Tooltip("Layer index target colliders sit on. This is the authoritative value used at runtime.")]
    [SerializeField] private int targetLayerNumber = 6;

    [Tooltip("Optional name of that same layer, cross-checked against the number above. A mismatch only warns — the number always wins. Leave blank to skip.")]
    [SerializeField] private string targetLayerName = "Target";

    [Header("Recognition")]
    [Tooltip("Seconds the participant must keep looking at the target after pressing the button.")]
    [SerializeField] private float gazeDuration = 3f;

    [Header("Crosshair")]
    [Tooltip("The red dot Image on your Canvas. Leave empty to skip crosshair colouring.")]
    [SerializeField] private Image crosshair;

    [SerializeField] private Color crosshairNormalColor = Color.red;
    [SerializeField] private Color crosshairRecognizedColor = Color.green;

    [Tooltip("Seconds the crosshair stays green after a recognition.")]
    [SerializeField] private float crosshairGreenDuration = 1f;

    [Header("Debug")]
    [Tooltip("Logs what the gaze ray hits and why it was rejected. Use this while setting up layers and target hierarchy, then turn it off.")]
    [SerializeField] private bool debugGaze = false;

    [Tooltip("Draws the gaze ray in the Scene view while playing.")]
    [SerializeField] private bool drawRay = false;

    [Header("Status (read-only)")]
    [SerializeField] private GameObject currentTarget;
    [SerializeField] private float gazeProgress;

    private int targetLayer;
    private bool isRecognizing;

    /// <summary>Object the gaze ray is currently on, or null.</summary>
    public GameObject CurrentTarget => currentTarget;

    /// <summary>True while a gaze hold is counting down.</summary>
    public bool IsRecognizing => isRecognizing;

    /// <summary>0-1 progress through the gaze hold. 0 when not recognizing.</summary>
    public float GazeProgress => gazeProgress;

    /// <summary>True when looking at a target of this trial that isn't recognized yet.</summary>
    public bool IsLookingAtPendingTarget =>
        trialManager != null && trialManager.IsPendingTrialTarget(currentTarget);

    /// <summary>Raised when a target has been held long enough to count as recognized.</summary>
    public event System.Action<GameObject> OnTargetRecognized;

    private GameObject gazeStartedOnTarget;
    private double gazeStartTime;
    private double responseLatencyAtPress;
    private string screenshotAtPress;
    private string lastDiagnostic;
    private float crosshairRevertAt = -1f;
    private Transform ignoreRoot;
    private readonly RaycastHit[] hitBuffer = new RaycastHit[16];

    private void Awake()
    {
        ResolveReferences();
        ResolveTargetLayer();

        if (crosshair != null) crosshair.color = crosshairNormalColor;
    }

    private void Update()
    {
        UpdateGazeTarget();
        HandleRecognition();
        UpdateCrosshair();

        if (drawRay && playerCamera != null)
        {
            Debug.DrawRay(playerCamera.transform.position,
                          playerCamera.transform.forward * maxGazeDistance,
                          currentTarget != null ? Color.green : Color.red);
        }
    }

    private void ResolveReferences()
    {
        if (playerCamera == null) playerCamera = Camera.main;
        if (playerCamera == null) Debug.LogError("Gaze: no camera assigned and no Camera.main found.");

        if (trialManager == null) trialManager = FindAnyObjectByType<TrialManager>();
        if (trialManager == null) Debug.LogError("Gaze: no TrialManager found in the scene.");

        // Everything under the player is excluded from the gaze ray.
        PlayerController pc = GetComponentInParent<PlayerController>();
        ignoreRoot = pc != null ? pc.transform : transform.root;
    }

    /// <summary>
    /// targetLayerNumber is used as-is. targetLayerName is only a sanity check: a
    /// mismatch or missing name logs a warning but never overrides the number.
    /// </summary>
    private void ResolveTargetLayer()
    {
        targetLayer = targetLayerNumber;

        if (string.IsNullOrEmpty(targetLayerName)) return;

        int byName = LayerMask.NameToLayer(targetLayerName);
        if (byName == -1)
        {
            Debug.LogWarning($"Gaze: no layer named '{targetLayerName}'; continuing with layer number {targetLayerNumber}.");
        }
        else if (byName != targetLayerNumber)
        {
            Debug.LogWarning($"Gaze: targetLayerNumber is {targetLayerNumber} but layer '{targetLayerName}' is index {byName}. Verify these are meant to match.");
        }
    }

    private void UpdateGazeTarget()
    {
        if (playerCamera == null)
        {
            currentTarget = null;
            return;
        }

        Ray ray = new(playerCamera.transform.position, playerCamera.transform.forward);

        if (!RaycastIgnoringPlayer(ray, out RaycastHit hit))
        {
            currentTarget = null;
            Diagnose("ray hit nothing (too far, or raycastLayerMask excludes the target's layer)");
            return;
        }

        GameObject hitObject = hit.collider.gameObject;

        if (hitObject.layer != targetLayer)
        {
            currentTarget = null;
            Diagnose($"hit '{GetPath(hitObject)}' on layer {hitObject.layer} ('{LayerMask.LayerToName(hitObject.layer)}') but targetLayerNumber is {targetLayer} ('{LayerMask.LayerToName(targetLayer)}')");
            return;
        }

        currentTarget = hitObject;

        // Layer is right, so check the trial side too: the object must be, or sit
        // under, a direct child of the currently active trial group.
        if (trialManager != null && trialManager.ResolveTarget(hitObject) == null)
        {
            Diagnose($"hit '{GetPath(hitObject)}' on the correct layer, but it is not part of the active trial group '{trialManager.CurrentGroupName}' (targets are the group's DIRECT children)");
        }
        else
        {
            Diagnose($"looking at '{GetPath(hitObject)}' - ready for button press");
        }
    }

    /// <summary>
    /// Nearest hit that is NOT part of the player's own hierarchy.
    ///
    /// The camera sits inside the player's CharacterController capsule, so a plain
    /// Physics.Raycast returns the player itself as the first hit whenever the
    /// player's layer is included in raycastLayerMask - and it usually must be,
    /// since the same mask decides what blocks the view. Skipping the player's own
    /// colliders here means this works regardless of which layer the player is on.
    ///
    /// Allocation-free: RaycastNonAlloc fills a reusable buffer. Results are NOT
    /// distance-sorted, so the nearest valid hit is found by comparison rather
    /// than by taking the first entry.
    /// </summary>
    private bool RaycastIgnoringPlayer(Ray ray, out RaycastHit nearest)
    {
        nearest = default;

        int count = Physics.RaycastNonAlloc(ray, hitBuffer, maxGazeDistance, raycastLayerMask);
        if (count == 0) return false;

        if (count == hitBuffer.Length)
        {
            Debug.LogWarning($"Gaze: raycast hit buffer full ({hitBuffer.Length}); a nearer target may have been missed. Increase the buffer size.");
        }

        float best = float.MaxValue;
        bool found = false;

        for (int i = 0; i < count; i++)
        {
            if (ignoreRoot != null && hitBuffer[i].collider.transform.IsChildOf(ignoreRoot)) continue;
            if (hitBuffer[i].distance >= best) continue;

            best = hitBuffer[i].distance;
            nearest = hitBuffer[i];
            found = true;
        }

        return found;
    }

    /// <summary>
    /// Throttled console output for setup debugging. Only logs when the message
    /// changes, so the console shows the current reason rather than one line per
    /// frame. Turn debugGaze off before running participants.
    /// </summary>
    private void Diagnose(string message)
    {
        if (!debugGaze) return;
        if (message == lastDiagnostic) return;

        lastDiagnostic = message;
        Debug.Log($"Gaze: {message}");
    }

    private static string GetPath(GameObject obj)
    {
        string path = obj.name;
        for (Transform t = obj.transform.parent; t != null; t = t.parent)
        {
            path = t.name + "/" + path;
        }
        return path;
    }

    private void HandleRecognition()
    {
        var controller = ExperimentController.Instance;
        bool trialActive = controller != null
            && (controller.State == SessionState.Running || controller.State == SessionState.Practice);

        if (!trialActive)
        {
            if (isRecognizing) CancelGaze();
            return;
        }

        // Looking away from the target being timed cancels it — gaze time never
        // carries over from one target to another.
        if (isRecognizing && currentTarget != gazeStartedOnTarget)
        {
            CancelGaze();
        }

        if (!trialManager.IsPendingTrialTarget(currentTarget))
        {
            if (isRecognizing) CancelGaze();
            return;
        }

        if (!isRecognizing && PlayerController.ConsumeInteract())
        {
            StartGaze();
        }

        if (isRecognizing)
        {
            double elapsed = ExperimentClock.Now - gazeStartTime;
            gazeProgress = Mathf.Clamp01((float)(elapsed / gazeDuration));

            if (elapsed >= gazeDuration)
            {
                CompleteGaze(elapsed);
            }
        }
    }

    private void StartGaze()
    {
        isRecognizing = true;
        gazeStartedOnTarget = currentTarget;
        gazeStartTime = ExperimentClock.Now;
        gazeProgress = 0f;

        responseLatencyAtPress = ExperimentController.Instance.TrialTime;

        GameObject group = trialManager.ResolveTarget(currentTarget);
        string label = group != null ? group.name : currentTarget.name;

        // Captured on the response frame itself; encoding/writing happens off the
        // main thread so this press costs no extra frame time.
        screenshotAtPress = ScreenshotCapture.Instance != null
            ? ScreenshotCapture.Instance.CaptureNow(label)
            : "";

        ExperimentController.Instance.ReportResponseOnset(
            label, trialManager.IndexOfTarget(group), screenshotAtPress);
    }

    /// <summary>Returns the crosshair to its normal colour once the green period ends.</summary>
    private void UpdateCrosshair()
    {
        if (crosshair == null || crosshairRevertAt < 0f) return;
        if (Time.unscaledTime < crosshairRevertAt) return;

        crosshair.color = crosshairNormalColor;
        crosshairRevertAt = -1f;
    }

    private void CancelGaze()
    {
        isRecognizing = false;
        gazeStartedOnTarget = null;
        gazeProgress = 0f;
        screenshotAtPress = "";
    }

    private void CompleteGaze(double actualDuration)
    {
        GameObject completed = gazeStartedOnTarget;
        string file = screenshotAtPress;
        double latency = responseLatencyAtPress;

        CancelGaze();

        trialManager.NotifyRecognized(completed, latency, actualDuration, file);
        OnTargetRecognized?.Invoke(completed);

        if (crosshair != null)
        {
            crosshair.color = crosshairRecognizedColor;
            crosshairRevertAt = Time.unscaledTime + crosshairGreenDuration;
        }
    }
}