using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

/// <summary>
/// Copies everything Unity logs into the session's own .log file, next to that
/// session's CSV in ExperimentData.
///
/// Unity already writes a Player.log, but it lives in AppData, is overwritten by
/// the next run, and is not tied to a participant. This keeps a per-session copy
/// beside the data it belongs to, so a session's warnings and errors travel with
/// its results.
///
/// Messages logged BEFORE the participant ID is entered are buffered and written
/// once the file opens, so startup errors are not lost. If the program exits
/// before a session starts, the buffer is still flushed to an "unstarted" file
/// rather than discarded - that is exactly the case where the log is most useful.
/// </summary>
public class SessionLog : MonoBehaviour
{
    public static SessionLog Instance { get; private set; }

    [SerializeField] private string folderName = "ExperimentData";

    [Tooltip("Include stack traces for errors and exceptions. Warnings and normal logs never get one, to keep the file readable.")]
    [SerializeField] private bool stackTracesForErrors = true;

    private StreamWriter writer;
    private readonly List<string> preSessionBuffer = new();
    private readonly object gate = new();
    private string filePath;
    private bool sessionStarted;

    public string FilePath => filePath;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        // Threaded variant so messages from background work (the screenshot
        // encoder, for instance) are captured too, not just main-thread logs.
        Application.logMessageReceivedThreaded += HandleLog;
    }

    private void HandleLog(string message, string stackTrace, LogType type)
    {
        StringBuilder line = new();
        line.Append(DateTime.Now.ToString("HH:mm:ss.fff"))
            .Append(" [").Append(type).Append("] ")
            .Append(message);

        bool isError = type == LogType.Error || type == LogType.Exception || type == LogType.Assert;
        if (stackTracesForErrors && isError && !string.IsNullOrEmpty(stackTrace))
        {
            line.Append('\n').Append(stackTrace.TrimEnd());
        }

        string text = line.ToString();

        // Locked because logs can arrive from several threads at once.
        lock (gate)
        {
            if (writer != null)
            {
                writer.WriteLine(text);
                writer.Flush();
            }
            else
            {
                preSessionBuffer.Add(text);
            }
        }
    }

    /// <summary>Opens the log file for this participant and flushes anything buffered.</summary>
    public void BeginSession(string participantId)
    {
        lock (gate)
        {
            string folder = Path.Combine(Application.dataPath, "..", folderName);
            Directory.CreateDirectory(folder);

            string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
            filePath = Path.GetFullPath(Path.Combine(folder, $"P{participantId}_{stamp}.log"));

            try
            {
                writer = new StreamWriter(filePath, append: false, Encoding.UTF8);
                writer.WriteLine($"# session log for participant {participantId}");
                writer.WriteLine($"# started {DateTime.Now:o}");
                writer.WriteLine($"# unity {Application.unityVersion} on {SystemInfo.operatingSystem}");
                writer.WriteLine($"# device {SystemInfo.deviceModel} / {SystemInfo.processorType} / {SystemInfo.graphicsDeviceName}");
                writer.WriteLine($"# screen {Screen.width}x{Screen.height} @ {Screen.currentResolution.refreshRateRatio.value:F1} Hz fullscreen={Screen.fullScreen}");
                writer.WriteLine("# --- buffered startup messages ---");

                foreach (string buffered in preSessionBuffer)
                {
                    writer.WriteLine(buffered);
                }

                writer.WriteLine("# --- live ---");
                writer.Flush();

                preSessionBuffer.Clear();
                sessionStarted = true;
            }
            catch (Exception e)
            {
                writer = null;
                Debug.LogError($"SessionLog: could not open log file — {e.Message}");
            }
        }
    }

    public void EndSession()
    {
        lock (gate)
        {
            if (writer == null) return;

            writer.WriteLine($"# closed {DateTime.Now:o}");
            writer.Flush();
            writer.Dispose();
            writer = null;
        }
    }

    /// <summary>
    /// Writes buffered messages even when no session ever started, so a run that
    /// failed during setup still leaves a log behind.
    /// </summary>
    private void FlushUnstarted()
    {
        lock (gate)
        {
            if (sessionStarted || preSessionBuffer.Count == 0) return;

            try
            {
                string folder = Path.Combine(Application.dataPath, "..", folderName);
                Directory.CreateDirectory(folder);

                string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
                string path = Path.GetFullPath(Path.Combine(folder, $"unstarted_{stamp}.log"));

                File.WriteAllLines(path, preSessionBuffer, Encoding.UTF8);
                preSessionBuffer.Clear();
            }
            catch
            {
                // Nothing useful to do here: this runs during shutdown, and
                // logging the failure would go nowhere.
            }
        }
    }

    private void OnDestroy()
    {
        Application.logMessageReceivedThreaded -= HandleLog;
        FlushUnstarted();
        EndSession();
    }

    private void OnApplicationQuit()
    {
        FlushUnstarted();
        EndSession();
    }
}