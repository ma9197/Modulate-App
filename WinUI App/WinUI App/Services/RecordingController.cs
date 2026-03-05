using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinUI_App.Models;

namespace WinUI_App.Services
{
    /// <summary>
    /// App-scoped recording + pending-report persistence so tray/hotkeys work even when the UI
    /// is hidden.  The lock (_opLock) is held only for the instant of a buffer snapshot;
    /// heavy encoding always runs outside the lock so hotkeys and tray never stall.
    /// </summary>
    public class RecordingController
    {
        private readonly SemaphoreSlim _opLock = new(1, 1);

        public CaptureService       Capture      { get; }
        public PendingReportsStore  PendingStore { get; }

        public event Action?       PendingReportsChanged;
        public event Action<bool>? RecordingStateChanged;

        public RecordingController(string reportsRootFolder)
        {
            Capture      = new CaptureService();
            PendingStore = new PendingReportsStore(reportsRootFolder);
        }

        public bool     IsRecording          => Capture.IsRecording;
        public DateTime RecordingStartTimeUtc => Capture.RecordingStartTime;

        // ── Start / Stop ──────────────────────────────────────────────────────────

        public async Task<(bool success, string error)> StartRecordingAsync()
        {
            await _opLock.WaitAsync();
            try
            {
                var result = await Capture.StartRecordingAsync();
                RecordingStateChanged?.Invoke(Capture.IsRecording);
                return result;
            }
            finally
            {
                _opLock.Release();
            }
        }

        public void StopRecording()
        {
            try   { Capture.StopRecording(); }
            finally { RecordingStateChanged?.Invoke(Capture.IsRecording); }
        }

        // ── Snapshot + Materialize (public API for UI and tray) ───────────────────

        /// <summary>
        /// Copies the rolling buffers in microseconds. Recording is never stopped.
        /// Safe to call from any thread — SnapshotBuffers uses its own internal locks.
        /// </summary>
        public ClipSnapshot SnapshotClip() => Capture.SnapshotBuffers();

        /// <summary>
        /// Encodes a snapshot into real WAV/AVI files on a background thread.
        /// Call this after SnapshotClip(), outside any lock, while recording continues.
        /// </summary>
        public Task<(string? audioPath, string? micPath, string? aviPath, double durationSec)>
            MaterializeClipAsync(ClipSnapshot snap) => Capture.MaterializeAsync(snap);

        // ── Flag to pending (silent, no dialog) ───────────────────────────────────

        /// <summary>
        /// Snapshots the buffer (lock held only for this instant), then encodes and saves
        /// as a pending report on a background thread.  Recording continues throughout.
        /// </summary>
        public async Task<(bool success, string error)> FlagToPendingAsync()
        {
            if (!Capture.IsRecording)
                return (false, "Not recording");

            // Hold lock only for the fast snapshot
            await _opLock.WaitAsync();
            ClipSnapshot snap;
            var flagTime = DateTime.UtcNow;
            try
            {
                snap = Capture.SnapshotBuffers();
            }
            finally
            {
                _opLock.Release();
            }

            try
            {
                // Heavy encoding outside the lock — recording continues uninterrupted
                var (audioPath, micPath, aviPath, _) = await Capture.MaterializeAsync(snap);

                if (string.IsNullOrEmpty(audioPath) || !File.Exists(audioPath))
                {
                    TryDelete(audioPath);
                    TryDelete(micPath);
                    TryDelete(aviPath);
                    return (false, "No audio data captured.");
                }

                var pendingId     = Guid.NewGuid().ToString("N");
                var pendingFolder = PendingStore.GetPendingFolder(pendingId);
                Directory.CreateDirectory(pendingFolder);

                // Move files instead of copy+delete — saves one full I/O pass
                var audioDest = Path.Combine(pendingFolder, MediaFileNamer.DesktopAudio(flagTime));
                File.Move(audioPath, audioDest);

                string? micDest = null;
                if (!string.IsNullOrEmpty(micPath) && File.Exists(micPath))
                {
                    micDest = Path.Combine(pendingFolder, MediaFileNamer.MicAudio(flagTime));
                    File.Move(micPath, micDest);
                }

                string? videoDest = null;
                if (!string.IsNullOrEmpty(aviPath) && File.Exists(aviPath))
                {
                    videoDest = Path.Combine(pendingFolder, MediaFileNamer.Video(flagTime));
                    File.Move(aviPath, videoDest);
                }

                var item = new PendingReportItem
                {
                    Id                = pendingId,
                    CreatedUtc        = DateTime.UtcNow,
                    FlagUtc           = flagTime,
                    RecordingStartUtc = Capture.RecordingStartTime,
                    GameName          = string.Empty,
                    OffenderName      = string.Empty,
                    Description       = string.Empty,
                    Targeted          = false,
                    DesiredAction     = string.Empty,
                    AudioPath         = PendingStore.ToRelativePath(audioDest),
                    MicrophonePath    = micDest    != null ? PendingStore.ToRelativePath(micDest)    : null,
                    VideoPath         = videoDest  != null ? PendingStore.ToRelativePath(videoDest)  : null
                };

                var items = PendingStore.Load();
                items.Add(item);
                PendingStore.Save(items);

                PendingReportsChanged?.Invoke();
                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                DebugLog.Error($"FlagToPending failed: {ex.Message}");
                return (false, ex.Message);
            }
        }

        // ── Capture clip (used by the old sequential dialog path, kept for compatibility) ──

        /// <summary>
        /// Snapshots and materializes a clip for the Report Now path.
        /// Prefer calling SnapshotClip() + MaterializeClipAsync() directly from the UI
        /// to allow the dialog to open immediately.
        /// </summary>
        public async Task<(string? audioPath, string? micPath, string? videoPath, DateTime recordingStartUtc)>
            CaptureClipAsync()
        {
            await _opLock.WaitAsync();
            ClipSnapshot snap;
            var start = Capture.RecordingStartTime;
            try
            {
                snap = Capture.SnapshotBuffers();
            }
            finally
            {
                _opLock.Release();
            }

            var (audioPath, micPath, aviPath, _) = await Capture.MaterializeAsync(snap);
            return (audioPath, micPath, aviPath, start);
        }

        // ── Misc ──────────────────────────────────────────────────────────────────

        public void CleanupTempFiles() => Capture.CleanupTempFiles();

        public void Dispose()
        {
            try { Capture.Dispose(); }
            catch { }
        }

        private static void TryDelete(string? path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
            catch { }
        }
    }
}
