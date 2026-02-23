using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using WinUI_App.Models;

namespace WinUI_App.Services
{
    public sealed class TrayHotkeyController : IDisposable
    {
        private readonly NativeMessageWindow _msgWindow;
        private readonly HotkeyService _hotkeys;
        private readonly TrayIconService _tray;

        private readonly MainWindow _window;
        private bool _disposed;

        public TrayHotkeyController(MainWindow window)
        {
            _window = window;

            _msgWindow = new NativeMessageWindow();
            _hotkeys = new HotkeyService(_msgWindow);
            _hotkeys.HotkeyPressed += Hotkeys_HotkeyPressed;

            _tray = new TrayIconService(
                _msgWindow,
                openApp: () => _window.RestoreAndFocus(),
                toggleRecording: () => _ = ToggleRecordingAsync(source: "tray"),
                flag: () => _ = FlagAsync(source: "tray"),
                openReportsFolder: () => OpenReportsFolder(),
                exitApp: () => _ = ExitAsync(),
                isRecording: () => App.Recording.IsRecording,
                canFlag: () => App.Recording.IsRecording);

            _tray.Create();
            RegisterHotkeysFromSettings();

            // If Windows toasts aren't available (common in unpackaged VS runs),
            // show a tray balloon while hidden so hotkey actions still feel responsive.
            App.Toasts.FallbackNotificationRequested += Toasts_FallbackNotificationRequested;
        }

        private void Toasts_FallbackNotificationRequested(string title, string body)
        {
            try
            {
                if (_window.IsHiddenToTray)
                {
                    _tray.ShowBalloon(title, body, isError: false);
                }
            }
            catch { }
        }

        public void RegisterHotkeysFromSettings()
        {
            RegisterHotkeyWithToast(HotkeyAction.ToggleRecording, App.Settings.HotkeyStartStop, "Toggle recording");
            RegisterHotkeyWithToast(HotkeyAction.Flag, App.Settings.HotkeyFlag, "Flag");

            // Optional open-app hotkey
            if (App.Settings.HotkeyOpenApp.Enabled)
            {
                RegisterHotkeyWithToast(HotkeyAction.OpenApp, App.Settings.HotkeyOpenApp, "Open app");
            }
            else
            {
                _hotkeys.Unregister(HotkeyAction.OpenApp);
            }
        }

        private void RegisterHotkeyWithToast(HotkeyAction action, HotkeyBinding binding, string label)
        {
            if (_hotkeys.Register(action, binding, out var error))
            {
                DebugLog.Info($"Hotkey registered: {label}: {binding}");
                return;
            }

            // Disable until changed
            var disabled = binding with { Enabled = false };
            switch (action)
            {
                case HotkeyAction.ToggleRecording:
                    App.Settings.HotkeyStartStop = disabled;
                    break;
                case HotkeyAction.Flag:
                    App.Settings.HotkeyFlag = disabled;
                    break;
                case HotkeyAction.OpenApp:
                    App.Settings.HotkeyOpenApp = disabled;
                    break;
            }

            App.Toasts.Show("Hotkey unavailable", $"{binding} is used by another app. ({error})");
        }

        private void Hotkeys_HotkeyPressed(HotkeyAction action)
        {
            DebugLog.Info($"Hotkey pressed: {action}");

            switch (action)
            {
                case HotkeyAction.ToggleRecording:
                    _ = ToggleRecordingAsync(source: "hotkey");
                    break;
                case HotkeyAction.Flag:
                    _ = FlagAsync(source: "hotkey");
                    break;
                case HotkeyAction.OpenApp:
                    _window.RestoreAndFocus();
                    break;
            }
        }

        private async Task ToggleRecordingAsync(string source)
        {
            try
            {
                if (!App.Recording.IsRecording)
                {
                    var (ok, err) = await App.Recording.StartRecordingAsync();
                    if (ok)
                    {
                        App.Toasts.Show("Recording started", $"Hotkey: {App.Settings.HotkeyStartStop}");
                    }
                    else
                    {
                        App.Toasts.Show("Recording failed", err);
                    }
                }
                else
                {
                    var duration = DateTime.UtcNow - App.Recording.RecordingStartTimeUtc;
                    App.Recording.StopRecording();
                    App.Recording.CleanupTempFiles();
                    App.Toasts.Show("Recording stopped", duration.ToString(@"hh\:mm\:ss"));
                }
            }
            catch (Exception ex)
            {
                DebugLog.Warn($"ToggleRecordingAsync failed ({source}): {ex.Message}");
                App.Toasts.Show("Recording error", ex.Message);
            }
        }

        private async Task FlagAsync(string source)
        {
            try
            {
                if (!App.Recording.IsRecording)
                {
                    App.Toasts.Show("Can’t flag — not recording", $"Hotkey: {App.Settings.HotkeyFlag}");
                    return;
                }

                var (ok, err) = await App.Recording.FlagToPendingAsync();
                if (ok)
                {
                    App.Toasts.Show("Flag saved", "Last 10 seconds captured");
                }
                else
                {
                    App.Toasts.Show("Flag failed", err);
                }
            }
            catch (Exception ex)
            {
                DebugLog.Warn($"FlagAsync failed ({source}): {ex.Message}");
                App.Toasts.Show("Flag error", ex.Message);
            }
        }

        private void OpenReportsFolder()
        {
            try
            {
                var root = Path.Combine(AppContext.BaseDirectory, "Reports");
                Directory.CreateDirectory(root);
                Process.Start(new ProcessStartInfo
                {
                    FileName = root,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                App.Toasts.Show("Open folder failed", ex.Message);
            }
        }

        private async Task ExitAsync()
        {
            try
            {
                if (App.Recording.IsRecording)
                {
                    // Prompt user (restore first to ensure dialog has a visible XamlRoot)
                    _window.RestoreAndFocus();

                    ContentDialog dialog = new()
                    {
                        Title = "Recording is active",
                        Content = "Recording is currently running. Stop recording and exit?",
                        PrimaryButtonText = "Stop and exit",
                        CloseButtonText = "Cancel",
                        DefaultButton = ContentDialogButton.Primary,
                        XamlRoot = _window.Content.XamlRoot
                    };

                    var result = await dialog.ShowAsync();
                    if (result != ContentDialogResult.Primary)
                    {
                        return;
                    }
                }

                try
                {
                    App.Recording.StopRecording();
                    App.Recording.CleanupTempFiles();
                    App.Recording.Dispose();
                }
                catch { }

                Dispose();
                Environment.Exit(0);
            }
            catch (Exception ex)
            {
                DebugLog.Warn($"Exit failed: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { App.Toasts.FallbackNotificationRequested -= Toasts_FallbackNotificationRequested; } catch { }
            try { _hotkeys.HotkeyPressed -= Hotkeys_HotkeyPressed; } catch { }
            try { _tray.Dispose(); } catch { }
            try { _hotkeys.Dispose(); } catch { }
            try { _msgWindow.Dispose(); } catch { }
        }
    }
}


