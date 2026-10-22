using System;
using System.Collections;
using System.IO;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

/// <summary>
/// Captures exactly what was on screen at the moment of the participant's
/// response, without stalling that frame.
///
/// Why not ScreenCapture.CaptureScreenshot: it blocks while encoding and writing
/// a PNG, which can cost tens of milliseconds — landing precisely on the frame
/// whose timing you care most about.
///
/// How this works instead:
///   1. At end of the response frame, copy the backbuffer into a RenderTexture
///      (GPU-side, cheap).
///   2. Request an async GPU readback — the CPU does not wait.
///   3. When the pixels arrive a few frames later, encode the PNG and write it
///      to disk on a background thread.
/// The image content is from the exact response frame; only the (invisible) cost
/// of saving it is deferred.
/// </summary>
public class ScreenshotCapture : MonoBehaviour
{
    public static ScreenshotCapture Instance { get; private set; }

    [Tooltip("Folder for screenshots, relative to the project/build root.")]
    [SerializeField] private string folderName = "Screenshots";

    [Tooltip("Vertically flip captured frames. Backbuffer origin differs by platform/graphics API — verify once against a real capture and set accordingly.")]
    [SerializeField] private bool flipVertically = true;

    [Tooltip("Disable to skip capture entirely (e.g. for a timing-only pilot run).")]
    [SerializeField] private bool captureEnabled = true;

    private string folderPath;
    private int pendingCaptures;

    /// <summary>Number of captures still encoding/writing. Used to wait before quitting.</summary>
    public int PendingCaptures => pendingCaptures;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;

        folderPath = Path.GetFullPath(Path.Combine(Application.dataPath, "..", folderName));
        Directory.CreateDirectory(folderPath);
    }

    /// <summary>
    /// Requests a capture of the current frame. Returns the filename that will be
    /// written, so the caller can log it immediately even though the file itself
    /// appears on disk a moment later.
    /// </summary>
    public string CaptureNow(string label)
    {
        if (!captureEnabled) return "";

        string safeLabel = string.IsNullOrEmpty(label) ? "frame" : SanitizeFileName(label);
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss-fff");
        string fileName = $"{safeLabel}_{stamp}.png";

        if (!SystemInfo.supportsAsyncGPUReadback)
        {
            // Older/limited hardware: fall back to Unity's blocking capture rather
            // than losing the image. This WILL cost a frame — flagged loudly so it
            // is never mistaken for the async path during a real scan.
            Debug.LogWarning("ScreenshotCapture: async GPU readback unsupported; using blocking capture. Response-frame timing will be affected.");
            ScreenCapture.CaptureScreenshot(Path.Combine(folderPath, fileName));
            return fileName;
        }

        StartCoroutine(CaptureRoutine(fileName));
        return fileName;
    }

    private IEnumerator CaptureRoutine(string fileName)
    {
        pendingCaptures++;

        // Must be end-of-frame: the backbuffer is only complete after rendering.
        yield return new WaitForEndOfFrame();

        int width = Screen.width;
        int height = Screen.height;

        RenderTexture rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32);
        rt.Create();
        ScreenCapture.CaptureScreenshotIntoRenderTexture(rt);

        AsyncGPUReadback.Request(rt, 0, TextureFormat.RGBA32, request =>
        {
            rt.Release();
            Destroy(rt);

            if (request.hasError)
            {
                Debug.LogError($"ScreenshotCapture: GPU readback failed for {fileName}");
                pendingCaptures--;
                return;
            }

            // Copy out of the native buffer before it is recycled, then hand the
            // managed array to a worker thread for encoding and disk I/O.
            byte[] pixels = request.GetData<byte>().ToArray();
            string fullPath = Path.Combine(folderPath, fileName);

            Task.Run(() => EncodeAndWrite(pixels, width, height, fullPath));
        });
    }

    private void EncodeAndWrite(byte[] pixels, int width, int height, string fullPath)
    {
        try
        {
            if (flipVertically) FlipRows(pixels, width, height);

            byte[] png = ImageConversion.EncodeArrayToPNG(
                pixels, GraphicsFormat.R8G8B8A8_UNorm, (uint)width, (uint)height);

            File.WriteAllBytes(fullPath, png);
        }
        catch (Exception e)
        {
            Debug.LogError($"ScreenshotCapture: failed to write {fullPath} — {e.Message}");
        }
        finally
        {
            pendingCaptures--;
        }
    }

    private static void FlipRows(byte[] pixels, int width, int height)
    {
        int stride = width * 4;
        byte[] scratch = new byte[stride];

        for (int y = 0; y < height / 2; y++)
        {
            int top = y * stride;
            int bottom = (height - 1 - y) * stride;

            Buffer.BlockCopy(pixels, top, scratch, 0, stride);
            Buffer.BlockCopy(pixels, bottom, pixels, top, stride);
            Buffer.BlockCopy(scratch, 0, pixels, bottom, stride);
        }
    }

    private static string SanitizeFileName(string raw)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            raw = raw.Replace(c, '_');
        }
        return raw;
    }
}