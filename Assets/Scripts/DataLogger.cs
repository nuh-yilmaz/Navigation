using System;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Writes one CSV row per experimental event, flushing to disk immediately.
///
/// Flushing every row is deliberate: a crash or an aborted scan at trial 18 must
/// not cost the whole session. The cost is a few milliseconds per event, and
/// events are rare (a handful per trial), so this never touches the render path.
///
/// All times are seconds since the first scanner trigger. Rows written before the
/// first trigger show NaN, which makes accidental pre-run logging obvious in
/// analysis rather than silently plausible.
/// </summary>
public class DataLogger : MonoBehaviour
{
    public static DataLogger Instance { get; private set; }

    [Tooltip("Folder for data files, relative to the project/build root.")]
    [SerializeField] private string folderName = "ExperimentData";

    private StreamWriter writer;
    private string filePath;
    private string participantId = "unset";

    /// <summary>Absolute path of the active data file, for display in the UI.</summary>
    public string FilePath => filePath;

    private static readonly string[] Columns =
    {
        "participant_id",
        "wall_clock_utc",
        "time_since_first_trigger",
        "tr_count",
        "block_type",
        "trial_number",
        "trial_time",
        "event",
        "target_name",
        "target_index",
        "response_latency",
        "gaze_duration_actual",
        "player_x", "player_y", "player_z",
        "player_yaw", "camera_pitch",
        "screenshot_file",
        "notes"
    };

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }

    /// <summary>
    /// Opens a new data file for this participant. Filenames include a timestamp
    /// so re-running the same participant ID can never overwrite existing data.
    /// </summary>
    public void BeginSession(string participant)
    {
        participantId = string.IsNullOrWhiteSpace(participant) ? "unset" : participant.Trim();

        string folder = Path.Combine(Application.dataPath, "..", folderName);
        Directory.CreateDirectory(folder);

        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        filePath = Path.GetFullPath(Path.Combine(folder, $"P{participantId}_{stamp}.csv"));

        try
        {
            writer = new StreamWriter(filePath, append: false, Encoding.UTF8);
            writer.WriteLine(string.Join(",", Columns));
            writer.Flush();
            Debug.Log($"DataLogger: writing to {filePath}");
        }
        catch (Exception e)
        {
            Debug.LogError($"DataLogger: could not open data file — {e.Message}");
            writer = null;
        }
    }

    /// <summary>
    /// Writes a free-form header comment (prefixed with #) — used to record the
    /// pre-generated trial order and RNG seed, so a session is fully
    /// reconstructable from its own data file alone.
    /// </summary>
    public void WriteHeaderComment(string text)
    {
        if (writer == null) return;
        writer.WriteLine("# " + text.Replace("\n", " "));
        writer.Flush();
    }

    public void Log(
        string eventName,
        string blockType = "",
        int trialNumber = -1,
        double trialTime = double.NaN,
        string targetName = "",
        int targetIndex = -1,
        double responseLatency = double.NaN,
        double gazeDurationActual = double.NaN,
        Transform player = null,
        Camera cam = null,
        string screenshotFile = "",
        string notes = "")
    {
        if (writer == null) return;

        Vector3 pos = player != null ? player.position : Vector3.zero;
        float yaw = player != null ? player.eulerAngles.y : float.NaN;
        float pitch = cam != null ? cam.transform.localEulerAngles.x : float.NaN;

        string[] row =
        {
            Esc(participantId),
            ExperimentClock.WallClockIso,
            Num(ExperimentClock.TimeSinceStart),
            ExperimentClock.TriggerCount.ToString(CultureInfo.InvariantCulture),
            Esc(blockType),
            trialNumber.ToString(CultureInfo.InvariantCulture),
            Num(trialTime),
            Esc(eventName),
            Esc(targetName),
            targetIndex.ToString(CultureInfo.InvariantCulture),
            Num(responseLatency),
            Num(gazeDurationActual),
            Num(pos.x), Num(pos.y), Num(pos.z),
            Num(yaw), Num(pitch),
            Esc(screenshotFile),
            Esc(notes)
        };

        writer.WriteLine(string.Join(",", row));
        writer.Flush();
    }

    private static string Num(double v) =>
        double.IsNaN(v) ? "NaN" : v.ToString("F6", CultureInfo.InvariantCulture);

    /// <summary>Quotes any field containing a comma, quote, or newline.</summary>
    private static string Esc(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) < 0) return s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    public void EndSession()
    {
        if (writer == null) return;
        writer.Flush();
        writer.Dispose();
        writer = null;
        Debug.Log($"DataLogger: session closed — {filePath}");
    }

    private void OnDestroy() => EndSession();
    private void OnApplicationQuit() => EndSession();
}