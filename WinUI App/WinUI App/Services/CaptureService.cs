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
using SharpAvi.Codecs;
using SharpAvi.Output;
using Windows.Storage;

namespace WinUI_App.Services
{
    /// <summary>
    /// Handles audio and video capture with rolling buffer
    /// </summary>
    public class CaptureService : IDisposable
    {
        private const int BUFFER_DURATION_SECONDS = 30;
        private const int SAMPLE_RATE = 44100;
        private const int CHANNELS = 2;

        // Audio capture
        private WasapiLoopbackCapture? _systemAudioCapture;
        private WaveInEvent? _microphoneCapture;
        private WaveFileWriter? _systemAudioWriter;
        private WaveFileWriter? _microphoneWriter;
        private WaveFormat? _systemWaveFormat;
        private WaveFormat? _microphoneWaveFormat;

        // Rolling buffer
        private readonly Queue<AudioSegment> _audioBuffer = new();
        private readonly Queue<AudioSegment> _microphoneBuffer = new();
        private readonly object _bufferLock = new();
        private DateTime _recordingStartTime;
        private bool _isRecording;

        // Temporary file paths
        private string? _tempSystemAudioPath;
        private string? _tempMicrophonePath;
        private string? _tempVideoPath;
        
        // Screen recording
        private Thread? _screenRecordThread;
        private bool _stopScreenRecording;
        private int _framesWritten;
        private string? _lastVideoError;
        private readonly Queue<VideoFrame> _videoBuffer = new();
        private readonly object _videoBufferLock = new();
        
        // Full HD 30 FPS requirement
        private const int VIDEO_WIDTH = 1920;
        private const int VIDEO_HEIGHT = 1080;
        private const int VIDEO_FPS = 30;
        private const int VIDEO_BUFFER_FRAMES = VIDEO_FPS * BUFFER_DURATION_SECONDS;

        public bool IsRecording => _isRecording;
        public DateTime RecordingStartTime => _recordingStartTime;

        public string TempFolderPath => ApplicationData.Current.TemporaryFolder.Path;
        public int FramesWritten => _framesWritten;
        public string? LastVideoError => _lastVideoError;

        private class AudioSegment
        {
            public byte[] Data { get; set; } = Array.Empty<byte>();
            public DateTime Timestamp { get; set; }
        }

        private class VideoFrame
        {
            public byte[] JpegData { get; set; } = Array.Empty<byte>();
        }

        // P/Invoke for screen capture
        [DllImport("user32.dll")]
        private static extern IntPtr GetDesktopWindow();
        
        [DllImport("user32.dll")]
        private static extern IntPtr GetDC(IntPtr hWnd);
        
        [DllImport("user32.dll")]
        private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        
        [DllImport("gdi32.dll")]
        private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);
        
        private const int SRCCOPY = 0x00CC0020;

        /// <summary>
        /// Start continuous recording with rolling buffer
        /// </summary>
        public async Task<(bool success, string error)> StartRecordingAsync()
        {
            if (_isRecording)
            {
                return (false, "Already recording");
            }

            try
            {
                // Create temporary files with unique timestamp+ID names
                var tempFolder = ApplicationData.Current.TemporaryFolder;
                var now = DateTime.UtcNow;

                _tempSystemAudioPath = Path.Combine(tempFolder.Path, MediaFileNamer.DesktopAudio(now));
                _tempMicrophonePath = Path.Combine(tempFolder.Path, MediaFileNamer.MicAudio(now));
                _tempVideoPath = null;

                // Start system audio capture (loopback)
                _systemAudioCapture = new WasapiLoopbackCapture();
                var systemFormat = _systemAudioCapture.WaveFormat;
                _systemWaveFormat = systemFormat;
                _systemAudioWriter = new WaveFileWriter(_tempSystemAudioPath, systemFormat);

                _systemAudioCapture.DataAvailable += SystemAudio_DataAvailable;
                _systemAudioCapture.RecordingStopped += (s, e) =>
                {
                    if (e.Exception != null)
                    {
                        System.Diagnostics.Debug.WriteLine($"System audio capture error: {e.Exception.Message}");
                    }
                };

                // Start microphone capture
                try
                {
                    _microphoneCapture = new WaveInEvent
                    {
                        WaveFormat = new WaveFormat(SAMPLE_RATE, CHANNELS),
                        BufferMilliseconds = 100
                    };

                    _microphoneWaveFormat = _microphoneCapture.WaveFormat;
                    _microphoneWriter = new WaveFileWriter(_tempMicrophonePath, _microphoneCapture.WaveFormat);
                    _microphoneCapture.DataAvailable += Microphone_DataAvailable;
                    _microphoneCapture.RecordingStopped += (s, e) =>
                    {
                        if (e.Exception != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"Microphone capture error: {e.Exception.Message}");
                        }
                    };
                }
                catch (Exception micEx)
                {
                    System.Diagnostics.Debug.WriteLine($"Microphone initialization failed: {micEx.Message}");
                    // Continue without microphone
                }

                // Mark recording as started first (screen thread depends on this state)
                _recordingStartTime = DateTime.UtcNow;
                _isRecording = true;

                lock (_bufferLock)
                {
                    _audioBuffer.Clear();
                    _microphoneBuffer.Clear();
                }
                lock (_videoBufferLock)
                {
                    _videoBuffer.Clear();
                }

                // Start screen recording (spawns background thread)
                StartScreenRecording();

                _systemAudioCapture.StartRecording();
                _microphoneCapture?.StartRecording();

                return (true, string.Empty);
            }
            catch (Exception ex)
            {
                Cleanup();
                return (false, $"Failed to start recording: {ex.Message}");
            }
        }

        private void StartScreenRecording()
        {
            try
            {
                _lastVideoError = null;
                _framesWritten = 0;

                _stopScreenRecording = false;

                // Start capture thread
                _screenRecordThread = new Thread(ScreenCaptureLoop)
                {
                    IsBackground = true,
                    Priority = ThreadPriority.BelowNormal
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
            var frameIntervalSeconds = 1.0 / VIDEO_FPS;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var nextFrameTimeSeconds = 0.0;
            byte[]? lastFrameJpeg = null;

            try
            {
                // Run until StopRecording() signals stop
                while (!_stopScreenRecording)
                {
                    var nowSeconds = stopwatch.Elapsed.TotalSeconds;
                    if (nowSeconds >= nextFrameTimeSeconds)
                    {
                        var frameJpeg = CaptureScreenFrameJpeg();
                        if (frameJpeg != null)
                        {
                            AddVideoFrameToBuffer(frameJpeg);
                            lastFrameJpeg = frameJpeg;
                        }

                        nextFrameTimeSeconds += frameIntervalSeconds;
                        nowSeconds = stopwatch.Elapsed.TotalSeconds;

                        while (nowSeconds >= nextFrameTimeSeconds && lastFrameJpeg != null)
                        {
                            AddVideoFrameToBuffer(lastFrameJpeg);
                            nextFrameTimeSeconds += frameIntervalSeconds;
                            nowSeconds = stopwatch.Elapsed.TotalSeconds;
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
                System.Diagnostics.Debug.WriteLine($"Screen capture error: {ex.Message}");
            }
        }

        private byte[]? CaptureScreenFrameJpeg()
        {
            try
            {
                // Get screen dimensions
                var screenBounds = System.Windows.Forms.Screen.PrimaryScreen.Bounds;

                // Capture full screen into a bitmap
                using var screenBitmap = new Bitmap(screenBounds.Width, screenBounds.Height, PixelFormat.Format32bppRgb);
                using (var screenGraphics = Graphics.FromImage(screenBitmap))
                {
                    IntPtr desktopDC = GetDC(GetDesktopWindow());
                    IntPtr memoryDC = screenGraphics.GetHdc();

                    BitBlt(memoryDC, 0, 0, screenBounds.Width, screenBounds.Height, desktopDC, 0, 0, SRCCOPY);

                    screenGraphics.ReleaseHdc(memoryDC);
                    ReleaseDC(GetDesktopWindow(), desktopDC);
                }

                // Resize to target resolution (1920x1080)
                using var frameBitmap = new Bitmap(VIDEO_WIDTH, VIDEO_HEIGHT, PixelFormat.Format32bppRgb);
                using (var frameGraphics = Graphics.FromImage(frameBitmap))
                {
                    frameGraphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                    frameGraphics.DrawImage(screenBitmap, 0, 0, VIDEO_WIDTH, VIDEO_HEIGHT);
                }

                return EncodeJpeg(frameBitmap, 70L);
            }
            catch (Exception ex)
            {
                _lastVideoError = $"Frame capture error: {ex}";
                System.Diagnostics.Debug.WriteLine(_lastVideoError);
            }
            return null;
        }

        private void AddVideoFrameToBuffer(byte[] jpegData)
        {
            lock (_videoBufferLock)
            {
                _videoBuffer.Enqueue(new VideoFrame { JpegData = jpegData });
                while (_videoBuffer.Count > VIDEO_BUFFER_FRAMES)
                {
                    _videoBuffer.Dequeue();
                }
                Interlocked.Increment(ref _framesWritten);
            }
        }

        private byte[] EncodeJpeg(Bitmap bitmap, long quality)
        {
            using var stream = new MemoryStream();
            var codec = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
            using var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, quality);
            bitmap.Save(stream, codec, encoderParams);
            return stream.ToArray();
        }

        private byte[] BitmapToBgr32Tight(Bitmap bitmap)
        {
            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var bitmapData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);

            // SharpAvi expects tightly-packed buffer (no row padding). Many bitmaps have stride padding.
            var expectedStride = bitmap.Width * 4;
            var srcStride = bitmapData.Stride;
            var bytes = new byte[expectedStride * bitmap.Height];

            if (srcStride == expectedStride)
            {
                Marshal.Copy(bitmapData.Scan0, bytes, 0, bytes.Length);
            }
            else
            {
                // Copy row-by-row to remove padding.
                var srcBase = bitmapData.Scan0;
                for (int y = 0; y < bitmap.Height; y++)
                {
                    var srcRow = IntPtr.Add(srcBase, y * srcStride);
                    Marshal.Copy(srcRow, bytes, y * expectedStride, expectedStride);
                }
            }

            bitmap.UnlockBits(bitmapData);
            return bytes;
        }

        private void SystemAudio_DataAvailable(object? sender, WaveInEventArgs e)
        {
            if (_systemAudioWriter != null && e.BytesRecorded > 0)
            {
                _systemAudioWriter.Write(e.Buffer, 0, e.BytesRecorded);
                _systemAudioWriter.Flush();

                // Add to rolling buffer (simplified for MVP)
                lock (_bufferLock)
                {
                    _audioBuffer.Enqueue(new AudioSegment
                    {
                        Data = e.Buffer.Take(e.BytesRecorded).ToArray(),
                        Timestamp = DateTime.UtcNow
                    });

                    // Remove segments older than buffer duration
                    while (_audioBuffer.Count > 0)
                    {
                        var oldest = _audioBuffer.Peek();
                        if ((DateTime.UtcNow - oldest.Timestamp).TotalSeconds > BUFFER_DURATION_SECONDS)
                        {
                            _audioBuffer.Dequeue();
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }
        }

        private void Microphone_DataAvailable(object? sender, WaveInEventArgs e)
        {
            if (_microphoneWriter != null && e.BytesRecorded > 0)
            {
                _microphoneWriter.Write(e.Buffer, 0, e.BytesRecorded);
                _microphoneWriter.Flush();

                lock (_bufferLock)
                {
                    _microphoneBuffer.Enqueue(new AudioSegment
                    {
                        Data = e.Buffer.Take(e.BytesRecorded).ToArray(),
                        Timestamp = DateTime.UtcNow
                    });

                    while (_microphoneBuffer.Count > 0)
                    {
                        var oldest = _microphoneBuffer.Peek();
                        if ((DateTime.UtcNow - oldest.Timestamp).TotalSeconds > BUFFER_DURATION_SECONDS)
                        {
                            _microphoneBuffer.Dequeue();
                        }
                        else
                        {
                            break;
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Stop recording
        /// </summary>
        public void StopRecording()
        {
            if (!_isRecording)
            {
                return;
            }

            _isRecording = false;
            _stopScreenRecording = true;

            _systemAudioCapture?.StopRecording();
            _microphoneCapture?.StopRecording();

            // Wait for screen recording thread to finish (do not close writer while it may still write)
            if (_screenRecordThread != null && _screenRecordThread.IsAlive)
            {
                _screenRecordThread.Join(15000); // Wait up to 15 seconds
            }

            Thread.Sleep(100); // Give time for final buffers to write

            _systemAudioWriter?.Dispose();
            _microphoneWriter?.Dispose();

            _systemAudioCapture?.Dispose();
            _microphoneCapture?.Dispose();

            _systemAudioWriter = null;
            _microphoneWriter = null;
            _systemAudioCapture = null;
            _microphoneCapture = null;
        }

        /// <summary>
        /// Get captured files (audio and video)
        /// </summary>
        public (string? systemAudioPath, string? microphonePath, string? videoPath) GetCapturedFiles()
        {
            return (_tempSystemAudioPath, _tempMicrophonePath, _tempVideoPath);
        }

        /// <summary>
        /// Save current recording and immediately restart new recording
        /// </summary>
        public (string? audioPath, string? micPath, string? videoPath) SaveAndRestartRecording()
        {
            if (!_isRecording)
            {
                return (null, null, null);
            }

            var tempFolder = ApplicationData.Current.TemporaryFolder;
            var now = DateTime.UtcNow;
            var clipAudioPath = Path.Combine(tempFolder.Path, MediaFileNamer.DesktopAudio(now));
            var clipMicPath = Path.Combine(tempFolder.Path, MediaFileNamer.MicAudio(now));

            string? savedClipAudioPath = null;
            string? savedClipMicPath = null;

            if (_systemWaveFormat != null)
            {
                savedClipAudioPath = SaveAudioClipFromBuffer(_audioBuffer, _systemWaveFormat, clipAudioPath);
                if (savedClipAudioPath == null)
                {
                    savedClipAudioPath = SaveSilentAudioClip(_systemWaveFormat, clipAudioPath);
                }
            }

            if (_microphoneWaveFormat != null)
            {
                savedClipMicPath = SaveAudioClipFromBuffer(_microphoneBuffer, _microphoneWaveFormat, clipMicPath);
            }

            var savedVideoClipPath = SaveVideoClipFromBuffer();

            var fullSystemPath = _tempSystemAudioPath;
            var fullMicPath = _tempMicrophonePath;

            // Stop current recording
            StopRecording();

            // Clear temp paths so new recording gets new files
            _tempSystemAudioPath = null;
            _tempMicrophonePath = null;
            _tempVideoPath = null;

            TryDeleteFile(fullSystemPath);
            TryDeleteFile(fullMicPath);

            // Restart recording immediately
            _ = StartRecordingAsync();

            return (savedClipAudioPath, savedClipMicPath, savedVideoClipPath);
        }

        private void TryDeleteFile(string? path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private string? SaveAudioClipFromBuffer(Queue<AudioSegment> buffer, WaveFormat format, string outputPath)
        {
            List<AudioSegment> segments;
            lock (_bufferLock)
            {
                if (buffer.Count == 0)
                {
                    return null;
                }
                segments = buffer.ToList();
            }

            using var writer = new WaveFileWriter(outputPath, format);
            foreach (var segment in segments)
            {
                writer.Write(segment.Data, 0, segment.Data.Length);
            }
            writer.Flush();
            return outputPath;
        }

        private string? SaveSilentAudioClip(WaveFormat format, string outputPath)
        {
            try
            {
                var durationSeconds = BUFFER_DURATION_SECONDS;
                var totalBytes = format.AverageBytesPerSecond * durationSeconds;
                var silence = new byte[totalBytes];

                using var writer = new WaveFileWriter(outputPath, format);
                writer.Write(silence, 0, silence.Length);
                writer.Flush();
                return outputPath;
            }
            catch
            {
                return null;
            }
        }

        private string? SaveVideoClipFromBuffer()
        {
            List<VideoFrame> frames;
            lock (_videoBufferLock)
            {
                if (_videoBuffer.Count == 0)
                {
                    return null;
                }
                frames = _videoBuffer.ToList();
            }

            var tempFolder = ApplicationData.Current.TemporaryFolder;
            var videoPath = Path.Combine(tempFolder.Path, MediaFileNamer.Video(DateTime.UtcNow));

            try
            {
                using var aviWriter = new AviWriter(videoPath)
                {
                    FramesPerSecond = VIDEO_FPS,
                    EmitIndex1 = true
                };

                var videoStream = aviWriter.AddMJpegWpfVideoStream(VIDEO_WIDTH, VIDEO_HEIGHT, quality: 70);

                foreach (var frame in frames)
                {
                    using var jpegStream = new MemoryStream(frame.JpegData);
                    using var sourceBitmap = new Bitmap(jpegStream);
                    using var frameBitmap = new Bitmap(VIDEO_WIDTH, VIDEO_HEIGHT, PixelFormat.Format32bppRgb);
                    using (var g = Graphics.FromImage(frameBitmap))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                        g.DrawImage(sourceBitmap, 0, 0, VIDEO_WIDTH, VIDEO_HEIGHT);
                    }

                    var frameData = BitmapToBgr32Tight(frameBitmap);
                    videoStream.WriteFrame(true, frameData, 0, frameData.Length);
                }
            }
            catch (Exception ex)
            {
                _lastVideoError = $"Video clip write error: {ex}";
                System.Diagnostics.Debug.WriteLine(_lastVideoError);
                return null;
            }

            _tempVideoPath = videoPath;
            return videoPath;
        }

        public long GetFileSizeBytes(string? path)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    return 0;
                }
                return new FileInfo(path).Length;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>
        /// Delete temporary files
        /// </summary>
        public void CleanupTempFiles()
        {
            try
            {
                if (_tempSystemAudioPath != null && File.Exists(_tempSystemAudioPath))
                {
                    File.Delete(_tempSystemAudioPath);
                    _tempSystemAudioPath = null;
                }
            }
            catch { }

            try
            {
                if (_tempMicrophonePath != null && File.Exists(_tempMicrophonePath))
                {
                    File.Delete(_tempMicrophonePath);
                    _tempMicrophonePath = null;
                }
            }
            catch { }

            try
            {
                if (_tempVideoPath != null && File.Exists(_tempVideoPath))
                {
                    File.Delete(_tempVideoPath);
                    _tempVideoPath = null;
                }
            }
            catch { }


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

        private void Cleanup()
        {
            StopRecording();
            CleanupTempFiles();
        }

        public void Dispose()
        {
            Cleanup();
            GC.SuppressFinalize(this);
        }
    }
}

