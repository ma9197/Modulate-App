using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using Windows.Graphics;
using Screen = System.Windows.Forms.Screen;

namespace WinUI_App.Services
{
    public enum OverlayStatus
    {
        Idle,
        Recording,
        Flagged
    }

    /// <summary>
    /// Always-on-top borderless overlay pill that is visible only while the app is
    /// hidden to tray (or always, depending on OverlayEnabled setting).
    /// Shows one of three states: Idle / Recording / Flagged.
    /// </summary>
    public sealed class OverlayStatusService : IDisposable
    {
        private readonly MainWindow _mainWindow;
        private readonly RecordingController _recording;
        private readonly DispatcherTimer _flagResetTimer;

        private Window? _overlayWindow;
        private AppWindow? _appWindow;
        private TextBlock? _statusText;

        private OverlayStatus _status = OverlayStatus.Idle;
        private bool _disposed;

        // ── Constructor ──────────────────────────────────────────────────────────

        public OverlayStatusService(MainWindow mainWindow, RecordingController recording)
        {
            _mainWindow = mainWindow;
            _recording  = recording;

            // "Flagged" badge resets back to the current recording state after 2 s.
            _flagResetTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _flagResetTimer.Tick += (_, _) =>
            {
                _flagResetTimer.Stop();
                _status = _recording.IsRecording ? OverlayStatus.Recording : OverlayStatus.Idle;
                UpdateOverlayVisual();
            };
        }

        // ── Public API ───────────────────────────────────────────────────────────

        public void Start()
        {
            _mainWindow.HiddenToTrayChanged += OnHiddenToTrayChanged;
            _recording.RecordingStateChanged += OnRecordingStateChanged;

            _status = _recording.IsRecording ? OverlayStatus.Recording : OverlayStatus.Idle;

            // Show overlay immediately if enabled and app is already hidden.
            if (_mainWindow.IsHiddenToTray && IsOverlayEnabled())
            {
                EnsureOverlayWindow();
                ShowOverlay();
            }
        }

        public void MarkFlagged()
        {
            _status = OverlayStatus.Flagged;
            UpdateOverlayVisual();
            _flagResetTimer.Stop();
            _flagResetTimer.Start();
        }

        /// <summary>Call when the user changes the corner position in Settings.</summary>
        public void ApplyPositionFromSettings()
        {
            PositionOverlayWindow();
        }

        /// <summary>
        /// Call when the user toggles the overlay on/off in Settings.
        /// Respects current tray-hidden state.
        /// </summary>
        public void RefreshVisibility()
        {
            if (!IsOverlayEnabled())
            {
                HideOverlay();
                return;
            }

            if (_mainWindow.IsHiddenToTray)
            {
                EnsureOverlayWindow();
                ShowOverlay();
            }
        }

        // ── Event handlers ───────────────────────────────────────────────────────

        private void OnHiddenToTrayChanged(bool hidden)
        {
            if (hidden && IsOverlayEnabled())
            {
                EnsureOverlayWindow();
                _status = _recording.IsRecording ? OverlayStatus.Recording : OverlayStatus.Idle;
                ShowOverlay();
            }
            else
            {
                HideOverlay();
            }
        }

        private void OnRecordingStateChanged(bool isRecording)
        {
            if (_status != OverlayStatus.Flagged)
            {
                _status = isRecording ? OverlayStatus.Recording : OverlayStatus.Idle;
                UpdateOverlayVisual();
            }
        }

        // ── Window lifecycle ─────────────────────────────────────────────────────

        private void EnsureOverlayWindow()
        {
            if (_overlayWindow != null) return;

            // ── Build content ────────────────────────────────────────────────────

            _statusText = new TextBlock
            {
                Text             = "Idle",
                FontSize         = 20,
                FontWeight       = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground       = new SolidColorBrush(Colors.White),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center
            };

            var pill = new Border
            {
                Background   = new SolidColorBrush(ColorHelper.FromArgb(180, 18, 18, 18)),
                BorderBrush  = new SolidColorBrush(ColorHelper.FromArgb(80, 255, 255, 255)),
                BorderThickness = new Thickness(1),
                CornerRadius    = new CornerRadius(10),
                Padding         = new Thickness(18, 8, 18, 8),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment   = VerticalAlignment.Top,
                Child = _statusText
            };

            var root = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
            root.Children.Add(pill);

            // ── Create the WinUI window ──────────────────────────────────────────

            _overlayWindow = new Window
            {
                Content                    = root,
                ExtendsContentIntoTitleBar = true
            };

            // Remove title bar handles so the grid background reads "no title bar"
            _overlayWindow.ExtendsContentIntoTitleBar = true;

            // Retrieve AppWindow so we can configure the presenter
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_overlayWindow);
            var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
            _appWindow = AppWindow.GetFromWindowId(windowId);

            // ── Strip chrome via OverlappedPresenter ─────────────────────────────
            if (_appWindow.Presenter is OverlappedPresenter p)
            {
                p.SetBorderAndTitleBar(false, false);
                p.IsResizable     = false;
                p.IsMaximizable   = false;
                p.IsMinimizable   = false;
                p.IsAlwaysOnTop   = true;
            }

            // Small pill size + hide from Alt-Tab / taskbar
            _appWindow.Resize(new SizeInt32(180, 56));
            _appWindow.IsShownInSwitchers = false;

            // Activate once so WinUI initialises the HWND
            _overlayWindow.Activate();

            // Force TOPMOST via SetWindowPos (belt-and-suspenders with IsAlwaysOnTop)
            MakeTopMost(hwnd);

            UpdateOverlayVisual();
            PositionOverlayWindow();

            // Start hidden — ShowOverlay() will reveal it when needed
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
        }

        private void ShowOverlay()
        {
            if (_overlayWindow == null || _appWindow == null) return;
            if (!IsOverlayEnabled()) return;

            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_overlayWindow);
                UpdateOverlayVisual();
                PositionOverlayWindow();
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);
                MakeTopMost(hwnd);
            }
            catch { }
        }

        private void HideOverlay()
        {
            if (_overlayWindow == null) return;
            try
            {
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_overlayWindow);
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
            }
            catch { }
        }

        private void MakeTopMost(IntPtr hwnd)
        {
            NativeMethods.SetWindowPos(
                hwnd,
                NativeMethods.HWND_TOPMOST,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }

        // ── Positioning ──────────────────────────────────────────────────────────

        private void PositionOverlayWindow()
        {
            if (_appWindow == null) return;

            try
            {
                var primary = Screen.PrimaryScreen;
                if (primary == null) return;

                var wa = primary.WorkingArea;
                const int w      = 180;
                const int h      = 56;
                const int margin = 14;

                int x, y;
                var pos = (App.Settings.OverlayPosition ?? "TopRight").Trim();

                switch (pos)
                {
                    case "TopLeft":
                        x = wa.Left + margin;
                        y = wa.Top  + margin;
                        break;
                    case "BottomLeft":
                        x = wa.Left + margin;
                        y = wa.Bottom - h - margin;
                        break;
                    case "BottomRight":
                        x = wa.Right  - w - margin;
                        y = wa.Bottom - h - margin;
                        break;
                    default: // TopRight
                        x = wa.Right - w - margin;
                        y = wa.Top   + margin;
                        break;
                }

                _appWindow.Move(new PointInt32(x, y));
            }
            catch { }
        }

        // ── Visual update ────────────────────────────────────────────────────────

        private void UpdateOverlayVisual()
        {
            if (_statusText == null) return;

            switch (_status)
            {
                case OverlayStatus.Recording:
                    _statusText.Text       = "Recording";
                    _statusText.Foreground = new SolidColorBrush(Colors.OrangeRed);
                    break;
                case OverlayStatus.Flagged:
                    _statusText.Text       = "Flagged";
                    _statusText.Foreground = new SolidColorBrush(Colors.Gold);
                    break;
                default:
                    _statusText.Text       = "Idle";
                    _statusText.Foreground = new SolidColorBrush(Colors.White);
                    break;
            }
        }

        // ── Helpers ──────────────────────────────────────────────────────────────

        private static bool IsOverlayEnabled()
            => App.Settings.OverlayEnabled;

        // ── Dispose ──────────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _mainWindow.HiddenToTrayChanged -= OnHiddenToTrayChanged; } catch { }
            try { _recording.RecordingStateChanged -= OnRecordingStateChanged; } catch { }
            try { _flagResetTimer.Stop(); } catch { }

            try
            {
                if (_overlayWindow != null)
                {
                    var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_overlayWindow);
                    NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
                }
            }
            catch { }
        }
    }
}
