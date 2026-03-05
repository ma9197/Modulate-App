using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using SharpAvi;
using SharpAvi.Output;
using Windows.Storage;

namespace WinUI_App.Services
{
    /// <summary>
    /// Immutable snapshot of the rolling buffers taken at a single instant.
    /// Passed to MaterializeAsync to encode files on a background thread
    /// while recording continues uninterrupted.
    /// </summary>
    public sealed record ClipSnapshot(
        DateTime     CapturedAtUtc,
        List<byte[]> SystemAudioChunks,
        List<byte[]> MicAudioChunks,
        List<byte[]> VideoJpegs,
        WaveFormat?  SystemFormat,
        WaveFormat?  MicFormat);

    /// <summary>
    /// Handles audio and video capture with a rolling in-memory buffer.
    /// Temporary file writes during recording have been removed — only in-memory
    /// buffers are maintained. Clip files are produced on-demand via MaterializeAsync.
    /// </summary>
    public class CaptureService : IDisposable
    {
        private const int BUFFER_DURATION_SECONDS = 30;
        private const int SAMPLE_RATE             = 44100;
        private const int CHANNELS                = 2;

        // Audio capture devices
        private WasapiLoopbackCapture? _systemAudioCapture;
        private WaveInEvent?           _microphoneCapture;
        private WaveFormat?            _systemWaveFormat;
        private WaveFormat?            _microphoneWaveFormat;
        private int                    _preferredMicrophoneDeviceNumber = -1;

        // Rolling audio buffers
        private readonly Queue<AudioSegment> _audioBuffer       = new();
        private readonly Queue<AudioSegment> _microphoneBuffer  = new();
        private readonly object              _bufferLock        = new();
        private DateTime _recordingStartTime;
        private bool     _isRecording;

        // Screen recording
        private Thread? _screenRecordThread;
        private bool    _stopScreenRecording;
        private int     _framesWritten;
        private string? _lastVideoError;

        // Rolling video buffer
        private readonly Queue<VideoFrame> _videoBuffer      = new();
        private readonly object            _videoBufferLock  = new();

        private const int VIDEO_WIDTH         = 1920;
        private const int VIDEO_HEIGHT        = 1080;
        private const int VIDEO_FPS           = 30;
        private const int VIDEO_BUFFER_FRAMES = VIDEO_FPS * BUFFER_DURATION_SECONDS;

        public bool     IsRecording        => _isRecording;
        public DateTime RecordingStartTime => _recordingStartTime;
        public string   TempFolderPath     => ApplicationData.Current.TemporaryFolder.Path;
        public int      FramesWritten      => _framesWritten;
        public string?  LastVideoError     => _lastVideoError;
        public int      PreferredMicrophoneDeviceNumber => _preferredMicrophoneDeviceNumber;

        public sealed record MicrophoneDeviceInfo(int DeviceNumber, string Name);

        private sealed class AudioSegment
        {
            public byte[]   Data      { get; set; } = Array.Empty<byte>();
            public DateTime Timestamp { get; set; }
        }

        private sealed class VideoFrame
        {
            public byte[] JpegData { get; set; } = Array.Empty<byte>();
        }

        public static IReadOnlyList<MicrophoneDeviceInfo> GetMicrophoneDevices()
        {
            var devices = new List<MicrophoneDeviceInfo>();
            try
            {
                for (int i = 0; i < WaveIn.DeviceCount; i++)
                {
                    var caps = WaveIn.GetCapabilities(i);
                    devices.Add(new MicrophoneDeviceInfo(i, caps.ProductName));
                }
            }
            catch
            {
                // Keep empty list on failure; UI will show default-only option.
            }
            return devices;
        }

        /// <summary>
        /// Sets the preferred WaveIn device number (-1 = default system device).
        /// Returns false if invalid or recording is currently active.
        /// </summary>
        public bool SetPreferredMicrophoneDevice(int deviceNumber)
        {
            if (_isRecording)
            {
                return false;
            }

            if (deviceNumber < -1)
            {
                return false;
            }

            if (deviceNumber >= 0 && deviceNumber >= WaveIn.DeviceCount)
            {
                return false;
            }

            _preferredMicrophoneDeviceNumber = deviceNumber;
            return true;
        }

        // P/Invoke for GDI screen capture
        [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern int    ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")]  private static extern bool   BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);
        private const int SRCCOPY = 0x00CC0020;

        // ── Snapshot ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Copies the current rolling buffer contents into a ClipSnapshot.
        /// Executes in microseconds — recording is never stopped or paused.
        /// </summary>
        public ClipSnapshot SnapshotBuffers()
        {
            List<byte[]> sys, mic;
            lock (_bufferLock)
            {
                sys = _audioBuffer.Select(x => x.Data).ToList();
                mic = _microphoneBuffer.Select(x => x.Data).ToList();
            }

            List<byte[]> vid;
            lock (_videoBufferLock)
            {
                vid = _videoBuffer.Select(x => x.JpegData).ToList();
            }

            return new ClipSnapshot(DateTime.UtcNow, sys, mic, vid, _systemWaveFormat, _microphoneWaveFormat);
        }

        // ── Materialize ───────────────────────────────────────────────────────────

        /// <summary>
        /// Encodes a ClipSnapshot into WAV and AVI files on a background thread.
        /// Recording continues uninterrupted during this call.
        /// Video optimization: JPEGs are already VIDEO_WIDTHxVIDEO_HEIGHT (encoded that way
        /// by CaptureScreenFrameJpeg), so we decode once directly to BGR32 — no second resize.
        /// </summary>
        public Task<(string? sysWav, string? micWav, string? avi, double durationSec)> MaterializeAsync(ClipSnapshot snap)
        {
            return Task.Run(() =>
            {
                var tempFolder = ApplicationData.Current.TemporaryFolder.Path;
                var now        = snap.CapturedAtUtc;

                string? sysWavPath  = null;
                string? micWavPath  = null;
                string? aviPath     = null;
                double  durationSec = 0;

                var videoDurationSec = snap.VideoJpegs.Count > 0
                    ? snap.VideoJpegs.Count / (double)VIDEO_FPS
                    : 0;
                var micDurationSec = (snap.MicFormat != null && snap.MicAudioChunks.Count > 0)
                    ? snap.MicAudioChunks.Sum(c => (long)c.Length) / (double)snap.MicFormat.AverageBytesPerSecond
                    : 0;

                // System audio: always materialize when format exists.
                // If loopback chunks are empty (e.g., user captured silence), create a short silent WAV
                // so Flag/Report still works instead of failing with "No audio data captured."
                if (snap.SystemFormat != null)
                {
                    sysWavPath = Path.Combine(tempFolder, MediaFileNamer.DesktopAudio(now));
                    using var writer = new WaveFileWriter(sysWavPath, snap.SystemFormat);
                    if (snap.SystemAudioChunks.Count > 0)
                    {
                        foreach (var chunk in snap.SystemAudioChunks)
                            writer.Write(chunk, 0, chunk.Length);
                        var totalBytes = snap.SystemAudioChunks.Sum(c => (long)c.Length);
                        durationSec = totalBytes / (double)snap.SystemFormat.AverageBytesPerSecond;
                    }
                    else
                    {
                        var fallbackDuration = Math.Clamp(
                            Math.Max(1.0, Math.Max(micDurationSec, videoDurationSec)),
                            1.0,
                            BUFFER_DURATION_SECONDS);
                        var bytes = (int)(snap.SystemFormat.AverageBytesPerSecond * fallbackDuration);
                        var silence = new byte[bytes];
                        writer.Write(silence, 0, silence.Length);
                        durationSec = fallbackDuration;
                    }
                    writer.Flush();
                }

                // Microphone audio
                if (snap.MicFormat != null && snap.MicAudioChunks.Count > 0)
                {
                    micWavPath = Path.Combine(tempFolder, MediaFileNamer.MicAudio(now));
                    using var writer = new WaveFileWriter(micWavPath, snap.MicFormat);
                    foreach (var chunk in snap.MicAudioChunks)
                        writer.Write(chunk, 0, chunk.Length);
                    writer.Flush();
                }

                // Fallback: if system loopback format is unavailable but mic exists, produce a primary WAV from mic.
                if (sysWavPath == null && snap.MicFormat != null && snap.MicAudioChunks.Count > 0)
                {
                    sysWavPath = Path.Combine(tempFolder, MediaFileNamer.DesktopAudio(now));
                    using var writer = new WaveFileWriter(sysWavPath, snap.MicFormat);
                    foreach (var chunk in snap.MicAudioChunks)
                        writer.Write(chunk, 0, chunk.Length);
                    writer.Flush();
                    durationSec = micDurationSec;
                }

                // Video — zero-decode path: frames are already JPEG at target resolution.
                // Write raw JPEG bytes directly as MJPEG AVI frames.
                // No Bitmap decode, no BGR32 conversion, no re-encode.
                if (snap.VideoJpegs.Count > 0)
                {
                    aviPath = Path.Combine(tempFolder, MediaFileNamer.Video(now));
                    try
                    {
                        using var aviWriter = new AviWriter(aviPath)
                        {
                            FramesPerSecond = VIDEO_FPS,
                            EmitIndex1      = true
                        };

                        var stream   = aviWriter.AddVideoStream(VIDEO_WIDTH, VIDEO_HEIGHT, BitsPerPixel.Bpp24);
                        stream.Codec = new FourCC("MJPG");

                        foreach (var jpeg in snap.VideoJpegs)
                            stream.WriteFrame(true, jpeg, 0, jpeg.Length);
                    }
                    catch (Exception ex)
                    {
                        _lastVideoError = $"Video materialize error: {ex.Message}";
                        System.Diagnostics.Debug.WriteLine(_lastVideoError);
                        TryDeleteFile(aviPath);
                        aviPath = null;
                    }
                }

                return (sysWavPath, micWavPath, aviPath, durationSec);
            });
        }

        // ── Recording lifecycle ───────────────────────────────────────────────────

        /// <summary>
        /// Starts continuous recording. Only in-memory buffers are maintained;
        /// no temp files are written to disk during recording.
        /// </summary>
        public async Task<(bool success, string error)> StartRecordingAsync()
        {
            if (_isRecording)
                return (false, "Already recording");

            try
            {
                // System audio (loopback)
                _systemAudioCapture = new WasapiLoopbackCapture();
                _systemWaveFormat   = _systemAudioCapture.WaveFormat;
                _systemAudioCapture.DataAvailable    += SystemAudio_DataAvailable;
                _systemAudioCapture.RecordingStopped += (s, e) =>
                {
                    if (e.Exception != null)
                        System.Diagnostics.Debug.WriteLine($"System audio error: {e.Exception.Message}");
                };

                // Microphone (optional — continues without it on failure)
                try
                {
                    if (WaveIn.DeviceCount <= 0)
                    {
                        throw new InvalidOperationException("No microphone input devices found.");
                    }

                    _microphoneCapture = new WaveInEvent
                    {
                        DeviceNumber      = _preferredMicrophoneDeviceNumber >= 0 ? _preferredMicrophoneDeviceNumber : 0,
                        WaveFormat         = new WaveFormat(SAMPLE_RATE, CHANNELS),
                        BufferMilliseconds = 100
                    };
                    _microphoneWaveFormat               = _microphoneCapture.WaveFormat;
                    _microphoneCapture.DataAvailable    += Microphone_DataAvailable;
                    _microphoneCapture.RecordingStopped += (s, e) =>
                    {
                        if (e.Exception != null)
                            System.Diagnostics.Debug.WriteLine($"Mic error: {e.Exception.Message}");
                    };
                }
                catch (Exception micEx)
                {
                    System.Diagnostics.Debug.WriteLine($"Microphone init failed: {micEx.Message}");
                }

                _recordingStartTime = DateTime.UtcNow;
                _isRecording        = true;

                lock (_bufferLock)
                {
                    _audioBuffer.Clear();
                    _microphoneBuffer.Clear();
                }
                lock (_videoBufferLock)
                {
                    _videoBuffer.Clear();
                }

                StartScreenRecording();
                _systemAudioCapture.StartRecording();
                _microphoneCapture?.StartRecording();

                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                CleanupCapture();
                return (false, $"Failed to start recording: {ex.Message}");
            }
        }

        public void StopRecording()
        {
            if (!_isRecording) return;

            _isRecording         = false;
            _stopScreenRecording = true;

            _systemAudioCapture?.StopRecording();
            _microphoneCapture?.StopRecording();

            if (_screenRecordThread?.IsAlive == true)
                _screenRecordThread.Join(15_000);

            Thread.Sleep(100);
            CleanupCapture();
        }

        private void CleanupCapture()
        {
            _systemAudioCapture?.Dispose();
            _microphoneCapture?.Dispose();
            _systemAudioCapture = null;
            _microphoneCapture  = null;
        }

        // ── Screen recording ──────────────────────────────────────────────────────

        private void StartScreenRecording()
        {
            try
            {
                _lastVideoError      = null;
                _framesWritten       = 0;
                _stopScreenRecording = false;

                _screenRecordThread = new Thread(ScreenCaptureLoop)
                {
                    IsBackground = true,
                    Priority     = ThreadPriority.BelowNormal
                };
                _screenRecordThread.Start();
            }
            catch (Exception ex)
            {
                _lastVideoError = $"Screen recording start failed: {ex.Message}";
                System.Diagnostics.Debug.WriteLine(_lastVideoError);
            }
        }

        private void ScreenCaptureLoop()
        {
            var frameInterval     = 1.0 / VIDEO_FPS;
            var sw                = System.Diagnostics.Stopwatch.StartNew();
            var nextFrameTime     = 0.0;
            byte[]? lastFrameJpeg = null;

            try
            {
                while (!_stopScreenRecording)
                {
                    var now = sw.Elapsed.TotalSeconds;
                    if (now >= nextFrameTime)
                    {
                        var jpeg = CaptureScreenFrameJpeg();
                        if (jpeg != null)
                        {
                            AddVideoFrameToBuffer(jpeg);
                            lastFrameJpeg = jpeg;
                        }

                        nextFrameTime += frameInterval;
                        now = sw.Elapsed.TotalSeconds;

                        // Catch up dropped frames by repeating last frame
                        while (now >= nextFrameTime && lastFrameJpeg != null)
                        {
                            AddVideoFrameToBuffer(lastFrameJpeg);
                            nextFrameTime += frameInterval;
                            now = sw.Elapsed.TotalSeconds;
                        }
                    }
                    else
                    {
                        Thread.Sleep(1);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Screen capture loop error: {ex.Message}");
            }
        }

        private byte[]? CaptureScreenFrameJpeg()
        {
            try
            {
                var bounds = System.Windows.Forms.Screen.PrimaryScreen.Bounds;

                // Capture raw screen
                using var screenBitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppRgb);
                using (var g = Graphics.FromImage(screenBitmap))
                {
                    var desktopDC = GetDC(GetDesktopWindow());
                    var memDC     = g.GetHdc();
                    BitBlt(memDC, 0, 0, bounds.Width, bounds.Height, desktopDC, 0, 0, SRCCOPY);
                    g.ReleaseHdc(memDC);
                    ReleaseDC(GetDesktopWindow(), desktopDC);
                }

                // Skip resize if screen is already the target resolution
                if (bounds.Width == VIDEO_WIDTH && bounds.Height == VIDEO_HEIGHT)
                    return EncodeJpeg(screenBitmap, 70L);

                using var frameBitmap = new Bitmap(VIDEO_WIDTH, VIDEO_HEIGHT, PixelFormat.Format32bppRgb);
                using (var g = Graphics.FromImage(frameBitmap))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                    g.DrawImage(screenBitmap, 0, 0, VIDEO_WIDTH, VIDEO_HEIGHT);
                }

                return EncodeJpeg(frameBitmap, 70L);
            }
            catch (Exception ex)
            {
                _lastVideoError = $"Frame capture error: {ex}";
                System.Diagnostics.Debug.WriteLine(_lastVideoError);
                return null;
            }
        }

        private void AddVideoFrameToBuffer(byte[] jpegData)
        {
            lock (_videoBufferLock)
            {
                _videoBuffer.Enqueue(new VideoFrame { JpegData = jpegData });
                while (_videoBuffer.Count > VIDEO_BUFFER_FRAMES)
                    _videoBuffer.Dequeue();
                Interlocked.Increment(ref _framesWritten);
            }
        }

        private byte[] EncodeJpeg(Bitmap bitmap, long quality)
        {
            using var stream = new MemoryStream();
            var codec        = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
            using var ep     = new EncoderParameters(1);
            ep.Param[0]      = new EncoderParameter(Encoder.Quality, quality);
            bitmap.Save(stream, codec, ep);
            return stream.ToArray();
        }

        // ── Audio data handlers ───────────────────────────────────────────────────

        private void SystemAudio_DataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded <= 0) return;

            lock (_bufferLock)
            {
                _audioBuffer.Enqueue(new AudioSegment
                {
                    Data      = e.Buffer.Take(e.BytesRecorded).ToArray(),
                    Timestamp = DateTime.UtcNow
                });
                EvictOldSegments(_audioBuffer);
            }
        }

        private void Microphone_DataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded <= 0) return;

            lock (_bufferLock)
            {
                _microphoneBuffer.Enqueue(new AudioSegment
                {
                    Data      = e.Buffer.Take(e.BytesRecorded).ToArray(),
                    Timestamp = DateTime.UtcNow
                });
                EvictOldSegments(_microphoneBuffer);
            }
        }

        private static void EvictOldSegments(Queue<AudioSegment> queue)
        {
            var cutoff = DateTime.UtcNow.AddSeconds(-BUFFER_DURATION_SECONDS);
            while (queue.Count > 0 && queue.Peek().Timestamp < cutoff)
                queue.Dequeue();
        }

        // ── Utility ───────────────────────────────────────────────────────────────

        public long GetFileSizeBytes(string? path)
        {
            try { return string.IsNullOrEmpty(path) || !File.Exists(path) ? 0 : new FileInfo(path).Length; }
            catch { return 0; }
        }

        /// <summary>
        /// Clears rolling in-memory buffers. Call after stopping recording or logging out.
        /// No files are deleted because no temp files are written during recording.
        /// </summary>
        public void CleanupTempFiles()
        {
            lock (_bufferLock)
            {
                _audioBuffer.Clear();
                _microphoneBuffer.Clear();
            }
            lock (_videoBufferLock)
            {
                _videoBuffer.Clear();
            }
        }

        private static void TryDeleteFile(string? path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); }
            catch { }
        }

        public void Dispose()
        {
            StopRecording();
            CleanupTempFiles();
            GC.SuppressFinalize(this);
        }
    }
}
