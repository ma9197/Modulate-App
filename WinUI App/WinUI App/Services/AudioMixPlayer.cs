using System;
using System.IO;
using Microsoft.UI.Dispatching;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace WinUI_App.Services
{
    /// <summary>
    /// Loads up to two WAV files (desktop audio + optional microphone), mixes them,
    /// and provides seekable playback with position reporting on the UI thread.
    /// </summary>
    public sealed class AudioMixPlayer : IDisposable
    {
        private readonly DispatcherQueue _dispatcher;

        private WaveOutEvent? _output;
        private MixingSampleProvider? _mixer;

        // Readers kept alive so we can seek
        private AudioFileReader? _desktopReader;
        private AudioFileReader? _micReader;

        private OffsetSampleProvider? _desktopTrimmed;
        private OffsetSampleProvider? _micTrimmed;

        // Position tracking
        private System.Threading.Timer? _posTimer;
        private double _startOffsetSec;
        private double _endSec;
        private bool _isPlaying;

        // ── Public surface ───────────────────────────────────────────────────────

        public double DurationSeconds { get; private set; }
        public bool IsPlaying => _isPlaying;

        /// <summary>
        /// Raised on the UI thread approximately every 50 ms while playing.
        /// Argument is the current playback position in seconds from the start of the file.
        /// </summary>
        public event Action<double>? PositionChanged;

        /// <summary>
        /// Raised on the UI thread when playback reaches the end position or is stopped.
        /// </summary>
        public event Action? PlaybackStopped;

        public AudioMixPlayer(DispatcherQueue dispatcher)
        {
            _dispatcher = dispatcher;
        }

        // ── Loading ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Loads and prepares playback for the given files.
        /// Call this once; call <see cref="SeekTo"/> / <see cref="Play"/> afterward.
        /// </summary>
        public void Load(string desktopPath, string? micPath = null)
        {
            DisposePlayback();

            _desktopReader = new AudioFileReader(desktopPath);
            DurationSeconds = _desktopReader.TotalTime.TotalSeconds;

            var targetFormat = WaveFormat.CreateIeeeFloatWaveFormat(
                _desktopReader.WaveFormat.SampleRate, 2);

            _mixer = new MixingSampleProvider(targetFormat) { ReadFully = true };

            // Desktop audio channel
            var desktopResampled = new WdlResamplingSampleProvider(
                ToStereoIfMono(_desktopReader), targetFormat.SampleRate);
            _desktopTrimmed = new OffsetSampleProvider(desktopResampled);
            _mixer.AddMixerInput(_desktopTrimmed);

            // Microphone channel (optional)
            if (!string.IsNullOrEmpty(micPath) && File.Exists(micPath))
            {
                _micReader = new AudioFileReader(micPath);
                var micResampled = new WdlResamplingSampleProvider(
                    ToStereoIfMono(_micReader), targetFormat.SampleRate);
                _micTrimmed = new OffsetSampleProvider(micResampled);
                _mixer.AddMixerInput(_micTrimmed);
            }

            _output = new WaveOutEvent { DesiredLatency = 100 };
            _output.Init(_mixer);
            _output.PlaybackStopped += OnPlaybackStopped;
        }

        // ── Transport ────────────────────────────────────────────────────────────

        /// <summary>
        /// Seek both readers to <paramref name="positionSec"/> seconds.
        /// Safe to call while paused or stopped.
        /// </summary>
        public void SeekTo(double positionSec)
        {
            if (_desktopReader == null) return;

            positionSec = Math.Max(0, Math.Min(positionSec, DurationSeconds));

            var wasPlaying = _isPlaying;
            if (wasPlaying) _output?.Pause();

            _desktopReader.CurrentTime = TimeSpan.FromSeconds(positionSec);
            if (_micReader != null)
            {
                var micSec = Math.Min(positionSec, _micReader.TotalTime.TotalSeconds);
                _micReader.CurrentTime = TimeSpan.FromSeconds(micSec);
            }

            if (wasPlaying) _output?.Play();
            DispatchPosition(positionSec);
        }

        /// <summary>
        /// Start playback, stopping automatically at <paramref name="endSec"/>.
        /// </summary>
        public void Play(double endSec = double.MaxValue)
        {
            if (_output == null || _desktopReader == null) return;

            _endSec = Math.Min(endSec, DurationSeconds);
            _startOffsetSec = _desktopReader.CurrentTime.TotalSeconds;
            _isPlaying = true;
            _output.Play();

            _posTimer = new System.Threading.Timer(_ => OnPositionTick(), null, 0, 50);
        }

        public void Pause()
        {
            if (!_isPlaying) return;
            _isPlaying = false;
            _output?.Pause();
            StopTimer();
        }

        public void Stop()
        {
            _isPlaying = false;
            _output?.Stop();
            StopTimer();
            SeekTo(0);
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private void OnPositionTick()
        {
            if (_desktopReader == null) return;

            var pos = _desktopReader.CurrentTime.TotalSeconds;
            DispatchPosition(pos);

            if (pos >= _endSec)
            {
                _isPlaying = false;
                _output?.Pause();
                StopTimer();
                _dispatcher.TryEnqueue(() => PlaybackStopped?.Invoke());
            }
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            _isPlaying = false;
            StopTimer();
            _dispatcher.TryEnqueue(() => PlaybackStopped?.Invoke());
        }

        private void DispatchPosition(double pos)
        {
            _dispatcher.TryEnqueue(() => PositionChanged?.Invoke(pos));
        }

        private void StopTimer()
        {
            _posTimer?.Dispose();
            _posTimer = null;
        }

        private static ISampleProvider ToStereoIfMono(ISampleProvider src)
        {
            if (src.WaveFormat.Channels == 1)
                return new MonoToStereoSampleProvider(src);
            return src;
        }

        private void DisposePlayback()
        {
            StopTimer();
            _output?.Stop();
            _output?.Dispose();
            _desktopReader?.Dispose();
            _micReader?.Dispose();
            _output = null;
            _desktopReader = null;
            _micReader = null;
            _desktopTrimmed = null;
            _micTrimmed = null;
            _mixer = null;
        }

        public void Dispose()
        {
            DisposePlayback();
        }
    }
}
