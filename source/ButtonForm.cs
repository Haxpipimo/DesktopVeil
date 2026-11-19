using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DesktopVeil
{
    internal sealed class ButtonForm : Form
    {
        private readonly VisibilityController controller;
        private readonly EventWaitHandle toggleSignal, restoreSignal, exitSignal;
        private readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        private readonly ToolTip hint = new ToolTip();
        private readonly ContextMenuStrip menu = new ContextMenuStrip();
        private readonly EdgeDock dock = new EdgeDock();
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private bool busy, dragging, pressed, hover, closing, toggleHotkey, restoreHotkey;
        private Point pointerStart, windowStart;
        private int dpi = 96;
        private readonly List<string> warnings = new List<string>();

        internal ButtonForm(VisibilityController controller, EventWaitHandle toggle, EventWaitHandle restore, EventWaitHandle exit)
        {
            this.controller = controller;
            toggleSignal = toggle; restoreSignal = restore; exitSignal = exit;
            Text = "桌面一键隐藏";
            AccessibleName = "桌面一键隐藏按钮";
            AccessibleRole = AccessibleRole.PushButton;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            using (Graphics graphics = CreateGraphics()) dpi = (int)graphics.DpiX;
            Size = new Size(ScalePixel(44), ScalePixel(44));
            Rectangle area = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(area.Right - Width, area.Top + (area.Height - Height) / 2);
            DockSide? savedSide = null;
            try
            {
                Preferences p = Storage.Load<Preferences>(Storage.PositionPath);
                if (p.HasPosition) Location = new Point(p.X, p.Y);
                if (p.DockSide == "Left") savedSide = DockSide.Left;
                if (p.DockSide == "Right") savedSide = DockSide.Right;
            }
            catch (Exception error) { Program.Log(error); }
            DockToEdge(savedSide);
            menu.Items.Add("恢复显示    Ctrl + Alt + F11", null, delegate { Perform(controller.Restore); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("恢复并退出", null, delegate { Close(); });
            ContextMenuStrip = menu;
            menu.Opening += delegate { dock.Expand(clock.ElapsedMilliseconds); ApplyDockBounds(); };
            menu.Closed += delegate { dock.Expand(clock.ElapsedMilliseconds); ApplyDockBounds(); };
            timer.Interval = 250;
            timer.Tick += Tick;
            SystemEvents.DisplaySettingsChanged += DisplayChanged;
            UpdateAppearance();
        }
        private int ScalePixel(int value) { return Math.Max(1, (int)Math.Round(value * dpi / 96.0)); }
        private void ApplyShape()
        {
            using (GraphicsPath path = Shape(new RectangleF(0, 0, Width, Height)))
            {
                Region previous = Region;
                Region = new Region(path);
                if (previous != null) previous.Dispose();
            }
            Invalidate();
        }
        private static GraphicsPath Shape(RectangleF bounds)
        {
            GraphicsPath path = new GraphicsPath();
            if (bounds.Width >= bounds.Height) path.AddEllipse(bounds);
            else
            {
                float d = bounds.Width;
                path.AddArc(bounds.Left, bounds.Top, d, d, 180, 180);
                path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 0, 180);
                path.CloseFigure();
            }
            return path;
        }
        private void DockToEdge(DockSide? preferred)
        {
            Rectangle area = Screen.FromRectangle(Bounds).WorkingArea;
            dock.Configure(area, ScalePixel(44), ScalePixel(8), Location, preferred, clock.ElapsedMilliseconds);
            ApplyDockBounds();
        }
        private void ApplyDockBounds()
        {
            if (!dock.Initialized) return;
            bool resized = Size != dock.Bounds.Size;
            Bounds = dock.Bounds;
            if (resized || Region == null) ApplyShape();
            Invalidate();
        }
        // Shared by the timer and component tests; this never sends input to other apps.
        internal void UpdateDock(long now, bool pointerInside, bool suppress)
        {
            dock.Update(now, pointerInside, suppress);
            ApplyDockBounds();
        }
        internal bool IsCollapsed { get { return dock.Collapsed; } }
        internal bool ShuttingDown { get; private set; }
        internal Rectangle ExpandedBounds { get { return dock.ExpandedBounds; } }
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            toggleHotkey = Native.RegisterHotKey(Handle, 1, 0x4003, (uint)Keys.F10);
            restoreHotkey = Native.RegisterHotKey(Handle, 2, 0x4003, (uint)Keys.F11);
            if (!toggleHotkey) warnings.Add("Ctrl + Alt + F10 已被占用，切换快捷键不可用。");
            if (!restoreHotkey) warnings.Add("Ctrl + Alt + F11 已被占用，恢复快捷键不可用。");
            Perform(delegate { if (!controller.RecoverPrevious()) controller.Hide(); });
            timer.Start();
            if (warnings.Count > 0)
                MessageBox.Show(this, String.Join("\n", warnings.ToArray()) + "\n仍可点击悬浮按钮或使用右键菜单。", "快捷键提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        private void Tick(object sender, EventArgs e)
        {
            if (busy || closing) return;
            if (exitSignal.WaitOne(0)) { Close(); return; }
            if (restoreSignal.WaitOne(0))
            {
                toggleSignal.WaitOne(0);
                Perform(controller.Restore);
            }
            else if (toggleSignal.WaitOne(0)) Perform(controller.Toggle);
            if (controller.Hidden) Perform(controller.Maintain);
            if (pressed || dragging) { dock.Expand(clock.ElapsedMilliseconds); return; }
            if (!closing && !IsDisposed)
            {
                Point pointer = PointToClient(MousePosition);
                bool inside = ClientRectangle.Contains(pointer) && Region != null && Region.IsVisible(pointer);
                UpdateDock(clock.ElapsedMilliseconds, inside, pressed || dragging || menu.Visible || !Enabled);
            }
        }
        private void Perform(Action action)
        {
            if (busy || closing) return;
            busy = true;
            try { action(); }
            catch (Exception error) { Program.ShowError(error); }
            finally { UpdateAppearance(); busy = false; }
        }
        internal void RecoverFromError(Exception error)
        {
            if (busy || closing)
            {
                Program.Log(error);
                return;
            }
            busy = true;
            timer.Stop();
            try
            {
                try { controller.Restore(); }
                catch (Exception recovery)
                {
                    error = new InvalidOperationException(error.Message + "\n" + recovery.Message, error);
                }
                UpdateAppearance();
                Program.ShowError(error);
            }
            finally
            {
                busy = false;
                if (!closing && !IsDisposed) timer.Start();
            }
        }
        private void UpdateAppearance()
        {
            bool restore = controller.ShouldOfferRecovery;
            AccessibleDescription = restore ? "点击恢复桌面图标和任务栏" : "点击隐藏桌面图标和任务栏";
            hint.SetToolTip(this, AccessibleDescription + "\n拖动吸附左右边缘；离开约 1 秒收起，移入展开\n右键可恢复并退出");
            Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            bool restore = controller.ShouldOfferRecovery;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Color background = restore ? Color.FromArgb(20, 101, 104) : Color.FromArgb(34, 43, 58);
            if (hover) background = ControlPaint.Light(background, 0.14f);
            e.Graphics.Clear(background);
            float inset = ScalePixel(1);
            using (Pen border = new Pen(Color.FromArgb(100, 220, 230, 235), ScalePixel(1)))
            using (GraphicsPath outline = Shape(new RectangleF(inset, inset, Math.Max(1, Width - inset * 2), Math.Max(1, Height - inset * 2))))
                e.Graphics.DrawPath(border, outline);
            if (dock.Collapsed)
            {
                using (Pen marker = new Pen(Color.FromArgb(225, 245, 245), ScalePixel(2)))
                {
                    marker.StartCap = LineCap.Round; marker.EndCap = LineCap.Round;
                    e.Graphics.DrawLine(marker, Width / 2f, Height / 2f - ScalePixel(7), Width / 2f, Height / 2f + ScalePixel(7));
                }
                return;
            }
            using (Font font = new Font("Microsoft YaHei UI", 16f * dpi / 96f, FontStyle.Regular, GraphicsUnit.Pixel))
                TextRenderer.DrawText(e.Graphics, restore ? "显" : "隐", font, ClientRectangle, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            hover = true;
            if (!pressed) { dock.Expand(clock.ElapsedMilliseconds); ApplyDockBounds(); }
            else Invalidate();
        }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = false; Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            dock.Expand(clock.ElapsedMilliseconds); ApplyDockBounds();
            pressed = true; dragging = false; pointerStart = MousePosition; windowStart = Location; Capture = true;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!pressed) return;
            Point current = MousePosition;
            int dx = current.X - pointerStart.X, dy = current.Y - pointerStart.Y;
            if (Math.Abs(dx) >= ScalePixel(5) || Math.Abs(dy) >= ScalePixel(5)) dragging = true;
            if (dragging) Location = new Point(windowStart.X + dx, windowStart.Y + dy);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left || !pressed) return;
            bool moved = dragging;
            pressed = false; dragging = false; Capture = false;
            if (moved) { DockToEdge(null); SavePosition(); }
            else if (ClientRectangle.Contains(e.Location) && Region.IsVisible(e.Location)) Perform(controller.Toggle);
            dock.Expand(clock.ElapsedMilliseconds); ApplyDockBounds();
        }
        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (!Capture && pressed)
            {
                bool moved = dragging;
                pressed = false; dragging = false;
                if (moved) { DockToEdge(null); SavePosition(); }
            }
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { Perform(controller.Toggle); e.Handled = true; }
        }
        private void SavePosition()
        {
            try { Storage.Save(Storage.PositionPath, new Preferences { HasPosition = true,
                X = dock.ExpandedBounds.X, Y = dock.ExpandedBounds.Y, DockSide = dock.Side.ToString() }); }
            catch (Exception error) { Program.Log(error); hint.SetToolTip(this, "位置保存失败；当前切换功能可正常使用。"); }
        }
        private void DisplayChanged(object sender, EventArgs e)
        {
            if (IsHandleCreated && !IsDisposed)
                try { BeginInvoke((Action)delegate { if (!IsDisposed && !closing) { DockToEdge(dock.Side); SavePosition(); } }); }
                catch (InvalidOperationException) { }
        }
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0312)
            {
                long keys = m.LParam.ToInt64();
                if (toggleHotkey && m.WParam == new IntPtr(1) && keys == (((long)Keys.F10 << 16) | 3))
                    Perform(controller.Toggle);
                if (restoreHotkey && m.WParam == new IntPtr(2) && keys == (((long)Keys.F11 << 16) | 3))
                    Perform(controller.Restore);
                return;
            }
            if (m.Msg == 0x02E0) // WM_DPICHANGED: per-monitor size and suggested position.
            {
                dpi = m.WParam.ToInt32() & 0xffff;
                Rect r = (Rect)Marshal.PtrToStructure(m.LParam, typeof(Rect));
                Location = new Point(r.Left, r.Top);
                Size = new Size(ScalePixel(44), ScalePixel(44));
                ApplyShape();
                if (pressed)
                {
                    pointerStart = MousePosition; windowStart = Location;
                }
                else if (dock.Initialized) DockToEdge(dock.Side);
                return;
            }
            base.WndProc(ref m);
            if (m.Msg == 0x001A && IsHandleCreated && !closing && !pressed && dock.Initialized) DockToEdge(dock.Side);
        }
        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            ShuttingDown = e.CloseReason == CloseReason.WindowsShutDown;
            if (busy && !ShuttingDown)
            {
                e.Cancel = true;
                base.OnFormClosing(e);
                return;
            }
            closing = true; timer.Stop();
            try { controller.Restore(); SavePosition(); }
            catch (Exception error)
            {
                if (ShuttingDown) Program.Log(error);
                else
                {
                    Program.ShowError(error);
                    e.Cancel = true;
                    closing = false;
                    timer.Start();
                }
            }
            base.OnFormClosing(e);
        }
        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            SystemEvents.DisplaySettingsChanged -= DisplayChanged;
            if (toggleHotkey) Native.UnregisterHotKey(Handle, 1);
            if (restoreHotkey) Native.UnregisterHotKey(Handle, 2);
            base.OnFormClosed(e);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.DisplaySettingsChanged -= DisplayChanged;
                timer.Dispose(); hint.Dispose(); menu.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
