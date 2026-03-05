using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using Windows.UI;
using WinUI_App.Services;

namespace WinUI_App.Dialogs
{
    public sealed partial class UploadReportDialog : ContentDialog
    {
        // ── Form properties ──────────────────────────────────────────────────────

        public string GameName      => GameNameTextBox.Text;
        public string OffenderName  => OffenderNameTextBox.Text;
        public string Description   => DescriptionTextBox.Text;
        public bool   Targeted      => TargetedCheckBox.IsChecked ?? false;
        public string DesiredAction => DesiredActionTextBox.Text;

        // ── Trim properties ──────────────────────────────────────────────────────

        public double TrimStartSec { get; private set; }
        public double TrimEndSec   { get; private set; }

        // ── Media / trimmer state ────────────────────────────────────────────────

        private string? _desktopAudioPath;
        private string? _micAudioPath;
        private double  _clipDurationSec;
        private bool    _trimmerVisible;
        private bool    _waveformLoaded;

        private float[] _waveform = Array.Empty<float>();
        private double  _playheadSec;
        private AudioMixPlayer? _player;

        // Drag state
        private enum DragTarget { None, Start, End, Seek }
        private DragTarget _dragging = DragTarget.None;
        private float _canvasW = 1f;
        private float _canvasH = 90f;
        private const float HandleHitRadius = 14f;

        // Colors
        private static readonly Color _waveformActive   = Color.FromArgb(255, 100, 160, 255);
        private static readonly Color _waveformInactive = Color.FromArgb(80,  100, 160, 255);
        private static readonly Color _selectionFill    = Color.FromArgb(40,  100, 160, 255);
        private static readonly Color _handleColor      = Color.FromArgb(255, 100, 160, 255);
        private static readonly Color _playheadColor    = Color.FromArgb(200, 255, 255, 255);

        // ── Constructor ──────────────────────────────────────────────────────────

        public UploadReportDialog()
        {
            InitializeComponent();
            Closed += OnDialogClosed;
        }

        // ── Public API ───────────────────────────────────────────────────────────

        /// <summary>
        /// Call with true immediately after ShowAsync() to disable submit/trim while media
        /// is being materialized in the background.  Call with false once SetMediaFiles()
        /// has been called to unlock the dialog for the user.
        /// </summary>
        public void SetLoadingState(bool isLoading)
        {
            IsPrimaryButtonEnabled   = !isLoading;
            IsSecondaryButtonEnabled = !isLoading;

            if (isLoading)
            {
                ClipSummaryText.Text    = "Preparing clip\u2026";
                EditClipButton.IsEnabled = false;
            }
            else if (!string.IsNullOrEmpty(_desktopAudioPath))
            {
                EditClipButton.IsEnabled = true;
            }
        }

        public void SetInitialValues(
            string? gameName,
            string? offenderName,
            string? description,
            bool    targeted,
            string? desiredAction)
        {
            GameNameTextBox.Text        = gameName      ?? string.Empty;
            OffenderNameTextBox.Text    = offenderName  ?? string.Empty;
            DescriptionTextBox.Text     = description   ?? string.Empty;
            TargetedCheckBox.IsChecked  = targeted;
            DesiredActionTextBox.Text   = desiredAction ?? string.Empty;
        }

        /// <summary>
        /// Provide the captured audio file paths so the inline trimmer can be activated.
        /// <paramref name="durationSec"/> is the actual captured buffer length (up to 30 s).
        /// </summary>
        public void SetMediaFiles(string? desktopAudioPath, string? micAudioPath, double durationSec)
        {
            _desktopAudioPath = desktopAudioPath;
            _micAudioPath     = micAudioPath;
            _clipDurationSec  = durationSec > 0 ? durationSec : 30.0;

            // Default: last 10 seconds
            TrimStartSec = Math.Max(0.0, _clipDurationSec - 10.0);
            TrimEndSec   = _clipDurationSec;
            _playheadSec = TrimStartSec;

            UpdateClipSummary();

            if (!string.IsNullOrEmpty(_desktopAudioPath))
                EditClipButton.IsEnabled = true;
        }

        // ── Edit clip toggle ─────────────────────────────────────────────────────

        private async void EditClipButton_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_desktopAudioPath)) return;

            _trimmerVisible = !_trimmerVisible;

            if (_trimmerVisible)
            {
                EditClipButton.Content = "Close editor";
                TrimmerPanel.Visibility = Visibility.Visible;

                if (!_waveformLoaded)
                {
                    TrimmerLoadingPanel.Visibility  = Visibility.Visible;
                    TrimmerContentPanel.Visibility  = Visibility.Collapsed;

                    _waveform = await AudioTrimmerService.BuildWaveformAsync(_desktopAudioPath, 500);

                    _player = new AudioMixPlayer(DispatcherQueue);
                    _player.Load(_desktopAudioPath, _micAudioPath);
                    _player.PositionChanged += pos =>
                    {
                        _playheadSec = pos;
                        WaveformCanvas.Invalidate();
                        PositionLabel.Text = FormatTime(pos);
                    };
                    _player.PlaybackStopped += () =>
                    {
                        PlayPauseIcon.Glyph = "\uE768";
                    };

                    DurationLabel.Text = FormatTime(_clipDurationSec);
                    UpdateTrimmerLabels();
                    UpdateRulerLabels();

                    TrimmerLoadingPanel.Visibility  = Visibility.Collapsed;
                    TrimmerContentPanel.Visibility  = Visibility.Visible;

                    WaveformCanvas.Invalidate();
                    _waveformLoaded = true;
                }
            }
            else
            {
                EditClipButton.Content = "Edit clip";
                TrimmerPanel.Visibility = Visibility.Collapsed;
                _player?.Pause();
                PlayPauseIcon.Glyph = "\uE768";
            }
        }

        // ── Win2D draw ───────────────────────────────────────────────────────────

        private void WaveformCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
        {
            var ds = args.DrawingSession;
            _canvasW = (float)sender.ActualWidth;
            _canvasH = (float)sender.ActualHeight;

            if (_canvasW <= 0 || _clipDurationSec <= 0) return;

            var startX = SecondsToX(TrimStartSec);
            var endX   = SecondsToX(TrimEndSec);

            // Selection background
            ds.FillRectangle(startX, 0, endX - startX, _canvasH, _selectionFill);

            // Waveform bars
            if (_waveform.Length > 0)
            {
                var barCount = _waveform.Length;
                var barWidth = _canvasW / barCount;
                var midY = _canvasH / 2f;

                for (var i = 0; i < barCount; i++)
                {
                    var x = i * barWidth;
                    var barH = Math.Max(2f, _waveform[i] * (_canvasH * 0.88f));
                    var barSec = (i / (double)barCount) * _clipDurationSec;
                    var color = barSec >= TrimStartSec && barSec <= TrimEndSec
                        ? _waveformActive
                        : _waveformInactive;
                    ds.FillRectangle(x + 0.5f, midY - barH / 2f, Math.Max(1f, barWidth - 1f), barH, color);
                }
            }

            // Selection handle lines
            ds.DrawLine(startX, 0, startX, _canvasH, _handleColor, 2f);
            ds.DrawLine(endX,   0, endX,   _canvasH, _handleColor, 2f);

            // Handle knobs (downward-pointing triangles at top)
            DrawHandle(ds, startX, isStart: true);
            DrawHandle(ds, endX,   isStart: false);

            // Playhead
            if (_waveformLoaded)
            {
                var phX = SecondsToX(_playheadSec);
                ds.DrawLine(phX, 0, phX, _canvasH, _playheadColor, 1.5f);
            }
        }

        private void DrawHandle(CanvasDrawingSession ds, float x, bool isStart)
        {
            const float w = 9f;
            const float h = 13f;
            var path = new CanvasPathBuilder(ds);
            path.BeginFigure(x, 0);
            path.AddLine(isStart ? x + w : x - w, 0);
            path.AddLine(x, h);
            path.EndFigure(CanvasFigureLoop.Closed);
            ds.FillGeometry(CanvasGeometry.CreatePath(path), _handleColor);
        }

        // ── Pointer interaction ──────────────────────────────────────────────────

        private void WaveformCanvas_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var x = (float)e.GetCurrentPoint(WaveformCanvas).Position.X;
            var startX = SecondsToX(TrimStartSec);
            var endX   = SecondsToX(TrimEndSec);

            if (Math.Abs(x - startX) <= HandleHitRadius)
                _dragging = DragTarget.Start;
            else if (Math.Abs(x - endX) <= HandleHitRadius)
                _dragging = DragTarget.End;
            else
                _dragging = DragTarget.Seek;

            WaveformCanvas.CapturePointer(e.Pointer);
            ApplyDrag(x);
        }

        private void WaveformCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_dragging == DragTarget.None) return;
            if (!e.GetCurrentPoint(WaveformCanvas).Properties.IsLeftButtonPressed) return;
            ApplyDrag((float)e.GetCurrentPoint(WaveformCanvas).Position.X);
        }

        private void WaveformCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _dragging = DragTarget.None;
            WaveformCanvas.ReleasePointerCapture(e.Pointer);
        }

        private void WaveformCanvas_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _dragging = DragTarget.None;
        }

        private void ApplyDrag(float x)
        {
            var sec = XToSeconds(x);
            sec = Math.Max(0, Math.Min(sec, _clipDurationSec));

            switch (_dragging)
            {
                case DragTarget.Start:
                    TrimStartSec = Math.Min(sec, TrimEndSec - 0.5);
                    if (_player?.IsPlaying == false) _playheadSec = TrimStartSec;
                    break;
                case DragTarget.End:
                    TrimEndSec = Math.Max(sec, TrimStartSec + 0.5);
                    break;
                case DragTarget.Seek:
                    _playheadSec = sec;
                    _player?.SeekTo(sec);
                    break;
            }

            WaveformCanvas.Invalidate();
            UpdateTrimmerLabels();
            UpdateClipSummary();
        }

        // ── Playback ─────────────────────────────────────────────────────────────

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (_player == null) return;

            if (_player.IsPlaying)
            {
                _player.Pause();
                PlayPauseIcon.Glyph = "\uE768";
            }
            else
            {
                if (_playheadSec >= TrimEndSec - 0.05)
                {
                    _player.SeekTo(TrimStartSec);
                    _playheadSec = TrimStartSec;
                }
                _player.Play(endSec: TrimEndSec);
                PlayPauseIcon.Glyph = "\uE769";
            }
        }

        // ── Label helpers ────────────────────────────────────────────────────────

        private void UpdateTrimmerLabels()
        {
            StartTimeLabel.Text  = FormatTime(TrimStartSec);
            EndTimeLabel.Text    = FormatTime(TrimEndSec);
            SelectionLabel.Text  = $"Selected: {(TrimEndSec - TrimStartSec):0.0} s";
        }

        private void UpdateRulerLabels()
        {
            RulerStart.Text = FormatTime(0);
            RulerMid.Text   = FormatTime(_clipDurationSec / 2.0);
            RulerEnd.Text   = FormatTime(_clipDurationSec);
        }

        private void UpdateClipSummary()
        {
            var selected = TrimEndSec - TrimStartSec;
            ClipSummaryText.Text = $"{FormatTime(TrimStartSec)} – {FormatTime(TrimEndSec)}  ({selected:0.0} s)";
        }

        private static string FormatTime(double sec)
        {
            var m = (int)(sec / 60);
            var s = sec % 60;
            return $"{m}:{s:00.0}";
        }

        // ── Coordinate conversion ────────────────────────────────────────────────

        private float SecondsToX(double sec)
            => _clipDurationSec > 0 ? (float)(sec / _clipDurationSec * _canvasW) : 0f;

        private double XToSeconds(float x)
            => _canvasW > 0 ? x / _canvasW * _clipDurationSec : 0.0;

        // ── Lifecycle ────────────────────────────────────────────────────────────

        private void OnDialogClosed(ContentDialog sender, ContentDialogClosedEventArgs args)
        {
            _player?.Dispose();
            _player = null;
        }
    }
}
