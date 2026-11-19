using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using DesktopVeil;

internal sealed class MemoryStore : IRecoveryStore
{
    internal RecoveryState Data;
    internal bool FailSave;
    internal bool FailLoad;
    internal int Saves;
    public bool Exists { get { return Data != null; } }
    public RecoveryState Load()
    {
        if (FailLoad) throw new IOException("simulated unreadable journal");
        return new RecoveryState { Targets = Data.Targets.ToList() };
    }
    public void Save(RecoveryState s)
    {
        Saves++;
        if (FailSave) throw new IOException("simulated journal failure");
        Data = new RecoveryState { Targets = s.Targets.ToList() };
    }
    public void Clear() { Data = null; }
}
internal sealed class FakeShell : IShellWindows
{
    internal List<ShellTarget> Windows = new List<ShellTarget>();
    internal Dictionary<long, bool> Visibility = new Dictionary<long, bool>();
    internal long FailHide, FailRestore;
    internal bool WritesWereJournaled = true;
    internal MemoryStore Store;
    internal int Writes;
    internal FakeShell(MemoryStore store) { Store = store; Add(1, "Icons", true); Add(2, "Taskbar", true); }
    internal void Add(long handle, string kind, bool visible)
    {
        Windows.Add(new ShellTarget { Handle = handle, ProcessId = 123, ProcessStarted = 1000, Kind = kind,
            ClassName = kind == "Icons" ? "SysListView32" : kind == "Taskbar" ? "Shell_TrayWnd" : "Shell_SecondaryTrayWnd", WasVisible = visible });
        Visibility[handle] = visible;
    }
    public List<ShellTarget> Discover()
    {
        return Windows.Select(w => new ShellTarget { Handle = w.Handle, ProcessId = w.ProcessId,
            ProcessStarted = w.ProcessStarted, Kind = w.Kind, ClassName = w.ClassName, WasVisible = Visibility[w.Handle] }).ToList();
    }
    public bool Alive(ShellTarget t) { return Windows.Any(w => w.Identity == t.Identity); }
    public bool Visible(ShellTarget t) { return Visibility[t.Handle]; }
    public void SetVisible(ShellTarget t, bool visible)
    {
        if (!Alive(t)) return;
        Writes++;
        if (!visible && (!Store.Exists || !Store.Data.Targets.Any(w => w.Identity == t.Identity))) WritesWereJournaled = false;
        if ((!visible && t.Handle == FailHide) || (visible && t.Handle == FailRestore)) throw new IOException("simulated window failure");
        Visibility[t.Handle] = visible;
    }
}
internal static partial class Tests
{
    private static int passed;
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Test(string name, Action action) { action(); passed++; Console.WriteLine("PASS " + name); }
    private static void Throws(Action a) { bool failed = false; try { a(); } catch (Exception) { failed = true; } Assert(failed, "Expected failure"); }
    private static void UnitTests()
    {
        Test("50 toggles restore every original window", delegate
        {
            var s = new MemoryStore(); var w = new FakeShell(s); var c = new VisibilityController(w, s);
            for (int i = 0; i < 50; i++) { c.Toggle(); Assert(c.Hidden && w.Visibility.Values.All(v => !v), "Hide failed"); c.Toggle(); Assert(!c.Hidden && w.Visibility.Values.All(v => v) && !s.Exists, "Restore failed"); }
            Assert(w.WritesWereJournaled, "Mutation preceded journal");
        });
        Test("originally hidden icons stay hidden", delegate
        {
            var s = new MemoryStore(); var w = new FakeShell(s); w.Visibility[1] = false; var c = new VisibilityController(w, s);
            c.Hide(); c.Restore(); Assert(!w.Visibility[1] && w.Visibility[2], "Original visibility lost");
        });
        Test("secondary taskbars are hidden and restored", delegate
        {
            var s = new MemoryStore(); var w = new FakeShell(s); w.Add(3, "SecondaryTaskbar", true); var c = new VisibilityController(w, s);
            c.Hide(); Assert(!w.Visibility[3], "Secondary bar not hidden"); c.Restore(); Assert(w.Visibility[3], "Secondary bar not restored");
        });
        Test("missing desktop leaves taskbar untouched", delegate
        {
            var s = new MemoryStore(); var w = new FakeShell(s); w.Windows.RemoveAt(0); var c = new VisibilityController(w, s);
            Throws(c.Hide); Assert(w.Writes == 0 && !s.Exists, "Partial mutation on missing shell");
        });
        Test("journal write failure makes no hide changes", delegate
        {
            var s = new MemoryStore { FailSave = true }; var w = new FakeShell(s); var c = new VisibilityController(w, s);
            Throws(c.Hide);
            Assert(w.Writes == 0 && w.Visibility.Values.All(v => v) && !c.Hidden, "Called the shell after the journal write failed");
        });
        Test("partial hide failure rolls back all windows", delegate
        {
            var s = new MemoryStore(); var w = new FakeShell(s) { FailHide = 2 }; var c = new VisibilityController(w, s);
            Throws(c.Hide); Assert(w.Visibility.Values.All(v => v) && !c.Hidden && !s.Exists, "Rollback failed");
        });
        Test("restore failure retains retryable recovery", delegate
        {
            var s = new MemoryStore(); var w = new FakeShell(s); var c = new VisibilityController(w, s); c.Hide(); w.FailRestore = 2;
            Throws(c.Restore); Assert(!c.Hidden && s.Exists && s.Data.Targets.Count == 1 && w.Visibility[1], "Lost failed restoration");
            w.FailRestore = 0; c.Toggle(); Assert(w.Visibility[2] && !s.Exists && !c.Hidden, "Retry failed");
        });
        Test("reappearing taskbar gets hidden again", delegate
        {
            var s = new MemoryStore(); var w = new FakeShell(s); var c = new VisibilityController(w, s); c.Hide(); w.Visibility[2] = true;
            c.Maintain(); Assert(!w.Visibility[2], "Maintenance failed"); c.Restore(); Assert(w.Visibility[2], "Restoration failed");
        });
        Test("Explorer restart journals and hides replacement windows", delegate
        {
            var s = new MemoryStore(); var w = new FakeShell(s); var c = new VisibilityController(w, s); c.Hide(); w.Windows.Clear();
            c.Maintain(); Assert(c.Hidden, "Empty shell cancelled hidden mode");
            w.Add(11, "Icons", true); w.Add(12, "Taskbar", true); c.Maintain();
            Assert(!w.Visibility[11] && !w.Visibility[12] && w.WritesWereJournaled, "New windows not journaled");
            c.Restore(); Assert(w.Visibility[11] && w.Visibility[12], "Replacement restoration failed");
        });
        Test("reused handles cannot restore unrelated windows", delegate
        {
            var s = new MemoryStore(); var w = new FakeShell(s); var c = new VisibilityController(w, s); c.Hide();
            w.Windows[1].ProcessStarted = 2000; int writes = w.Writes; c.Restore();
            Assert(w.Writes == writes + 1 && !s.Exists, "Restored a recycled handle");
        });
        Test("maintenance failure restores baseline", delegate
        {
            var s = new MemoryStore(); var w = new FakeShell(s); var c = new VisibilityController(w, s); c.Hide();
            w.Visibility[2] = true; w.FailHide = 2; Throws(c.Maintain);
            Assert(!c.Hidden && w.Visibility.Values.All(v => v) && !s.Exists, "Maintenance rollback failed");
        });
        Test("abnormal restart recovers without hiding again", delegate
        {
            var s = new MemoryStore(); var w = new FakeShell(s); new VisibilityController(w, s).Hide();
            var next = new VisibilityController(w, s); Assert(next.RecoverPrevious(), "Recovery not detected");
            Assert(!next.Hidden && w.Visibility.Values.All(v => v) && !s.Exists, "Recovery did not remain visible");
        });
        Test("recovery with pre-hidden icons preserves their state", delegate
        {
            var s = new MemoryStore(); var w = new FakeShell(s); w.Visibility[1] = false;
            new VisibilityController(w, s).Hide(); new VisibilityController(w, s).RecoverPrevious();
            Assert(!w.Visibility[1] && w.Visibility[2], "Recovery revealed hidden icons");
        });
        Test("XML journal roundtrip and atomic replacement", delegate
        {
            string dir = Path.Combine(Path.GetTempPath(), "DesktopVeil-Test-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "recovery.xml");
            try
            {
                var s = new MemoryStore(); var w = new FakeShell(s); var state = new RecoveryState { Targets = w.Discover() };
                Storage.Save(path, state); Assert(Storage.Load<RecoveryState>(path).Targets.Count == 2, "Bad roundtrip");
                state.Targets[0].WasVisible = false; Storage.Save(path, state);
                Assert(!Storage.Load<RecoveryState>(path).Targets[0].WasVisible && !File.Exists(path + ".tmp"), "Bad replace");
            }
            finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
        });
        Test("unreadable journal is retained and recovery can be retried", delegate
        {
            var s = new MemoryStore(); var w = new FakeShell(s); new VisibilityController(w, s).Hide();
            s.FailLoad = true; var next = new VisibilityController(w, s);
            Throws(delegate { next.RecoverPrevious(); }); Throws(next.Restore);
            Assert(s.Exists && w.Visibility.Values.All(v => !v), "Unreadable recovery was discarded");
            s.FailLoad = false; next.Restore(); Assert(!s.Exists && w.Visibility.Values.All(v => v), "Recovery retry failed");
        });
    }
    private static Process Launch(string exe, string args)
    {
        return Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false });
    }
    private static void Wait(Func<bool> condition, string message)
    {
        Stopwatch timeout = Stopwatch.StartNew();
        while (!condition()) { if (timeout.ElapsedMilliseconds > 8000) throw new Exception(message); Thread.Sleep(80); }
    }
    private static void LiveTests(string exe)
    {
        var shell = new ShellWindows();
        List<ShellTarget> baseline = shell.Discover();
        Assert(baseline.Any(t => t.Kind == "Icons") && baseline.Any(t => t.Kind == "Taskbar"), "Shell missing");
        int session = Process.GetCurrentProcess().SessionId;
        foreach (Process existing in Process.GetProcessesByName("DesktopVeil"))
            using (existing) Assert(existing.SessionId != session, "Close the existing DesktopVeil instance before live testing");
        Assert(!new RecoveryStore().Exists, "Existing recovery journal: restore the app before live testing");
        Console.WriteLine("LIVE baseline: " + String.Join(", ", baseline.Select(t => t.Kind + "=" + t.WasVisible).ToArray()));
        Console.WriteLine("LIVE screens: " + System.Windows.Forms.Screen.AllScreens.Length);
        Process running = null;
        Func<bool> allHidden = delegate { return baseline.All(t => shell.Alive(t) && !shell.Visible(t)); };
        Func<bool> restored = delegate { return baseline.All(t => shell.Alive(t) && shell.Visible(t) == t.WasVisible); };
        try
        {
            running = Launch(exe, ""); Wait(allHidden, "Startup hide timed out");
            Test("LIVE startup hides all shell targets", delegate { Assert(File.Exists(Storage.RecoveryPath), "Journal absent"); });
            Test("LIVE relaunch toggles and keeps one instance", delegate
            {
                using (Process second = Launch(exe, "")) Assert(second.WaitForExit(5000), "Second instance did not exit");
                Wait(restored, "Second launch did not restore"); Assert(!running.HasExited, "First instance exited");
            });
            Test("LIVE repeated launch toggles 10 times", delegate
            {
                for (int i = 0; i < 10; i++)
                {
                    using (Process second = Launch(exe, "")) second.WaitForExit(5000);
                    Wait(i % 2 == 0 ? allHidden : restored, "Toggle timed out " + i);
                }
            });
            Test("LIVE explicit restore and exit", delegate
            {
                using (Process second = Launch(exe, "")) second.WaitForExit(5000); Wait(allHidden, "Rehide failed");
                using (Process second = Launch(exe, "--restore")) second.WaitForExit(5000); Wait(restored, "--restore failed");
                using (Process second = Launch(exe, "--exit")) second.WaitForExit(5000);
                Assert(running.WaitForExit(5000), "--exit did not exit"); Assert(restored(), "Exit lost baseline");
            });
            Test("LIVE killed app recovers on next launch", delegate
            {
                running.Dispose(); running = Launch(exe, ""); Wait(allHidden, "Hide before kill failed");
                running.Kill(); running.WaitForExit(); running.Dispose(); running = Launch(exe, "");
                Wait(restored, "Crash recovery failed"); Thread.Sleep(600); Assert(restored(), "Recovery hid desktop again");
                using (Process second = Launch(exe, "--exit")) second.WaitForExit(5000);
                Assert(running.WaitForExit(5000), "Exit after recovery failed");
            });
            Test("LIVE pre-hidden icons stay hidden after toggle", delegate
            {
                ShellTarget icons = baseline.First(t => t.Kind == "Icons");
                shell.SetVisible(icons, false);
                try
                {
                    running.Dispose(); running = Launch(exe, ""); Wait(allHidden, "Pre-hidden startup failed");
                    using (Process second = Launch(exe, "--exit")) second.WaitForExit(5000);
                    Assert(running.WaitForExit(5000), "Exit failed");
                    Assert(!shell.Visible(icons), "Program made pre-hidden icons visible");
                }
                finally { shell.SetVisible(icons, icons.WasVisible); }
            });
        }
        finally
        {
            if (running != null && !running.HasExited)
            {
                using (Process recovery = Launch(exe, "--exit")) recovery.WaitForExit(5000);
                if (!running.WaitForExit(5000)) { running.Kill(); running.WaitForExit(); }
            }
            using (Process recovery = Launch(exe, "--restore")) recovery.WaitForExit(5000);
            foreach (ShellTarget t in baseline) if (shell.Alive(t)) shell.SetVisible(t, t.WasVisible);
            if (running != null) running.Dispose();
        }
        Assert(restored(), "Final baseline was not restored");
    }
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--ui")
            {
                DesktopVeil.Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
                System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
                System.Windows.Forms.Application.EnableVisualStyles();
                System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
                var store = new MemoryStore(); var shell = new FakeShell(store);
                using (var toggle = new EventWaitHandle(false, EventResetMode.AutoReset))
                using (var restore = new EventWaitHandle(false, EventResetMode.AutoReset))
                using (var exit = new EventWaitHandle(false, EventResetMode.AutoReset))
                using (var form = new ButtonForm(new VisibilityController(shell, store), toggle, restore, exit))
                {
                    // Expose exactly the production control to UI automation, which filters owned tool windows.
                    form.ShowInTaskbar = true;
                    form.Text = "桌面一键隐藏 · 界面验收";
                    System.Windows.Forms.Application.Run(form);
                }
                return 0;
            }
            DesktopVeil.Native.SetProcessDpiAwarenessContext(new IntPtr(-4));
            System.Windows.Forms.Application.SetUnhandledExceptionMode(System.Windows.Forms.UnhandledExceptionMode.CatchException);
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            UnitTests();
            DockTests();
            SecurityTests();
            int previewIndex = Array.IndexOf(args, "--preview");
            ComponentTests(previewIndex >= 0 ? args[previewIndex + 1] : null);
            int liveIndex = Array.IndexOf(args, "--live");
            if (liveIndex >= 0) LiveTests(Path.GetFullPath(args[liveIndex + 1]));
            Console.WriteLine("TOTAL " + passed + " PASS"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
