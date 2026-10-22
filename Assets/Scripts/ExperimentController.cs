using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public enum SessionState
{
    AwaitingParticipantId,
    AwaitingOperatorChecklist,
    AwaitingFirstTrigger,
    Practice,
    Running,
    OnBreak,
    Finished
}

/// <summary>
/// Owns the whole session: builds a reproducible trial plan, waits for the
/// scanner, runs practice then main trials, inserts breaks, and finishes cleanly.
///
/// Trials advance WITHOUT reloading the scene. Everything already lives in this
/// one scene, so a transition is just "deactivate the old targets, activate the
/// new ones, reset the player". Reloading cost an unpredictable frame hitch,
/// re-ran every Start(), re-instantiated all the FitMask objects, and forced
/// state to survive via statics — all of which is now gone.
/// </summary>
[RequireComponent(typeof(TrialManager))]
public class ExperimentController : MonoBehaviour
{
    public static ExperimentController Instance { get; private set; }

    [Header("References")]
    [SerializeField] private TrialManager trialManager;
    [SerializeField] private MRTriggerReceiver triggerReceiver;
    [SerializeField] private PlayerController player;
    [SerializeField] private Camera playerCamera;

    [Header("Design")]
    [Tooltip("Number of scored trials.")]
    [SerializeField] private int totalTrials = 20;

    [Tooltip("Unscored practice trials. Uses the separate practice groups on TrialManager, so practice never consumes a scored group. Set to 0 for none.")]
    [SerializeField] private int practiceTrials = 0;

    [Tooltip("Insert a rest break after every N main trials. Set to 0 to disable.")]
    [SerializeField] private int trialsPerBlock = 10;

    [Header("Timing")]
    [Tooltip("Blank/fixation gap between trials, in seconds.")]
    [SerializeField] private float interTrialInterval = 2f;

    [Header("Player Start Pose")]
    [Tooltip("Transform the player is reset to at the start of every trial. If unset, the player's pose at startup is used.")]
    [SerializeField] private Transform startPose;

    [Header("Status (read-only)")]
    [SerializeField] private SessionState state = SessionState.AwaitingParticipantId;
    [SerializeField] private int currentTrialNumber;

    private string participantId = "";
    private System.Random rng;
    private List<int> trialPlan = new();
    private List<int> practicePlan = new();
    private double trialOnsetTime = double.NaN;
    private Vector3 startPosition;
    private Quaternion startRotation;

    public SessionState State => state;
    public int CurrentTrialNumber => currentTrialNumber;
    public int TotalTrials => totalTrials;
    public int TargetsPerTrial => trialManager != null ? trialManager.ActiveTargetCount : 0;
    public bool IsPractice => state == SessionState.Practice;
    public string BlockType => IsPractice ? "practice" : "main";
    public Transform PlayerTransform => player != null ? player.transform : null;
    public Camera PlayerCamera => playerCamera;


    /// <summary>Seconds since the current trial began. NaN between trials.</summary>
    public double TrialTime =>
        double.IsNaN(trialOnsetTime) ? double.NaN : ExperimentClock.TimeSinceStart - trialOnsetTime;

    private void Awake()
    {
        Instance = this;

        if (trialManager == null) trialManager = GetComponent<TrialManager>();
        if (triggerReceiver == null) triggerReceiver = FindAnyObjectByType<MRTriggerReceiver>();
        if (player == null) player = FindAnyObjectByType<PlayerController>();
        if (playerCamera == null && player != null) playerCamera = player.GetComponentInChildren<Camera>();

        if (triggerReceiver == null)
        {
            Debug.LogError("ExperimentController: no MRTriggerReceiver in scene. The run cannot be locked to the scanner.");
        }

        if (player != null)
        {
            startPosition = startPose != null ? startPose.position : player.transform.position;
            startRotation = startPose != null ? startPose.rotation : player.transform.rotation;
        }
    }

    private void Start()
    {
        ExperimentClock.Reset();
        SetState(SessionState.AwaitingParticipantId);
        FreezePlayer(true);
        trialManager.DeactivateAllGroups();
    }

    /// <summary>Called by SessionUI once the operator has entered the participant ID.</summary>
    public void BeginSession(string id)
    {
        if (state != SessionState.AwaitingParticipantId) return;

        participantId = string.IsNullOrWhiteSpace(id) ? "unset" : id.Trim();
        SetState(SessionState.AwaitingOperatorChecklist);
    }

    /// <summary>
    /// Called by SessionUI once the operator confirms the eye tracker has been
    /// calibrated in MRC's own software AND recording has been started manually.
    ///
    /// This gate exists precisely because the tracker is kept separate from Unity:
    /// nothing in this program can verify the tracker is actually running, so the
    /// only protection against discovering afterwards that a session has no gaze
    /// data is an explicit human confirmation — recorded in the data file.
    /// </summary>
    /// <summary>
    /// Set when the session cannot start. Shown on screen by SessionUI, because a
    /// build has no Console — without this the experiment would appear to start
    /// and immediately finish with no explanation.
    /// </summary>
    public string SetupError { get; private set; } = "";

    /// <summary>
    /// Checks everything the session needs before arming. Runs on the real
    /// serialized values, so it catches inspector assignments that were made
    /// during Play mode and therefore never saved into the build.
    /// </summary>
    private bool ValidateSetup()
    {
        List<string> problems = new();

        if (trialManager == null)
        {
            problems.Add("No TrialManager found.");
        }
        else
        {
            if (trialManager.TrialGroupCount == 0)
            {
                problems.Add("TrialManager has NO trial groups assigned. Assign your target group parents to the Trial Groups list (and check they were not assigned while Play mode was running, which discards them).");
            }
            else if (trialManager.TrialGroupCount < totalTrials)
            {
                problems.Add($"Only {trialManager.TrialGroupCount} trial groups for {totalTrials} trials.");
            }

            if (practiceTrials > 0 && trialManager.PracticeGroupCount == 0)
            {
                problems.Add($"{practiceTrials} practice trials configured but no practice groups assigned.");
            }
        }

        if (totalTrials <= 0) problems.Add("Total Trials is 0.");
        if (player == null) problems.Add("No PlayerController found.");
        if (playerCamera == null) problems.Add("No player Camera found.");
        if (triggerReceiver == null) problems.Add("No MRTriggerReceiver found — the run cannot start on the scanner trigger.");

        SetupError = problems.Count == 0 ? "" : string.Join("\n\n", problems);

        if (problems.Count > 0)
        {
            Debug.LogError("ExperimentController setup problems:\n" + SetupError);
        }

        return problems.Count == 0;
    }

    public void ConfirmChecklistAndArm()
    {
        if (state != SessionState.AwaitingOperatorChecklist) return;

        if (!ValidateSetup()) return;

        BuildTrialPlan();

        if (SessionLog.Instance != null) SessionLog.Instance.BeginSession(participantId);

        DataLogger.Instance.BeginSession(participantId);
        DataLogger.Instance.WriteHeaderComment($"seed={StableSeed(participantId)}");
        DataLogger.Instance.WriteHeaderComment($"total_trials={totalTrials} practice_trials={practiceTrials} trial_groups={trialManager.TrialGroupCount}");
        DataLogger.Instance.WriteHeaderComment($"screen={Screen.width}x{Screen.height} camera_fov={(playerCamera != null ? playerCamera.fieldOfView : -1f)}");
        DataLogger.Instance.WriteHeaderComment("eyetracker=MRC 60-M recorded separately; align to pose log via scanner TR index");
        DataLogger.Instance.WriteHeaderComment("operator_confirmed_et_calibrated_and_recording=true");
        DataLogger.Instance.WriteHeaderComment("trial_plan=" + DescribePlan());



        Debug.Log($"ExperimentController armed: {trialPlan.Count} trials from {trialManager.TrialGroupCount} groups, {practicePlan.Count} practice.");

        SetState(SessionState.AwaitingFirstTrigger);
        Log("session_start");

        if (triggerReceiver != null)
        {
            triggerReceiver.OnFirstTrigger += HandleFirstTrigger;
        }
    }

    /// <summary>
    /// The entire trial order is generated up front from a participant-derived
    /// seed and written into the data file header. This is what makes a session
    /// reconstructable: given the participant ID alone you can regenerate exactly
    /// what they saw, and the plan is on record even if the run aborts early.
    /// </summary>
    private void BuildTrialPlan()
    {
        rng = new System.Random(StableSeed(participantId));

        trialPlan = ShuffledIndices(trialManager.TrialGroupCount);
        if (trialPlan.Count > totalTrials) trialPlan = trialPlan.GetRange(0, totalTrials);

        if (trialPlan.Count < totalTrials)
        {
            Debug.LogWarning(
                $"ExperimentController: only {trialManager.TrialGroupCount} trial groups assigned " +
                $"but {totalTrials} trials configured. The session will run {trialPlan.Count} trials.");
        }

        practicePlan = ShuffledIndices(trialManager.PracticeGroupCount);
        if (practicePlan.Count > practiceTrials) practicePlan = practicePlan.GetRange(0, practiceTrials);

        if (practiceTrials > 0 && practicePlan.Count < practiceTrials)
        {
            Debug.LogWarning(
                $"ExperimentController: {practiceTrials} practice trials configured but only " +
                $"{trialManager.PracticeGroupCount} practice groups assigned on TrialManager.");
        }
    }

    /// <summary>Every index 0..count-1, shuffled. Each group is used at most once.</summary>
    private List<int> ShuffledIndices(int count)
    {
        List<int> list = new(count);
        for (int i = 0; i < count; i++) list.Add(i);
        Shuffle(list);
        return list;
    }

    /// <summary>
    /// Deterministic seed from the participant ID string. Deliberately NOT
    /// string.GetHashCode(), which is randomized per process in modern .NET and
    /// would give a different order every launch for the same participant.
    /// </summary>
    private static int StableSeed(string id)
    {
        unchecked
        {
            int hash = 23;
            foreach (char c in id) hash = hash * 31 + c;
            return hash;
        }
    }

    private void Shuffle(List<int> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private string DescribePlan() => string.Join("-", trialPlan);

    private void HandleFirstTrigger()
    {
        if (state != SessionState.AwaitingFirstTrigger) return;

        // Wall-clock anchor. This is the LAST-RESORT alignment path: MRC's .trk
        // header records an absolute clock time on its start line, so if no TTL
        // and no software markers reach the tracker, the two recordings can still
        // be crudely aligned via wall clock — but only if both machines' clocks
        // are properly disciplined (NTP/PTP), and only to whatever accuracy that
        // sync provides. Default Windows time sync can be seconds off, which is
        // useless for event-related gaze analysis. Treat this as damage control,
        // not as a substitute for a real trigger.
        DataLogger.Instance.WriteHeaderComment(
            $"first_trigger_wall_clock={DateTime.Now:o}");

        Log("first_trigger");

        StartCoroutine(RunSession());
    }

    private IEnumerator RunSession()
    {
        if (trialPlan.Count == 0 && practicePlan.Count == 0)
        {
            SetupError = "No trials to run — the trial plan is empty. Check TrialManager's Trial Groups list.";
            Debug.LogError(SetupError);
            SetState(SessionState.AwaitingOperatorChecklist);
            yield break;
        }

        if (practicePlan.Count > 0)
        {
            SetState(SessionState.Practice);
            for (int t = 0; t < practicePlan.Count; t++)
            {
                currentTrialNumber = t + 1;
                yield return RunTrial(practicePlan[t], practice: true);
            }
        }

        SetState(SessionState.Running);

        for (int t = 0; t < trialPlan.Count; t++)
        {
            currentTrialNumber = t + 1;
            yield return RunTrial(trialPlan[t], practice: false);

            bool moreTrials = t < trialPlan.Count - 1;
            bool blockBoundary = trialsPerBlock > 0 && (t + 1) % trialsPerBlock == 0;

            if (moreTrials && blockBoundary)
            {
                yield return RunBreak();
            }
        }


        FinishExperiment();
    }

    private IEnumerator RunTrial(int groupIndex, bool practice)
    {
        // Inter-trial interval: targets hidden, player frozen at start pose.
        trialManager.DeactivateAllGroups();
        ResetPlayerPose();
        FreezePlayer(true);
        trialOnsetTime = double.NaN;

        Log("iti_start");
        yield return new WaitForSeconds(interTrialInterval);

        trialManager.SetUpTrial(groupIndex, practice);
        trialOnsetTime = ExperimentClock.TimeSinceStart;
        FreezePlayer(false);


        Log("trial_onset", notes: $"group={trialManager.CurrentGroupName} targets={trialManager.ActiveTargetCount}");

        // TrialManager flips this once every target in the trial is recognized.
        while (!trialManager.TrialComplete)
        {
            yield return null;
        }


        Log("trial_end", trialTime: TrialTime);

        FreezePlayer(true);
        trialOnsetTime = double.NaN;
    }

    private IEnumerator RunBreak()
    {
        SessionState previous = state;
        SetState(SessionState.OnBreak);

        trialManager.DeactivateAllGroups();
        FreezePlayer(true);

        Log("break_start");

        // The scan usually keeps running through a break, so this waits for the
        // participant's own button press rather than a fixed duration.
        while (!PlayerController.IsInteractTriggered)
        {
            yield return null;
        }

        Log("break_end");
        SetState(previous);
    }

    private void FinishExperiment()
    {
        SetState(SessionState.Finished);
        trialManager.DeactivateAllGroups();
        FreezePlayer(true);

        Log("session_end");

        StartCoroutine(CloseOutWhenSafe());
    }

    /// <summary>
    /// Waits for any in-flight screenshot writes to land before closing the data
    /// file, so the session never ends with a half-written PNG.
    /// </summary>
    private IEnumerator CloseOutWhenSafe()
    {
        float timeout = Time.realtimeSinceStartup + 10f;

        while (ScreenshotCapture.Instance != null
               && ScreenshotCapture.Instance.PendingCaptures > 0
               && Time.realtimeSinceStartup < timeout)
        {
            yield return null;
        }

        DataLogger.Instance.EndSession();
        if (SessionLog.Instance != null) SessionLog.Instance.EndSession();
        Debug.Log("Experiment finished. Data files closed. You may now stop the eye tracker recording.");
    }

    /// <summary>Called by TrialManager on each recognition, for logging and markers.</summary>
    public void ReportRecognition(string targetName, int targetIndex, double responseLatency, double gazeDuration, string screenshotFile, int foundSoFar, int totalInTrial, string groupName)
    {
        DataLogger.Instance.Log(
            eventName: "target_recognized",
            blockType: BlockType,
            trialNumber: currentTrialNumber,
            trialTime: TrialTime,
            targetName: targetName,
            targetIndex: targetIndex,
            responseLatency: responseLatency,
            gazeDurationActual: gazeDuration,
            player: PlayerTransform,
            cam: playerCamera,
            screenshotFile: screenshotFile,
            notes: $"group={groupName} found={foundSoFar}/{totalInTrial}");


    }

    /// <summary>Called by Gaze when the participant presses the button on a target.</summary>
    public void ReportResponseOnset(string targetName, int targetIndex, string screenshotFile)
    {
        DataLogger.Instance.Log(
            eventName: "response_press",
            blockType: BlockType,
            trialNumber: currentTrialNumber,
            trialTime: TrialTime,
            targetName: targetName,
            targetIndex: targetIndex,
            player: PlayerTransform,
            cam: playerCamera,
            screenshotFile: screenshotFile);


    }

    private void Log(string eventName, double trialTime = double.NaN, string notes = "")
    {
        DataLogger.Instance.Log(
            eventName: eventName,
            blockType: BlockType,
            trialNumber: currentTrialNumber,
            trialTime: trialTime,
            player: PlayerTransform,
            cam: playerCamera,
            notes: notes);
    }

    private void SetState(SessionState next)
    {
        state = next;
        Debug.Log($"ExperimentController: state -> {next}");
    }

    private void FreezePlayer(bool frozen)
    {
        if (player != null) player.MovementEnabled = !frozen;
    }

    private void ResetPlayerPose()
    {
        if (player == null) return;
        player.TeleportTo(startPosition, startRotation);
    }

    private void OnDestroy()
    {
        if (triggerReceiver != null)
        {
            triggerReceiver.OnFirstTrigger -= HandleFirstTrigger;
        }
    }
}