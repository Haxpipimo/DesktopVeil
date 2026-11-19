using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace DesktopVeil
{
    internal static class Native
    {
        internal delegate bool EnumProc(IntPtr hwnd, IntPtr param);
        [DllImport("user32.dll")] internal static extern bool EnumWindows(EnumProc callback, IntPtr param);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
        [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll", SetLastError = true)] internal static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("kernel32.dll")] private static extern void SetLastError(uint error);
        [DllImport("user32.dll")] internal static extern IntPtr GetShellWindow();
        [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] internal static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool ShowWindowAsync(IntPtr hwnd, int command);
        [DllImport("user32.dll", SetLastError = true)] internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint key);
        [DllImport("user32.dll")] internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);
        [DllImport("user32.dll")] internal static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(IntPtr context);
        internal static string ClassName(IntPtr hwnd)
        {
            StringBuilder s = new StringBuilder(256);
            GetClassName(hwnd, s, s.Capacity);
            return s.ToString();
        }
        internal static uint ProcessId(IntPtr hwnd)
        {
            uint pid;
            return GetWindowThreadProcessId(hwnd, out pid) == 0 ? 0 : pid;
        }
        internal static bool Visible(IntPtr hwnd)
        {
            SetLastError(0);
            int style = GetWindowLong(hwnd, -16);
            int error = Marshal.GetLastWin32Error();
            if (style == 0 && error != 0) throw new Win32Exception(error);
            return (style & 0x10000000) != 0;
        }
    }

    public class ShellTarget
    {
        public long Handle;
        public int ProcessId;
        public long ProcessStarted;
        public string ClassName;
        public string Kind;
        public bool WasVisible;
        public string Identity { get { return Handle + ":" + ProcessId + ":" + ProcessStarted + ":" + ClassName; } }
    }

    internal interface IShellWindows
    {
        List<ShellTarget> Discover();
        bool Alive(ShellTarget target);
        bool Visible(ShellTarget target);
        void SetVisible(ShellTarget target, bool visible);
    }

    internal sealed class ShellWindows : IShellWindows
    {
        internal static bool IsTargetClass(string kind, string cls)
        {
            return (kind == "Icons" && cls == "SysListView32") ||
                (kind == "Taskbar" && cls == "Shell_TrayWnd") ||
                (kind == "SecondaryTaskbar" && cls == "Shell_SecondaryTrayWnd");
        }

        private static bool MatchesShellTarget(IntPtr hwnd, string kind, uint shellPid)
        {
            if (shellPid == 0 || Native.ProcessId(hwnd) != shellPid ||
                !IsTargetClass(kind, Native.ClassName(hwnd))) return false;
            if (kind != "Icons") return Native.GetAncestor(hwnd, 2 /* GA_ROOT */) == hwnd;

            IntPtr view = Native.GetAncestor(hwnd, 1 /* GA_PARENT */);
            IntPtr root = Native.GetAncestor(hwnd, 2 /* GA_ROOT */);
            string rootClass = Native.ClassName(root);
            return (rootClass == "Progman" || rootClass == "WorkerW") &&
                Native.ClassName(view) == "SHELLDLL_DefView" &&
                Native.GetAncestor(view, 1) == root &&
                Native.ProcessId(view) == shellPid && Native.ProcessId(root) == shellPid;
        }

        public List<ShellTarget> Discover()
        {
            List<ShellTarget> targets = new List<ShellTarget>();
            uint shellPid = Native.ProcessId(Native.GetShellWindow());
            if (shellPid == 0) return targets;
            long started;
            using (Process process = Process.GetProcessById((int)shellPid))
            {
                if (!String.Equals(process.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase)) return targets;
                started = process.StartTime.ToUniversalTime().Ticks;
            }
            var candidates = new List<KeyValuePair<IntPtr, string>>();
            Native.EnumWindows(delegate(IntPtr top, IntPtr unused)
            {
                if (Native.ProcessId(top) != shellPid) return true;
                string cls = Native.ClassName(top);
                if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd")
                    candidates.Add(new KeyValuePair<IntPtr, string>(top, cls == "Shell_TrayWnd" ? "Taskbar" : "SecondaryTaskbar"));
                if (cls == "Progman" || cls == "WorkerW")
                {
                    IntPtr view = Native.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
                    if (view != IntPtr.Zero)
                    {
                        IntPtr icons = Native.FindWindowEx(view, IntPtr.Zero, "SysListView32", null);
                        if (icons != IntPtr.Zero)
                            candidates.Add(new KeyValuePair<IntPtr, string>(icons, "Icons"));
                    }
                }
                return true;
            }, IntPtr.Zero);
            foreach (var candidate in candidates)
            {
                if (!MatchesShellTarget(candidate.Key, candidate.Value, shellPid)) continue;
                targets.Add(new ShellTarget
                {
                    Handle = candidate.Key.ToInt64(),
                    ProcessId = (int)shellPid,
                    ProcessStarted = started,
                    ClassName = Native.ClassName(candidate.Key),
                    Kind = candidate.Value,
                    WasVisible = Native.Visible(candidate.Key)
                });
            }
            return targets;
        }

        public bool Alive(ShellTarget t)
        {
            IntPtr hwnd = new IntPtr(t.Handle);
            if (!Native.IsWindow(hwnd) || Native.ProcessId(hwnd) != (uint)t.ProcessId ||
                Native.ClassName(hwnd) != t.ClassName) return false;
            uint shellPid = Native.ProcessId(Native.GetShellWindow());
            if (shellPid == 0)
                throw new InvalidOperationException("暂时无法确认桌面 Shell。恢复记录已保留。");
            if (!MatchesShellTarget(hwnd, t.Kind, shellPid)) return false;
            try
            {
                using (Process process = Process.GetProcessById(t.ProcessId))
                    return String.Equals(process.ProcessName, "explorer", StringComparison.OrdinalIgnoreCase) &&
                        process.StartTime.ToUniversalTime().Ticks == t.ProcessStarted;
            }
            catch (ArgumentException)
            {
                if (!Native.IsWindow(hwnd)) return false;
                throw;
            }
        }
        public bool Visible(ShellTarget t) { return Native.Visible(new IntPtr(t.Handle)); }
        public void SetVisible(ShellTarget t, bool visible)
        {
            if (!Alive(t)) return;
            IntPtr hwnd = new IntPtr(t.Handle);
            if (!Native.ShowWindowAsync(hwnd, visible ? 8 /* SW_SHOWNA */ : 0 /* SW_HIDE */))
            {
                int error = Marshal.GetLastWin32Error();
                if (!Alive(t)) return;
                throw new Win32Exception(error, "Windows 未接受可见状态请求：" + t.Kind);
            }
            Stopwatch timeout = Stopwatch.StartNew();
            while (Native.IsWindow(hwnd) && Visible(t) != visible && timeout.ElapsedMilliseconds < 400) Thread.Sleep(10);
            if (Alive(t) && Visible(t) != visible)
                throw new InvalidOperationException("无法" + (visible ? "恢复" : "隐藏") + "窗口：" + t.Kind);
        }
    }
}
