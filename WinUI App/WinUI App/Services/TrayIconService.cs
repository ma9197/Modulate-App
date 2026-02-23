using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using WinUI_App.Models;

namespace WinUI_App.Services
{
    internal sealed class TrayIconService : IDisposable
    {
        private const int WM_TRAYICON = NativeMethods.WM_APP + 42;

        private const int CMD_OPEN = 1001;
        private const int CMD_TOGGLE_REC = 1002;
        private const int CMD_FLAG = 1003;
        private const int CMD_OPEN_REPORTS = 1004;
        private const int CMD_EXIT = 1005;

        private readonly NativeMessageWindow _msgWindow;
        private readonly Func<bool> _isRecording;
        private readonly Func<bool> _canFlag;

        private readonly Action _openApp;
        private readonly Action _toggleRecording;
        private readonly Action _flag;
        private readonly Action _openReportsFolder;
        private readonly Action _exitApp;

        private NOTIFYICONDATA _nid;
        private bool _created;

        public TrayIconService(
            NativeMessageWindow msgWindow,
            Action openApp,
            Action toggleRecording,
            Action flag,
            Action openReportsFolder,
            Action exitApp,
            Func<bool> isRecording,
            Func<bool> canFlag)
        {
            _msgWindow = msgWindow;
            _openApp = openApp;
            _toggleRecording = toggleRecording;
            _flag = flag;
            _openReportsFolder = openReportsFolder;
            _exitApp = exitApp;
            _isRecording = isRecording;
            _canFlag = canFlag;

            _msgWindow.MessageReceived += OnMessage;
        }

        public void Create()
        {
            if (_created)
            {
                return;
            }

            _nid = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _msgWindow.Handle,
                uID = 1,
                uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
                uCallbackMessage = WM_TRAYICON,
                hIcon = GetSmallAppIcon(),
                szTip = "Toxicity Reporter"
            };

            _created = Shell_NotifyIcon(NIM_ADD, ref _nid);
            if (!_created)
            {
                var code = Marshal.GetLastWin32Error();
                DebugLog.Warn($"Shell_NotifyIcon ADD failed: {code}");
            }
        }

        public void ShowBalloon(string title, string body, bool isError = false)
        {
            if (!_created)
            {
                return;
            }

            try
            {
                _nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_INFO;
                _nid.szInfoTitle = Truncate(title, 63);
                _nid.szInfo = Truncate(body, 255);
                _nid.dwInfoFlags = isError ? NIIF_ERROR : NIIF_INFO;
                Shell_NotifyIcon(NIM_MODIFY, ref _nid);
            }
            catch (Exception ex)
            {
                DebugLog.Warn($"ShowBalloon failed: {ex.Message}");
            }
        }

        private static string Truncate(string s, int maxLen)
        {
            if (string.IsNullOrEmpty(s))
            {
                return string.Empty;
            }
            return s.Length <= maxLen ? s : s.Substring(0, maxLen);
        }

        private bool OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg == WM_TRAYICON)
            {
                var l = lParam.ToInt32();
                if (l == NativeMethods.WM_LBUTTONDBLCLK)
                {
                    _openApp();
                    return true;
                }
                if (l == NativeMethods.WM_RBUTTONUP || l == NativeMethods.WM_LBUTTONUP)
                {
                    ShowContextMenu();
                    return true;
                }
            }

            if (msg == NativeMethods.WM_COMMAND)
            {
                var cmd = (int)(wParam.ToInt64() & 0xFFFF);
                HandleCommand(cmd);
                return true;
            }

            return false;
        }

        private void ShowContextMenu()
        {
            if (!NativeMethods.GetCursorPos(out var pt))
            {
                return;
            }

            var menu = NativeMethods.CreatePopupMenu();
            if (menu == IntPtr.Zero)
            {
                return;
            }

            try
            {
                var recText = _isRecording() ? "Stop Recording" : "Start Recording";

                NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, CMD_OPEN, "Open");
                NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, 0, string.Empty);

                NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, CMD_TOGGLE_REC, recText);

                var flagFlags = NativeMethods.MF_STRING | (_canFlag() ? 0u : NativeMethods.MF_GRAYED);
                NativeMethods.AppendMenuW(menu, flagFlags, CMD_FLAG, "Flag");

                NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, 0, string.Empty);
                NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, CMD_OPEN_REPORTS, "Open Reports Folder");
                NativeMethods.AppendMenuW(menu, NativeMethods.MF_SEPARATOR, 0, string.Empty);
                NativeMethods.AppendMenuW(menu, NativeMethods.MF_STRING, CMD_EXIT, "Exit");

                NativeMethods.SetMenuDefaultItem(menu, CMD_OPEN, 0);

                // Ensure the menu closes correctly by setting foreground, then tracking.
                NativeMethods.SetForegroundWindow(_msgWindow.Handle);
                var cmd = NativeMethods.TrackPopupMenuEx(menu,
                    NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_NONOTIFY,
                    pt.X, pt.Y,
                    _msgWindow.Handle,
                    IntPtr.Zero);

                if (cmd != 0)
                {
                    HandleCommand((int)cmd);
                }
            }
            finally
            {
                NativeMethods.DestroyMenu(menu);
            }
        }

        private void HandleCommand(int cmd)
        {
            try
            {
                switch (cmd)
                {
                    case CMD_OPEN:
                        _openApp();
                        break;
                    case CMD_TOGGLE_REC:
                        _toggleRecording();
                        break;
                    case CMD_FLAG:
                        _flag();
                        break;
                    case CMD_OPEN_REPORTS:
                        _openReportsFolder();
                        break;
                    case CMD_EXIT:
                        _exitApp();
                        break;
                }
            }
            catch (Exception ex)
            {
                DebugLog.Warn($"Tray command failed: {cmd}: {ex.Message}");
            }
        }

        private static IntPtr GetSmallAppIcon()
        {
            try
            {
                var exe = Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exe))
                {
                    using var ico = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                    return ico?.Handle ?? IntPtr.Zero;
                }
            }
            catch { }

            return IntPtr.Zero;
        }

        public void Dispose()
        {
            try
            {
                _msgWindow.MessageReceived -= OnMessage;
            }
            catch { }

            try
            {
                if (_created)
                {
                    Shell_NotifyIcon(NIM_DELETE, ref _nid);
                    _created = false;
                }
            }
            catch { }
        }

        private const uint NIF_MESSAGE = 0x00000001;
        private const uint NIF_ICON = 0x00000002;
        private const uint NIF_TIP = 0x00000004;
        private const uint NIF_INFO = 0x00000010;

        private const uint NIM_ADD = 0x00000000;
        private const uint NIM_MODIFY = 0x00000001;
        private const uint NIM_DELETE = 0x00000002;

        private const uint NIIF_INFO = 0x00000001;
        private const uint NIIF_ERROR = 0x00000003;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NOTIFYICONDATA
        {
            public uint cbSize;
            public IntPtr hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public IntPtr hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szTip;
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
            public string szInfoTitle;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
            public string szInfo;
            public uint dwInfoFlags;
        }

        [DllImport("shell32.dll", SetLastError = true)]
        private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);
    }
}


