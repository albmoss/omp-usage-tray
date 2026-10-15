// Taskbar widget for omp provider usage limits.
// Data comes from usage.tsv, written by the omp extension in extension/usage-tray.ts.
// Lifetime: every omp process registers sessions/<pid>; the widget exits once all of them are gone.
// Shows remaining quota as bracket gauges (left arc 5h, right arc 7d) around each provider's mark: a strip
// embedded in the taskbar next to the tray, plus a tray icon.
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

        public static Font Pick(string family, string fallback, float size, GraphicsUnit unit)
        {
            var font = new Font(family, size, unit);
            if (font.Name == family) return font;
            font.Dispose();
            return new Font(fallback, size, unit);
        }

        public static bool IsShort(LimitRow row)
        {
            if (row.DurationMs > 0) return row.DurationMs < 86400000L;
            return !row.Window.EndsWith("d", StringComparison.Ordinal);
        }

        public static void Sides(ProviderGroup group, long now, out LimitRow shortRow, out LimitRow longRow)
        {
            shortRow = null;
            longRow = null;
            foreach (LimitRow row in group.ByWindow(now))
            {
                if (IsShort(row)) { if (shortRow == null) shortRow = row; }
                else if (longRow == null) longRow = row;
            }
        }

        public static string Countdown(LimitRow row, long now)
        {
            return row.ResetsAt > 0 ? Span(row.ResetsAt - now) : row.Window;
        }

        public static bool ResetParts(LimitRow row, long now, out string day, out string time, out string suffix)
        {
            day = time = suffix = "";
            if (row.ResetsAt <= now) return false;
            DateTime at = DateTimeOffset.FromUnixTimeMilliseconds(row.ResetsAt).LocalDateTime;
            DateTime today = DateTimeOffset.FromUnixTimeMilliseconds(now).LocalDateTime.Date;
            string pattern = CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern;
            bool designator = pattern.Contains("t");
            time = at.ToString(designator ? pattern.Replace("t", "").Trim() : pattern, CultureInfo.CurrentCulture);
            suffix = designator ? at.ToString("tt", CultureInfo.CurrentCulture) : "";
            if (at.Date != today) day = at.ToString("ddd", CultureInfo.InvariantCulture);
            return true;
        }

        public static string ResetsAt(LimitRow row, long now)
        {
            if (row.ResetsAt <= 0) return "";
            string day, time, suffix;
            if (!ResetParts(row, now, out day, out time, out suffix)) return "refilled";
            return (day.Length > 0 ? day + " " : "") + time + (suffix.Length > 0 ? " " + suffix : "");
        }
    }

    static class Gauge
    {
        const float Gap = 16f, Span = 180f - 2 * Gap;

        public static double Remaining(LimitRow row, long now)
        {
            return row == null ? double.NaN : row.RemainingPct(now);
        }

        public static void Brackets(Graphics g, RectangleF box, float stroke, double left, double right, Color color, Color track)
        {
            RectangleF ring = RectangleF.Inflate(box, -stroke / 2, -stroke / 2);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var trackPen = new Pen(track, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var valuePen = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                if (!double.IsNaN(left)) Side(g, ring, trackPen, valuePen, 90 + Gap, Span, left);
                if (!double.IsNaN(right)) Side(g, ring, trackPen, valuePen, 90 - Gap, -Span, right);
            }
        }

        static void Side(Graphics g, RectangleF ring, Pen track, Pen value, float start, float span, double pct)
        {
            g.DrawArc(track, ring, start, span);
            float sweep = (float)(span * Math.Max(0, Math.Min(100, pct)) / 100);
            if (Math.Abs(sweep) >= 0.5f) g.DrawArc(value, ring, start, sweep);
        }
    }

    static class Brand
    {
        sealed class Mark
        {
            public RectangleF ViewBox;
            public GraphicsPath Path;
        }

        static readonly Dictionary<string, string[]> Sources = new Dictionary<string, string[]>
        {
            { "Claude", new[] { "0 0 24 24", "m4.7144 15.9555 4.7174-2.6471.079-.2307-.079-.1275h-.2307l-.7893-.0486-2.6956-.0729-2.3375-.0971-2.2646-.1214-.5707-.1215-.5343-.7042.0546-.3522.4797-.3218.686.0608 1.5179.1032 2.2767.1578 1.6514.0972 2.4468.255h.3886l.0546-.1579-.1336-.0971-.1032-.0972L6.973 9.8356l-2.55-1.6879-1.3356-.9714-.7225-.4918-.3643-.4614-.1578-1.0078.6557-.7225.8803.0607.2246.0607.8925.686 1.9064 1.4754 2.4893 1.8336.3643.3035.1457-.1032.0182-.0728-.164-.2733-1.3539-2.4467-1.445-2.4893-.6435-1.032-.17-.6194c-.0607-.255-.1032-.4674-.1032-.7285L6.287.1335 6.6997 0l.9957.1336.419.3642.6192 1.4147 1.0018 2.2282 1.5543 3.0296.4553.8985.2429.8318.091.255h.1579v-.1457l.1275-1.706.2368-2.0947.2307-2.6957.0789-.7589.3764-.9107.7468-.4918.5828.2793.4797.686-.0668.4433-.2853 1.8517-.5586 2.9021-.3643 1.9429h.2125l.2429-.2429.9835-1.3053 1.6514-2.0643.7286-.8196.85-.9046.5464-.4311h1.0321l.759 1.1293-.34 1.1657-1.0625 1.3478-.8804 1.1414-1.2628 1.7-.7893 1.36.0729.1093.1882-.0183 2.8535-.607 1.5421-.2794 1.8396-.3157.8318.3886.091.3946-.3278.8075-1.967.4857-2.3072.4614-3.4364.8136-.0425.0304.0486.0607 1.5482.1457.6618.0364h1.621l3.0175.2247.7892.522.4736.6376-.079.4857-1.2142.6193-1.6393-.3886-3.825-.9107-1.3113-.3279h-.1822v.1093l1.0929 1.0686 2.0035 1.8092 2.5075 2.3314.1275.5768-.3218.4554-.34-.0486-2.2039-1.6575-.85-.7468-1.9246-1.621h-.1275v.17l.4432.6496 2.3436 3.5214.1214 1.0807-.17.3521-.6071.2125-.6679-.1214-1.3721-1.9246L14.38 17.959l-1.1414-1.9428-.1397.079-.674 7.2552-.3156.3703-.7286.2793-.6071-.4614-.3218-.7468.3218-1.4753.3886-1.9246.3157-1.53.2853-1.9004.17-.6314-.0121-.0425-.1397.0182-1.4328 1.9672-2.1796 2.9446-1.7243 1.8456-.4128.164-.7164-.3704.0667-.6618.4008-.5889 2.386-3.0357 1.4389-1.882.929-1.0868-.0062-.1579h-.0546l-6.3385 4.1164-1.1293.1457-.4857-.4554.0608-.7467.2307-.2429 1.9064-1.3114Z" } },
            { "Codex", new[] { "0 0 24 24", "M22.2819 9.8211a5.9847 5.9847 0 0 0-.5157-4.9108 6.0462 6.0462 0 0 0-6.5098-2.9A6.0651 6.0651 0 0 0 4.9807 4.1818a5.9847 5.9847 0 0 0-3.9977 2.9 6.0462 6.0462 0 0 0 .7427 7.0966 5.98 5.98 0 0 0 .511 4.9107 6.051 6.051 0 0 0 6.5146 2.9001A5.9847 5.9847 0 0 0 13.2599 24a6.0557 6.0557 0 0 0 5.7718-4.2058 5.9894 5.9894 0 0 0 3.9977-2.9001 6.0557 6.0557 0 0 0-.7475-7.0729zm-9.022 12.6081a4.4755 4.4755 0 0 1-2.8764-1.0408l.1419-.0804 4.7783-2.7582a.7948.7948 0 0 0 .3927-.6813v-6.7369l2.02 1.1686a.071.071 0 0 1 .038.052v5.5826a4.504 4.504 0 0 1-4.4945 4.4944zm-9.6607-4.1254a4.4708 4.4708 0 0 1-.5346-3.0137l.142.0852 4.783 2.7582a.7712.7712 0 0 0 .7806 0l5.8428-3.3685v2.3324a.0804.0804 0 0 1-.0332.0615L9.74 19.9502a4.4992 4.4992 0 0 1-6.1408-1.6464zM2.3408 7.8956a4.485 4.485 0 0 1 2.3655-1.9728V11.6a.7664.7664 0 0 0 .3879.6765l5.8144 3.3543-2.0201 1.1685a.0757.0757 0 0 1-.071 0l-4.8303-2.7865A4.504 4.504 0 0 1 2.3408 7.872zm16.5963 3.8558L13.1038 8.364 15.1192 7.2a.0757.0757 0 0 1 .071 0l4.8303 2.7913a4.4944 4.4944 0 0 1-.6765 8.1042v-5.6772a.79.79 0 0 0-.407-.667zm2.0107-3.0231l-.142-.0852-4.7735-2.7818a.7759.7759 0 0 0-.7854 0L9.409 9.2297V6.8974a.0662.0662 0 0 1 .0284-.0615l4.8303-2.7866a4.4992 4.4992 0 0 1 6.6802 4.66zM8.3065 12.863l-2.02-1.1638a.0804.0804 0 0 1-.038-.0567V6.0742a4.4992 4.4992 0 0 1 7.3757-3.4537l-.142.0805L8.704 5.459a.7948.7948 0 0 0-.3927.6813zm1.0976-2.3654l2.602-1.4998 2.6069 1.4998v2.9994l-2.5974 1.4997-2.6067-1.4997Z" } },
            { "Antigravity", new[] { "8.5 10.5 94 94", "M89.6992 93.695C94.3659 97.195 101.366 94.8617 94.9492 88.445C75.6992 69.7783 79.7825 18.445 55.8659 18.445C31.9492 18.445 36.0325 69.7783 16.7825 88.445C9.78251 95.445 17.3658 97.195 22.0325 93.695C40.1159 81.445 38.9492 59.8617 55.8659 59.8617C72.7825 59.8617 71.6159 81.445 89.6992 93.695Z" } },
            { "Gemini CLI", new[] { "0 0 24 24", "M11.04 19.32Q12 21.51 12 24q0-2.49.93-4.68.96-2.19 2.58-3.81t3.81-2.55Q21.51 12 24 12q-2.49 0-4.68-.93a12.3 12.3 0 0 1-3.81-2.58 12.3 12.3 0 0 1-2.58-3.81Q12 2.49 12 0q0 2.49-.96 4.68-.93 2.19-2.55 3.81a12.3 12.3 0 0 1-3.81 2.58Q2.49 12 0 12q2.49 0 4.68.96 2.19.93 3.81 2.55t2.55 3.81" } },
            { "Copilot", new[] { "0 0 24 24", "M23.922 16.997C23.061 18.492 18.063 22.02 12 22.02 5.937 22.02.939 18.492.078 16.997A.641.641 0 0 1 0 16.741v-2.869a.883.883 0 0 1 .053-.22c.372-.935 1.347-2.292 2.605-2.656.167-.429.414-1.055.644-1.517a10.098 10.098 0 0 1-.052-1.086c0-1.331.282-2.499 1.132-3.368.397-.406.89-.717 1.474-.952C7.255 2.937 9.248 1.98 11.978 1.98c2.731 0 4.767.957 6.166 2.093.584.235 1.077.546 1.474.952.85.869 1.132 2.037 1.132 3.368 0 .368-.014.733-.052 1.086.23.462.477 1.088.644 1.517 1.258.364 2.233 1.721 2.605 2.656a.841.841 0 0 1 .053.22v2.869a.641.641 0 0 1-.078.256Zm-11.75-5.992h-.344a4.359 4.359 0 0 1-.355.508c-.77.947-1.918 1.492-3.508 1.492-1.725 0-2.989-.359-3.782-1.259a2.137 2.137 0 0 1-.085-.104L4 11.746v6.585c1.435.779 4.514 2.179 8 2.179 3.486 0 6.565-1.4 8-2.179v-6.585l-.098-.104s-.033.045-.085.104c-.793.9-2.057 1.259-3.782 1.259-1.59 0-2.738-.545-3.508-1.492a4.359 4.359 0 0 1-.355-.508Zm2.328 3.25c.549 0 1 .451 1 1v2c0 .549-.451 1-1 1-.549 0-1-.451-1-1v-2c0-.549.451-1 1-1Zm-5 0c.549 0 1 .451 1 1v2c0 .549-.451 1-1 1-.549 0-1-.451-1-1v-2c0-.549.451-1 1-1Zm3.313-6.185c.136 1.057.403 1.913.878 2.497.442.544 1.134.938 2.344.938 1.573 0 2.292-.337 2.657-.751.384-.435.558-1.15.558-2.361 0-1.14-.243-1.847-.705-2.319-.477-.488-1.319-.862-2.824-1.025-1.487-.161-2.192.138-2.533.529-.269.307-.437.808-.438 1.578v.021c0 .265.021.562.063.893Zm-1.626 0c.042-.331.063-.628.063-.894v-.02c-.001-.77-.169-1.271-.438-1.578-.341-.391-1.046-.69-2.533-.529-1.505.163-2.347.537-2.824 1.025-.462.472-.705 1.179-.705 2.319 0 1.211.175 1.926.558 2.361.365.414 1.084.751 2.657.751 1.21 0 1.902-.394 2.344-.938.475-.584.742-1.44.878-2.497Z" } },
        };
        static readonly Dictionary<string, Mark> cache = new Dictionary<string, Mark>();

        public static string BaseName(string name, out int account)
        {
            account = 1;
            int space = name.LastIndexOf(' '), n;
            if (space > 0 && int.TryParse(name.Substring(space + 1), NumberStyles.None, CultureInfo.InvariantCulture, out n) && n > 1)
            {
                account = n;
                return name.Substring(0, space);
            }
            return name;
        }

        public static void Draw(Graphics g, string name, RectangleF box, Color color)
        {
            int account;
            string baseName = BaseName(name, out account);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            Mark mark = Find(baseName);
            if (mark != null)
            {
                using (var path = (GraphicsPath)mark.Path.Clone())
                using (var matrix = new Matrix())
                using (var brush = new SolidBrush(color))
                {
                    float k = Math.Min(box.Width / mark.ViewBox.Width, box.Height / mark.ViewBox.Height);
                    matrix.Translate(box.X + (box.Width - mark.ViewBox.Width * k) / 2, box.Y + (box.Height - mark.ViewBox.Height * k) / 2);
                    matrix.Scale(k, k);
                    matrix.Translate(-mark.ViewBox.X, -mark.ViewBox.Y);
                    path.Transform(matrix);
                    g.FillPath(brush, path);
                }
            }
            else
            {
                string initial = baseName.Length > 0 ? baseName.Substring(0, 1).ToUpperInvariant() : "?";
                Text(g, initial, box, box.Height * 0.85f, color);
            }
            if (account > 1) Badge(g, account, box, color);
        }

        static void Badge(Graphics g, int account, RectangleF box, Color color)
        {
            float d = box.Width * 0.62f;
            var dot = new RectangleF(box.Right - d * 0.6f, box.Bottom - d * 0.6f, d, d);
            using (var brush = new SolidBrush(color)) g.FillEllipse(brush, dot);
            bool bright = color.R * 0.299 + color.G * 0.587 + color.B * 0.114 > 150;
            Text(g, account > 9 ? "+" : account.ToString(CultureInfo.InvariantCulture), dot, d * 0.8f,
                bright ? Color.FromArgb(24, 24, 24) : Color.White);
        }

        static void Text(Graphics g, string text, RectangleF box, float pixels, Color color)
        {
            using (Font font = Fmt.Pick("Segoe UI Variable Text Semibold", "Segoe UI Semibold", pixels, GraphicsUnit.Pixel))
            using (var brush = new SolidBrush(color))
            using (var format = new StringFormat(StringFormat.GenericTypographic) { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(text, font, brush, box, format);
        }

        static Mark Find(string baseName)
        {
            Mark mark;
            if (cache.TryGetValue(baseName, out mark)) return mark;
            string[] source;
            if (Sources.TryGetValue(baseName, out source))
            {
                float[] vb = source[0].Split(' ').Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                mark = new Mark { ViewBox = new RectangleF(vb[0], vb[1], vb[2], vb[3]), Path = SvgPath.Parse(source[1]) };
            }
            cache[baseName] = mark;
            return mark;
        }
    }

    static class SvgPath
    {
        public static GraphicsPath Parse(string d)
        {
            var path = new GraphicsPath(FillMode.Winding);
            int pos = 0;
            char cmd = ' ', prev = ' ';
            PointF cur = PointF.Empty, start = PointF.Empty, ctrl = PointF.Empty;
            while (true)
            {
                SkipSeparators(d, ref pos);
                if (pos >= d.Length) break;
                if (IsCommand(d[pos])) cmd = d[pos++];
                else if (cmd == ' ') break;
                bool rel = char.IsLower(cmd);
                char op = char.ToUpperInvariant(cmd);
                float ox = rel ? cur.X : 0, oy = rel ? cur.Y : 0;
                switch (op)
                {
                    case 'M':
                        cur = start = new PointF(ox + Number(d, ref pos), oy + Number(d, ref pos));
                        path.StartFigure();
                        cmd = rel ? 'l' : 'L';
                        break;
                    case 'L':
                        cur = Line(path, cur, new PointF(ox + Number(d, ref pos), oy + Number(d, ref pos)));
                        break;
                    case 'H':
                        cur = Line(path, cur, new PointF(ox + Number(d, ref pos), cur.Y));
                        break;
                    case 'V':
                        cur = Line(path, cur, new PointF(cur.X, oy + Number(d, ref pos)));
                        break;
                    case 'C':
                    case 'S':
                    {
                        PointF c1 = op == 'C' ? new PointF(ox + Number(d, ref pos), oy + Number(d, ref pos))
                            : prev == 'C' || prev == 'S' ? Reflect(ctrl, cur) : cur;
                        PointF c2 = new PointF(ox + Number(d, ref pos), oy + Number(d, ref pos));
                        PointF end = new PointF(ox + Number(d, ref pos), oy + Number(d, ref pos));
                        path.AddBezier(cur, c1, c2, end);
                        ctrl = c2;
                        cur = end;
                        break;
                    }
                    case 'Q':
                    case 'T':
                    {
                        PointF q = op == 'Q' ? new PointF(ox + Number(d, ref pos), oy + Number(d, ref pos))
                            : prev == 'Q' || prev == 'T' ? Reflect(ctrl, cur) : cur;
                        PointF end = new PointF(ox + Number(d, ref pos), oy + Number(d, ref pos));
                        path.AddBezier(cur, Lerp(cur, q, 2f / 3), Lerp(end, q, 2f / 3), end);
                        ctrl = q;
                        cur = end;
                        break;
                    }
                    case 'A':
                    {
                        float rx = Number(d, ref pos), ry = Number(d, ref pos), angle = Number(d, ref pos);
                        bool large = Flag(d, ref pos), sweep = Flag(d, ref pos);
                        PointF end = new PointF(ox + Number(d, ref pos), oy + Number(d, ref pos));
                        Arc(path, cur, rx, ry, angle, large, sweep, end);
                        cur = end;
                        break;
                    }
                    case 'Z':
                        path.CloseFigure();
                        cur = start;
                        cmd = ' ';
                        break;
                    default:
                        return path;
                }
                prev = op;
            }
            return path;
        }

        static bool IsCommand(char c)
        {
            return "MmLlHhVvCcSsQqTtAaZz".IndexOf(c) >= 0;
        }

        static void SkipSeparators(string d, ref int pos)
        {
            while (pos < d.Length && (d[pos] == ',' || char.IsWhiteSpace(d[pos]))) pos++;
        }

        static float Number(string d, ref int pos)
        {
            SkipSeparators(d, ref pos);
            int begin = pos;
            if (pos < d.Length && (d[pos] == '-' || d[pos] == '+')) pos++;
            bool dot = false;
            while (pos < d.Length && (char.IsDigit(d[pos]) || (d[pos] == '.' && !dot)))
            {
                if (d[pos] == '.') dot = true;
                pos++;
            }
            if (pos < d.Length && (d[pos] == 'e' || d[pos] == 'E'))
            {
                pos++;
                if (pos < d.Length && (d[pos] == '-' || d[pos] == '+')) pos++;
                while (pos < d.Length && char.IsDigit(d[pos])) pos++;
            }
            return float.Parse(d.Substring(begin, pos - begin), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static bool Flag(string d, ref int pos)
        {
            SkipSeparators(d, ref pos);
            return d[pos++] == '1';
        }

        static PointF Line(GraphicsPath path, PointF from, PointF to)
        {
            path.AddLine(from, to);
            return to;
        }

        static PointF Reflect(PointF control, PointF about)
        {
            return new PointF(2 * about.X - control.X, 2 * about.Y - control.Y);
        }

        static PointF Lerp(PointF a, PointF b, float t)
        {
            return new PointF(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);
        }

        static void Arc(GraphicsPath path, PointF p0, float rxIn, float ryIn, float angle, bool large, bool sweep, PointF p1)
        {
            if (p0 == p1) return;
            double rx = Math.Abs(rxIn), ry = Math.Abs(ryIn);
            if (rx == 0 || ry == 0) { path.AddLine(p0, p1); return; }
            double phi = angle * Math.PI / 180, cos = Math.Cos(phi), sin = Math.Sin(phi);
            double dx = (p0.X - p1.X) / 2.0, dy = (p0.Y - p1.Y) / 2.0;
            double x1 = cos * dx + sin * dy, y1 = -sin * dx + cos * dy;
            double lambda = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
            if (lambda > 1) { rx *= Math.Sqrt(lambda); ry *= Math.Sqrt(lambda); }
            double num = rx * rx * ry * ry - rx * rx * y1 * y1 - ry * ry * x1 * x1;
            double den = rx * rx * y1 * y1 + ry * ry * x1 * x1;
            double coef = (large == sweep ? -1 : 1) * Math.Sqrt(Math.Max(0, num / den));
            double cxp = coef * rx * y1 / ry, cyp = -coef * ry * x1 / rx;
            double cx = cos * cxp - sin * cyp + (p0.X + p1.X) / 2.0, cy = sin * cxp + cos * cyp + (p0.Y + p1.Y) / 2.0;
            double ux = (x1 - cxp) / rx, uy = (y1 - cyp) / ry, vx = (-x1 - cxp) / rx, vy = (-y1 - cyp) / ry;
            double theta = Math.Atan2(uy, ux);
            double delta = Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
            if (!sweep && delta > 0) delta -= 2 * Math.PI;
            else if (sweep && delta < 0) delta += 2 * Math.PI;
            int segments = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2) - 1e-9));
            double step = delta / segments, k = 4.0 / 3 * Math.Tan(step / 4);
            Func<double, double, PointF> map = (u, v) => new PointF(
                (float)(cx + rx * u * cos - ry * v * sin), (float)(cy + rx * u * sin + ry * v * cos));
            for (int i = 0; i < segments; i++)
            {
                double a = theta + i * step, b = a + step;
                double ca = Math.Cos(a), sa = Math.Sin(a), cb = Math.Cos(b), sb = Math.Sin(b);
                path.AddBezier(i == 0 ? p0 : map(ca, sa), map(ca - k * sa, sa + k * ca), map(cb + k * sb, sb - k * cb),
                    i == segments - 1 ? p1 : map(cb, sb));
            }
        }
    }

    sealed class UsagePopup : Form
    {
        const int CornerPreference = 33, RoundCorners = 2;
        const TextFormatFlags Line = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis;
        static readonly Color Bg = Color.FromArgb(32, 33, 36);
        static readonly Color CardFill = Color.FromArgb(42, 44, 48);
        static readonly Color BarTrack = Color.FromArgb(56, 58, 63);
        static readonly Color ControlFill = Color.FromArgb(23, 24, 26);
        static readonly Color ControlStroke = Color.FromArgb(42, 43, 46);
        static readonly Color SelectedFill = Color.FromArgb(58, 60, 64);
        static readonly Color HoverFill = Color.FromArgb(47, 48, 52);
        static readonly Color Primary = Color.FromArgb(243, 244, 246);
        static readonly Color Secondary = Color.FromArgb(180, 184, 192);
        static readonly Color Tertiary = Color.FromArgb(134, 138, 147);
        static readonly Color Disabled = Color.FromArgb(78, 80, 86);

        readonly Font chromeFont = Pick("Segoe UI Variable Text Semibold", "Segoe UI Semibold", 9f);
        readonly Font nameFont = Pick("Segoe UI Variable Display Semib", "Segoe UI Semibold", 11f);
        readonly Font bodyFont = Pick("Segoe UI Variable Text", "Segoe UI", 9f);
        readonly Font smallFont = Pick("Segoe UI Variable Small", "Segoe UI", 8.25f);
        readonly Font smallStrongFont = Pick("Segoe UI Variable Text Semibold", "Segoe UI Semibold", 8.25f);
        readonly Font bigFont = Pick("Segoe UI Variable Display Semib", "Segoe UI Semibold", 15f);
        readonly Font pctFont = Pick("Segoe UI Variable Text Semibold", "Segoe UI Semibold", 9.75f);
        readonly Font glyphFont = Pick("Segoe Fluent Icons", "Segoe MDL2 Assets", 7.5f);
        readonly System.Windows.Forms.Timer clock = new System.Windows.Forms.Timer { Interval = 30000 };
        readonly ToolTip tip = new ToolTip { AutoPopDelay = 20000 };
        readonly Rectangle[] hits = new Rectangle[3];
        Snapshot snap = new Snapshot();
        Color accent = Color.FromArgb(76, 194, 255);
        bool placeLeft;
        int hover = -1;
        int lastHide;
        int columns = 1;

        public Func<bool> Centered = TaskbarStrip.CenteredTaskbar;
        public Func<ProviderGroup, bool> OnTaskbar = group => true;
        public event Action<bool> PositionPicked;

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

        static Font Pick(string family, string fallback, float points)
        {
            return Fmt.Pick(family, fallback, points, GraphicsUnit.Point);
        }

        static Color SystemAccent()
        {
            var palette = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Accent", "AccentPalette", null) as byte[];
            if (palette == null || palette.Length < 8) return Color.FromArgb(76, 194, 255);
            return Color.FromArgb(palette[4], palette[5], palette[6]);
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
        float F(float px) { return px * DeviceDpi / 96f; }

        public void SetSnapshot(Snapshot next)
        {
            snap = next;
            if (Visible) { Relayout(); Invalidate(); }
        }

        public void SetPosition(bool left)
        {
            placeLeft = left;
            if (Visible) Invalidate();
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
            if (Visible) return;
            clock.Stop();
            hover = -1;
            tip.SetToolTip(this, null);
        }

        int Pad { get { return S(12); } }
        int Gap { get { return S(8); } }
        int HeaderH { get { return S(44); } }
        int CardW { get { return S(312); } }
        int CardPad { get { return S(14); } }
        int BigH { get { return S(28); } }
        int ExtraH { get { return S(24); } }

        static List<LimitRow> Extras(ProviderGroup group, long now)
        {
            LimitRow shortRow, longRow;
            Fmt.Sides(group, now, out shortRow, out longRow);
            return group.Rows.Where(row => row != shortRow && row != longRow).ToList();
        }

        int SideHeight(bool shown) { return S(16) + BigH + (shown ? 0 : S(16)); }

        int CardHeight(ProviderGroup group, long now)
        {
            int extras = Extras(group, now).Count;
            return CardPad + S(22) + S(12) + SideHeight(OnTaskbar(group)) + (extras > 0 ? S(14) + S(18) + extras * ExtraH : 0) + CardPad;
        }

        /// <summary>Top-left of each provider card; each goes into the currently shortest column.</summary>
        List<Point> Place(int cols, long now, out int contentHeight)
        {
            var heights = new int[cols];
            var origins = new List<Point>();
            foreach (ProviderGroup group in snap.Groups)
            {
                int col = 0;
                for (int c = 1; c < cols; c++) if (heights[c] < heights[col]) col = c;
                origins.Add(new Point(col * (CardW + Gap), heights[col]));
                heights[col] += CardHeight(group, now) + Gap;
            }
            contentHeight = snap.Groups.Count == 0 ? S(48) : heights.Max() - Gap;
            return origins;
        }

        /// <summary>One column; a second one once the list would outgrow the screen.</summary>
        void Relayout()
        {
            long now = Snapshot.Now();
            int maxHeight = Screen.FromPoint(Cursor.Position).WorkingArea.Height - S(20), content;
            Place(1, now, out content);
            columns = HeaderH + content + Pad > maxHeight && snap.Groups.Count > 1 ? 2 : 1;
            Place(columns, now, out content);
            int width = 2 * Pad + columns * CardW + (columns - 1) * Gap;
            Size = new Size(width, HeaderH + content + Pad);
            accent = SystemAccent();
            hits[2] = new Rectangle(width - S(8) - S(28), S(8), S(28), S(28));
            int button = S(30), h = S(24), y = S(10);
            hits[1] = new Rectangle(hits[2].X - S(8) - button, y, button, h);
            hits[0] = new Rectangle(hits[1].X - button, y, button, h);
        }

        int Hit(Point p)
        {
            for (int i = 0; i < hits.Length; i++) if (hits[i].Contains(p)) return i;
            return -1;
        }

        bool HitEnabled(int index) { return index == 1 || index == 2 || (index == 0 && Centered()); }

        string TipFor(int index)
        {
            switch (index)
            {
                case 0: return Centered() ? "Taskbar position: left edge" : "Left needs a centered taskbar";
                case 1: return "Taskbar position: right, next to the tray";
                default: return null;
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            long now = Snapshot.Now();
            DrawHeader(g, now);

            int content;
            List<Point> origins = Place(columns, now, out content);
            if (snap.Groups.Count == 0)
            {
                var box = new Rectangle(Pad, HeaderH, Width - 2 * Pad, content);
                FillRounded(g, box, F(10), CardFill, Color.Empty);
                TextRenderer.DrawText(g, "Waiting for data from omp\u2026", bodyFont, Rectangle.Inflate(box, -CardPad, 0), Tertiary, Line);
            }
            for (int i = 0; i < snap.Groups.Count; i++)
            {
                ProviderGroup group = snap.Groups[i];
                DrawCard(g, group, new Rectangle(Pad + origins[i].X, HeaderH + origins[i].Y, CardW, CardHeight(group, now)), now);
            }
        }

        void DrawHeader(Graphics g, long now)
        {
            int x = Pad + S(2), right = hits[0].X - S(10);
            int titleW = Math.Min(TextRenderer.MeasureText(g, "omp usage", chromeFont, Size.Empty, TextFormatFlags.NoPadding).Width + S(2), right - x);
            TextRenderer.DrawText(g, "omp usage", chromeFont, new Rectangle(x, S(8), titleW, S(28)), Secondary, Line);
            if (snap.FetchedAt > 0)
                TextRenderer.DrawText(g, "updated " + Fmt.Ago(now - snap.FetchedAt), smallFont, new Rectangle(x + titleW + S(8), S(8), right - x - titleW - S(8), S(28)), Tertiary, Line);

            int selected = placeLeft && Centered() ? 0 : 1;
            FillRounded(g, Rectangle.Inflate(Rectangle.Union(hits[0], hits[1]), S(2), S(2)), F(7), ControlFill, ControlStroke);
            for (int i = 0; i < 2; i++)
            {
                bool enabled = HitEnabled(i), on = i == selected, hot = enabled && !on && hover == i;
                if (on) FillRounded(g, hits[i], F(5), SelectedFill, Color.Empty);
                else if (hot) FillRounded(g, hits[i], F(5), HoverFill, Color.Empty);
                Color ink = !enabled ? Disabled : on ? accent : hot ? Secondary : Tertiary;
                DrawPositionIcon(g, hits[i], ink, i == 0);
            }

            Rectangle close = hits[2];
            bool closeHot = hover == 2;
            if (closeHot) FillRounded(g, close, F(5), HoverFill, Color.Empty);
            TextRenderer.DrawText(g, "\uE8BB", glyphFont, close, closeHot ? Primary : Secondary, Line | TextFormatFlags.HorizontalCenter);
        }

        void DrawPositionIcon(Graphics g, Rectangle button, Color ink, bool atLeft)
        {
            float w = F(16), h = F(10), x = button.X + (button.Width - w) / 2, y = button.Y + (button.Height - h) / 2, cy = y + h / 2;
            using (var pen = new Pen(ink, F(1.3f)))
            using (GraphicsPath frame = RoundedRect(new RectangleF(x, y, w, h), F(2.5f)))
                g.DrawPath(pen, frame);
            float m = F(4), mx = atLeft ? x + F(2.2f) : x + w - F(2.2f) - m;
            FillRounded(g, new RectangleF(mx, cy - m / 2, m, m), F(1), ink, Color.Empty);
        }

        void DrawCard(Graphics g, ProviderGroup group, Rectangle r, long now)
        {
            FillRounded(g, r, F(12), CardFill, Color.Empty);
            Color color = group.ColorOn(false);
            bool shown = OnTaskbar(group);
            int x = r.X + CardPad, y = r.Y + CardPad, inner = r.Width - 2 * CardPad;
            Brand.Draw(g, group.Name, new RectangleF(x, y + F(3), F(16), F(16)), color);
            int nameX = x + S(24);
            int nameW = Math.Min(TextRenderer.MeasureText(g, group.Name, nameFont, Size.Empty, TextFormatFlags.NoPadding).Width + S(2), inner / 2);
            TextRenderer.DrawText(g, group.Name, nameFont, new Rectangle(nameX, y, nameW, S(22)), Primary, Line);
            int afterName = nameX + nameW + S(8);
            if (!shown)
            {
                const string hidden = "not on taskbar";
                int hiddenW = TextRenderer.MeasureText(g, hidden, smallFont, Size.Empty, TextFormatFlags.NoPadding).Width + S(2);
                TextRenderer.DrawText(g, hidden, smallFont, new Rectangle(afterName, y + S(1), hiddenW, S(22)), color, Line);
                afterName += hiddenW + S(10);
            }
            TextRenderer.DrawText(g, group.Account, smallFont, new Rectangle(afterName, y + S(1), x + inner - afterName, S(22)), Tertiary, Line | TextFormatFlags.Right);

            y += S(22) + S(12);
            LimitRow shortRow, longRow;
            Fmt.Sides(group, now, out shortRow, out longRow);
            if (shortRow != null) DrawSide(g, shortRow, x, y, false, shown, color, now);
            if (longRow != null) DrawSide(g, longRow, x + inner, y, true, shown, color, now);
            y += SideHeight(shown);

            List<LimitRow> extras = Extras(group, now);
            if (extras.Count == 0) return;
            y += S(14);
            TextRenderer.DrawText(g, "Other limits", smallStrongFont, new Rectangle(x, y, inner, S(16)), Tertiary, Line);
            y += S(18);
            int resetW = S(84), pctW = S(40), barW = S(36), resetX = x + inner - resetW, pctRight = resetX - S(10), barX = pctRight - pctW - S(8) - barW;
            foreach (LimitRow row in extras)
            {
                double pct = row.RemainingPct(now);
                TextRenderer.DrawText(g, row.Label, bodyFont, new Rectangle(x, y, barX - S(10) - x, ExtraH), Secondary, Line);
                float bh = F(4), by = y + (ExtraH - bh) / 2;
                FillRounded(g, new RectangleF(barX, by, barW, bh), bh / 2, BarTrack, Color.Empty);
                float fill = (float)(barW * Math.Max(0, Math.Min(100, pct)) / 100);
                if (fill > 0) FillRounded(g, new RectangleF(barX, by, Math.Max(bh, fill), bh), bh / 2, color, Color.Empty);
                int top = y + (ExtraH - TextRenderer.MeasureText(g, "0", pctFont, Size.Empty, TextFormatFlags.NoPadding).Height) / 2;
                DrawRun(g, pctRight, top, true, new Run(Fmt.Pct(pct).TrimEnd('%'), pctFont, Primary, 0), new Run("%", smallFont, Tertiary, 1));
                TextRenderer.DrawText(g, Fmt.ResetsAt(row, now), smallFont, new Rectangle(resetX, y, resetW, ExtraH), Tertiary, Line | TextFormatFlags.Right);
                y += ExtraH;
            }
        }

        sealed class Run
        {
            public readonly string Text;
            public readonly Font Font;
            public readonly Color Color;
            public readonly int GapBefore;
            public Run(string text, Font font, Color color, int gapBefore) { Text = text; Font = font; Color = color; GapBefore = gapBefore; }
        }

        static float Ascent(Graphics g, Font font)
        {
            return font.FontFamily.GetCellAscent(font.Style) * font.GetHeight(g) / font.FontFamily.GetLineSpacing(font.Style);
        }

        void DrawRun(Graphics g, int edgeX, int top, bool alignRight, params Run[] runs)
        {
            const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
            Run[] parts = runs.Where(run => run.Text.Length > 0).ToArray();
            if (parts.Length == 0) return;
            var widths = parts.Select(run => TextRenderer.MeasureText(g, run.Text, run.Font, Size.Empty, flags).Width).ToArray();
            int total = widths.Sum();
            for (int i = 1; i < parts.Length; i++) total += S(parts[i].GapBefore);
            float ascent = parts.Max(run => Ascent(g, run.Font));
            int x = alignRight ? edgeX - total : edgeX;
            for (int i = 0; i < parts.Length; i++)
            {
                if (i > 0) x += S(parts[i].GapBefore);
                TextRenderer.DrawText(g, parts[i].Text, parts[i].Font, new Point(x, top + (int)Math.Round(ascent - Ascent(g, parts[i].Font))), parts[i].Color, flags);
                x += widths[i];
            }
        }

        static string PoolOf(LimitRow row)
        {
            string suffix = " " + row.Window;
            return row.Label.EndsWith(suffix, StringComparison.Ordinal) ? row.Label.Substring(0, row.Label.Length - suffix.Length) : "";
        }

        void DrawSide(Graphics g, LimitRow row, int edgeX, int y, bool alignRight, bool shown, Color color, long now)
        {
            DrawRun(g, edgeX, y, alignRight, new Run(PoolOf(row), smallFont, Tertiary, 0), new Run(row.Window, smallStrongFont, color, 4),
                new Run(shown && row.ResetsAt > now ? "resets" : "", smallFont, Tertiary, 4));
            int top = y + S(16);
            string day, time, suffix;
            bool future = Fmt.ResetParts(row, now, out day, out time, out suffix);
            if (shown)
            {
                if (future) DrawRun(g, edgeX, top, alignRight, new Run(day, bodyFont, Secondary, 0), new Run(time, bigFont, Primary, 5), new Run(suffix, smallFont, Tertiary, 3));
                else DrawRun(g, edgeX, top, alignRight, new Run(row.ResetsAt > 0 ? "refilled" : "no reset time", bigFont, Secondary, 0));
                return;
            }
            DrawRun(g, edgeX, top, alignRight, new Run(Fmt.Pct(row.RemainingPct(now)).TrimEnd('%'), bigFont, Primary, 0), new Run("%", bodyFont, Tertiary, 1));
            string reset = Fmt.ResetsAt(row, now);
            DrawRun(g, edgeX, top + BigH, alignRight, new Run(future ? "resets " + reset : reset, smallFont, Tertiary, 0));
        }

        static void FillRounded(Graphics g, RectangleF r, float radius, Color fill, Color stroke)
        {
            RectangleF box = stroke.IsEmpty ? r : RectangleF.Inflate(r, -0.5f, -0.5f);
            using (GraphicsPath shape = RoundedRect(box, radius))
            {
                using (var brush = new SolidBrush(fill)) g.FillPath(brush, shape);
                if (!stroke.IsEmpty) using (var pen = new Pen(stroke)) g.DrawPath(pen, shape);
            }
        }

        static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var path = new GraphicsPath();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int hit = Hit(e.Location);
            if (hit == hover) return;
            hover = hit;
            Cursor = hit >= 0 && HitEnabled(hit) ? Cursors.Hand : Cursors.Default;
            tip.SetToolTip(this, hit >= 0 ? TipFor(hit) : null);
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (hover < 0) return;
            hover = -1;
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int hit = e.Button == MouseButtons.Left ? Hit(e.Location) : -1;
            if (hit == 2) { Hide(); return; }
            if (hit < 0 || !HitEnabled(hit) || PositionPicked == null) return;
            PositionPicked(hit == 0);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                clock.Dispose();
                tip.Dispose();
                foreach (Font font in new[] { chromeFont, nameFont, bodyFont, smallFont, smallStrongFont, bigFont, pctFont, glyphFont })
                    font.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// Per-pixel-alpha child window inside the taskbar (Shell_TrayWnd), left of the tray area or, on a centered
    /// taskbar, optionally at the left edge. As a taskbar child it hides with the taskbar in fullscreen apps and
    /// never covers other windows.
    /// </summary>
    sealed class TaskbarStrip : NativeWindow
    {
        const int WS_CHILD = 0x40000000, WS_CLIPSIBLINGS = 0x04000000;
        const int WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
        const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        const int WM_LBUTTONUP = 0x202, WM_RBUTTONUP = 0x205, WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
        const int SW_HIDE = 0;

        readonly float scale;
        readonly Font numberFont, percentFont, labelFont;
        readonly Bitmap probe = new Bitmap(1, 1);
        readonly Graphics measure;
        readonly StringFormat typographic = (StringFormat)StringFormat.GenericTypographic.Clone();
        Snapshot snap = new Snapshot();
        IntPtr parent = IntPtr.Zero;
        Rectangle bounds = Rectangle.Empty;
        bool shown;

        public event EventHandler LeftClick;
        public event EventHandler RightClick;

        public TaskbarStrip(float dpi)
        {
            scale = dpi / 96f;
            numberFont = Fmt.Pick("Segoe UI Variable Display Semib", "Segoe UI Semibold", 13 * scale, GraphicsUnit.Pixel);
            percentFont = Fmt.Pick("Segoe UI Variable Text Semibold", "Segoe UI Semibold", 10 * scale, GraphicsUnit.Pixel);
            labelFont = Fmt.Pick("Segoe UI Variable Text", "Segoe UI", 11 * scale, GraphicsUnit.Pixel);
            measure = Graphics.FromImage(probe);
        }

        int S(float px) { return (int)Math.Round(px * scale); }

        int ProviderGap { get { return S(12); } }
        int GaugeSize { get { return S(32); } }
        int ColumnGap { get { return S(3); } }
        int NumberH { get { return S(16); } }
        int LabelH { get { return S(13); } }

        /// <summary>Position the user picked; persisted by the tray context. Right is the default.</summary>
        public bool PlaceLeft { get; private set; }
        // What actually fits right now: Sync drops the providers with the most quota left.
        List<ProviderGroup> visible = new List<ProviderGroup>();
        string layoutKey = "";
        // Taskbar geometry the current fit was computed for; the slow timer skips all work while it holds.
        Native.RECT lastTaskbar, lastTray, lastStart, lastRebar;

        public void SetPosition(bool placeLeft)
        {
            PlaceLeft = placeLeft;
            Sync(true);
        }

        public bool Shows(ProviderGroup group)
        {
            return shown && visible.Contains(group);
        }

        /// <summary>The providers that fit into <paramref name="room"/> pixels: the ones with the most quota left go first.</summary>
        public List<ProviderGroup> Fit(List<ProviderGroup> all, int room, long now)
        {
            var groups = new List<ProviderGroup>(all);
            while (groups.Count > 0 && WidthFor(groups, now) > room)
            {
                int roomiest = 0;
                for (int i = 1; i < groups.Count; i++)
                    if (groups[i].MinRemaining(now) > groups[roomiest].MinRemaining(now)) roomiest = i;
                groups.RemoveAt(roomiest);
            }
            return groups;
        }

        int SideWidth(LimitRow row, long now)
        {
            if (row == null) return 0;
            float label = Width(Fmt.Countdown(row, now), labelFont);
            return (int)Math.Ceiling(Math.Max(NumberWidth(row, now), label)) + S(1);
        }

        float Width(string text, Font font)
        {
            return measure.MeasureString(text, font, PointF.Empty, typographic).Width;
        }

        float NumberWidth(LimitRow row, long now)
        {
            return Width(Fmt.Pct(row.RemainingPct(now)).TrimEnd('%'), numberFont) + Width("%", percentFont);
        }

        static float Ascent(Font font)
        {
            return font.Size * font.FontFamily.GetCellAscent(font.Style) / font.FontFamily.GetEmHeight(font.Style);
        }

        int ProviderWidth(ProviderGroup group, long now)
        {
            LimitRow shortRow, longRow;
            Fmt.Sides(group, now, out shortRow, out longRow);
            return GaugeSize + (shortRow != null ? SideWidth(shortRow, now) + ColumnGap : 0) + (longRow != null ? SideWidth(longRow, now) + ColumnGap : 0);
        }

        int WidthFor(List<ProviderGroup> groups, long now)
        {
            int w = S(4) * 2 + Math.Max(0, groups.Count - 1) * ProviderGap;
            foreach (ProviderGroup group in groups) w += ProviderWidth(group, now);
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

            int h = tb.Bottom - tb.Top, left, right;
            bool atLeft = FreeSpace(tb, tray, startRect, hasStart, rebarRect, hasRebar, CenteredTaskbar(), WidgetsButton(), out left, out right);

            long now = Snapshot.Now();
            List<ProviderGroup> groups = Fit(snap.Groups, right - left, now);
            int w = WidthFor(groups, now);
            if (groups.Count == 0)
            {
                Hide();
                return;
            }

            string key = string.Join("|", groups.Select(group => group.Name));
            bool layoutChanged = key != layoutKey;
            visible = groups;
            layoutKey = key;

            var next = new Rectangle(atLeft ? left : right - w, 0, w, h);
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

        bool FreeSpace(Native.RECT tb, Native.RECT tray, Native.RECT startRect, bool hasStart, Native.RECT rebarRect, bool hasRebar,
            bool centeredSetting, bool widgets, out int left, out int right)
        {
            bool centered = hasStart && centeredSetting, atLeft = PlaceLeft && centered;
            right = tray.Left - tb.Left - S(16);
            left = 0;
            if (atLeft)
            {
                left = S(8) + (widgets ? S(160) : 0);
                right = startRect.Left - tb.Left - S(8);
            }
            else if (centered) left = tb.Right - startRect.Left + S(8);
            else if (hasRebar) left = rebarRect.Right - tb.Left + S(8);
            return atLeft;
        }

        /// <summary>Windows 11 taskbar alignment; missing value means the default, centered.</summary>
        public static bool CenteredTaskbar()
        {
            object value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAl", 1);
            return !(value is int) || (int)value != 0;
        }

        static bool WidgetsButton()
        {
            object policy = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 1);
            if (policy is int && (int)policy == 0) return false;
            object value = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarDa", 1);
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
            using (var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g = Graphics.FromImage(bmp)) Paint(g, bmp.Height);
                Present(bmp);
            }
        }

        void Paint(Graphics g, int h)
        {
            long now = Snapshot.Now();
            bool light = Fmt.LightTaskbar();
            Color text = light ? Color.FromArgb(235, 0, 0, 0) : Color.FromArgb(245, 255, 255, 255);
            Color dim = light ? Color.FromArgb(130, 0, 0, 0) : Color.FromArgb(140, 255, 255, 255);

            using (var textBrush = new SolidBrush(text))
            using (var dimBrush = new SolidBrush(dim))
            {
                // Alpha 1 is invisible but keeps the whole strip clickable.
                g.Clear(Color.FromArgb(1, 0, 0, 0));
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int x = S(4), top = (h - NumberH - LabelH) / 2;
                float d = GaugeSize, icon = S(14);
                foreach (ProviderGroup group in visible)
                {
                    Color color = group.ColorOn(light);
                    LimitRow shortRow, longRow;
                    Fmt.Sides(group, now, out shortRow, out longRow);
                    if (shortRow != null)
                    {
                        int w = SideWidth(shortRow, now);
                        DrawValue(g, shortRow, now, x, top, w, true, textBrush, dimBrush);
                        x += w + ColumnGap;
                    }
                    var box = new RectangleF(x, (h - d) / 2f, d, d);
                    Gauge.Brackets(g, box, 3.25f * scale, Gauge.Remaining(shortRow, now), Gauge.Remaining(longRow, now), color, Color.FromArgb(44, color));
                    Brand.Draw(g, group.Name, new RectangleF(x + (d - icon) / 2, (h - icon) / 2f, icon, icon), color);
                    x += GaugeSize;
                    if (longRow != null)
                    {
                        int w = SideWidth(longRow, now);
                        x += ColumnGap;
                        DrawValue(g, longRow, now, x, top, w, false, textBrush, dimBrush);
                        x += w;
                    }
                    x += ProviderGap;
                }
            }
        }

        void DrawValue(Graphics g, LimitRow row, long now, float x, float top, float width, bool alignRight, Brush number, Brush dim)
        {
            StringFormat format = typographic;
            string digits = Fmt.Pct(row.RemainingPct(now)).TrimEnd('%');
            float nx = alignRight ? x + width - NumberWidth(row, now) : x, baseline = top + S(1) + Ascent(numberFont);
            g.DrawString(digits, numberFont, number, nx, baseline - Ascent(numberFont), format);
            g.DrawString("%", percentFont, dim, nx + Width(digits, numberFont), baseline - Ascent(percentFont), format);
            string countdown = Fmt.Countdown(row, now);
            float cx = alignRight ? x + width - Width(countdown, labelFont) : x;
            g.DrawString(countdown, labelFont, dim, cx, top + NumberH, format);
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
            numberFont.Dispose();
            percentFont.Dispose();
            labelFont.Dispose();
            typographic.Dispose();
            measure.Dispose();
            probe.Dispose();
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

            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) strip = new TaskbarStrip(g.DpiX);
            layoutFile = Path.Combine(root, "layout.txt");
            bool placeLeft = false;
            try { placeLeft = File.ReadAllText(layoutFile).Split((char[])null, StringSplitOptions.RemoveEmptyEntries).Contains("left"); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            strip.SetPosition(placeLeft);
            popup.SetPosition(placeLeft);
            popup.PositionPicked += SavePosition;

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
            popup.OnTaskbar = strip.Shows;
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

        const int CmdDetails = 1, CmdLeft = 2, CmdRight = 3, CmdQuit = 4;

        public static IntPtr BuildMenu(bool placeLeft, bool centered)
        {
            const uint MF_STRING = 0, MF_GRAYED = 0x1, MF_CHECKED = 0x8, MF_SEPARATOR = 0x800;
            bool atLeft = placeLeft && centered;
            IntPtr menu = Native.CreatePopupMenu();
            Native.AppendMenu(menu, MF_STRING, (UIntPtr)CmdDetails, "Details");
            Native.AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            Native.AppendMenu(menu, MF_STRING | MF_GRAYED, UIntPtr.Zero, "Taskbar position");
            Native.AppendMenu(menu, MF_STRING | (atLeft ? MF_CHECKED : 0) | (centered ? 0 : MF_GRAYED), (UIntPtr)CmdLeft, "Left");
            Native.AppendMenu(menu, MF_STRING | (atLeft ? 0 : MF_CHECKED), (UIntPtr)CmdRight, "Right");
            Native.AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
            Native.AppendMenu(menu, MF_STRING, (UIntPtr)CmdQuit, "Quit");
            return menu;
        }

        void ShowMenu()
        {
            const uint TPM_RIGHTBUTTON = 0x2, TPM_BOTTOMALIGN = 0x20, TPM_RETURNCMD = 0x100;
            IntPtr menu = BuildMenu(strip.PlaceLeft, TaskbarStrip.CenteredTaskbar());
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
                case CmdLeft:
                case CmdRight:
                    SavePosition(cmd == CmdLeft);
                    break;
                case CmdQuit:
                    UserQuit = true;
                    ExitThread();
                    return;
            }
            ScheduleTrim();
        }

        void SavePosition(bool placeLeft)
        {
            strip.SetPosition(placeLeft);
            popup.SetPosition(placeLeft);
            try { File.WriteAllText(layoutFile, placeLeft ? "left" : "right"); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
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

        /// <summary>Brackets (short window left, long window right) of the provider closest to a limit, in its color.</summary>
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
            double left = 0, right = 0;
            if (tightestGroup != null)
            {
                LimitRow shortRow, longRow;
                Fmt.Sides(tightestGroup, now, out shortRow, out longRow);
                left = Gauge.Remaining(shortRow, now);
                right = Gauge.Remaining(longRow, now);
            }
            // Most refreshes change nothing the icon shows; skip the bitmap and HICON churn then.
            string key = text + "\0" + light + "\0" + size + "\0" + (tightestGroup != null ? tightestGroup.Name : "") + "\0" + Math.Round(left) + "\0" + Math.Round(right);
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
                    Gauge.Brackets(g, new RectangleF(0.5f, 0.5f, size - 1, size - 1), stroke, left, right, color, trackColor);
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
