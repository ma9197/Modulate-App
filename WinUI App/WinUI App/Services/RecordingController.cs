using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinUI_App.Models;

namespace WinUI_App.Services
{
    /// <summary>
    /// App-scoped recording + pending-report persistence so tray/hotkeys work even when UI is hidden.
    /// </summary>
    public class RecordingController
    {
        private readonly SemaphoreSlim _opLock = new(1, 1);

        public CaptureService Capture { get; }
        public PendingReportsStore PendingStore { get; }

        public event Action? PendingReportsChanged;
        public event Action<bool>? RecordingStateChanged;

        public RecordingController(string reportsRootFolder)
        {
            Capture = new CaptureService();
            PendingStore = new PendingReportsStore(reportsRootFolder);
        }

        public bool IsRecording => Capture.IsRecording;
        public DateTime RecordingStartTimeUtc => Capture.RecordingStartTime;

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
            try
            {
                Capture.StopRecording();
            }
            finally
            {
                RecordingStateChanged?.Invoke(Capture.IsRecording);
            }
        }

        /// <summary>
        /// Captures the buffered clip and saves it as a pending report with empty metadata.
        /// </summary>
        public async Task<(bool success, string error)> FlagToPendingAsync()
        {
            if (!Capture.IsRecording)
            {
                return (false, "Not recording");
            }

            await _opLock.WaitAsync();
            try
            {
                var flagTime = DateTime.UtcNow;
                var savedFiles = await Task.Run(() => Capture.SaveAndRestartRecording());

                if (string.IsNullOrEmpty(savedFiles.audioPath) || !File.Exists(savedFiles.audioPath))
                {
                    TryDelete(savedFiles.audioPath);
                    TryDelete(savedFiles.micPath);
                    TryDelete(savedFiles.videoPath);
                    return (false, "No audio data captured.");
                }

                var pendingId = Guid.NewGuid().ToString("N");
                var pendingFolder = PendingStore.GetPendingFolder(pendingId);
                Directory.CreateDirectory(pendingFolder);

                var savedAt = flagTime;
                var audioDest = Path.Combine(pendingFolder, MediaFileNamer.DesktopAudio(savedAt));
                File.Copy(savedFiles.audioPath, audioDest, overwrite: true);

                string? micDest = null;
                if (!string.IsNullOrEmpty(savedFiles.micPath) && File.Exists(savedFiles.micPath))
                {
                    micDest = Path.Combine(pendingFolder, MediaFileNamer.MicAudio(savedAt));
                    File.Copy(savedFiles.micPath, micDest, overwrite: true);
                }

                string? videoDest = null;
                if (!string.IsNullOrEmpty(savedFiles.videoPath) && File.Exists(savedFiles.videoPath))
                {
                    videoDest = Path.Combine(pendingFolder, MediaFileNamer.Video(savedAt));
                    File.Copy(savedFiles.videoPath, videoDest, overwrite: true);
                }

                var item = new PendingReportItem
                {
                    Id = pendingId,
                    CreatedUtc = DateTime.UtcNow,
                    FlagUtc = flagTime,
                    RecordingStartUtc = Capture.RecordingStartTime,
                    GameName = string.Empty,
                    OffenderName = string.Empty,
                    Description = string.Empty,
                    Targeted = false,
                    DesiredAction = string.Empty,
                    AudioPath = PendingStore.ToRelativePath(audioDest),
                    MicrophonePath = micDest != null ? PendingStore.ToRelativePath(micDest) : null,
                    VideoPath = videoDest != null ? PendingStore.ToRelativePath(videoDest) : null
                };

                var items = PendingStore.Load();
                items.Add(item);
                PendingStore.Save(items);

                PendingReportsChanged?.Invoke();

                TryDelete(savedFiles.audioPath);
                TryDelete(savedFiles.micPath);
                TryDelete(savedFiles.videoPath);

                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                DebugLog.Error($"FlagToPending failed: {ex.Message}");
                return (false, ex.Message);
            }
            finally
            {
                _opLock.Release();
            }
        }

        public async Task<(string? audioPath, string? micPath, string? videoPath, DateTime recordingStartUtc)> CaptureClipAsync()
        {
            await _opLock.WaitAsync();
            try
            {
                var start = Capture.RecordingStartTime;
                var saved = await Task.Run(() => Capture.SaveAndRestartRecording());
                return (saved.audioPath, saved.micPath, saved.videoPath, start);
            }
            finally
            {
                _opLock.Release();
            }
        }

        public void CleanupTempFiles()
        {
            Capture.CleanupTempFiles();
        }

        public void Dispose()
        {
            try
            {
                Capture.Dispose();
            }
            catch { }
        }

        private static void TryDelete(string? path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch { }
        }
    }
}


