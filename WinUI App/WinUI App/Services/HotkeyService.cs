using System;
using System.Collections.Generic;
using WinUI_App.Models;

namespace WinUI_App.Services
{
    public enum HotkeyAction
    {
        ToggleRecording = 1,
        Flag = 2,
        OpenApp = 3
    }

    internal sealed class HotkeyService : IDisposable
    {
        private readonly NativeMessageWindow _msgWindow;
        private readonly Dictionary<int, HotkeyAction> _idToAction = new();
        private readonly Dictionary<int, DateTime> _lastFire = new();
        private readonly TimeSpan _debounce = TimeSpan.FromMilliseconds(300);

        public event Action<HotkeyAction>? HotkeyPressed;

        public HotkeyService(NativeMessageWindow msgWindow)
        {
            _msgWindow = msgWindow;
            _msgWindow.MessageReceived += OnMessage;
        }

        public bool Register(HotkeyAction action, HotkeyBinding binding, out string error)
        {
            error = string.Empty;

            var id = (int)action;
            Unregister(action);

            if (!binding.Enabled)
            {
                return true;
            }

            var ok = NativeMethods.RegisterHotKey(_msgWindow.Handle, id, (uint)binding.Modifiers, (uint)binding.Key);
            if (!ok)
            {
                var code = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
                error = $"RegisterHotKey failed (code {code})";
                DebugLog.Warn($"{error} for {action}: {binding}");
                return false;
            }

            _idToAction[id] = action;
            return true;
        }

        public void Unregister(HotkeyAction action)
        {
            var id = (int)action;
            try
            {
                NativeMethods.UnregisterHotKey(_msgWindow.Handle, id);
            }
            catch { }
            _idToAction.Remove(id);
            _lastFire.Remove(id);
        }

        private bool OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (msg != NativeMethods.WM_HOTKEY)
            {
                return false;
            }

            var id = wParam.ToInt32();
            if (!_idToAction.TryGetValue(id, out var action))
            {
                return false;
            }

            var now = DateTime.UtcNow;
            if (_lastFire.TryGetValue(id, out var last) && (now - last) < _debounce)
            {
                return true; // handled but ignored
            }

            _lastFire[id] = now;
            HotkeyPressed?.Invoke(action);
            return true;
        }

        public void Dispose()
        {
            try
            {
                _msgWindow.MessageReceived -= OnMessage;
            }
            catch { }
        }
    }
}


