using Microsoft.Windows.AppNotifications;
using System;
using System.Security;
using System.Runtime.InteropServices;

namespace WinUI_App.Services
{
    public class ToastService
    {
        private bool _initialized;
        private bool _available;

        public bool IsAvailable => _available;

        public event Action<string?>? ToastInvoked;
        public event Action<string, string>? FallbackNotificationRequested;

        public void Initialize()
        {
            if (_initialized)
            {
                return;
            }

            _initialized = true;
            try
            {
                AppNotificationManager.Default.NotificationInvoked += (_, args) =>
                {
                    try
                    {
                        // We only need "toast clicked" to restore the app; arguments are optional.
                        ToastInvoked?.Invoke(null);
                    }
                    catch { }
                };

                AppNotificationManager.Default.Register();
                _available = true;
                DebugLog.Info("Toast notifications enabled.");
            }
            catch (COMException ex)
            {
                // Most commonly happens when running unpackaged (no identity / no COM registration).
                _available = false;
                DebugLog.Warn($"Toast registration failed (COM): {ex.Message}");
            }
            catch (Exception ex)
            {
                _available = false;
                DebugLog.Warn($"Toast registration failed: {ex.Message}");
            }
        }

        public void Show(string title, string body, string? args = null)
        {
            try
            {
                if (!_available)
                {
                    FallbackNotificationRequested?.Invoke(title, body);
                    return;
                }

                var launch = string.IsNullOrWhiteSpace(args) ? "" : $" launch='{EscapeAttr(args)}'";
                var xml =
                    "<toast" + launch + ">" +
                    "<visual><binding template='ToastGeneric'>" +
                    $"<text>{EscapeText(title)}</text>" +
                    $"<text>{EscapeText(body)}</text>" +
                    "</binding></visual>" +
                    "</toast>";

                AppNotificationManager.Default.Show(new AppNotification(xml));
            }
            catch (Exception ex)
            {
                DebugLog.Warn($"Toast failed: {ex.Message}");
                try
                {
                    FallbackNotificationRequested?.Invoke(title, body);
                }
                catch { }
            }
        }

        private static string EscapeText(string s) => SecurityElement.Escape(s) ?? string.Empty;
        private static string EscapeAttr(string s) => SecurityElement.Escape(s) ?? string.Empty;
    }
}


