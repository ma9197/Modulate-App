using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NAudio.Wave;
using SharpAvi;
using SharpAvi.Codecs;
using SharpAvi.Output;

namespace WinUI_App.Services
{
    /// <summary>
    /// Applies start/end trim offsets to WAV and AVI clip files.
    /// All times are in seconds relative to the start of the source file.
    /// </summary>
    public static class AudioTrimmerService
    {
        /// <summary>
        /// Reads PCM samples from <paramref name="sourcePath"/> in the range
        /// [<paramref name="startSec"/>, <paramref name="endSec"/>] and writes
        /// them to <paramref name="destPath"/> as a new WAV file.
        /// Returns <paramref name="destPath"/> on success, null on failure.
        /// </summary>
        public static string? TrimWav(string sourcePath, double startSec, double endSec, string destPath)
        {
            try
            {
                using var reader = new WaveFileReader(sourcePath);

                var totalSeconds = reader.TotalTime.TotalSeconds;
                startSec = Math.Max(0, Math.Min(startSec, totalSeconds));
                endSec = Math.Max(startSec, Math.Min(endSec, totalSeconds));

                reader.CurrentTime = TimeSpan.FromSeconds(startSec);

                var endPosition = (long)(endSec * reader.WaveFormat.AverageBytesPerSecond);
                // Align to block boundary
                endPosition = (endPosition / reader.WaveFormat.BlockAlign) * reader.WaveFormat.BlockAlign;
                endPosition = Math.Min(endPosition, reader.Length);

                using var writer = new WaveFileWriter(destPath, reader.WaveFormat);

                var buffer = new byte[reader.WaveFormat.AverageBytesPerSecond]; // 1-second chunks
                long remaining = endPosition - reader.Position;

                while (remaining > 0)
                {
                    var toRead = (int)Math.Min(buffer.Length, remaining);
                    var read = reader.Read(buffer, 0, toRead);
                    if (read == 0) break;
                    writer.Write(buffer, 0, read);
                    remaining -= read;
                }

                writer.Flush();
                return destPath;
            }
            catch (Exception ex)
            {
                DebugLog.Error($"TrimWav failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Reads waveform amplitude data from a WAV file for visualisation.
        /// Returns an array of <paramref name="sampleCount"/> normalised peak values in [0, 1].
        /// Each value represents the peak amplitude in that time slice.
        /// </summary>
        public static async Task<float[]> BuildWaveformAsync(string wavPath, int sampleCount = 500)
        {
            return await Task.Run(() =>
            {
                try
                {
                    using var reader = new AudioFileReader(wavPath);
                    var totalSamples = (int)(reader.Length / sizeof(float));
                    var samplesPerBar = Math.Max(1, totalSamples / sampleCount);
                    var result = new float[sampleCount];
                    var buffer = new float[samplesPerBar * reader.WaveFormat.Channels];
                    var globalMax = 0f;

                    for (var i = 0; i < sampleCount; i++)
                    {
                        var read = reader.Read(buffer, 0, buffer.Length);
                        if (read == 0) break;

                        var peak = 0f;
                        for (var j = 0; j < read; j++)
                        {
                            var abs = Math.Abs(buffer[j]);
                            if (abs > peak) peak = abs;
                        }
                        result[i] = peak;
                        if (peak > globalMax) globalMax = peak;
                    }

                    // Normalise
                    if (globalMax > 0f)
                    {
                        for (var i = 0; i < result.Length; i++)
                            result[i] /= globalMax;
                    }

                    return result;
                }
                catch (Exception ex)
                {
                    DebugLog.Error($"BuildWaveformAsync failed: {ex.Message}");
                    return new float[sampleCount];
                }
            });
        }

        /// <summary>
        /// Reads the MJPEG frames from <paramref name="sourcePath"/> that correspond to the
        /// time range [<paramref name="startSec"/>, <paramref name="endSec"/>] and writes
        /// a new AVI to <paramref name="destPath"/>.
        /// Returns <paramref name="destPath"/> on success, null on failure.
        /// </summary>
        public static string? TrimAvi(
            string sourcePath,
            double startSec,
            double endSec,
            int fps,
            int width,
            int height,
            string destPath)
        {
            try
            {
                var startFrame = (int)Math.Floor(startSec * fps);
                var endFrame = (int)Math.Ceiling(endSec * fps);

                var frames = ReadJpegFramesFromAvi(sourcePath);
                if (frames.Count == 0) return null;

                startFrame = Math.Max(0, Math.Min(startFrame, frames.Count - 1));
                endFrame = Math.Max(startFrame + 1, Math.Min(endFrame, frames.Count));

                using var aviWriter = new AviWriter(destPath)
                {
                    FramesPerSecond = fps,
                    EmitIndex1 = true
                };

                var videoStream = aviWriter.AddMJpegWpfVideoStream(width, height, quality: 70);

                for (var i = startFrame; i < endFrame; i++)
                {
                    var jpeg = frames[i];
                    using var ms = new MemoryStream(jpeg);
                    using var sourceBitmap = new Bitmap(ms);
                    using var frameBitmap = new Bitmap(width, height, PixelFormat.Format32bppRgb);
                    using (var g = Graphics.FromImage(frameBitmap))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                        g.DrawImage(sourceBitmap, 0, 0, width, height);
                    }
                    var frameData = BitmapToBgr32Tight(frameBitmap);
                    videoStream.WriteFrame(true, frameData, 0, frameData.Length);
                }

                return destPath;
            }
            catch (Exception ex)
            {
                DebugLog.Error($"TrimAvi failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Reads all JPEG-encoded frames from an MJPEG AVI file by parsing the RIFF structure.
        /// Returns raw JPEG bytes per frame.
        /// </summary>
        private static List<byte[]> ReadJpegFramesFromAvi(string path)
        {
            var frames = new List<byte[]>();
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                using var br = new BinaryReader(fs);

                // Minimal RIFF/AVI parser: look for '00dc' (video data) chunks
                var fileLen = fs.Length;
                while (fs.Position < fileLen - 8)
                {
                    var fourcc = new string(br.ReadChars(4));
                    var chunkSize = br.ReadUInt32();

                    if (fourcc == "00dc" || fourcc == "00DB")
                    {
                        // Video frame chunk
                        var data = br.ReadBytes((int)chunkSize);
                        if (chunkSize % 2 == 1 && fs.Position < fileLen)
                            br.ReadByte(); // RIFF padding byte
                        frames.Add(data);
                    }
                    else if (fourcc == "RIFF" || fourcc == "LIST")
                    {
                        // Skip the sub-type FourCC, descend into it
                        br.ReadBytes(4);
                    }
                    else
                    {
                        // Skip chunk body + optional padding
                        fs.Seek(chunkSize + (chunkSize % 2), SeekOrigin.Current);
                    }
                }
            }
            catch (Exception ex)
            {
                DebugLog.Error($"ReadJpegFramesFromAvi failed: {ex.Message}");
            }

            return frames;
        }

        private static byte[] BitmapToBgr32Tight(Bitmap bitmap)
        {
            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var bitmapData = bitmap.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            var expectedStride = bitmap.Width * 4;
            var srcStride = bitmapData.Stride;
            var bytes = new byte[expectedStride * bitmap.Height];

            if (srcStride == expectedStride)
            {
                System.Runtime.InteropServices.Marshal.Copy(bitmapData.Scan0, bytes, 0, bytes.Length);
            }
            else
            {
                var srcBase = bitmapData.Scan0;
                for (var y = 0; y < bitmap.Height; y++)
                {
                    var srcRow = IntPtr.Add(srcBase, y * srcStride);
                    System.Runtime.InteropServices.Marshal.Copy(srcRow, bytes, y * expectedStride, expectedStride);
                }
            }

            bitmap.UnlockBits(bitmapData);
            return bytes;
        }
    }
}
