using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using NAudio.Wave;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using WinUI_App.Dialogs;
using WinUI_App.Models;
using WinUI_App.Services;

namespace WinUI_App.Views
{
    public sealed partial class MainPage : Page
    {
        private SupabaseAuthService? _authService;
        private readonly ReportsApiClient _reportsClient;
        private readonly CaptureService _captureService;
        private readonly PendingReportsStore _pendingStore;
        private readonly ObservableCollection<PendingReportItem> _pendingItems = new();

        private DateTime _flagTime;
        private string? _lastReportFolderPath;
        private bool _isUploading;
        private bool _isCapturingClip;
        private DispatcherTimer? _recordingTimer;
        private DispatcherTimer? _snackbarTimer;
        private string _currentViewTag = "record";
        private bool _subscribedToAppEvents;

        public MainPage()
        {
            this.InitializeComponent();
            _reportsClient = new ReportsApiClient();

            // Use app-scoped controller so tray/hotkeys and UI share the same recording state.
            _captureService = App.Recording.Capture;
            _pendingStore = App.Recording.PendingStore;

            PendingReportsListView.ItemsSource = _pendingItems;

            AttachCardShadows();

            this.Loaded += MainPage_Loaded;
            this.Unloaded += MainPage_Unloaded;

            // Default: Record
            if (RootNav.MenuItems.Count > 0)
            {
                RootNav.SelectedItem = RootNav.MenuItems[0];
            }

            InitTimers();
            UpdateControlState();
            UpdateRecordingUi();
            LoadSettingsUi();
        }

        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            if (_subscribedToAppEvents)
            {
                return;
            }

            _subscribedToAppEvents = true;

            App.Recording.RecordingStateChanged += Recording_RecordingStateChanged;
            App.Recording.PendingReportsChanged += Recording_PendingReportsChanged;

            // If Windows toasts aren't available (unpackaged runs), fall back to in-app snackbar.
            App.Toasts.FallbackNotificationRequested += Toasts_FallbackNotificationRequested;

            // Sync UI to current state (important if recording was started via hotkey/tray before UI appeared).
            Recording_RecordingStateChanged(App.Recording.IsRecording);
        }

        private void MainPage_Unloaded(object sender, RoutedEventArgs e)
        {
            if (!_subscribedToAppEvents)
            {
                return;
            }

            _subscribedToAppEvents = false;

            try { App.Recording.RecordingStateChanged -= Recording_RecordingStateChanged; } catch { }
            try { App.Recording.PendingReportsChanged -= Recording_PendingReportsChanged; } catch { }
            try { App.Toasts.FallbackNotificationRequested -= Toasts_FallbackNotificationRequested; } catch { }
        }

        private void Recording_RecordingStateChanged(bool isRecording)
        {
            try
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (isRecording)
                    {
                        _recordingTimer?.Start();
                    }
                    else
                    {
                        _recordingTimer?.Stop();
                    }

                    UpdateControlState();
                    UpdateRecordingUi();
                });
            }
            catch { }
        }

        private void Recording_PendingReportsChanged()
        {
            try
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    LoadPendingReports();
                });
            }
            catch { }
        }

        private void Toasts_FallbackNotificationRequested(string title, string body)
        {
            try
            {
                // Keep it short; snackbar is already used as the app's status surface.
                ShowStatus($"{title}: {body}", InfoBarSeverity.Informational);
            }
            catch { }
        }

        private void AttachCardShadows()
        {
            try
            {
                if (RecordCard.Shadow is Microsoft.UI.Xaml.Media.ThemeShadow recordShadow)
                {
                    recordShadow.Receivers.Add(ShadowReceiver);
                }
                if (PendingCard.Shadow is Microsoft.UI.Xaml.Media.ThemeShadow pendingShadow)
                {
                    pendingShadow.Receivers.Add(ShadowReceiver);
                }
                if (SettingsCard.Shadow is Microsoft.UI.Xaml.Media.ThemeShadow settingsShadow)
                {
                    settingsShadow.Receivers.Add(ShadowReceiver);
                }
            }
            catch
            {
                // Shadow is a nice-to-have; don't fail the page if it can't attach.
            }
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is SupabaseAuthService authService)
            {
                _authService = authService;
                
                if (_authService.IsAuthenticated && _authService.CurrentUser != null)
                {
                    UserEmailText.Text = $"Logged in as: {_authService.CurrentUser.Email}";
                }
            }
            else
            {
                Frame.Navigate(typeof(LoginPage));
            }

            LoadPendingReports();
        }

        private void InitTimers()
        {
            _recordingTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _recordingTimer.Tick += (_, _) => UpdateRecordingUi();

            _snackbarTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3.2)
            };
            _snackbarTimer.Tick += (_, _) =>
            {
                _snackbarTimer?.Stop();
                HideSnackbar();
            };
        }

        private void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            _captureService.StopRecording();
            _captureService.CleanupTempFiles();
            _authService?.Logout();
            Frame.Navigate(typeof(LoginPage));
        }

        private void RootNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
            {
                return;
            }

            SwitchContent(tag);
        }

        private void SwitchContent(string tag)
        {
            var show = GetView(tag);
            var hide = GetView(_currentViewTag);

            if (show.Visibility == Visibility.Visible && show.Opacity >= 1)
            {
                return;
            }

            _currentViewTag = tag;
            show.Visibility = Visibility.Visible;
            show.IsHitTestVisible = true;
            hide.IsHitTestVisible = false;

            // Fast, subtle fade transition (~150ms)
            var duration = new Duration(TimeSpan.FromMilliseconds(150));

            var sb = new Storyboard();

            var fadeIn = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = duration,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fadeIn, show);
            Storyboard.SetTargetProperty(fadeIn, "Opacity");

            var fadeOut = new DoubleAnimation
            {
                From = 1,
                To = 0,
                Duration = duration,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fadeOut, hide);
            Storyboard.SetTargetProperty(fadeOut, "Opacity");

            sb.Children.Add(fadeIn);
            sb.Children.Add(fadeOut);

            sb.Completed += (_, _) =>
            {
                hide.Visibility = Visibility.Collapsed;
                hide.Opacity = 0;
                hide.IsHitTestVisible = false;
                show.Opacity = 1;
            };

            // Ensure starting state
            show.Opacity = 0;
            hide.Opacity = 1;
            sb.Begin();
        }

        private UIElement GetView(string tag)
        {
            return tag switch
            {
                "record" => RecordScroll,
                "pending" => PendingScroll,
                "settings" => SettingsScroll,
                _ => RecordScroll
            };
        }

        private async void RecordToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (_captureService.IsRecording)
            {
                StopRecording();
                return;
            }

            RecordToggleButton.IsEnabled = false;
            var (success, error) = await App.Recording.StartRecordingAsync();

            if (!success)
            {
                ShowStatus($"Failed to start recording: {error}", InfoBarSeverity.Error);
                RecordToggleButton.IsEnabled = true;
                UpdateRecordingUi();
                return;
            }

            _recordingTimer?.Start();
            UpdateControlState();
            UpdateRecordingUi();
            ShowStatus("Recording started. Last 30 seconds are being buffered.", InfoBarSeverity.Success);
            ToastIfHidden("Recording started", $"Hotkey: {App.Settings.HotkeyStartStop}");
        }

        private void StopRecording()
        {
            App.Recording.StopRecording();

            _captureService.CleanupTempFiles();
            _recordingTimer?.Stop();
            UpdateControlState();
            UpdateRecordingUi();
            ShowStatus("Recording stopped.", InfoBarSeverity.Informational);

            var duration = DateTime.UtcNow - App.Recording.RecordingStartTimeUtc;
            if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
            ToastIfHidden("Recording stopped", duration.ToString(@"hh\:mm\:ss"));
        }

        private async void ReportNowButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_captureService.IsRecording)
            {
                ShowStatus("No active recording to flag.", InfoBarSeverity.Warning);
                return;
            }

            // Capture the current moment
            var recordingStartUtc = _captureService.RecordingStartTime;
            _flagTime = DateTime.UtcNow;
            
            // Save current recording and immediately restart
            var savedFiles = await CaptureClipAsync();
            if (savedFiles.audioPath == null && savedFiles.micPath == null && savedFiles.videoPath == null)
            {
                var debug = "Failed to capture clip.";
                if (!string.IsNullOrEmpty(_captureService.LastVideoError))
                {
                    debug += $" LastVideoError={_captureService.LastVideoError}";
                }
                ShowStatus(debug, InfoBarSeverity.Error);
                return;
            }

            // Show upload dialog
            var dialog = new UploadReportDialog
            {
                XamlRoot = this.XamlRoot
            };

            var capturedDurationSec = GetCapturedDurationSec(savedFiles.audioPath);
            dialog.SetMediaFiles(savedFiles.audioPath, savedFiles.micPath, capturedDurationSec);

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                // Apply user's trim selection before upload
                var trimmedFiles = await ApplyTrimAsync(savedFiles, dialog.TrimStartSec, dialog.TrimEndSec);
                var clipDurationSec = dialog.TrimEndSec - dialog.TrimStartSec;

                await SubmitReportAsync(
                    dialog.GameName,
                    dialog.OffenderName,
                    dialog.Description,
                    dialog.Targeted,
                    dialog.DesiredAction,
                    trimmedFiles,
                    _flagTime,
                    recordingStartUtc,
                    clipDurationSec);
            }
            else if (result == ContentDialogResult.Secondary)
            {
                // Apply trim then save for later
                var trimmedFiles = await ApplyTrimAsync(savedFiles, dialog.TrimStartSec, dialog.TrimEndSec);
                var saved = SavePendingReport(trimmedFiles, dialog.GameName, dialog.OffenderName, dialog.Description, dialog.Targeted, dialog.DesiredAction, _flagTime, recordingStartUtc);
                if (saved)
                {
                    ShowStatus("Saved for later.", InfoBarSeverity.Success);
                    ToastIfHidden("Report saved for later", "Pending report created");
                }
                else
                {
                    ShowStatus("Failed to save pending report.", InfoBarSeverity.Error);
                    ToastIfHidden("Save failed", "Failed to save pending report");
                }
            }
            else
            {
                // User clicked Cancel - delete the saved files
                CancelUpload(savedFiles);
            }
        }

        private void FlagForLaterButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_captureService.IsRecording)
            {
                ShowStatus("No active recording to flag.", InfoBarSeverity.Warning);
                return;
            }

            _ = SavePendingFromCaptureAsync();
        }

        private async System.Threading.Tasks.Task SavePendingFromCaptureAsync()
        {
            var (ok, err) = await App.Recording.FlagToPendingAsync();
            if (ok)
            {
                ShowStatus("Saved for later.", InfoBarSeverity.Success);
                App.Toasts.Show("Flag saved", "Last 30 seconds captured");
                LoadPendingReports();
            }
            else
            {
                ShowStatus($"Failed to save pending report: {err}", InfoBarSeverity.Error);
                App.Toasts.Show("Flag failed", err);
            }
        }

        private void LoadSettingsUi()
        {
            try
            {
                MinimizeToTrayToggle.IsOn = App.Settings.MinimizeToTray;
                CloseExitsToggle.IsOn = App.Settings.CloseButtonExitsApp;

                RefreshHotkeyLabels();
            }
            catch { }
        }

        private void RefreshHotkeyLabels()
        {
            HotkeyStartStopText.Text = App.Settings.HotkeyStartStop.ToString();
            HotkeyFlagText.Text = App.Settings.HotkeyFlag.ToString();
            HotkeyOpenAppText.Text = App.Settings.HotkeyOpenApp.ToString();
        }

        private void MinimizeToTrayToggle_Toggled(object sender, RoutedEventArgs e)
        {
            App.Settings.MinimizeToTray = MinimizeToTrayToggle.IsOn;
        }

        private void CloseExitsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            App.Settings.CloseButtonExitsApp = CloseExitsToggle.IsOn;
        }

        private async void ChangeStartStopHotkey_Click(object sender, RoutedEventArgs e)
        {
            var captured = await CaptureHotkeyAsync("Start/Stop recording", App.Settings.HotkeyStartStop);
            if (captured == null) return;

            App.Settings.HotkeyStartStop = captured.Value with { Enabled = true };
            App.TrayHotkeys?.RegisterHotkeysFromSettings();
            RefreshHotkeyLabels();
        }

        private async void ChangeFlagHotkey_Click(object sender, RoutedEventArgs e)
        {
            var captured = await CaptureHotkeyAsync("Flag", App.Settings.HotkeyFlag);
            if (captured == null) return;

            App.Settings.HotkeyFlag = captured.Value with { Enabled = true };
            App.TrayHotkeys?.RegisterHotkeysFromSettings();
            RefreshHotkeyLabels();
        }

        private async void ChangeOpenAppHotkey_Click(object sender, RoutedEventArgs e)
        {
            var captured = await CaptureHotkeyAsync("Open app", App.Settings.HotkeyOpenApp);
            if (captured == null) return;

            App.Settings.HotkeyOpenApp = captured.Value with { Enabled = true };
            App.TrayHotkeys?.RegisterHotkeysFromSettings();
            RefreshHotkeyLabels();
        }

        private async System.Threading.Tasks.Task<HotkeyBinding?> CaptureHotkeyAsync(string title, HotkeyBinding current)
        {
            HotkeyBinding? captured = null;

            var hint = new TextBlock
            {
                Text = "Press a new shortcut (use Ctrl/Shift/Alt/Win + key).",
                Style = (Style)Application.Current.Resources["BodyTextStyle"],
                Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"]
            };

            var currentText = new TextBlock
            {
                Text = $"Current: {current}",
                Style = (Style)Application.Current.Resources["BodyTextStyle"],
                Foreground = (Brush)Application.Current.Resources["SecondaryTextBrush"],
                Margin = new Thickness(0, 8, 0, 0)
            };

            var capturedText = new TextBlock
            {
                Text = "New: (waiting…)",
                Style = (Style)Application.Current.Resources["BodyTextStyle"],
                Margin = new Thickness(0, 8, 0, 0)
            };

            var panel = new StackPanel
            {
                Spacing = 8,
                Children = { hint, currentText, capturedText }
            };

            var dialog = new ContentDialog
            {
                Title = title,
                Content = panel,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary,
                IsPrimaryButtonEnabled = false,
                XamlRoot = this.XamlRoot
            };

            void OnKeyDown(object s, Microsoft.UI.Xaml.Input.KeyRoutedEventArgs e)
            {
                var key = e.Key;
                if (key is Windows.System.VirtualKey.Shift or Windows.System.VirtualKey.Control or Windows.System.VirtualKey.Menu
                    or Windows.System.VirtualKey.LeftWindows or Windows.System.VirtualKey.RightWindows)
                {
                    return;
                }

                var mods = GetModifiers();
                if (mods == HotkeyModifiers.None)
                {
                    capturedText.Text = "New: (add a modifier like Ctrl/Shift/Alt/Win)";
                    dialog.IsPrimaryButtonEnabled = false;
                    return;
                }

                captured = new HotkeyBinding(mods, key, Enabled: true);
                capturedText.Text = $"New: {captured.Value}";
                dialog.IsPrimaryButtonEnabled = true;
                e.Handled = true;
            }

            dialog.KeyDown += OnKeyDown;
            var result = await dialog.ShowAsync();
            dialog.KeyDown -= OnKeyDown;

            return result == ContentDialogResult.Primary ? captured : null;
        }

        private static HotkeyModifiers GetModifiers()
        {
            HotkeyModifiers mods = HotkeyModifiers.None;
            var ks = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread;

            if ((ks(Windows.System.VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0) mods |= HotkeyModifiers.Control;
            if ((ks(Windows.System.VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0) mods |= HotkeyModifiers.Shift;
            if ((ks(Windows.System.VirtualKey.Menu) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0) mods |= HotkeyModifiers.Alt;
            if ((ks(Windows.System.VirtualKey.LeftWindows) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0) mods |= HotkeyModifiers.Win;
            if ((ks(Windows.System.VirtualKey.RightWindows) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0) mods |= HotkeyModifiers.Win;

            return mods;
        }

        private void ToastIfHidden(string title, string body)
        {
            try
            {
                if (App.MainWindowInstance?.IsHiddenToTray == true)
                {
                    App.Toasts.Show(title, body);
                }
            }
            catch { }
        }

        private async System.Threading.Tasks.Task<(string? audioPath, string? micPath, string? videoPath)> CaptureClipAsync()
        {
            try
            {
                SetCaptureBusy(true);
                var (audioPath, micPath, videoPath, _) = await App.Recording.CaptureClipAsync();
                return (audioPath, micPath, videoPath);
            }
            finally
            {
                SetCaptureBusy(false);
            }
        }

        /// <summary>
        /// Returns the actual duration (seconds) of a WAV file, falling back to 30 s.
        /// </summary>
        private static double GetCapturedDurationSec(string? wavPath)
        {
            if (string.IsNullOrEmpty(wavPath) || !File.Exists(wavPath))
                return 30.0;
            try
            {
                using var reader = new WaveFileReader(wavPath);
                return reader.TotalTime.TotalSeconds;
            }
            catch { return 30.0; }
        }

        /// <summary>
        /// Trims all three captured files to [startSec, endSec] and returns the new paths.
        /// Original files are deleted after successful trim.
        /// If no trim is needed (full range) the original paths are returned unchanged.
        /// </summary>
        private async Task<(string? audioPath, string? micPath, string? videoPath)> ApplyTrimAsync(
            (string? audioPath, string? micPath, string? videoPath) files,
            double startSec,
            double endSec)
        {
            // Short-circuit: if selection is the whole file, nothing to trim
            var fullDur = GetCapturedDurationSec(files.audioPath);
            if (startSec <= 0.05 && endSec >= fullDur - 0.05)
                return files;

            return await Task.Run(() =>
            {
                var tempFolder = App.Recording.Capture.TempFolderPath;
                var now = DateTime.UtcNow;

                string? trimmedAudio = null;
                if (!string.IsNullOrEmpty(files.audioPath) && File.Exists(files.audioPath))
                {
                    var dest = Path.Combine(tempFolder, MediaFileNamer.DesktopAudio(now));
                    trimmedAudio = AudioTrimmerService.TrimWav(files.audioPath, startSec, endSec, dest)
                                   ?? files.audioPath; // keep original on failure
                    if (trimmedAudio != files.audioPath)
                        TryDeleteFile(files.audioPath);
                }

                string? trimmedMic = null;
                if (!string.IsNullOrEmpty(files.micPath) && File.Exists(files.micPath))
                {
                    var dest = Path.Combine(tempFolder, MediaFileNamer.MicAudio(now));
                    trimmedMic = AudioTrimmerService.TrimWav(files.micPath, startSec, endSec, dest)
                                 ?? files.micPath;
                    if (trimmedMic != files.micPath)
                        TryDeleteFile(files.micPath);
                }

                string? trimmedVideo = null;
                if (!string.IsNullOrEmpty(files.videoPath) && File.Exists(files.videoPath))
                {
                    var dest = Path.Combine(tempFolder, MediaFileNamer.Video(now));
                    trimmedVideo = AudioTrimmerService.TrimAvi(
                        files.videoPath, startSec, endSec,
                        fps: 30, width: 1920, height: 1080, destPath: dest)
                                   ?? files.videoPath;
                    if (trimmedVideo != files.videoPath)
                        TryDeleteFile(files.videoPath);
                }

                return (trimmedAudio, trimmedMic, trimmedVideo);
            });
        }

        private static void TryDeleteFile(string? path)
        {
            try
            {
                if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        private void CancelUpload((string? audioPath, string? micPath, string? videoPath) savedFiles)
        {
            DeleteSavedFiles(savedFiles);
            ShowStatus("Upload cancelled. Recording continues.", InfoBarSeverity.Informational);
        }

        private bool SavePendingReport(
            (string? audioPath, string? micPath, string? videoPath) savedFiles,
            string gameName,
            string offenderName,
            string description,
            bool targeted,
            string desiredAction,
            DateTime flagTimeUtc,
            DateTime recordingStartUtc)
        {
            try
            {
                if (string.IsNullOrEmpty(savedFiles.audioPath) || !File.Exists(savedFiles.audioPath))
                {
                    DeleteSavedFiles(savedFiles);
                    return false;
                }

                var pendingId = Guid.NewGuid().ToString("N");
                var pendingFolder = _pendingStore.GetPendingFolder(pendingId);
                Directory.CreateDirectory(pendingFolder);

                var savedAt = flagTimeUtc;
                var audioDest = Path.Combine(pendingFolder, MediaFileNamer.DesktopAudio(savedAt));
                CopyIfExists(savedFiles.audioPath, audioDest);

                string? micDest = null;
                if (!string.IsNullOrEmpty(savedFiles.micPath) && File.Exists(savedFiles.micPath))
                {
                    micDest = Path.Combine(pendingFolder, MediaFileNamer.MicAudio(savedAt));
                    CopyIfExists(savedFiles.micPath, micDest);
                }

                string? videoDest = null;
                if (!string.IsNullOrEmpty(savedFiles.videoPath) && File.Exists(savedFiles.videoPath))
                {
                    videoDest = Path.Combine(pendingFolder, MediaFileNamer.Video(savedAt));
                    CopyIfExists(savedFiles.videoPath, videoDest);
                }

                var item = new PendingReportItem
                {
                    Id = pendingId,
                    CreatedUtc = DateTime.UtcNow,
                    FlagUtc = flagTimeUtc,
                    RecordingStartUtc = recordingStartUtc,
                    GameName = gameName,
                    OffenderName = offenderName,
                    Description = description,
                    Targeted = targeted,
                    DesiredAction = desiredAction,
                    AudioPath = _pendingStore.ToRelativePath(audioDest),
                    MicrophonePath = micDest != null ? _pendingStore.ToRelativePath(micDest) : null,
                    VideoPath = videoDest != null ? _pendingStore.ToRelativePath(videoDest) : null
                };

                var items = _pendingStore.Load();
                items.Add(item);
                _pendingStore.Save(items);

                _pendingItems.Insert(0, item);
                UpdatePendingEmptyState();
            }
            catch
            {
                DeleteSavedFiles(savedFiles);
                return false;
            }

            DeleteSavedFiles(savedFiles);
            return true;
        }

        private async System.Threading.Tasks.Task<bool> SubmitReportAsync(
            string gameName,
            string offenderName,
            string description,
            bool targeted,
            string desiredAction,
            (string? audioPath, string? micPath, string? videoPath) savedFiles,
            DateTime flagTimeUtc,
            DateTime recordingStartUtc,
            double clipDurationSec = 10.0)
        {
            if (_authService == null || !_authService.IsAuthenticated)
            {
                ShowStatus("Not authenticated. Please log in again.", InfoBarSeverity.Error);
                Frame.Navigate(typeof(LoginPage));
                return false;
            }

            SetLoading(true);

            try
            {
                var (systemAudioPath, micPath, videoPath) = savedFiles;

                if (string.IsNullOrEmpty(systemAudioPath) || !File.Exists(systemAudioPath))
                {
                    ShowStatus("No audio data captured.", InfoBarSeverity.Error);
                    SetLoading(false);
                    return false;
                }

                var hasMic = !string.IsNullOrEmpty(micPath) && File.Exists(micPath);
                var hasVideo = !string.IsNullOrEmpty(videoPath) && File.Exists(videoPath);

                var localVideoSize = _captureService.GetFileSizeBytes(videoPath);
                if (hasVideo && localVideoSize == 0)
                {
                    var debug = $"Video capture produced 0 bytes. FramesWritten={_captureService.FramesWritten}. TempFolder={_captureService.TempFolderPath}.";
                    if (!string.IsNullOrEmpty(_captureService.LastVideoError))
                    {
                        debug += $" LastError={_captureService.LastVideoError}";
                    }
                    ShowStatus(debug, InfoBarSeverity.Error);
                    SetLoading(false);
                    return false;
                }

                // Initialize report and get signed upload URLs
                var (initSuccess, initResult, initError) = await InitReportWithRefreshAsync(hasMic, hasVideo);

                if (!initSuccess || initResult == null)
                {
                    ShowStatus($"Failed to initialize report: {initError}", InfoBarSeverity.Error);
                    SetLoading(false);
                    return false;
                }

                // Save local copies before upload
                try
                {
                    var reportFolder = CreateLocalReportFolder(initResult.ReportId);
                    _lastReportFolderPath = reportFolder;
                    var localSavedAt = flagTimeUtc;
                    CopyIfExists(systemAudioPath, Path.Combine(reportFolder, MediaFileNamer.DesktopAudio(localSavedAt)));
                    if (hasMic && micPath != null)
                    {
                        CopyIfExists(micPath, Path.Combine(reportFolder, MediaFileNamer.MicAudio(localSavedAt)));
                    }
                    if (hasVideo && videoPath != null)
                    {
                        CopyIfExists(videoPath, Path.Combine(reportFolder, MediaFileNamer.Video(localSavedAt)));
                    }
                }
                catch (Exception ex)
                {
                    ShowStatus($"Failed to save local report copy: {ex.Message}", InfoBarSeverity.Warning);
                }

                var totalBytes = 0L;
                var systemAudioSize = new FileInfo(systemAudioPath).Length;
                totalBytes += systemAudioSize;
                var micSize = 0L;
                var videoSize = 0L;
                if (hasMic)
                {
                    micSize = new FileInfo(micPath!).Length;
                    totalBytes += micSize;
                }
                if (hasVideo)
                {
                    videoSize = new FileInfo(videoPath!).Length;
                    totalBytes += videoSize;
                }

                var uploadedBase = 0L;
                UpdateUploadProgress(0);

                IProgress<double> CreateProgress(long fileSize)
                {
                    return new Progress<double>(p =>
                    {
                        if (totalBytes <= 0)
                        {
                            UpdateUploadProgress(0);
                            return;
                        }
                        var overall = (uploadedBase + (long)(p * fileSize)) / (double)totalBytes * 100.0;
                        UpdateUploadProgress(overall);
                    });
                }

                // Upload system audio
                var (audioUploadSuccess, audioUploadError) = await _reportsClient.UploadToSignedUrlAsync(
                    initResult.AudioUploadUrl,
                    systemAudioPath,
                    "audio/wav",
                    initResult.AudioUploadToken,
                    CreateProgress(systemAudioSize));

                if (!audioUploadSuccess)
                {
                    ShowStatus($"Audio upload failed: {audioUploadError}", InfoBarSeverity.Error);
                    SetLoading(false);
                    return false;
                }
                uploadedBase += systemAudioSize;

                // Upload microphone audio if present
                if (hasMic && initResult.MicrophoneUploadUrl != null)
                {
                    var (micUploadSuccess, micUploadError) = await _reportsClient.UploadToSignedUrlAsync(
                        initResult.MicrophoneUploadUrl,
                        micPath!,
                        "audio/wav",
                        initResult.MicrophoneUploadToken,
                        CreateProgress(micSize));

                    if (!micUploadSuccess)
                    {
                        ShowStatus($"Microphone upload failed: {micUploadError}", InfoBarSeverity.Error);
                        SetLoading(false);
                        return false;
                    }
                    uploadedBase += micSize;
                }

                // Upload video if present
                if (hasVideo && initResult.VideoUploadUrl != null)
                {
                    var (videoUploadSuccess, videoUploadError) = await _reportsClient.UploadToSignedUrlAsync(
                        initResult.VideoUploadUrl,
                        videoPath!,
                        "video/x-msvideo",
                        initResult.VideoUploadToken,
                        CreateProgress(videoSize));

                    if (!videoUploadSuccess)
                    {
                        ShowStatus($"Video upload failed: {videoUploadError}", InfoBarSeverity.Error);
                        SetLoading(false);
                        return false;
                    }
                    uploadedBase += videoSize;
                }

                // Complete report creation
                var completeRequest = new ReportCompleteRequest
                {
                    ReportId = initResult.ReportId,
                    GameName = gameName,
                    OffenderName = offenderName,
                    Description = description,
                    Targeted = targeted,
                    DesiredAction = desiredAction,
                    RecordingStartUtc = recordingStartUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    FlagUtc = flagTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    ClipStartOffsetSec = 0,
                    ClipEndOffsetSec = (int)Math.Ceiling(clipDurationSec),
                    AudioPath = initResult.AudioPath,
                    MicrophonePath = initResult.MicrophonePath,
                    VideoPath = initResult.VideoPath
                };

                var (success, reportId, error) = await _reportsClient.CompleteReportAsync(
                    _authService.AccessToken!,
                    completeRequest);

                SetLoading(false);

                if (success)
                {
                    ShowStatus($"Report submitted successfully! ID: {reportId}. Recording continues.", InfoBarSeverity.Success);
                    
                    // Cleanup submitted files
                    try
                    {
                        if (systemAudioPath != null && File.Exists(systemAudioPath))
                            File.Delete(systemAudioPath);
                        if (micPath != null && File.Exists(micPath))
                            File.Delete(micPath);
                        if (videoPath != null && File.Exists(videoPath))
                            File.Delete(videoPath);
                    }
                    catch { }
                    
                    return true;
                }
                else
                {
                    ShowStatus($"Failed to submit report: {error}", InfoBarSeverity.Error);
                    return false;
                }
            }
            catch (Exception ex)
            {
                SetLoading(false);
                ShowStatus($"Error: {ex.Message}", InfoBarSeverity.Error);
                return false;
            }
        }

        private async System.Threading.Tasks.Task<(bool success, ReportInitResponse? init, string error)> InitReportWithRefreshAsync(bool hasMic, bool hasVideo)
        {
            if (_authService == null || string.IsNullOrEmpty(_authService.AccessToken))
            {
                return (false, null, "Not authenticated");
            }

            var initRequest = new ReportInitRequest
            {
                HasMicrophone = hasMic,
                HasVideo = hasVideo
            };

            var (success, init, error) = await _reportsClient.InitReportAsync(_authService.AccessToken, initRequest);
            if (success)
            {
                return (success, init, error);
            }

            if (!ShouldRefreshToken(error))
            {
                return (success, init, error);
            }

            var (refreshOk, refreshError) = await _authService.RefreshSessionAsync();
            if (!refreshOk || string.IsNullOrEmpty(_authService.AccessToken))
            {
                return (false, null, $"Session expired. Please log in again. {refreshError}");
            }

            return await _reportsClient.InitReportAsync(_authService.AccessToken, initRequest);
        }

        private bool ShouldRefreshToken(string error)
        {
            if (string.IsNullOrEmpty(error))
            {
                return false;
            }

            var lower = error.ToLowerInvariant();
            return lower.Contains("jwt verification failed")
                   || lower.Contains("exp")
                   || lower.Contains("token");
        }

        private void LoadPendingReports()
        {
            _pendingItems.Clear();
            var items = _pendingStore.Load()
                .OrderByDescending(x => x.CreatedUtc)
                .ToList();
            foreach (var item in items)
            {
                _pendingItems.Add(item);
            }
            UpdatePendingEmptyState();
        }

        private void UpdatePendingEmptyState()
        {
            PendingEmptyText.Visibility = _pendingItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private async void PendingReportsListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (!_isUploading && !_isCapturingClip && e.ClickedItem is PendingReportItem item)
            {
                await OpenPendingItemAsync(item);
            }
        }

        private async void PendingItem_Open_Click(object sender, RoutedEventArgs e)
        {
            if (_isUploading || _isCapturingClip)
            {
                return;
            }

            if (sender is MenuFlyoutItem m && m.CommandParameter is PendingReportItem item)
            {
                await OpenPendingItemAsync(item);
            }
        }

        private void PendingItem_Remove_Click(object sender, RoutedEventArgs e)
        {
            if (_isUploading || _isCapturingClip)
            {
                return;
            }

            if (sender is MenuFlyoutItem m && m.CommandParameter is PendingReportItem item)
            {
                RemovePendingItem(item, deleteFolder: true);
                ShowStatus("Pending report removed.", InfoBarSeverity.Informational);
            }
        }

        private async System.Threading.Tasks.Task OpenPendingItemAsync(PendingReportItem item)
        {
            var dialog = new UploadReportDialog
            {
                XamlRoot = this.XamlRoot
            };
            dialog.SetInitialValues(item.GameName, item.OffenderName, item.Description, item.Targeted, item.DesiredAction);

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                var savedFiles = ResolvePendingFiles(item);
                var success = await SubmitReportAsync(
                    dialog.GameName,
                    dialog.OffenderName,
                    dialog.Description,
                    dialog.Targeted,
                    dialog.DesiredAction,
                    savedFiles,
                    item.FlagUtc,
                    item.RecordingStartUtc);

                if (success)
                {
                    RemovePendingItem(item, deleteFolder: true);
                }
            }
            else if (result == ContentDialogResult.Secondary)
            {
                item.Description = dialog.Description;
                item.Targeted = dialog.Targeted;
                item.DesiredAction = dialog.DesiredAction;
                item.GameName = dialog.GameName;
                item.OffenderName = dialog.OffenderName;

                var items = _pendingStore.Load();
                _pendingStore.SaveItem(items, item);
                LoadPendingReports();
            }
        }

        private void RemovePendingItem(PendingReportItem item, bool deleteFolder)
        {
            var items = _pendingStore.Load();
            var existing = items.FirstOrDefault(x => x.Id == item.Id);
            if (existing != null)
            {
                items.Remove(existing);
                _pendingStore.Save(items);
            }

            if (deleteFolder)
            {
                _pendingStore.RemoveFolder(item.Id);
            }

            _pendingItems.Remove(item);
            UpdatePendingEmptyState();
        }

        private (string? audioPath, string? micPath, string? videoPath) ResolvePendingFiles(PendingReportItem item)
        {
            string? audioPath = null;
            string? micPath = null;
            string? videoPath = null;

            if (!string.IsNullOrEmpty(item.AudioPath))
            {
                audioPath = _pendingStore.ToAbsolutePath(item.AudioPath);
            }
            if (!string.IsNullOrEmpty(item.MicrophonePath))
            {
                micPath = _pendingStore.ToAbsolutePath(item.MicrophonePath);
            }
            if (!string.IsNullOrEmpty(item.VideoPath))
            {
                videoPath = _pendingStore.ToAbsolutePath(item.VideoPath);
            }

            return (audioPath, micPath, videoPath);
        }

        private string CreateLocalReportFolder(string reportId)
        {
            var root = _pendingStore.ReportsRoot;
            var reportFolder = Path.Combine(root, reportId);
            Directory.CreateDirectory(reportFolder);
            return reportFolder;
        }

        private void CopyIfExists(string sourcePath, string destinationPath)
        {
            if (!string.IsNullOrEmpty(sourcePath) && File.Exists(sourcePath))
            {
                File.Copy(sourcePath, destinationPath, overwrite: true);
            }
        }

        private void DeleteSavedFiles((string? audioPath, string? micPath, string? videoPath) savedFiles)
        {
            try
            {
                if (savedFiles.audioPath != null && File.Exists(savedFiles.audioPath))
                    File.Delete(savedFiles.audioPath);
                if (savedFiles.micPath != null && File.Exists(savedFiles.micPath))
                    File.Delete(savedFiles.micPath);
                if (savedFiles.videoPath != null && File.Exists(savedFiles.videoPath))
                    File.Delete(savedFiles.videoPath);
            }
            catch
            {
            }
        }

        private void OpenReportsFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var folderToOpen = _lastReportFolderPath ?? _pendingStore.ReportsRoot;
            try
            {
                Directory.CreateDirectory(folderToOpen);
                Process.Start(new ProcessStartInfo
                {
                    FileName = folderToOpen,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                ShowStatus($"Failed to open reports folder: {ex.Message}", InfoBarSeverity.Error);
            }
        }

        private void ShowStatus(string message, InfoBarSeverity severity)
        {
            ShowSnackbar(message, severity);
        }

        private void ShowSnackbar(string message, InfoBarSeverity severity)
        {
            SnackbarText.Text = message;

            // Glyphs from Segoe MDL2 Assets
            // Info: E946, Success: E73E, Warning: E7BA, Error: EA39
            SnackbarIcon.Glyph = severity switch
            {
                InfoBarSeverity.Success => "\uE73E",
                InfoBarSeverity.Warning => "\uE7BA",
                InfoBarSeverity.Error => "\uEA39",
                _ => "\uE946"
            };

            SnackbarIcon.Foreground = severity switch
            {
                InfoBarSeverity.Error => (Brush)Application.Current.Resources["DangerBrush"],
                InfoBarSeverity.Success => (Brush)Application.Current.Resources["SuccessBrush"],
                InfoBarSeverity.Warning => (Brush)Application.Current.Resources["WarningBrush"],
                _ => (Brush)Application.Current.Resources["SecondaryTextBrush"]
            };

            // Show
            SnackbarHost.Visibility = Visibility.Visible;
            _snackbarTimer?.Stop();

            var duration = new Duration(TimeSpan.FromMilliseconds(150));
            var sb = new Storyboard();

            var fadeIn = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = duration,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fadeIn, SnackbarHost);
            Storyboard.SetTargetProperty(fadeIn, "Opacity");
            sb.Children.Add(fadeIn);

            sb.Begin();
            _snackbarTimer?.Start();
        }

        private void HideSnackbar()
        {
            if (SnackbarHost.Visibility != Visibility.Visible)
            {
                return;
            }

            var duration = new Duration(TimeSpan.FromMilliseconds(150));
            var sb = new Storyboard();

            var fadeOut = new DoubleAnimation
            {
                From = SnackbarHost.Opacity,
                To = 0,
                Duration = duration,
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fadeOut, SnackbarHost);
            Storyboard.SetTargetProperty(fadeOut, "Opacity");
            sb.Children.Add(fadeOut);

            sb.Completed += (_, _) =>
            {
                SnackbarHost.Visibility = Visibility.Collapsed;
            };

            sb.Begin();
        }

        private void SetLoading(bool isLoading)
        {
            _isUploading = isLoading;
            UpdateControlState();

            if (!isLoading)
            {
                UploadProgressBar.Visibility = Visibility.Collapsed;
                UploadProgressBar.Value = 0;
            }
        }

        private void UpdateUploadProgress(double percent)
        {
            UploadProgressBar.Visibility = Visibility.Visible;
            UploadProgressBar.Value = Math.Max(0, Math.Min(100, percent));
        }

        private void SetCaptureBusy(bool isBusy)
        {
            _isCapturingClip = isBusy;
            UpdateControlState();
        }

        private void UpdateControlState()
        {
            LoadingRing.IsActive = _isUploading || _isCapturingClip;
            var allowActions = !_isUploading && !_isCapturingClip;

            RecordToggleButton.IsEnabled = allowActions;
            FlagForLaterButton.IsEnabled = allowActions && _captureService.IsRecording;
            ReportNowButton.IsEnabled = allowActions && _captureService.IsRecording;
            PendingReportsListView.IsEnabled = allowActions;
        }

        private void UpdateRecordingUi()
        {
            var isRecording = _captureService.IsRecording;

            // Status + timer
            RecordingStatusText.Text = isRecording ? "Recording" : "Not recording";
            RecordingStatusText.Foreground = isRecording
                ? new SolidColorBrush(Microsoft.UI.Colors.White)
                : (Brush)Application.Current.Resources["SecondaryTextBrush"];

            var elapsed = isRecording ? (DateTime.UtcNow - _captureService.RecordingStartTime) : TimeSpan.Zero;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            RecordingTimerText.Text = elapsed.ToString(@"hh\:mm\:ss");

            // Dot + animation
            RecordingDot.Fill = isRecording
                ? (Brush)Application.Current.Resources["DangerBrush"]
                : (Brush)Application.Current.Resources["SecondaryTextBrush"];

            if (isRecording)
            {
                RecordingHintText.Text = "Flag or report while recording";
                RecordingDotPulseStoryboard?.Begin();
                RecordToggleButton.Content = "Stop Recording";
                RecordToggleButton.Style = (Style)Application.Current.Resources["DangerOutlineButtonStyle"];
            }
            else
            {
                RecordingHintText.Text = string.Empty;
                RecordingDotPulseStoryboard?.Stop();
                RecordingDot.Opacity = 1;
                RecordToggleButton.Content = "Start Recording";
                RecordToggleButton.Style = (Style)Application.Current.Resources["PrimaryButtonStyle"];
            }
        }
    }
}
