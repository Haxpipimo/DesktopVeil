using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using DesktopVeil;

internal static partial class Tests
{
    private static void DockTests()
    {
        Test("dock snaps to nearest left or right working-area edge", delegate
        {
            var d = new EdgeDock(); var area = new Rectangle(0, 0, 1920, 1040);
            d.Configure(area, 44, 8, new Point(100, 500), null, 0);
            Assert(d.Side == DockSide.Left && d.Bounds == new Rectangle(0, 500, 44, 44), "Left snap failed");
            d.Configure(area, 44, 8, new Point(1500, 500), null, 0);
            Assert(d.Side == DockSide.Right && d.Bounds == new Rectangle(1876, 500, 44, 44), "Right snap failed");
        });
        Test("dock waits one second then leaves an 8px visible tab", delegate
        {
            var d = new EdgeDock(); var area = new Rectangle(0, 0, 1920, 1040);
            d.Configure(area, 44, 8, new Point(1800, 500), null, 0);
            d.Update(999, false, false); Assert(!d.Collapsed, "Collapsed too early");
            d.Update(1000, false, false);
            Assert(d.Collapsed && d.Bounds == new Rectangle(1912, 500, 8, 44) && area.Contains(d.Bounds), "Tab missing or offscreen");
        });
        Test("hover expands immediately and renews idle timeout", delegate
        {
            var d = new EdgeDock(); d.Configure(new Rectangle(0, 0, 1920, 1040), 44, 8, new Point(1800, 500), null, 0);
            d.Update(1000, false, false); d.Update(1100, true, false);
            Assert(!d.Collapsed && d.Bounds.Width == 44, "Hover did not expand");
            d.Update(2099, false, false); Assert(!d.Collapsed, "Timeout was not reset");
            d.Update(2100, false, false); Assert(d.Collapsed, "Did not collapse after renewed timeout");
        });
        Test("drag and context menu suppress auto-hide", delegate
        {
            var d = new EdgeDock(); d.Configure(new Rectangle(0, 0, 1920, 1040), 44, 8, new Point(100, 500), null, 0);
            d.Update(5000, false, true); Assert(!d.Collapsed, "Collapsed during interaction");
            d.Update(5999, false, false); Assert(!d.Collapsed, "No grace after interaction");
            d.Update(6000, false, false); Assert(d.Collapsed, "Idle hide not resumed");
        });
        Test("negative-coordinate monitor tab stays entirely inside its own display", delegate
        {
            var d = new EdgeDock(); var area = new Rectangle(-1920, -200, 1920, 1040);
            d.Configure(area, 44, 8, new Point(-40, 500), null, 0); d.Update(1000, false, false);
            Assert(d.Side == DockSide.Right && d.Bounds.Right == 0 && area.Contains(d.Bounds), "Tab spilled into adjacent screen");
            d.Configure(area, 44, 8, new Point(-1900, 500), null, 0); d.Update(1000, false, false);
            Assert(d.Side == DockSide.Left && d.Bounds.Left == -1920 && area.Contains(d.Bounds), "Left tab escaped screen");
        });
        Test("DPI scaling keeps circle proportions and scales peek width", delegate
        {
            foreach (int scale in new int[] { 1, 2, 3 })
            {
                var d = new EdgeDock(); d.Configure(new Rectangle(0, 0, 3840, 2100), 44 * scale, 8 * scale, new Point(3000, 500), null, 0);
                Assert(d.Bounds.Width == 44 * scale && d.Bounds.Height == 44 * scale, "Diameter did not scale");
                d.Update(1000, false, false); Assert(d.Bounds.Width == 8 * scale, "Peek did not scale");
            }
        });
        Test("monitor changes clamp vertical position and preserve selected side", delegate
        {
            var d = new EdgeDock(); var area = new Rectangle(100, 50, 800, 550);
            d.Configure(area, 44, 8, new Point(5000, 5000), DockSide.Left, 0);
            Assert(d.Bounds == new Rectangle(100, 556, 44, 44), "Offscreen saved position not clamped");
            d.Configure(area, 44, 8, new Point(-900, -900), DockSide.Right, 0);
            Assert(d.Bounds == new Rectangle(856, 50, 44, 44), "Side or upper bound lost");
        });
        Test("collapsed location is separate from persisted expanded bounds", delegate
        {
            var d = new EdgeDock(); d.Configure(new Rectangle(0, 0, 1920, 1040), 44, 8, new Point(1800, 400), null, 0);
            Rectangle original = d.ExpandedBounds; d.Update(1000, false, false);
            Assert(d.ExpandedBounds == original && d.Bounds.Left != original.Left, "Collapsed coordinates replaced expanded coordinates");
            d.Expand(1100); Assert(d.Bounds == original, "Expand drifted from saved position");
        });
        Test("legacy preferences deserialize and new docking side roundtrips", delegate
        {
            string dir = Path.Combine(Path.GetTempPath(), "DesktopVeil-Test-" + Guid.NewGuid().ToString("N"));
            string path = Path.Combine(dir, "position.xml"); Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllText(path, "<Preferences><HasPosition>true</HasPosition><X>700</X><Y>300</Y></Preferences>");
                var p = Storage.Load<Preferences>(path); Assert(p.HasPosition && p.X == 700 && p.DockSide == null, "Legacy position rejected");
                p.DockSide = "Left"; Storage.Save(path, p); Assert(Storage.Load<Preferences>(path).DockSide == "Left", "Dock side not persisted");
            }
            finally { Directory.Delete(dir, true); }
        });
    }

    private static void ComponentTests(string previewPath)
    {
        var store = new MemoryStore(); var shell = new FakeShell(store);
        using (var toggle = new EventWaitHandle(false, EventResetMode.AutoReset))
        using (var restore = new EventWaitHandle(false, EventResetMode.AutoReset))
        using (var exit = new EventWaitHandle(false, EventResetMode.AutoReset))
        using (var form = new ButtonForm(new VisibilityController(shell, store), toggle, restore, exit))
        {
            using (var preview = new Bitmap(form.Width * 4, form.Height * 2))
            using (var canvas = Graphics.FromImage(preview))
            {
                canvas.Clear(Color.FromArgb(235, 240, 245));
                Test("WinForms expanded window has a circular hit region", delegate
                {
                    Assert(form.Width == form.Height && form.Region.IsVisible(form.Width / 2, form.Height / 2), "Circle center missing");
                    Assert(!form.Region.IsVisible(1, 1) && !form.Region.IsVisible(form.Width - 1, 1), "Corner remained clickable");
                });
                DrawPreview(form, canvas, 16, 16);
                int fullWidth = form.Width, fullHeight = form.Height;
                form.UpdateDock(100000, false, false);
                Test("WinForms idle transition shrinks actual window and preserves anchor", delegate
                {
                    Assert(form.IsCollapsed && form.Width < fullWidth && form.Height == fullHeight, "Actual form did not shrink");
                    Assert(form.Bounds.Left == form.ExpandedBounds.Left || form.Bounds.Right == form.ExpandedBounds.Right, "Collapsed anchor moved");
                    Assert(form.Region.IsVisible(form.Width / 2, form.Height / 2), "Visible tab cannot receive pointer");
                });
                DrawPreview(form, canvas, fullWidth * 2 + 16, 16);
                form.UpdateDock(100100, true, false);
                Test("WinForms hover restores actual circle without changing desktop state", delegate
                {
                    Assert(!form.IsCollapsed && form.Width == fullWidth && form.Height == fullHeight && !form.Region.IsVisible(1, 1), "Circle did not return");
                    Assert(shell.Writes == 0 && !store.Exists, "Dock hover changed desktop visibility");
                });
                if (previewPath != null) preview.Save(previewPath, ImageFormat.Png);
            }
        }
    }
    private static void DrawPreview(ButtonForm form, Graphics canvas, int x, int y)
    {
        using (Bitmap rendered = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(rendered, form.ClientRectangle);
            for (int row = 0; row < rendered.Height; row++)
                for (int col = 0; col < rendered.Width; col++)
                    if (!form.Region.IsVisible(col, row)) rendered.SetPixel(col, row, Color.Transparent);
            canvas.DrawImageUnscaled(rendered, x, y);
        }
    }
}
