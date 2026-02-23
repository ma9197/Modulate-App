using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace WinUI_App.Services
{
    internal sealed class NativeMessageWindow : IDisposable
    {
        private static readonly ConcurrentDictionary<IntPtr, NativeMessageWindow> _instances = new();
        private static readonly string _className = $"WinUI_App.NativeMessageWindow.{Guid.NewGuid():N}";

        private readonly NativeMethods.WndProc _wndProcDelegate;
        private IntPtr _hwnd;
        private bool _disposed;

        public IntPtr Handle => _hwnd;

        public event Func<uint, IntPtr, IntPtr, bool>? MessageReceived;

        public NativeMessageWindow()
        {
            _wndProcDelegate = WndProc;

            var hInstance = NativeMethods.GetModuleHandleW(null);
            var wc = new NativeMethods.WNDCLASSW
            {
                lpszClassName = _className,
                hInstance = hInstance,
                lpfnWndProc = _wndProcDelegate
            };

            NativeMethods.RegisterClassW(ref wc);

            _hwnd = NativeMethods.CreateWindowExW(
                dwExStyle: 0,
                lpClassName: _className,
                lpWindowName: string.Empty,
                dwStyle: 0,
                x: 0,
                y: 0,
                nWidth: 0,
                nHeight: 0,
                hWndParent: NativeMethods.HWND_MESSAGE,
                hMenu: IntPtr.Zero,
                hInstance: hInstance,
                lpParam: IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                throw new InvalidOperationException("Failed to create native message window.");
            }

            _instances[_hwnd] = this;
        }

        private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            if (_instances.TryGetValue(hWnd, out var instance))
            {
                try
                {
                    var handled = instance.MessageReceived?.Invoke(msg, wParam, lParam) ?? false;
                    if (handled)
                    {
                        return IntPtr.Zero;
                    }
                }
                catch (Exception ex)
                {
                    DebugLog.Warn($"NativeMessageWindow WndProc error: {ex.Message}");
                }
            }

            return NativeMethods.DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            try
            {
                if (_hwnd != IntPtr.Zero)
                {
                    _instances.TryRemove(_hwnd, out _);
                    NativeMethods.DestroyWindow(_hwnd);
                    _hwnd = IntPtr.Zero;
                }
            }
            catch { }

            GC.SuppressFinalize(this);
        }
    }
}


