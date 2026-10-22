using System;
using System.Diagnostics;
using UnityEngine;

/// <summary>
/// Single source of timing truth for the whole experiment.
///
/// Provides two independent time bases, because alignment may rely on either:
///
///  1. TimeSinceStart — seconds since the first scanner trigger. Use whenever the
///     tracker receives the trigger (TTL or software markers).
///
///  2. WallClockNow — high-resolution absolute time. This is the alignment path
///     when Unity and the MRC software share one computer: MRC's .trk header maps
///     its internal ms counter to absolute time on the start and stop lines, so
///     rescaling that counter to wall clock lets it be joined directly against
///     these timestamps.
///
/// WallClockNow is NOT DateTime.Now. On Windows, DateTime.Now typically advances
/// in ~15.6 ms steps, which would dominate the error budget in a gaze study. It
/// is instead a single DateTime anchor taken at startup, advanced by a
/// high-resolution Stopwatch — absolute like DateTime, precise like Stopwatch.
/// The tradeoff is that it does not follow later NTP corrections, which is what
/// you want mid-session: a clock step during a run would corrupt alignment far
/// worse than a small drift.
/// </summary>
public static class ExperimentClock
{
    private static double firstTriggerRealtime = -1.0;

    private static readonly DateTime WallClockAnchor = DateTime.UtcNow;
    private static readonly Stopwatch HighResTimer = Stopwatch.StartNew();

    /// <summary>True once the first scanner trigger has been received.</summary>
    public static bool Started => firstTriggerRealtime >= 0.0;

    /// <summary>Raw high-resolution clock, independent of frame rate and timeScale.</summary>
    public static double Now => Time.realtimeSinceStartupAsDouble;

    /// <summary>
    /// Absolute UTC time at sub-millisecond resolution. Join MRC .trk data against
    /// this when both systems share a machine.
    /// </summary>
    public static DateTime WallClockNow => WallClockAnchor + HighResTimer.Elapsed;

    /// <summary>ISO-8601 UTC string with microsecond precision, for log files.</summary>
    public static string WallClockIso => WallClockNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffffZ");

    /// <summary>Number of TR pulses counted since (and including) the first.</summary>
    public static int TriggerCount { get; private set; }

    /// <summary>Clock time of the most recent TR pulse, relative to the first trigger.</summary>
    public static double LastTriggerTime { get; private set; }

    /// <summary>
    /// Seconds since the first scanner trigger. Returns NaN before the run starts,
    /// so any accidental pre-trigger logging is obviously wrong in the data file
    /// rather than quietly looking like a valid time near zero.
    /// </summary>
    public static double TimeSinceStart =>
        Started ? Now - firstTriggerRealtime : double.NaN;

    /// <summary>Called by MRTriggerReceiver on every TR pulse.</summary>
    public static void RegisterTrigger()
    {
        if (!Started)
        {
            firstTriggerRealtime = Now;
        }

        TriggerCount++;
        LastTriggerTime = TimeSinceStart;
    }

    /// <summary>Clears all state. Call when starting a fresh session.</summary>
    public static void Reset()
    {
        firstTriggerRealtime = -1.0;
        TriggerCount = 0;
        LastTriggerTime = 0.0;
    }
}