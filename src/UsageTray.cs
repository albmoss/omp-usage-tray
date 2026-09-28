// Taskbar widget for omp provider usage limits.
// Data comes from usage.tsv, written by the omp extension in extension/usage-tray.ts.
// Lifetime: every omp process registers sessions/<pid>; the widget exits once all of them are gone.
// Shows remaining quota as horseshoe gauges: a strip embedded in the taskbar next to the tray, plus a tray icon.
// Builds with the C# 5 compiler bundled in .NET Framework 4.x (see install.cmd).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Linq;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace OmpUsageTray
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct SIZE { public int Cx, Cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr handle);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string className, string windowName);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string windowName);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
        [DllImport("user32.dll")]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr dstDc, IntPtr dstPos, ref SIZE size, IntPtr srcDc,
            ref POINT srcPos, int colorKey, ref BLENDFUNCTION blend, int flags);
        [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenu(IntPtr menu, uint flags, UIntPtr id, string text);
        [DllImport("user32.dll")] public static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr owner, IntPtr options);
        [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr menu);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] public static extern bool SetProcessWorkingSetSize(IntPtr process, IntPtr min, IntPtr max);
        // Undocumented but stable since Windows 10 1903: lets Win32 popup menus follow the dark app theme.
        [DllImport("uxtheme.dll", EntryPoint = "#135")] public static extern int SetPreferredAppMode(int mode);
        [DllImport("uxtheme.dll", EntryPoint = "#133")] public static extern bool AllowDarkModeForWindow(IntPtr hwnd, bool allow);
        [DllImport("uxtheme.dll", EntryPoint = "#136")] public static extern void FlushMenuThemes();
    }

    sealed class LimitRow
    {
        public string Label;
        public string Window;
        public double UsedPct;
        public long ResetsAt;
        public long DurationMs;

        /// <summary>Remaining quota; a window past its reset time counts as fully refilled.</summary>
        public double RemainingPct(long now)
        {
            if (ResetsAt > 0 && ResetsAt <= now) return 100;
            return Math.Max(0, Math.Min(100, 100 - UsedPct));
        }
    }

    sealed class ProviderGroup
    {
        public string Name;
        public string Account;
        public Color Color;
        /// <summary>Monochrome provider: white on dark surfaces, black on light ones.</summary>
        public bool Mono;

        public Color ColorOn(bool lightSurface)
        {
            if (!Mono) return Color;
            return lightSurface ? Color.FromArgb(24, 24, 24) : Color.FromArgb(242, 242, 242);
        }
        public readonly List<LimitRow> Rows = new List<LimitRow>();

        public double MinRemaining(long now)
        {
            double min = 100;
            foreach (LimitRow row in Rows) min = Math.Min(min, row.RemainingPct(now));
            return min;
        }

        /// <summary>The most-constrained limit per window ("5h", "7d"), shortest window first.</summary>
        public List<LimitRow> ByWindow(long now)
        {
            var result = new List<LimitRow>();
            foreach (LimitRow row in Rows)
            {
                int i = result.FindIndex(r => r.Window == row.Window);
                if (i < 0) result.Add(row);
                else if (row.RemainingPct(now) < result[i].RemainingPct(now)) result[i] = row;
            }
            // Stable: rows without a duration (older data files) keep their shortest-first file order.
            return result.OrderBy(r => r.DurationMs).ToList();
        }
    }

    sealed class Snapshot
    {
        public long FetchedAt;
        public readonly List<ProviderGroup> Groups = new List<ProviderGroup>();

        public static long Now()
        {
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }

        static Color ParseColor(string hex)
        {
            int rgb;
            if (hex != null && hex.Length == 7 && hex[0] == '#' &&
                int.TryParse(hex.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb))
                return Color.FromArgb(255, Color.FromArgb(rgb));
            return Color.FromArgb(154, 160, 166);
        }

        /// <summary>Returns null when the file is missing or mid-replace.</summary>
        public static Snapshot Load(string file)
        {
            string text;
            try
            {
                using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (var reader = new StreamReader(stream, Encoding.UTF8))
                    text = reader.ReadToEnd();
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }

            var snap = new Snapshot();
            ProviderGroup current = null;
            foreach (string raw in text.Split('\n'))
            {
                string[] f = raw.TrimEnd('\r').Split('\t');
                switch (f[0])
                {
                    case "fetched":
                        if (f.Length > 1) long.TryParse(f[1], out snap.FetchedAt);
                        break;
                    case "provider":
                        string color = f.Length > 3 ? f[3] : null;
                        current = new ProviderGroup
                        {
                            Name = f.Length > 1 ? f[1] : "?",
                            Account = f.Length > 2 ? f[2] : "",
                            Color = ParseColor(color),
                            Mono = color == "mono",
                        };
                        snap.Groups.Add(current);
                        break;
                    case "limit":
                        if (current == null || f.Length < 5) break;
                        var row = new LimitRow { Label = f[1], Window = f[4] };
                        double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out row.UsedPct);
                        long.TryParse(f[3], out row.ResetsAt);
                        if (f.Length > 5) long.TryParse(f[5], out row.DurationMs);
                        current.Rows.Add(row);
                        break;
                }
            }
            return snap;
        }
    }

    static class Fmt
    {
        public static string Pct(double pct)
        {
            if (pct > 0 && pct < 1) return "<1%";
            if (pct > 99 && pct < 100) return "99%";
            return Math.Round(pct).ToString(CultureInfo.InvariantCulture) + "%";
        }

        public static string Span(long ms)
        {
            if (ms <= 0) return "now";
            long minutes = ms / 60000;
            if (minutes < 1) return "<1m";
            long days = minutes / 1440, hours = minutes / 60 % 24, mins = minutes % 60;
            if (days > 0) return days + "d" + (hours > 0 ? hours + "h" : "");
            if (hours > 0) return hours + "h" + mins + "m";
            return mins + "m";
        }

        public static string Ago(long ms)
        {
            if (ms < 60000) return "just now";
            return Span(ms) + " ago";
        }

        public static bool LightTaskbar()
        {
            object value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0);
            return value is int && (int)value == 1;
        }

        /// <summary>Open ring (270°, gap at the bottom) filled clockwise from the lower left to <paramref name="pct"/>.</summary>
        public static void Horseshoe(Graphics g, RectangleF box, float width, double pct, Color color, Color track)
        {
            RectangleF ring = RectangleF.Inflate(box, -width / 2, -width / 2);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var trackPen = new Pen(track, width) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(trackPen, ring, 135, 270);
            float sweep = (float)(270 * Math.Max(0, Math.Min(100, pct)) / 100);
            if (sweep < 0.5f) return;
            using (var valuePen = new Pen(color, width) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(valuePen, ring, 135, sweep);
        }
    }

    sealed class UsagePopup : Form
    {
        const int CornerPreference = 33, RoundCorners = 2;
        static readonly Color Bg = Color.FromArgb(28, 28, 30);
        static readonly Color Fg = Color.FromArgb(235, 235, 240);
        static readonly Color Dim = Color.FromArgb(150, 150, 158);
        static readonly Color Track = Color.FromArgb(55, 55, 60);
        static readonly Color Edge = Color.FromArgb(60, 60, 66);

        readonly Font headerFont = new Font("Segoe UI Semibold", 9.5f);
        readonly Font bodyFont = new Font("Segoe UI", 9f);
        readonly Font smallFont = new Font("Segoe UI", 8f);
        readonly System.Windows.Forms.Timer clock = new System.Windows.Forms.Timer { Interval = 30000 };
        Snapshot snap = new Snapshot();
        int lastHide;

        public Func<int> SessionCount = () => 0;

        public UsagePopup()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Bg;
            DoubleBuffered = true;
            clock.Tick += (s, e) => Invalidate();
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x80;      // WS_EX_TOOLWINDOW: no Alt+Tab entry
                cp.ClassStyle |= 0x20000; // CS_DROPSHADOW
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int pref = RoundCorners;
            try { Native.DwmSetWindowAttribute(Handle, CornerPreference, ref pref, sizeof(int)); }
            catch (DllNotFoundException) { }
            catch (EntryPointNotFoundException) { }
        }

        int S(float px) { return (int)Math.Round(px * DeviceDpi / 96f); }

        const int Columns = 4;
        int PanelW { get { return S(348); } }
        int PanelGap { get { return S(28); } }
        int providerColumns = 1;

        public void SetSnapshot(Snapshot next)
        {
            snap = next;
            if (Visible) { Relayout(); Invalidate(); }
        }

        public void Toggle()
        {
            if (Visible) { Hide(); return; }
            // A click on the tray icon or strip first deactivates the open popup; don't reopen it immediately.
            if (unchecked(Environment.TickCount - lastHide) < 300) return;
            ShowNearCursor();
        }

        public void ShowNearCursor()
        {
            Relayout();
            Point cursor = Cursor.Position;
            Rectangle wa = Screen.FromPoint(cursor).WorkingArea;
            int margin = S(10);
            int x = Math.Max(wa.Left + margin, Math.Min(cursor.X - Width / 2, wa.Right - Width - margin));
            int y = cursor.Y > wa.Top + wa.Height / 2 ? wa.Bottom - Height - margin : wa.Top + margin;
            Location = new Point(x, y);
            Show();
            Activate();
            clock.Start();
        }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (!Visible) return;
            Hide();
            lastHide = Environment.TickCount;
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) clock.Stop();
        }

        int GaugeHeight { get { return S(46) + S(6) + S(16) + S(15); } }

        int GroupHeight(ProviderGroup group)
        {
            return S(24) + (group.Rows.Count + Columns - 1) / Columns * GaugeHeight + S(12);
        }

        /// <summary>Top-left of each provider panel; each goes into the currently shortest column.</summary>
        List<Point> Place(int columns, out int contentHeight)
        {
            var heights = new int[columns];
            var origins = new List<Point>();
            foreach (ProviderGroup group in snap.Groups)
            {
                int col = 0;
                for (int c = 1; c < columns; c++) if (heights[c] < heights[col]) col = c;
                origins.Add(new Point(col * (PanelW + PanelGap), heights[col]));
                heights[col] += GroupHeight(group);
            }
            contentHeight = snap.Groups.Count == 0 ? S(22) : heights.Max();
            return origins;
        }

        /// <summary>One provider column; a second one once the list would outgrow the screen.</summary>
        void Relayout()
        {
            int pad = S(16), chrome = pad * 2 + S(28), content;
            int maxHeight = Screen.FromPoint(Cursor.Position).WorkingArea.Height - S(20);
            Place(1, out content);
            providerColumns = chrome + content > maxHeight && snap.Groups.Count > 1 ? 2 : 1;
            Place(providerColumns, out content);
            int width = pad * 2 + providerColumns * PanelW + (providerColumns - 1) * PanelGap;
            Size = new Size(width, chrome + content);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            long now = Snapshot.Now();
            int w = ClientSize.Width, pad = S(16);
            const TextFormatFlags left = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            const TextFormatFlags right = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
            const TextFormatFlags center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;

            using (var edge = new Pen(Edge)) g.DrawRectangle(edge, 0, 0, w - 1, ClientSize.Height - 1);

            if (snap.Groups.Count == 0)
                TextRenderer.DrawText(g, "Waiting for data from omp...", bodyFont, new Rectangle(pad, pad, w - 2 * pad, S(22)), Dim, left);

            int contentHeight;
            List<Point> origins = Place(providerColumns, out contentHeight);
            int cellW = PanelW / Columns, ringD = S(46);
            for (int gi = 0; gi < snap.Groups.Count; gi++)
            {
                ProviderGroup group = snap.Groups[gi];
                int x = pad + origins[gi].X, y = pad + origins[gi].Y;
                Color color = group.ColorOn(false);
                TextRenderer.DrawText(g, group.Name, headerFont, new Rectangle(x, y, PanelW / 2, S(22)), color, left);
                TextRenderer.DrawText(g, group.Account, smallFont, new Rectangle(x + PanelW / 2, y, PanelW / 2, S(22)), Dim, right | TextFormatFlags.EndEllipsis);
                y += S(24);
                for (int i = 0; i < group.Rows.Count; i++)
                {
                    LimitRow row = group.Rows[i];
                    int cellX = x + i % Columns * cellW, cellY = y + i / Columns * GaugeHeight;
                    var ring = new Rectangle(cellX + (cellW - ringD) / 2, cellY, ringD, ringD);
                    double pct = row.RemainingPct(now);
                    Fmt.Horseshoe(g, ring, S(4), pct, color, Track);
                    TextRenderer.DrawText(g, Fmt.Pct(pct), bodyFont, new Rectangle(ring.X, ring.Y - S(1), ring.Width, ring.Height), Fg, center);
                    int textY = cellY + ringD + S(6);
                    TextRenderer.DrawText(g, row.Label, smallFont, new Rectangle(cellX, textY, cellW, S(16)), Dim, center);
                    string reset = row.ResetsAt > 0 ? Fmt.Span(row.ResetsAt - now) : "";
                    TextRenderer.DrawText(g, reset, smallFont, new Rectangle(cellX, textY + S(15), cellW, S(15)), Fg, center);
                }
            }

            int footerY = pad + contentHeight;
            using (var edge = new Pen(Edge)) g.DrawLine(edge, pad, footerY, w - pad, footerY);
            footerY += S(6);
            string updated = "remaining quota" + (snap.FetchedAt > 0 ? " · updated " + Fmt.Ago(now - snap.FetchedAt) : "");
            int count = SessionCount();
            string sessions = count == 1 ? "1 omp process" : count + " omp processes";
            TextRenderer.DrawText(g, updated, smallFont, new Rectangle(pad, footerY, w - 2 * pad, S(22)), Dim, left);
            TextRenderer.DrawText(g, sessions, smallFont, new Rectangle(pad, footerY, w - 2 * pad, S(22)), Dim, right);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                clock.Dispose();
                headerFont.Dispose();
                bodyFont.Dispose();
                smallFont.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Per-pixel-alpha child window inside the taskbar (Shell_TrayWnd), left of the tray area.
    /// As a taskbar child it hides with the taskbar in fullscreen apps and never covers other windows.
    /// </summary>
    sealed class TaskbarStrip : NativeWindow
    {
        const int WS_CHILD = 0x40000000, WS_CLIPSIBLINGS = 0x04000000;
        const int WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
        const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        const int WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205, WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
        const int SW_HIDE = 0;

        readonly float scale;
        readonly Font nameFont, rowPctFont, ringPctFont, resetFont;
        Snapshot snap = new Snapshot();
        IntPtr parent = IntPtr.Zero;
        Rectangle bounds = Rectangle.Empty;
        bool shown;

        public event EventHandler LeftClick;
        public event EventHandler RightClick;

        public TaskbarStrip(float dpi)
        {
            scale = dpi / 96f;
            nameFont = new Font("Segoe UI Semibold", S(11), GraphicsUnit.Pixel);
            rowPctFont = new Font("Segoe UI Semibold", S(10), GraphicsUnit.Pixel);
            ringPctFont = new Font("Segoe UI Semibold", S(9), GraphicsUnit.Pixel);
            resetFont = new Font("Segoe UI", S(10), GraphicsUnit.Pixel);
        }

        int S(float px) { return (int)Math.Round(px * scale); }

        const int MaxGaugesPerProvider = 2;
        int ProviderGap { get { return S(14); } }
        int LineHeight { get { return S(13); } }

        // Nested: outer ring = shortest window (5h), inner ring = next one (7d); text column beside it.
        int NestedRing { get { return S(34); } }
        int NestedTextX { get { return NestedRing + S(7); } }
        int NestedTextW { get { return S(80); } }
        int NestedPctW { get { return S(33); } }

        // Minimal: one small ring per window under the provider name.
        int SideRing { get { return S(24); } }
        int SideCell { get { return S(36); } }

        /// <summary>Layout the user picked; persisted by the tray context. Minimal is the default.</summary>
        public bool PreferNested { get; private set; }
        // What actually fits right now: Sync falls back to Minimal and then drops providers.
        bool nested;
        List<ProviderGroup> visible = new List<ProviderGroup>();
        string layoutKey = "";
        // Taskbar geometry the current fit was computed for; the slow timer skips all work while it holds.
        Native.RECT lastTaskbar, lastTray, lastStart, lastRebar;

        public void SetLayout(bool preferNested)
        {
            PreferNested = preferNested;
            Sync(true);
        }

        int ProviderWidth(ProviderGroup group, bool nestedLayout, long now)
        {
            if (nestedLayout) return NestedTextX + NestedTextW;
            int n = Math.Min(MaxGaugesPerProvider, group.ByWindow(now).Count);
            return Math.Max(S(64), n * SideCell + Math.Max(0, n - 1) * S(2));
        }

        int WidthFor(List<ProviderGroup> groups, bool nestedLayout, long now)
        {
            int w = S(4) * 2 + Math.Max(0, groups.Count - 1) * ProviderGap;
            foreach (ProviderGroup group in groups) w += ProviderWidth(group, nestedLayout, now);
            return w;
        }

        public void SetSnapshot(Snapshot next)
        {
            snap = next;
            Sync(true);
        }

        /// <summary>
        /// Attach to the current taskbar and follow its layout. The slow timer passes refit=false and costs a few
        /// window-rect reads while the taskbar is unchanged; refit=true re-fits and redraws (new data, clock tick).
        /// </summary>
        public void Sync(bool refit)
        {
            IntPtr taskbar = Native.FindWindow("Shell_TrayWnd", null);
            if (Handle != IntPtr.Zero && taskbar != parent) DestroyHandle();
            if (taskbar == IntPtr.Zero) return;
            if (Handle == IntPtr.Zero && !Create(taskbar)) return;

            Native.RECT tb, tray, startRect = new Native.RECT(), rebarRect = new Native.RECT();
            IntPtr notify = Native.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null);
            if (snap.Groups.Count == 0 || !Native.GetWindowRect(taskbar, out tb) || notify == IntPtr.Zero ||
                !Native.GetWindowRect(notify, out tray) || tb.Right - tb.Left < tb.Bottom - tb.Top)
            {
                Hide();
                return;
            }
            // Windows 11 draws app buttons in XAML; ReBarWindow32 keeps a stale rect. The "Start" window does
            // track the real button group: centered, the group is symmetric, so its right edge mirrors Start's left.
            IntPtr start = Native.FindWindowEx(taskbar, IntPtr.Zero, "Start", null);
            IntPtr rebar = Native.FindWindowEx(taskbar, IntPtr.Zero, "ReBarWindow32", null);
            bool hasStart = start != IntPtr.Zero && Native.GetWindowRect(start, out startRect);
            bool hasRebar = rebar != IntPtr.Zero && Native.GetWindowRect(rebar, out rebarRect);
            if (!refit && shown && Same(tb, lastTaskbar) && Same(tray, lastTray) && Same(startRect, lastStart) && Same(rebarRect, lastRebar))
                return;
            lastTaskbar = tb;
            lastTray = tray;
            lastStart = startRect;
            lastRebar = rebarRect;

            int h = tb.Bottom - tb.Top;
            int right = tray.Left - tb.Left - S(16), left = 0;
            if (hasStart && CenteredTaskbar()) left = tb.Right - startRect.Left + S(8);
            else if (hasRebar) left = rebarRect.Right - tb.Left + S(8);

            // Fit into the free space: preferred layout, then Minimal, then drop the providers with the most quota left.
            long now = Snapshot.Now();
            var groups = new List<ProviderGroup>(snap.Groups);
            bool useNested = PreferNested;
            int w = WidthFor(groups, useNested, now);
            while (groups.Count > 0 && w > right - left)
            {
                if (useNested) useNested = false;
                else
                {
                    int roomiest = 0;
                    for (int i = 1; i < groups.Count; i++)
                        if (groups[i].MinRemaining(now) > groups[roomiest].MinRemaining(now)) roomiest = i;
                    groups.RemoveAt(roomiest);
                }
                w = WidthFor(groups, useNested, now);
            }
            if (groups.Count == 0)
            {
                Hide();
                return;
            }

            var key = new StringBuilder(useNested ? "nested" : "minimal");
            foreach (ProviderGroup group in groups) key.Append('|').Append(group.Name);
            bool layoutChanged = key.ToString() != layoutKey;
            nested = useNested;
            visible = groups;
            layoutKey = key.ToString();

            var next = new Rectangle(right - w, 0, w, h);
            if (next == bounds && shown && !layoutChanged)
            {
                if (refit) Render();
                return;
            }
            bool resized = next.Size != bounds.Size;
            Native.SetWindowPos(Handle, IntPtr.Zero, next.X, 0, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            bounds = next;
            shown = true;
            if (refit || resized || layoutChanged) Render();
        }

        static bool Same(Native.RECT a, Native.RECT b)
        {
            return a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;
        }

        /// <summary>Windows 11 taskbar alignment; missing value means the default, centered.</summary>
        static bool CenteredTaskbar()
        {
            object value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAl", 1);
            return !(value is int) || (int)value != 0;
        }

        bool Create(IntPtr taskbar)
        {
            var cp = new CreateParams
            {
                Caption = "omp-usage-strip",
                Parent = taskbar,
                Style = WS_CHILD | WS_CLIPSIBLINGS,
                ExStyle = WS_EX_LAYERED | WS_EX_NOACTIVATE,
                Width = 1,
                Height = 1,
            };
            try { CreateHandle(cp); }
            catch (System.ComponentModel.Win32Exception) { return false; }
            parent = taskbar;
            bounds = Rectangle.Empty;
            shown = false;
            return true;
        }

        void Hide()
        {
            if (Handle != IntPtr.Zero && shown) Native.ShowWindow(Handle, SW_HIDE);
            shown = false;
        }

        public void Render()
        {
            if (Handle == IntPtr.Zero || bounds.IsEmpty) return;
            long now = Snapshot.Now();
            bool light = Fmt.LightTaskbar();
            Color text = light ? Color.FromArgb(235, 0, 0, 0) : Color.FromArgb(245, 255, 255, 255);
            Color dim = light ? Color.FromArgb(150, 0, 0, 0) : Color.FromArgb(170, 255, 255, 255);
            Color trackColor = light ? Color.FromArgb(40, 0, 0, 0) : Color.FromArgb(50, 255, 255, 255);

            using (var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                using (var textBrush = new SolidBrush(text))
                using (var dimBrush = new SolidBrush(dim))
                using (var leftFmt = new StringFormat(StringFormatFlags.NoWrap) { LineAlignment = StringAlignment.Center })
                using (var centerFmt = new StringFormat(StringFormatFlags.NoWrap) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                {
                    // Alpha 1 is invisible but keeps the whole strip clickable.
                    g.Clear(Color.FromArgb(1, 0, 0, 0));
                    g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                    int x = S(4), h = bounds.Height, lineH = LineHeight;
                    foreach (ProviderGroup group in visible)
                    {
                        Color color = group.ColorOn(light);
                        List<LimitRow> windows = group.ByWindow(now);
                        int count = Math.Min(MaxGaugesPerProvider, windows.Count);
                        using (var colorBrush = new SolidBrush(color))
                        {
                            if (nested)
                            {
                                float stroke = 3 * scale;
                                var ring = new RectangleF(x, (h - NestedRing) / 2f, NestedRing, NestedRing);
                                for (int i = 0; i < count; i++)
                                {
                                    Fmt.Horseshoe(g, ring, stroke, windows[i].RemainingPct(now), color, trackColor);
                                    ring = RectangleF.Inflate(ring, -(stroke + 2.5f * scale), -(stroke + 2.5f * scale));
                                }
                                // Line order matches ring order: outer ring first.
                                int tx = x + NestedTextX, ty = (h - lineH * 3) / 2;
                                g.DrawString(group.Name, nameFont, colorBrush, new RectangleF(tx, ty, NestedTextW, lineH), leftFmt);
                                for (int i = 0; i < count; i++)
                                {
                                    LimitRow row = windows[i];
                                    int y = ty + lineH * (i + 1);
                                    g.DrawString(Fmt.Pct(row.RemainingPct(now)), rowPctFont, textBrush, new RectangleF(tx, y, NestedPctW, lineH), leftFmt);
                                    string reset = row.ResetsAt > 0 ? Fmt.Span(row.ResetsAt - now) : row.Window;
                                    g.DrawString(reset, resetFont, dimBrush, new RectangleF(tx + NestedPctW, y, NestedTextW - NestedPctW, lineH), leftFmt);
                                }
                            }
                            else
                            {
                                int ringY = S(1) + lineH;
                                g.DrawString(group.Name, nameFont, colorBrush, new RectangleF(x, S(1), ProviderWidth(group, false, now), lineH), leftFmt);
                                for (int i = 0; i < count; i++)
                                {
                                    LimitRow row = windows[i];
                                    int cellX = x + i * (SideCell + S(2));
                                    double pct = row.RemainingPct(now);
                                    var ring = new RectangleF(cellX + (SideCell - SideRing) / 2f, ringY, SideRing, SideRing);
                                    Fmt.Horseshoe(g, ring, 2.5f * scale, pct, color, trackColor);
                                    g.DrawString(Fmt.Pct(pct).TrimEnd('%'), ringPctFont, textBrush, ring, centerFmt);
                                    string reset = row.ResetsAt > 0 ? Fmt.Span(row.ResetsAt - now) : row.Window;
                                    g.DrawString(reset, resetFont, dimBrush, new RectangleF(cellX - S(4), ringY + SideRing - S(3), SideCell + S(8), S(12)), centerFmt);
                                }
                            }
                        }
                        x += ProviderWidth(group, nested, now) + ProviderGap;
                    }
                }
                Present(bmp);
            }
        }

        void Present(Bitmap bmp)
        {
            IntPtr screenDc = Native.GetDC(IntPtr.Zero);
            IntPtr memDc = Native.CreateCompatibleDC(screenDc);
            IntPtr hBitmap = bmp.GetHbitmap(Color.FromArgb(0)); // premultiplied 32bpp DIB
            IntPtr old = Native.SelectObject(memDc, hBitmap);
            try
            {
                var size = new Native.SIZE { Cx = bmp.Width, Cy = bmp.Height };
                var src = new Native.POINT();
                var blend = new Native.BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                Native.UpdateLayeredWindow(Handle, screenDc, IntPtr.Zero, ref size, memDc, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
            }
            finally
            {
                Native.SelectObject(memDc, old);
                Native.DeleteObject(hBitmap);
                Native.DeleteDC(memDc);
                Native.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        protected override void WndProc(ref Message m)
        {
            switch (m.Msg)
            {
                case WM_MOUSEACTIVATE:
                    m.Result = (IntPtr)MA_NOACTIVATE;
                    return;
                case WM_LBUTTONUP:
                    if (LeftClick != null) LeftClick(this, EventArgs.Empty);
                    break;
                case WM_RBUTTONUP:
                    if (RightClick != null) RightClick(this, EventArgs.Empty);
                    break;
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) DestroyHandle();
            nameFont.Dispose();
            rowPctFont.Dispose();
            ringPctFont.Dispose();
            resetFont.Dispose();
        }
    }

    sealed class TrayContext : ApplicationContext
    {
        static readonly DateTime Epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        readonly string dataFile, sessionsDir;
        readonly NotifyIcon tray = new NotifyIcon();
        readonly UsagePopup popup = new UsagePopup();
        readonly TaskbarStrip strip;
        // Hidden tool window that owns the native context menu; never shown, so no taskbar button.
        readonly NativeWindow menuOwner = new NativeWindow();
        readonly string layoutFile;
        readonly FileSystemWatcher watcher;
        readonly System.Windows.Forms.Timer reloadDebounce = new System.Windows.Forms.Timer { Interval = 250 };
        readonly System.Windows.Forms.Timer idleExit = new System.Windows.Forms.Timer { Interval = 5000 };
        readonly System.Windows.Forms.Timer layout = new System.Windows.Forms.Timer { Interval = 2000 };
        // Countdowns and past-reset windows change with time alone; redraw once a minute, on the minute.
        readonly System.Windows.Forms.Timer minute = new System.Windows.Forms.Timer();
        readonly System.Windows.Forms.Timer trim = new System.Windows.Forms.Timer { Interval = 1500 };
        readonly Dictionary<int, Process> sessions = new Dictionary<int, Process>();
        IntPtr iconHandle = IntPtr.Zero;
        Snapshot snap = new Snapshot();
        string iconKey;

        public bool UserQuit { get; private set; }

        public TrayContext(string root)
        {
            dataFile = Path.Combine(root, "usage.tsv");
            sessionsDir = Path.Combine(root, "sessions");
            Directory.CreateDirectory(sessionsDir);

            IntPtr forceHandle = popup.Handle; // watchers and process events marshal through it
            popup.SessionCount = () => sessions.Count;

            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) strip = new TaskbarStrip(g.DpiX);
            layoutFile = Path.Combine(root, "layout.txt");
            bool preferNested = false;
            try { preferNested = File.ReadAllText(layoutFile).Trim() == "nested"; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            strip.SetLayout(preferNested);

            menuOwner.CreateHandle(new CreateParams { Caption = "omp-usage-menu", ExStyle = 0x80 /* WS_EX_TOOLWINDOW */ });
            try
            {
                Native.SetPreferredAppMode(1); // AllowDark: menus follow the Windows app theme
                Native.AllowDarkModeForWindow(menuOwner.Handle, true);
                Native.FlushMenuThemes();
            }
            catch (EntryPointNotFoundException) { }

            tray.MouseClick += (s, e) =>
            {
                if (e.Button == MouseButtons.Left) popup.Toggle();
                else if (e.Button == MouseButtons.Right) ShowMenu();
            };
            strip.LeftClick += (s, e) => popup.Toggle();
            strip.RightClick += (s, e) => ShowMenu();
            // The taskbar re-lays out when tray icons change or explorer restarts; follow it cheaply.
            layout.Tick += (s, e) => strip.Sync(false);
            minute.Tick += (s, e) =>
            {
                ArmMinute();
                strip.Sync(true);
                UpdateIcon();
                ScheduleTrim();
            };
            trim.Tick += (s, e) => { trim.Stop(); TrimMemory(); };
            popup.VisibleChanged += (s, e) => { if (!popup.Visible) ScheduleTrim(); };

            reloadDebounce.Tick += (s, e) => { reloadDebounce.Stop(); Reload(); };
            idleExit.Tick += (s, e) => { idleExit.Stop(); if (sessions.Count == 0) ExitThread(); };

            // One watcher for usage.tsv and sessions/<pid>.
            watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                SynchronizingObject = popup,
            };
            string sessionPrefix = "sessions" + Path.DirectorySeparatorChar;
            FileSystemEventHandler changed = (s, e) =>
            {
                if (string.Equals(e.Name, "usage.tsv", StringComparison.OrdinalIgnoreCase))
                {
                    reloadDebounce.Stop();
                    reloadDebounce.Start();
                }
                else if (e.ChangeType == WatcherChangeTypes.Created && e.Name.StartsWith(sessionPrefix, StringComparison.OrdinalIgnoreCase))
                    Register(e.FullPath);
            };
            watcher.Changed += changed;
            watcher.Created += changed;
            watcher.Renamed += (s, e) => changed(s, e);
            watcher.EnableRaisingEvents = true;

            foreach (string file in Directory.GetFiles(sessionsDir)) Register(file);

            SystemEvents.UserPreferenceChanged += OnPreferenceChanged;
            Reload();
            tray.Visible = true;
            layout.Start();
            ArmMinute();
            ScheduleTrim();
            if (sessions.Count == 0) idleExit.Start();
        }

        void ArmMinute()
        {
            minute.Stop();
            minute.Interval = (int)(60000 - Snapshot.Now() % 60000) + 50;
            minute.Start();
        }

        void ScheduleTrim()
        {
            trim.Stop();
            trim.Start();
        }

        /// <summary>
        /// The widget idles between redraws; hand its pages back to Windows instead of holding the
        /// startup/JIT peak. The next redraw faults back in only what it touches.
        /// </summary>
        void TrimMemory()
        {
            if (popup.Visible) return;
            GC.Collect();
            GC.WaitForPendingFinalizers();
            Native.SetProcessWorkingSetSize(Native.GetCurrentProcess(), (IntPtr)(-1), (IntPtr)(-1));
        }

        void ShowMenu()
        {
            const uint MF_STRING = 0, MF_CHECKED = 0x8, MF_SEPARATOR = 0x800;
            const uint TPM_RIGHTBUTTON = 0x2, TPM_BOTTOMALIGN = 0x20, TPM_RETURNCMD = 0x100;
            const int CmdDetails = 1, CmdMinimal = 2, CmdNested = 3, CmdQuit = 4;
            IntPtr menu = Native.CreatePopupMenu();
            Native.AppendMenu(menu, MF_STRING, (UIntPtr)CmdDetails, "Details");
            Native.AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            Native.AppendMenu(menu, MF_STRING | (strip.PreferNested ? 0 : MF_CHECKED), (UIntPtr)CmdMinimal, "Minimal");
            Native.AppendMenu(menu, MF_STRING | (strip.PreferNested ? MF_CHECKED : 0), (UIntPtr)CmdNested, "Nested");
            Native.AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            Native.AppendMenu(menu, MF_STRING, (UIntPtr)CmdQuit, "Quit");
            Point at = Cursor.Position;
            // A popup menu closes on outside clicks only while its owner is the foreground window;
            // WM_NULL afterwards is the documented companion so the next open works too.
            Native.SetForegroundWindow(menuOwner.Handle);
            int cmd = Native.TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_BOTTOMALIGN | TPM_RETURNCMD, at.X, at.Y, menuOwner.Handle, IntPtr.Zero);
            Native.PostMessage(menuOwner.Handle, 0, IntPtr.Zero, IntPtr.Zero);
            Native.DestroyMenu(menu);
            switch (cmd)
            {
                case CmdDetails:
                    popup.ShowNearCursor();
                    break;
                case CmdMinimal:
                case CmdNested:
                    strip.SetLayout(cmd == CmdNested);
                    try { File.WriteAllText(layoutFile, cmd == CmdNested ? "nested" : "minimal"); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                    break;
                case CmdQuit:
                    UserQuit = true;
                    ExitThread();
                    return;
            }
            ScheduleTrim();
        }

        void OnPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            UpdateIcon();
            strip.Render();
        }

        void Register(string path)
        {
            int pid;
            if (!int.TryParse(Path.GetFileName(path), out pid) || sessions.ContainsKey(pid)) return;

            long startedAt = 0;
            try { long.TryParse(File.ReadAllText(path).Trim(), out startedAt); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            Process process;
            try { process = Process.GetProcessById(pid); }
            catch (ArgumentException) { TryDelete(path); return; }

            // A stale file from a crashed omp can name a reused PID; the start time tells them apart.
            if (startedAt > 0)
            {
                try
                {
                    long actual = (long)(process.StartTime.ToUniversalTime() - Epoch).TotalMilliseconds;
                    if (Math.Abs(actual - startedAt) > 10000) { process.Dispose(); TryDelete(path); return; }
                }
                catch (InvalidOperationException) { process.Dispose(); TryDelete(path); return; }
                catch (System.ComponentModel.Win32Exception) { }
            }

            process.SynchronizingObject = popup;
            process.Exited += (s, e) =>
            {
                sessions.Remove(pid);
                process.Dispose();
                TryDelete(path);
                popup.Invalidate();
                if (sessions.Count == 0) idleExit.Start();
            };
            sessions[pid] = process;
            idleExit.Stop();
            try { process.EnableRaisingEvents = true; }
            catch (InvalidOperationException) { sessions.Remove(pid); process.Dispose(); TryDelete(path); }
            catch (System.ComponentModel.Win32Exception) { }
        }

        static void TryDelete(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>True when an omp process registered after this instance stopped watching.</summary>
        public bool HasLiveSessionsOnDisk()
        {
            foreach (string file in Directory.GetFiles(sessionsDir))
            {
                int pid;
                if (!int.TryParse(Path.GetFileName(file), out pid)) continue;
                try
                {
                    using (Process p = Process.GetProcessById(pid))
                        if (!p.HasExited) return true;
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { return true; }
            }
            return false;
        }

        void Reload()
        {
            Snapshot next = Snapshot.Load(dataFile);
            if (next == null)
            {
                if (File.Exists(dataFile)) reloadDebounce.Start(); // locked mid-replace; retry shortly
                else UpdateIcon();
                return;
            }
            snap = next;
            popup.SetSnapshot(next);
            strip.SetSnapshot(next);
            UpdateIcon();
            ScheduleTrim();
        }

        /// <summary>One horseshoe for the most-constrained window across providers, in that provider's color.</summary>
        void UpdateIcon()
        {
            long now = Snapshot.Now();
            int size = SystemInformation.SmallIconSize.Width;
            bool light = Fmt.LightTaskbar();
            Color trackColor = light ? Color.FromArgb(70, 0, 0, 0) : Color.FromArgb(80, 255, 255, 255);
            ProviderGroup tightestGroup = null;
            double tightest = 101;
            foreach (ProviderGroup group in snap.Groups)
            {
                double pct = group.MinRemaining(now);
                if (pct < tightest) { tightest = pct; tightestGroup = group; }
            }

            var tip = new StringBuilder();
            foreach (ProviderGroup group in snap.Groups)
            {
                if (tip.Length > 0) tip.Append(" | ");
                tip.Append(group.Name).Append(' ').Append(Fmt.Pct(group.MinRemaining(now)));
            }
            string text = tip.Length > 0 ? tip.Append(" left").ToString() : "omp usage: waiting for data";
            if (text.Length > 63) text = text.Substring(0, 63); // NotifyIcon rejects longer text
            // Most refreshes change nothing the icon shows; skip the bitmap and HICON churn then.
            string key = text + "\0" + light + "\0" + size + "\0" + (tightestGroup != null ? tightestGroup.Name + "\0" + Math.Round(tightest) : "");
            if (key == iconKey) return;
            iconKey = key;

            IntPtr handle;
            using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.Transparent);
                    float stroke = Math.Max(2f, size / 6f);
                    Color color = tightestGroup != null ? tightestGroup.ColorOn(light) : trackColor;
                    Fmt.Horseshoe(g, new RectangleF(0.5f, 0.5f, size - 1, size - 1), stroke, tightestGroup != null ? tightest : 0, color, trackColor);
                }
                handle = bmp.GetHicon();
            }

            tray.Icon = Icon.FromHandle(handle);
            if (iconHandle != IntPtr.Zero) Native.DestroyIcon(iconHandle);
            iconHandle = handle;
            tray.Text = text;
        }

        protected override void ExitThreadCore()
        {
            SystemEvents.UserPreferenceChanged -= OnPreferenceChanged;
            layout.Stop();
            minute.Stop();
            trim.Stop();
            watcher.Dispose();
            foreach (Process p in sessions.Values) p.Dispose();
            sessions.Clear();
            strip.Dispose();
            tray.Visible = false;
            tray.Dispose();
            menuOwner.DestroyHandle();
            popup.Dispose();
            if (iconHandle != IntPtr.Zero) { Native.DestroyIcon(iconHandle); iconHandle = IntPtr.Zero; }
            base.ExitThreadCore();
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            string root = Path.GetDirectoryName(Application.ExecutablePath);
            // Launched from omp's project directory; don't keep it locked while the widget runs.
            Directory.SetCurrentDirectory(root);
            bool relaunch;
            bool created;
            using (var mutex = new Mutex(true, @"Local\omp-usage-tray", out created))
            {
                if (!created) return;
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var context = new TrayContext(root);
                Application.Run(context);
                mutex.ReleaseMutex();
                // An omp that started during shutdown saw the mutex held and skipped its launch.
                relaunch = !context.UserQuit && context.HasLiveSessionsOnDisk();
            }
            if (relaunch) Process.Start(Application.ExecutablePath);
        }
    }
}
