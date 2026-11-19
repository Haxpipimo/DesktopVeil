using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Serialization;

namespace DesktopVeil
{
    public class RecoveryState
    {
        public int Version = 1;
        public List<ShellTarget> Targets = new List<ShellTarget>();
    }
    public class Preferences
    {
        public bool HasPosition;
        public int X;
        public int Y;
        public string DockSide;
    }
    internal static class Storage
    {
        internal const int MaxXmlBytes = 128 * 1024;
        internal static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopVeil");
        internal static string RecoveryPath { get { return Path.Combine(Folder, "recovery-" + System.Diagnostics.Process.GetCurrentProcess().SessionId + ".xml"); } }
        internal static string PositionPath { get { return Path.Combine(Folder, "position.xml"); } }
        internal static T Load<T>(string path) where T : new()
        {
            if (!RegularFileExists(path)) return new T();
            using (FileStream stream = File.OpenRead(path))
            {
                if (stream.Length > MaxXmlBytes) throw new InvalidDataException("本地 XML 文件超出大小限制。");
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = MaxXmlBytes
                };
                using (XmlReader reader = XmlReader.Create(stream, settings))
                    return (T)new XmlSerializer(typeof(T)).Deserialize(reader);
            }
        }
        private static FileAttributes? ReadAttributes(string path)
        {
            try { return File.GetAttributes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }
        internal static bool RegularFileExists(string path)
        {
            string parent = Path.GetDirectoryName(path);
            FileAttributes? folder = ReadAttributes(parent);
            if (folder.HasValue && (folder.Value & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != FileAttributes.Directory)
                throw new IOException("本地数据目录不可用。");
            FileAttributes? file = ReadAttributes(path);
            if (!file.HasValue) return false;
            if ((file.Value & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new IOException("本地数据路径不是常规文件。");
            return true;
        }
        internal static void Save<T>(string path, T data)
        {
            WriteAtomically(path, delegate(FileStream stream)
            {
                new XmlSerializer(typeof(T)).Serialize(stream, data);
                if (stream.Length > MaxXmlBytes) throw new InvalidDataException("本地 XML 文件超出大小限制。");
            });
        }
        internal static void WriteAtomically(string path, Action<FileStream> write)
        {
            RegularFileExists(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            bool created = false;
            try
            {
                using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    created = true;
                    write(stream);
                    stream.Flush(true);
                }
                if (RegularFileExists(path)) File.Replace(temp, path, null);
                else File.Move(temp, path);
            }
            finally
            {
                if (created)
                    try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
    internal interface IRecoveryStore
    {
        bool Exists { get; }
        RecoveryState Load();
        void Save(RecoveryState state);
        void Clear();
    }
    internal sealed class RecoveryStore : IRecoveryStore
    {
        private const int MaxTargets = 128;
        private readonly string path;
        internal RecoveryStore() : this(Storage.RecoveryPath) { }
        internal RecoveryStore(string path) { this.path = path; }
        public bool Exists { get { return Storage.RegularFileExists(path); } }
        public RecoveryState Load()
        {
            RecoveryState state = Storage.Load<RecoveryState>(path);
            Validate(state);
            return state;
        }
        private static void Validate(RecoveryState state)
        {
            if (state == null || state.Version != 1 || state.Targets == null || state.Targets.Count > MaxTargets)
                throw new InvalidDataException("恢复记录不受支持或超出容量。");
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (ShellTarget t in state.Targets)
                if (t == null || t.Handle == 0 || t.Handle != unchecked((int)t.Handle) ||
                    t.ProcessId <= 0 || t.ProcessStarted <= 0 || t.ProcessStarted > DateTime.MaxValue.Ticks ||
                    !ShellWindows.IsTargetClass(t.Kind, t.ClassName) || !identities.Add(t.Identity))
                    throw new InvalidDataException("恢复记录含有无效或重复的窗口。");
        }
        public void Save(RecoveryState state)
        {
            Validate(state);
            Storage.Save(path, state);
        }
        public void Clear() { if (Exists) File.Delete(path); }
    }

    internal sealed class VisibilityController
    {
        private readonly IShellWindows shell;
        private readonly IRecoveryStore store;
        private RecoveryState state = new RecoveryState();
        internal bool Hidden { get; private set; }
        internal bool RecoveryPending { get { return state.Targets.Count > 0 || store.Exists; } }
        internal bool ShouldOfferRecovery
        {
            get
            {
                try { return Hidden || RecoveryPending; }
                catch (IOException) { return true; }
                catch (UnauthorizedAccessException) { return true; }
            }
        }
        internal VisibilityController(IShellWindows shell, IRecoveryStore store) { this.shell = shell; this.store = store; }

        internal bool RecoverPrevious()
        {
            if (!store.Exists) return false;
            state = store.Load();
            Restore();
            return true;
        }
        internal void Toggle() { if (Hidden || RecoveryPending) Restore(); else Hide(); }
        internal void Hide()
        {
            if (RecoveryPending) throw new InvalidOperationException("请先恢复上一次隐藏状态。");
            List<ShellTarget> targets = shell.Discover();
            if (!targets.Any(t => t.Kind == "Icons") || !targets.Any(t => t.Kind == "Taskbar"))
                throw new InvalidOperationException("未找到桌面图标或主任务栏。请等待 Windows 桌面加载完成后重试。");
            var next = new RecoveryState { Targets = targets };
            // The recovery snapshot must be on disk before any window operation.
            store.Save(next);
            state = next;
            try
            {
                foreach (ShellTarget t in state.Targets) shell.SetVisible(t, false);
                Hidden = true;
            }
            catch (Exception error) { Rollback(error); }
        }
        internal void Maintain()
        {
            if (!Hidden) return;
            try
            {
                List<ShellTarget> found = shell.Discover();
                if (found.Count == 0) return;
                var next = new RecoveryState { Targets = state.Targets.Where(shell.Alive).ToList() };
                foreach (ShellTarget t in found)
                    if (!next.Targets.Any(old => old.Identity == t.Identity)) next.Targets.Add(t);
                if (!next.Targets.Select(t => t.Identity).SequenceEqual(state.Targets.Select(t => t.Identity)))
                {
                    store.Save(next);
                    state = next;
                }
                foreach (ShellTarget t in found)
                    if (shell.Visible(t)) shell.SetVisible(t, false);
            }
            catch (Exception error) { Rollback(error); }
        }
        private void Rollback(Exception original)
        {
            try { Restore(); }
            catch (Exception rollback) { throw new InvalidOperationException(original.Message + "\n恢复未全部完成，请再次点击“恢复显示”。\n" + rollback.Message, original); }
            throw new InvalidOperationException(original.Message + "\n本次操作已撤销。", original);
        }
        internal void Restore()
        {
            Hidden = false; // Stop enforcement before doing any restoring.
            if (state.Targets.Count == 0 && store.Exists) state = store.Load();
            List<ShellTarget> failed = new List<ShellTarget>();
            List<string> errors = new List<string>();
            foreach (ShellTarget t in state.Targets)
            {
                try { if (shell.Alive(t)) shell.SetVisible(t, t.WasVisible); }
                catch (Exception error) { failed.Add(t); errors.Add(error.Message); }
            }
            state = new RecoveryState { Targets = failed };
            if (failed.Count > 0)
            {
                store.Save(state);
                throw new InvalidOperationException(String.Join("\n", errors.ToArray()));
            }
            store.Clear();
        }
    }
}
