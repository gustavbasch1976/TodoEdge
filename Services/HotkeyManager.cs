using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;

namespace TodoEdge
{
    /// <summary>System-wide keyboard shortcuts that work in any app.</summary>
    public sealed class HotkeyManager : IDisposable
    {
        private readonly HwndSource _source;
        private readonly Dictionary<int, Action> _actions = new Dictionary<int, Action>();
        private int _nextId = 0xB000;

        public HotkeyManager(Window window)
        {
            IntPtr handle = new WindowInteropHelper(window).EnsureHandle();
            _source = HwndSource.FromHwnd(handle);
            _source.AddHook(WndProc);
        }

        public bool Register(uint modifiers, uint virtualKey, Action action)
        {
            int id = _nextId++;
            if (!Native.RegisterHotKey(_source.Handle, id, modifiers | Native.MOD_NOREPEAT, virtualKey))
                return false;
            _actions[id] = action;
            return true;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
            {
                handled = true;
                action();
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            foreach (int id in _actions.Keys) Native.UnregisterHotKey(_source.Handle, id);
            _actions.Clear();
            _source.RemoveHook(WndProc);
        }
    }
}
