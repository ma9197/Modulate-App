using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Windowing;
using System;
using System.Runtime.InteropServices;
using WinUI_App.Services;
using WinUI_App.Views;

namespace WinUI_App
{
    /// <summary>
    /// Main application window with navigation frame
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private bool _isHiddenToTray;

        public MainWindow()
        {
            InitializeComponent();
            
            // Set window size
            this.AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 700));

            // Window close/minimize rules (tray behavior is implemented later; this wires the lifecycle)
            try
            {
                this.AppWindow.Closing += AppWindow_Closing;
                this.AppWindow.Changed += AppWindow_Changed;
            }
            catch { }
            
            // Navigate to login page on startup
            RootFrame.Navigate(typeof(LoginPage));
        }

        public void RestoreAndFocus()
        {
            try
            {
                _isHiddenToTray = false;
                var hwnd = GetWindowHandle();
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
                NativeMethods.SetForegroundWindow(hwnd);
            }
            catch { }
        }

        public void HideToTray()
        {
            try
            {
                _isHiddenToTray = true;
                var hwnd = GetWindowHandle();
                NativeMethods.ShowWindow(hwnd, NativeMethods.SW_HIDE);
            }
            catch { }
        }

        public bool IsHiddenToTray => _isHiddenToTray;

        private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
        {
            if (!args.DidPresenterChange)
            {
                return;
            }

            if (App.Settings.MinimizeToTray && sender.Presenter is OverlappedPresenter p && p.State == OverlappedPresenterState.Minimized)
            {
                HideToTray();
            }
        }

        private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
        {
            if (App.Settings.CloseButtonExitsApp)
            {
                return;
            }

            // Default: hide to tray instead of exiting
            args.Cancel = true;
            HideToTray();

            if (!App.Settings.HasShownRunningInBackgroundToast)
            {
                App.Settings.HasShownRunningInBackgroundToast = true;
                App.Toasts.Show("Running in background", "Toxicity Reporter is still running in the system tray.");
            }
        }

        private IntPtr GetWindowHandle()
        {
            return WinRT.Interop.WindowNative.GetWindowHandle(this);
        }
    }
}
