using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using DesktopVeil;

internal static partial class Tests
{
    private static void WithPrivateTestFolder(Action<string> action)
    {
        string folder = Path.Combine(Path.GetTempPath(), "DesktopVeil-validation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try { action(folder); }
        finally
        {
            foreach (string file in Directory.GetFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
    }

    private static void SecurityTests()
    {
        Test("oversize or DTD XML is rejected without modifying the original", delegate
        {
            WithPrivateTestFolder(delegate(string folder)
            {
                string path = Path.Combine(folder, "recovery.xml");
                byte[] large = new byte[Storage.MaxXmlBytes + 1];
                File.WriteAllBytes(path, large);
                Throws(delegate { Storage.Load<RecoveryState>(path); });
                Assert(new FileInfo(path).Length == large.Length, "Oversize record was modified");

                const string xml = "<!DOCTYPE RecoveryState><RecoveryState />";
                File.WriteAllText(path, xml);
                Throws(delegate { Storage.Load<RecoveryState>(path); });
                Assert(File.ReadAllText(path) == xml, "Rejected XML was modified");
            });
        });
        Test("a directory at the journal path is an error, not a missing record", delegate
        {
            WithPrivateTestFolder(delegate(string folder)
            {
                string path = Path.Combine(folder, "recovery.xml");
                Directory.CreateDirectory(path);
                try { Throws(delegate { var exists = new RecoveryStore(path).Exists; }); }
                finally { Directory.Delete(path); }
            });
        });
        Test("recovery validation rejects duplicates and invalid window records", delegate
        {
            WithPrivateTestFolder(delegate(string folder)
            {
                string path = Path.Combine(folder, "recovery.xml");
                var state = new RecoveryState { Targets = new FakeShell(new MemoryStore()).Discover() };
                var store = new RecoveryStore(path);
                store.Save(state);
                Assert(store.Load().Targets.Count == 2, "Valid recovery record rejected");
                state.Targets.Add(state.Targets[0]);
                Throws(delegate { store.Save(state); });
                Assert(store.Load().Targets.Count == 2, "Rejected save replaced the old record");
                state.Targets.RemoveAt(2);
                state.Targets[0].Handle = 0;
                Storage.Save(path, state);
                Throws(delegate { store.Load(); });
                Assert(store.Exists, "Invalid record disappeared");
            });
        });
        Test("replacement windows do not accumulate stale recovery records", delegate
        {
            var store = new MemoryStore();
            var shell = new FakeShell(store);
            var controller = new VisibilityController(shell, store);
            controller.Hide();
            for (int i = 0; i < 150; i++)
            {
                shell.Windows.Clear();
                shell.Add(10 + i * 2, "Icons", true);
                shell.Add(11 + i * 2, "Taskbar", true);
                controller.Maintain();
                Assert(store.Data.Targets.Count == 2 && shell.WritesWereJournaled, "Journal grew or writes preceded saving");
            }
            controller.Restore();
            Assert(!store.Exists, "Recovery record was not cleared");
        });
        Test("IPC objects are owned by the current account and still deliver commands", delegate
        {
            using (WindowsIdentity identity = WindowsIdentity.GetCurrent())
            {
                string name = "DesktopVeil.Tests." + Guid.NewGuid().ToString("N");
                int session = Process.GetCurrentProcess().SessionId;
                using (var owner = new InstanceChannel(identity.User, session, name))
                {
                    Assert(owner.TryAcquire(), "Test mutex was unavailable");
                    using (var signal = owner.CreateSignal("restore"))
                    using (var client = new InstanceChannel(identity.User, session, name))
                    {
                        EventWaitHandleSecurity security = signal.GetAccessControl();
                        Assert(security.AreAccessRulesProtected && identity.User.Equals(security.GetOwner(typeof(SecurityIdentifier))), "Unexpected event ACL owner");
                        Assert(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<EventWaitHandleAccessRule>()
                            .All(rule => identity.User.Equals(rule.IdentityReference)), "Unexpected event ACL principal");
                        client.Signal("restore");
                        Assert(signal.WaitOne(1000), "Authorized command was not delivered");
                    }
                }
            }
        });
    }
}
