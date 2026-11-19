using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("桌面一键隐藏")]
[assembly: AssemblyDescription("桌面图标与任务栏的可恢复隐藏按钮")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: InternalsVisibleTo("DesktopVeil.Tests")]

namespace DesktopVeil
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            string command = args.Length == 0 ? "toggle" : args[0].ToLowerInvariant();
            if (args.Length > 1 || (command != "toggle" && command != "--restore" && command != "--exit"))
            {
                MessageBox.Show("双击运行：立即隐藏；再次运行：切换。\n--restore：恢复显示\n--exit：恢复并退出", "桌面一键隐藏");
                return;
            }
            try { Native.SetProcessDpiAwarenessContext(new IntPtr(-4)); }
            catch (EntryPointNotFoundException) { Native.SetProcessDPIAware(); }
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            try { Run(command); }
            catch (Exception error) { ShowError(error); }
        }

        private static void Run(string command)
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            using (var channel = new InstanceChannel(identity.User, Process.GetCurrentProcess().SessionId))
            {
                if (!channel.TryAcquire())
                {
                    channel.Signal(command.TrimStart('-'));
                    return;
                }
                VisibilityController controller = new VisibilityController(new ShellWindows(), new RecoveryStore());
                if (command != "toggle")
                {
                    controller.RecoverPrevious();
                    return;
                }
                using (EventWaitHandle toggle = channel.CreateSignal("toggle"))
                using (EventWaitHandle restore = channel.CreateSignal("restore"))
                using (EventWaitHandle exit = channel.CreateSignal("exit"))
                using (ButtonForm form = new ButtonForm(controller, toggle, restore, exit))
                {
                    ThreadExceptionEventHandler handler = delegate(object sender, ThreadExceptionEventArgs e)
                    {
                        form.RecoverFromError(e.Exception);
                    };
                    Application.ThreadException += handler;
                    // Process-wide fatal exceptions can run off the UI thread. The durable
                    // journal handles that case without a concurrent restore on another thread.
                    try { Application.Run(form); }
                    finally
                    {
                        Application.ThreadException -= handler;
                        if (!form.ShuttingDown) controller.Restore();
                    }
                }
            }
        }
        internal static void Log(Exception error)
        {
            try
            {
                string path = Path.Combine(Storage.Folder, "errors.log");
                string message = DateTime.Now.ToString("s") + " " + error + Environment.NewLine;
                byte[] bytes = Encoding.UTF8.GetBytes(message.Substring(0, Math.Min(message.Length, 8192)));
                Storage.WriteAtomically(path, stream => stream.Write(bytes, 0, bytes.Length));
            }
            catch { }
        }
        internal static void ShowError(Exception error)
        {
            Log(error);
            MessageBox.Show(error.Message + "\n\n可用 Ctrl + Alt + F11 或右键菜单重试恢复。", "桌面一键隐藏", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    internal sealed class InstanceChannel : IDisposable
    {
        private readonly SecurityIdentifier owner;
        private readonly string name;
        private readonly Mutex mutex;
        private bool acquired;

        internal InstanceChannel(SecurityIdentifier owner, int session, string channel = "DesktopVeil")
        {
            this.owner = owner;
            name = "Local\\" + channel + "." + owner.Value + "." + session;
            var security = new MutexSecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new MutexAccessRule(owner, MutexRights.FullControl, AccessControlType.Allow));
            bool created;
            mutex = new Mutex(false, name, out created, security);
            try { CheckOwner(mutex.GetAccessControl()); }
            catch { mutex.Dispose(); throw; }
        }

        private void CheckOwner(ObjectSecurity security)
        {
            if (!owner.Equals(security.GetOwner(typeof(SecurityIdentifier))))
                throw new UnauthorizedAccessException("桌面一键隐藏的实例通信对象所有者不匹配。");
        }

        internal bool TryAcquire()
        {
            try { acquired = mutex.WaitOne(0); }
            catch (AbandonedMutexException) { acquired = true; }
            return acquired;
        }

        internal EventWaitHandle CreateSignal(string command)
        {
            var security = new EventWaitHandleSecurity();
            security.SetOwner(owner);
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new EventWaitHandleAccessRule(owner, EventWaitHandleRights.FullControl, AccessControlType.Allow));
            bool created;
            var signal = new EventWaitHandle(false, EventResetMode.AutoReset, name + "." + command, out created, security);
            try
            {
                CheckOwner(signal.GetAccessControl());
                // The mutex is held here. Clear any stale signal if an old sender kept it open.
                if (!created) signal.Reset();
                return signal;
            }
            catch { signal.Dispose(); throw; }
        }

        internal void Signal(string command)
        {
            Stopwatch wait = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    using (EventWaitHandle signal = EventWaitHandle.OpenExisting(name + "." + command,
                        EventWaitHandleRights.Modify | EventWaitHandleRights.ReadPermissions))
                    {
                        CheckOwner(signal.GetAccessControl());
                        signal.Set();
                    }
                    return;
                }
                catch (WaitHandleCannotBeOpenedException)
                {
                    if (wait.ElapsedMilliseconds >= 5000) throw new InvalidOperationException("现有程序未响应，请稍后重试。");
                    Thread.Sleep(50);
                }
            }
        }

        public void Dispose()
        {
            if (acquired) mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }
}
