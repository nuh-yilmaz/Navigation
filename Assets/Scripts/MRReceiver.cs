using System;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;

/// <summary>
/// Receives TR pulses from the scanner and feeds them to ExperimentClock.
///
/// Most scanner trigger interfaces (including Current Designs boxes) present
/// themselves as an HID keyboard and emit a single key per volume — commonly "5"
/// on Siemens or "t" on some GE/Philips setups. Confirm which your box sends and
/// set triggerKey accordingly; there is no safe default that works everywhere.
///
/// The experiment does not begin until the first trigger arrives, so stimulus
/// onset is locked to volume acquisition rather than to whenever the operator
/// happened to press Play.
/// </summary>
public class MRTriggerReceiver : MonoBehaviour
{
    [Header("Trigger Source")]
    [Tooltip("Keyboard key the scanner trigger interface emits per volume. Siemens is commonly Digit5; verify against your own hardware.")]
    [SerializeField] private Key triggerKey = Key.Digit5;

    [Tooltip("Optional: also accept a joystick button as a trigger (leave blank to disable). Only use if your trigger arrives on the response device.")]
    [SerializeField] private string triggerJoystickButton = "";

    [Header("Diagnostics")]
    [Tooltip("Expected TR in seconds. Used only to warn about missed/extra triggers — does not affect timing.")]
    [SerializeField] private float expectedTR = 2.0f;

    [Tooltip("Warn if an inter-trigger interval deviates from expectedTR by more than this fraction.")]
    [SerializeField] private float trToleranceFraction = 0.25f;

    [Header("Status (read-only)")]
    [SerializeField] private int triggersReceived;
    [SerializeField] private double lastInterval;

    /// <summary>Raised on every TR pulse. Subscribers get the trigger index.</summary>
    public event Action<int> OnTrigger;

    /// <summary>Raised once, on the very first TR pulse of the run.</summary>
    public event Action OnFirstTrigger;

    private double previousTriggerTime = -1.0;

    private void Update()
    {
        if (!TriggerPressedThisFrame()) return;

        bool wasFirst = !ExperimentClock.Started;

        ExperimentClock.RegisterTrigger();
        triggersReceived = ExperimentClock.TriggerCount;

        CheckInterval();

        if (wasFirst)
        {
            Debug.Log("MRTriggerReceiver: first scanner trigger received — experiment clock started.");
            OnFirstTrigger?.Invoke();
        }

        OnTrigger?.Invoke(triggersReceived);
    }

    private bool TriggerPressedThisFrame()
    {
        if (Keyboard.current != null && Keyboard.current[triggerKey].wasPressedThisFrame)
        {
            return true;
        }

        if (!string.IsNullOrEmpty(triggerJoystickButton) && Joystick.current != null)
        {
            var button = Joystick.current.GetChildControl<ButtonControl>(triggerJoystickButton);
            if (button != null && button.wasPressedThisFrame) return true;
        }

        return false;
    }

    /// <summary>
    /// Flags irregular inter-trigger intervals. A missed trigger is silent damage:
    /// it corrupts every subsequent volume-relative timestamp, so it needs to be
    /// visible in the console while the participant is still in the scanner.
    /// </summary>
    private void CheckInterval()
    {
        double now = ExperimentClock.TimeSinceStart;

        if (previousTriggerTime >= 0.0)
        {
            lastInterval = now - previousTriggerTime;
            double deviation = Math.Abs(lastInterval - expectedTR);

            if (deviation > expectedTR * trToleranceFraction)
            {
                Debug.LogWarning(
                    $"MRTriggerReceiver: irregular TR interval {lastInterval:F3}s " +
                    $"(expected ~{expectedTR:F3}s) at trigger #{triggersReceived}. " +
                    "Possible missed or spurious trigger.");
            }
        }

        previousTriggerTime = now;
    }
}