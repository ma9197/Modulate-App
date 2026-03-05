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
        public event Action<bool>? HiddenToTrayChanged;

        private const int MinWindowWidth  = 1000;
        private const int MinWindowHeight = 720;

        private NativeMethods.WndProc? _minSizeWndProc;
        private IntPtr _oldWndProc;

        public MainWindow()
        {
            InitializeComponent();

            this.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 820));

            InstallMinSizeHook();

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

        private void InstallMinSizeHook()
        {
            try
            {
                var hwnd = GetWindowHandle();
                _minSizeWndProc = MinSizeWndProc;
                _oldWndProc = NativeMethods.SetWindowLongPtrW(
                    hwnd,
                    NativeMethods.GWLP_WNDPROC,
                    Marshal.GetFunctionPointerForDelegate(_minSizeWndProc));
            }
            catch { }
        }

        private IntPtr MinSizeWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == NativeMethods.WM_GETMINMAXINFO)
            {
                var info = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);
                info.ptMinTrackSize.X = MinWindowWidth;
                info.ptMinTrackSize.Y = MinWindowHeight;
                Marshal.StructureToPtr(info, lParam, false);
            }
            return NativeMethods.CallWindowProcW(_oldWndProc, hwnd, msg, wParam, lParam);
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
                HiddenToTrayChanged?.Invoke(false);
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
                HiddenToTrayChanged?.Invoke(true);
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
