using UnityEngine;

/// <summary>
/// Operator- and participant-facing screens: participant ID entry before the run,
/// a "waiting for scanner" screen, break screens, and an explicit end screen.
///
/// Deliberately IMGUI: it has no scene dependencies, cannot be accidentally
/// unwired in the inspector, and renders over whatever else is on screen. Swap
/// for a Canvas UI if you want it styled for the projector.
/// </summary>
public class SessionUI : MonoBehaviour
{
    [SerializeField] private ExperimentController controller;
    [SerializeField] private int fontSize = 28;

    private string participantIdField = "";
    private bool focusedIdField;
    private bool etCalibrated;
    private bool etRecording;
    private bool triggerRouted;
    private GUIStyle boxStyle;
    private GUIStyle fieldStyle;
    private GUIStyle buttonStyle;

    private void Awake()
    {
        if (controller == null) controller = FindAnyObjectByType<ExperimentController>();
    }

    private void EnsureStyles()
    {
        if (boxStyle != null) return;

        boxStyle = new GUIStyle(GUI.skin.box)
        {
            fontSize = fontSize,
            alignment = TextAnchor.MiddleCenter,
            wordWrap = true
        };
        boxStyle.normal.textColor = Color.white;

        fieldStyle = new GUIStyle(GUI.skin.textField) { fontSize = fontSize };
        buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = fontSize };
    }

    private void OnGUI()
    {
        if (controller == null) return;
        EnsureStyles();

        switch (controller.State)
        {
            case SessionState.AwaitingParticipantId: DrawIdEntry(); break;
            case SessionState.AwaitingOperatorChecklist: DrawChecklist(); break;
            case SessionState.AwaitingFirstTrigger: DrawMessage("Waiting for scanner trigger…\n\n(Unity starts on the first trigger from scanner!)"); break;
            case SessionState.OnBreak: DrawMessage("Rest break.\n\nPress the button when you are ready to continue."); break;
            case SessionState.Finished: DrawMessage("The experiment is complete.\n\nThank you."); break;
            //case SessionState.Practice: DrawTrialCounter("Practice"); break;
            //case SessionState.Running: DrawTrialCounter("Trial"); break;
        }
    }

    private Rect CentreRect(float w, float h) =>
        new((Screen.width - w) / 2f, (Screen.height - h) / 2f, w, h);

    private void DrawIdEntry()
    {
        Rect r = CentreRect(560, 260);
        GUI.Box(r, "", boxStyle);

        GUILayout.BeginArea(new Rect(r.x + 30, r.y + 30, r.width - 60, r.height - 60));
        GUILayout.Label("Participant ID", boxStyle);
        GUILayout.Space(10);

        GUI.SetNextControlName("pid");
        participantIdField = GUILayout.TextField(participantIdField, fieldStyle, GUILayout.Height(fontSize + 18));

        // Focus the field on first draw so typing works without clicking it.
        if (!focusedIdField)
        {
            GUI.FocusControl("pid");
            focusedIdField = true;
        }
        GUILayout.Space(16);

        bool submitted = GUILayout.Button("Start session", buttonStyle, GUILayout.Height(fontSize + 22));

        // Enter also submits, so the operator never has to find the mouse.
        if (Event.current.type == EventType.KeyDown
            && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter))
        {
            submitted = true;
        }

        GUILayout.EndArea();

        if (submitted && !string.IsNullOrWhiteSpace(participantIdField))
        {
            controller.BeginSession(participantIdField);
        }
    }

    /// <summary>
    /// Operator pre-flight. Unity has no way to see the eye tracker, so these are
    /// hard-gated on deliberate confirmation rather than being advisory text: the
    /// failure mode this prevents is finishing a whole scan and only then finding
    /// the tracker was never recording.
    /// </summary>
    private void DrawChecklist()
    {
        Rect r = CentreRect(760, 420);
        GUI.Box(r, "", boxStyle);

        GUILayout.BeginArea(new Rect(r.x + 30, r.y + 24, r.width - 60, r.height - 48));
        GUILayout.Label("Before starting", boxStyle);
        GUILayout.Space(12);

        var item = new GUIStyle(GUI.skin.toggle) { fontSize = 20, wordWrap = true };

        etCalibrated = GUILayout.Toggle(etCalibrated,
            "  Eye tracker calibrated in MRC Eye-Tracking software", item, GUILayout.Height(34));
        etRecording = GUILayout.Toggle(etRecording,
            "  Eye tracker recording STARTED (manually)", item, GUILayout.Height(34));
        triggerRouted = GUILayout.Toggle(triggerRouted,
            "  Eye tracker and Unity confirmed running on THIS machine (shared clock)", item, GUILayout.Height(52));

        GUILayout.Space(16);

        bool ready = etCalibrated && etRecording && triggerRouted;
        GUI.enabled = ready;
        bool go = GUILayout.Button(ready ? "Arm — wait for scanner" : "Complete all items above",
                                   buttonStyle, GUILayout.Height(fontSize + 22));
        GUI.enabled = true;

        GUILayout.EndArea();

        if (go && ready) controller.ConfirmChecklistAndArm();

        DrawSetupError();
    }

    /// <summary>
    /// Shows setup problems on screen. A build has no Console, so without this a
    /// misconfigured scene looks like an experiment that starts and instantly
    /// finishes, with nothing to explain why.
    /// </summary>
    private void DrawSetupError()
    {
        if (string.IsNullOrEmpty(controller.SetupError)) return;

        var style = new GUIStyle(GUI.skin.box)
        {
            fontSize = 18,
            alignment = TextAnchor.UpperLeft,
            wordWrap = true
        };
        style.normal.textColor = new Color(1f, 0.6f, 0.6f);

        GUI.Box(new Rect(20, Screen.height - 240, Screen.width - 40, 220),
                "Setup problems — fix these before running:\n\n" + controller.SetupError, style);
    }

    private void DrawMessage(string message)
    {
        GUI.Box(CentreRect(700, 260), message, boxStyle);
    }

    /// <summary>
    /// Small corner counter for the operator. Keep it unobtrusive — the
    /// participant should not be counting down their own trials.
    /// </summary>
    /* private void DrawTrialCounter(string label)
    {
        var style = new GUIStyle(GUI.skin.label) { fontSize = 16 };
        style.normal.textColor = new Color(1f, 1f, 1f, 0.5f);

        string text = controller.State == SessionState.Practice
            ? $"{label} {controller.CurrentTrialNumber}"
            : $"{label} {controller.CurrentTrialNumber}/{controller.TotalTrials}";

        GUI.Label(new Rect(12, 8, 300, 30), text, style);
    } 
    */
}