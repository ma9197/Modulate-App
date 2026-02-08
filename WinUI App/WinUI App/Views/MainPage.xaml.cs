using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
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

        private DateTime _flagTime;
        private bool _hasPendingReport;

        // P/Invoke for global hotkey
        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private const int HOTKEY_ID = 9000;
        private const uint MOD_NONE = 0x0000;
        private const uint VK_F9 = 0x78;

        public MainPage()
        {
            this.InitializeComponent();
            _reportsClient = new ReportsApiClient();
            _captureService = new CaptureService();
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
        }

        private void LogoutButton_Click(object sender, RoutedEventArgs e)
        {
            _captureService.StopRecording();
            _captureService.CleanupTempFiles();
            _authService?.Logout();
            Frame.Navigate(typeof(LoginPage));
        }

        private async void StartRecordingButton_Click(object sender, RoutedEventArgs e)
        {
            StartRecordingButton.IsEnabled = false;
            var (success, error) = await _captureService.StartRecordingAsync();

            if (success)
            {
                RecordingStatusText.Text = "Recording... (Press F9 or click Flag Event to capture)";
                RecordingStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Microsoft.UI.Colors.Green);
                
                StopRecordingButton.IsEnabled = true;
                FlagEventButton.IsEnabled = true;

                ShowStatus("Recording started. Last 10 seconds are being buffered.", InfoBarSeverity.Success);
            }
            else
            {
                ShowStatus($"Failed to start recording: {error}", InfoBarSeverity.Error);
                StartRecordingButton.IsEnabled = true;
            }
        }

        private void StopRecordingButton_Click(object sender, RoutedEventArgs e)
        {
            _captureService.StopRecording();

            RecordingStatusText.Text = "Not recording";
            RecordingStatusText.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.Colors.Gray);

            StartRecordingButton.IsEnabled = true;
            StopRecordingButton.IsEnabled = false;
            FlagEventButton.IsEnabled = false;

            if (!_hasPendingReport)
            {
                _captureService.CleanupTempFiles();
                ShowStatus("Recording stopped.", InfoBarSeverity.Informational);
            }
        }

        private async void FlagEventButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_captureService.IsRecording)
            {
                ShowStatus("No active recording to flag.", InfoBarSeverity.Warning);
                return;
            }

            // Capture the current moment
            _flagTime = DateTime.UtcNow;
            
            // Save current recording and immediately restart
            var savedFiles = _captureService.SaveAndRestartRecording();
            _hasPendingReport = true;

            // Show upload dialog
            var dialog = new UploadReportDialog
            {
                XamlRoot = this.XamlRoot
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
            {
                // User clicked Submit
                await SubmitReportAsync(dialog.Description, dialog.Targeted, dialog.DesiredAction, savedFiles);
            }
            else
            {
                // User clicked Cancel - delete the saved files
                CancelUpload(savedFiles);
            }
        }

        private void CancelUpload((string? audioPath, string? micPath, string? videoPath) savedFiles)
        {
            // Delete the saved files
            try
            {
                if (savedFiles.audioPath != null && File.Exists(savedFiles.audioPath))
                    File.Delete(savedFiles.audioPath);
                if (savedFiles.micPath != null && File.Exists(savedFiles.micPath))
                    File.Delete(savedFiles.micPath);
                if (savedFiles.videoPath != null && File.Exists(savedFiles.videoPath))
                    File.Delete(savedFiles.videoPath);
            }
            catch { }

            _hasPendingReport = false;
            ShowStatus("Upload cancelled. Recording continues.", InfoBarSeverity.Informational);
        }

        private async System.Threading.Tasks.Task SubmitReportAsync(
            string description, 
            bool targeted, 
            string desiredAction,
            (string? audioPath, string? micPath, string? videoPath) savedFiles)
        {
            if (_authService == null || !_authService.IsAuthenticated)
            {
                ShowStatus("Not authenticated. Please log in again.", InfoBarSeverity.Error);
                Frame.Navigate(typeof(LoginPage));
                return;
            }

            SetLoading(true);
            StatusBar.IsOpen = false;

            try
            {
                var (systemAudioPath, micPath, videoPath) = savedFiles;

                if (string.IsNullOrEmpty(systemAudioPath) || !File.Exists(systemAudioPath))
                {
                    ShowStatus("No audio data captured.", InfoBarSeverity.Error);
                    SetLoading(false);
                    return;
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
                    return;
                }

                // Initialize report and get signed upload URLs
                var (initSuccess, initResult, initError) = await _reportsClient.InitReportAsync(
                    _authService.AccessToken!,
                    new ReportInitRequest
                    {
                        HasMicrophone = hasMic,
                        HasVideo = hasVideo
                    });

                if (!initSuccess || initResult == null)
                {
                    ShowStatus($"Failed to initialize report: {initError}", InfoBarSeverity.Error);
                    SetLoading(false);
                    return;
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
                    return;
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
                        return;
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
                        return;
                    }
                    uploadedBase += videoSize;
                }

                // Complete report creation
                var completeRequest = new ReportCompleteRequest
                {
                    ReportId = initResult.ReportId,
                    Description = description,
                    Targeted = targeted,
                    DesiredAction = desiredAction,
                    RecordingStartUtc = _captureService.RecordingStartTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    FlagUtc = _flagTime.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    ClipStartOffsetSec = 0,
                    ClipEndOffsetSec = 10,
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
                    
                    _hasPendingReport = false;
                }
                else
                {
                    ShowStatus($"Failed to submit report: {error}", InfoBarSeverity.Error);
                    _hasPendingReport = false;
                }
            }
            catch (Exception ex)
            {
                SetLoading(false);
                ShowStatus($"Error: {ex.Message}", InfoBarSeverity.Error);
            }
        }

        private void ShowStatus(string message, InfoBarSeverity severity)
        {
            StatusBar.Message = message;
            StatusBar.Severity = severity;
            StatusBar.IsOpen = true;
        }

        private void SetLoading(bool isLoading)
        {
            LoadingRing.IsActive = isLoading;
            StartRecordingButton.IsEnabled = !isLoading && !_hasPendingReport;
            StopRecordingButton.IsEnabled = !isLoading && _captureService.IsRecording;
            FlagEventButton.IsEnabled = !isLoading && _captureService.IsRecording;

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
    }
}
