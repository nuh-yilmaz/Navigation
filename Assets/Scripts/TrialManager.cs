using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Runs one trial at a time. A TRIAL GROUP is a parent object holding the
/// several copies of one object placed around the scene - e.g. a parent
/// "basketball" whose children are the four basketballs at four locations.
///
/// Activating the group activates its children with it, so a trial is a single
/// SetActive call. The children are the things the participant must find, and
/// the trial ends when every child has been recognized.
///
/// This holds no state across trials: ExperimentController tells it which group
/// to show, and it reports back when that group is finished.
/// </summary>
public class TrialManager : MonoBehaviour
{
    [Tooltip("One entry per trial. Each entry is a PARENT object whose children are " +
             "that trial's targets (e.g. 'basketball' containing four basketballs). " +
             "For 20 trials you need 20 entries here.")]
    [SerializeField] private List<GameObject> trialGroups = new();

    [Tooltip("Optional separate groups for practice trials, so practice does not " +
             "consume groups meant for the scored trials. Leave empty for no practice.")]
    [SerializeField] private List<GameObject> practiceGroups = new();

    [Tooltip("How many targets each group is expected to contain. Used only to warn " +
             "about a mis-built group - the real count comes from the children.")]
    [SerializeField] private int expectedTargetsPerGroup = 4;

    private GameObject currentGroup;
    private readonly List<GameObject> currentTrialTargets = new();
    private readonly HashSet<GameObject> recognizedThisTrial = new();

    /// <summary>True once every target in the current group has been recognized.</summary>
    public bool TrialComplete { get; private set; }

    public int TrialGroupCount => trialGroups.Count;
    public int PracticeGroupCount => practiceGroups.Count;
    public int ActiveTargetCount => currentTrialTargets.Count;
    public int RecognizedCount => recognizedThisTrial.Count;
    public string CurrentGroupName => currentGroup != null ? currentGroup.name : "";

    /// <summary>Hides every group, practice and scored alike.</summary>
    public void DeactivateAllGroups()
    {
        foreach (GameObject group in trialGroups)
        {
            if (group != null) group.SetActive(false);
        }
        foreach (GameObject group in practiceGroups)
        {
            if (group != null) group.SetActive(false);
        }

        currentGroup = null;
        currentTrialTargets.Clear();
        recognizedThisTrial.Clear();
        TrialComplete = false;
    }

    /// <summary>
    /// Activates one group and takes its direct children as this trial's targets.
    /// </summary>
    public void SetUpTrial(int groupIndex, bool practice)
    {
        DeactivateAllGroups();

        List<GameObject> source = practice ? practiceGroups : trialGroups;

        if (groupIndex < 0 || groupIndex >= source.Count || source[groupIndex] == null)
        {
            Debug.LogError($"TrialManager: trial group index {groupIndex} is missing or empty.");
            return;
        }

        currentGroup = source[groupIndex];
        currentGroup.SetActive(true);

        // Direct children only. A child may have its own mesh/mask descendants,
        // but each CHILD is one findable target - descendants belong to it rather
        // than being targets in their own right.
        foreach (Transform child in currentGroup.transform)
        {
            child.gameObject.SetActive(true);
            currentTrialTargets.Add(child.gameObject);
        }

        if (currentTrialTargets.Count == 0)
        {
            Debug.LogError($"TrialManager: group '{currentGroup.name}' has no children, so this trial can never finish.");
        }
        else if (currentTrialTargets.Count != expectedTargetsPerGroup)
        {
            Debug.LogWarning($"TrialManager: group '{currentGroup.name}' has {currentTrialTargets.Count} children, expected {expectedTargetsPerGroup}.");
        }

        Debug.Log($"Trial group '{currentGroup.name}' active with {currentTrialTargets.Count} targets.");
    }

    /// <summary>
    /// Walks up from whatever the gaze ray hit to the child that represents a
    /// target. The ray usually lands on a mesh or collider nested below the
    /// target, so this climbs until it reaches an object that is a direct child
    /// of the active group. Returns null if the hit isn't part of this trial.
    /// </summary>
    public GameObject ResolveTarget(GameObject hit)
    {
        for (Transform t = hit != null ? hit.transform : null; t != null; t = t.parent)
        {
            if (currentTrialTargets.Contains(t.gameObject)) return t.gameObject;
        }
        return null;
    }

    /// <summary>True if the hit belongs to a target in this trial that isn't recognized yet.</summary>
    public bool IsPendingTrialTarget(GameObject hit)
    {
        GameObject target = ResolveTarget(hit);
        return target != null && !recognizedThisTrial.Contains(target);
    }

    /// <summary>Index of a target within the active group, or -1.</summary>
    public int IndexOfTarget(GameObject target) => currentTrialTargets.IndexOf(target);

    /// <summary>
    /// Called by Gaze once a target has been held in view for the required time.
    /// responseLatency is time from trial onset to the button press; gazeDuration
    /// is the true measured hold rather than the nominal setting.
    /// </summary>
    public void NotifyRecognized(GameObject hit, double responseLatency, double gazeDuration, string screenshotFile)
    {
        GameObject target = ResolveTarget(hit);
        if (target == null || recognizedThisTrial.Contains(target)) return;

        recognizedThisTrial.Add(target);

        ExperimentController.Instance.ReportRecognition(
            target.name,
            IndexOfTarget(target),
            responseLatency,
            gazeDuration,
            screenshotFile,
            recognizedThisTrial.Count,
            currentTrialTargets.Count,
            CurrentGroupName);

        Debug.Log($"Recognized {target.name} ({recognizedThisTrial.Count}/{currentTrialTargets.Count} in '{CurrentGroupName}').");

        if (recognizedThisTrial.Count >= currentTrialTargets.Count)
        {
            TrialComplete = true;
        }
    }
}