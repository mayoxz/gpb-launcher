// Google Play Games (beta) aspect-ratio launcher.
// 1) Remember the monitor's current mode, then switch to the requested resolution
//    (usually a custom resolution made in the GPU control panel)
// 2) Launch the game through its googleplaygames:// link
// 3) When the game window (crosvm.exe) shows up, close the Google Play Games launcher window (client.exe)
// 4) Switch the monitor back to the mode remembered in step 1
//
// Args:          --game <package>            (required unless the package is embedded; e.g. com.example.game)
//                <W>x<H>[@Hz]                 (the resolution to launch the game at, e.g. 3840x1728@144;
//                also --width <px> --height <px> --hz <rate>; without them the resolution is left as is
//                and only steps 2-3 run)
//                The monitor that supports the resolution is found automatically: the primary monitor
//                is checked first, then the other monitors.
// Optional args: --settle 0 (extra seconds to wait after the game window appears)  --timeout 90
//
// build.ps1 embeds the game package into the exe (AssemblyMetadata "GpbGame"). Run the exe with no
// arguments to open "바로가기 만들기": pick a resolution and it writes "<exe name> (WxH@Hz).lnk" next
// to the exe, so the same exe can make shortcuts for several resolutions.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

static class GpbLauncher
{
    // Google Play Games always installs its program here; only the data folder is user-chosen.
    const string Bootstrapper = @"C:\Program Files\Google\Play Games\Bootstrapper.exe";
    const string Title = "GPB Launcher";

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    const int DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;
    const int DISP_CHANGE_SUCCESSFUL = 0;
    const uint WM_CLOSE = 0x0010;

    delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string dev, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string dev, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr lParam);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string dev, IntPtr dm, IntPtr hwnd, uint flags, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr hWnd, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern bool SetProcessDPIAware();

    [STAThread]
    static int Main(string[] args)
    {
        string device = null;
        string game = null;
        int width = 0, height = 0, hz = 0, settle = 0, timeout = 90;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--game": if (i + 1 < args.Length) game = args[++i]; break;
                case "--width": if (i + 1 < args.Length) width = int.Parse(args[++i]); break;
                case "--height": if (i + 1 < args.Length) height = int.Parse(args[++i]); break;
                case "--hz": if (i + 1 < args.Length) hz = int.Parse(args[++i]); break;
                case "--settle": if (i + 1 < args.Length) settle = int.Parse(args[++i]); break;
                case "--timeout": if (i + 1 < args.Length) timeout = int.Parse(args[++i]); break;
                default:
                    // Shorthand resolution, e.g. 3840x1728 or 3840x1728@144.
                    Match m = Regex.Match(args[i], @"^(\d+)x(\d+)(?:@(\d+))?$", RegexOptions.IgnoreCase);
                    if (m.Success)
                    {
                        width = int.Parse(m.Groups[1].Value);
                        height = int.Parse(m.Groups[2].Value);
                        if (m.Groups[3].Success) hz = int.Parse(m.Groups[3].Value);
                    }
                    break;
            }
        }

        // No resolution given: keep the current mode and only launch the game.
        bool change = width > 0 || height > 0;

        // The game package build.ps1 embedded into this exe, if any.
        string embeddedGame = ReadEmbeddedGame();
        if (string.IsNullOrEmpty(game))
        {
            // Opened directly (e.g. double-clicked): no game and no resolution means "make shortcuts".
            if (!change && embeddedGame != null)
                return ShowShortcutMaker(embeddedGame);
            // A resolution was given without --game: use the game embedded in this exe.
            if (change && embeddedGame != null)
                game = embeddedGame;
            else
            {
                Error("이 파일은 직접 실행하지 않고, 해상도가 지정된 바로가기로 실행합니다.\n" +
                      "같은 폴더의 바로가기를 실행하세요. 바로가기가 없으면 build.ps1로 만드세요.");
                return 1;
            }
        }
        if (!Regex.IsMatch(game, "^[A-Za-z0-9._]+$"))
        {
            Error("바로가기의 게임 이름(--game)이 올바르지 않습니다: " + game + "\nbuild.ps1을 실행해서 바로가기를 다시 만드세요.");
            return 1;
        }
        string uri = "googleplaygames://launch/?id=" + game + "&lid=1&pid=1";
        if (change && (width <= 0 || height <= 0))
        {
            Error("--width와 --height를 함께 지정하세요.\n예: GpbLauncher.exe --game com.example.game 3840x1728@144");
            return 1;
        }

        // Find a monitor that supports the mode: the primary monitor first, then the others.
        if (change)
        {
            bool found = false;
            foreach (Screen s in OrderedScreens())
            {
                int tryHz = hz;
                if (tryHz <= 0)
                {
                    DEVMODE cur = NewDevMode();
                    tryHz = EnumDisplaySettings(s.DeviceName, -1, ref cur) ? cur.dmDisplayFrequency : 0; // -1 = ENUM_CURRENT_SETTINGS
                }
                DEVMODE probe = NewDevMode();
                if (FindMode(s.DeviceName, width, height, tryHz, ref probe))
                {
                    device = s.DeviceName;
                    hz = tryHz;
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                Error(string.Format("연결된 모니터 중에 {0}x{1}{2} 해상도를 지원하는 모니터가 없습니다.\nNVIDIA 앱 같은 그래픽 설정에서 사용자 지정 해상도를 확인하세요.", width, height, hz > 0 ? "@" + hz : ""));
                return 1;
            }
        }

        // The mode to come back to (a -> b -> a).
        DEVMODE original = NewDevMode();
        if (change)
        {
            if (!EnumDisplaySettings(device, -1, ref original)) // -1 = ENUM_CURRENT_SETTINGS
            {
                Error(device + "의 현재 해상도를 읽지 못했습니다.");
                return 1;
            }
            original.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
            if (original.dmPelsWidth == width && original.dmPelsHeight == height && original.dmDisplayFrequency == hz)
            {
                // Already at the target (e.g. a previous run was interrupted).
                // Coming back to "current" would keep the target, so use the saved Windows mode instead.
                DEVMODE saved = NewDevMode();
                if (EnumDisplaySettings(device, -2, ref saved)) // -2 = ENUM_REGISTRY_SETTINGS
                {
                    original = saved;
                    original.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
                }
            }
        }

        if (change)
        {
            DEVMODE dm = NewDevMode();
            if (!FindMode(device, width, height, hz, ref dm))
            {
                Error(string.Format("{0}에 {1}x{2}@{3} 모드가 없습니다.\nNVIDIA 앱의 사용자 지정 해상도를 확인하세요.", device, width, height, hz));
                return 1;
            }

            // flags = 0: dynamic change only, not saved to the registry.
            // A reboot or a crash always comes back to the mode saved in Windows display settings.
            int r = ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
            if (r != DISP_CHANGE_SUCCESSFUL)
            {
                Error("해상도 변경 실패 (코드 " + r + ")");
                return 1;
            }
        }

        string message = null; // shown after the resolution is restored, so a dialog never holds the switched mode
        try
        {
            if (change) Thread.Sleep(300); // let the desktop settle before the game reads the screen size
            if (System.IO.File.Exists(Bootstrapper))
                Process.Start(Bootstrapper, "\"" + uri + "\"");
            else
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });

            DateTime deadline = DateTime.Now.AddSeconds(timeout);
            while (DateTime.Now < deadline && FindWindow("crosvm", null) == IntPtr.Zero)
                Thread.Sleep(100);

            IntPtr gameWindow = FindWindow("crosvm", null);
            if (gameWindow == IntPtr.Zero)
            {
                message = timeout + "초 안에 게임 창이 뜨지 않았습니다." + (change ? " 해상도를 되돌렸습니다." : "");
                return 2;
            }

            // Google Play Games opens the game on the monitor it was last on, and the ratio follows that monitor.
            string gameScreen = Screen.FromHandle(gameWindow).DeviceName;
            if (change && !string.Equals(gameScreen, device, StringComparison.OrdinalIgnoreCase))
                message = "게임 창이 다른 모니터에 떠서 화면비가 바뀌지 않았을 수 있습니다.\n" +
                          "게임 창을 원하는 모니터로 옮겨 두고 게임을 끈 뒤 다시 실행하세요.";

            if (settle > 0) Thread.Sleep(settle * 1000);

            // Restore happens in finally right after this; WM_CLOSE is posted (non-blocking).
            IntPtr gpg = FindWindow("client", "Google Play Games");
            if (gpg != IntPtr.Zero)
                PostMessage(gpg, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            return 0;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return 3;
        }
        finally
        {
            // Back to the mode the monitor had before launch; fall back to the saved Windows mode.
            if (change && ChangeDisplaySettingsEx(device, ref original, IntPtr.Zero, 0, IntPtr.Zero) != DISP_CHANGE_SUCCESSFUL)
                ChangeDisplaySettingsEx(device, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
            if (message != null) Error(message);
        }
    }

    // The game package embedded by build.ps1 (AssemblyMetadata "GpbGame"), if it is valid.
    static string ReadEmbeddedGame()
    {
        object[] attrs = Assembly.GetExecutingAssembly().GetCustomAttributes(typeof(AssemblyMetadataAttribute), false);
        foreach (object attr in attrs)
        {
            AssemblyMetadataAttribute meta = attr as AssemblyMetadataAttribute;
            if (meta != null && meta.Key == "GpbGame" && !string.IsNullOrEmpty(meta.Value) && Regex.IsMatch(meta.Value, "^[A-Za-z0-9._]+$"))
                return meta.Value;
        }
        return null;
    }

    // Opened without arguments: show the shortcut maker for the game embedded in this exe.
    static int ShowShortcutMaker(string game)
    {
        SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.Run(new ShortcutForm(game));
        return 0;
    }

    // Primary monitor first, then the others (the same order the launcher uses).
    static Screen[] OrderedScreens()
    {
        Screen primary = Screen.PrimaryScreen;
        Screen[] screens = Screen.AllScreens;
        Screen[] ordered = new Screen[screens.Length];
        int n = 0;
        ordered[n++] = primary;
        foreach (Screen s in screens)
            if (!string.Equals(s.DeviceName, primary.DeviceName, StringComparison.OrdinalIgnoreCase))
                ordered[n++] = s;
        return ordered;
    }

    // Ratio as "x:9" (20:9, 21.33:9, 16:9), never reduced to odd pairs like 41:18.
    static string Ratio(int w, int h)
    {
        return Math.Round(w * 9.0 / h, 2).ToString("0.##", CultureInfo.InvariantCulture) + ":9";
    }

    static DEVMODE NewDevMode()
    {
        DEVMODE dm = new DEVMODE();
        dm.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
        return dm;
    }

    static bool FindMode(string device, int w, int h, int hz, ref DEVMODE found)
    {
        DEVMODE dm = NewDevMode();
        for (int i = 0; EnumDisplaySettings(device, i, ref dm); i++)
        {
            if (dm.dmPelsWidth == w && dm.dmPelsHeight == h && dm.dmDisplayFrequency == hz)
            {
                found = dm;
                found.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
                return true;
            }
        }
        return false;
    }

    // First visible top-level window owned by a process with this name (and this exact title, if given).
    static IntPtr FindWindow(string processName, string title)
    {
        IntPtr result = IntPtr.Zero;
        EnumWindows(delegate (IntPtr h, IntPtr l)
        {
            if (!IsWindowVisible(h)) return true;
            StringBuilder sb = new StringBuilder(256);
            GetWindowText(h, sb, sb.Capacity);
            if (sb.Length == 0) return true;
            if (title != null && sb.ToString() != title) return true;
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            try
            {
                using (Process p = Process.GetProcessById((int)pid))
                {
                    if (!string.Equals(p.ProcessName, processName, StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            catch (ArgumentException) { return true; }
            result = h;
            return false;
        }, IntPtr.Zero);
        return result;
    }

    static void Error(string msg)
    {
        MessageBox.Show(msg, Title, MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    class Mode
    {
        public int W, H, Hz;
    }

    // Opened by double-clicking the exe: pick one of the primary monitor's modes (or type a mode
    // by hand) and write "<exe name> (WxH@Hz).lnk" next to the exe.
    class ShortcutForm : Form
    {
        readonly string game;
        readonly string exePath, exeDir, exeBase, device;
        int curW, curH, curHz;
        List<Mode> modes;

        ListBox listBox;
        TextBox txtW, txtH, txtHz;

        public ShortcutForm(string game)
        {
            this.game = game;
            exePath = Application.ExecutablePath;
            exeDir = Path.GetDirectoryName(exePath);
            exeBase = Path.GetFileNameWithoutExtension(exePath);
            device = Screen.PrimaryScreen.DeviceName;
            ReadCurrent();
            modes = BuildModes();
            BuildUi();
        }

        void ReadCurrent()
        {
            DEVMODE dm = NewDevMode();
            if (EnumDisplaySettings(device, -1, ref dm)) // -1 = ENUM_CURRENT_SETTINGS
            {
                curW = dm.dmPelsWidth;
                curH = dm.dmPelsHeight;
                curHz = dm.dmDisplayFrequency;
            }
        }

        // Every distinct WxH the primary monitor offers, ordered like the Windows display settings
        // resolution list (width descending, then height descending). Because the sort also puts the
        // highest refresh rate first for a repeated size, the first entry per WxH keeps that rate.
        List<Mode> BuildModes()
        {
            List<Mode> all = new List<Mode>();
            DEVMODE dm = NewDevMode();
            for (int i = 0; EnumDisplaySettings(device, i, ref dm); i++)
            {
                if (dm.dmPelsWidth <= 0 || dm.dmPelsHeight <= 0) continue;
                Mode m = new Mode();
                m.W = dm.dmPelsWidth;
                m.H = dm.dmPelsHeight;
                m.Hz = dm.dmDisplayFrequency;
                all.Add(m);
            }
            all.Sort(delegate (Mode a, Mode b)
            {
                if (a.W != b.W) return b.W.CompareTo(a.W);
                if (a.H != b.H) return b.H.CompareTo(a.H);
                return b.Hz.CompareTo(a.Hz);
            });

            // One per WxH; the first one in the sorted list has that size's highest rate.
            List<Mode> result = new List<Mode>();
            foreach (Mode m in all)
            {
                if (!HasSize(result, m.W, m.H)) result.Add(m);
            }
            return result;
        }

        static bool HasSize(List<Mode> list, int w, int h)
        {
            foreach (Mode m in list)
                if (m.W == w && m.H == h) return true;
            return false;
        }

        void BuildUi()
        {
            Text = exeBase + " - 바로가기 만들기";
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Malgun Gothic", 10f);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowIcon = false;
            StartPosition = FormStartPosition.CenterScreen;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.Padding = new Padding(12);

            listBox = new ListBox();
            listBox.Dock = DockStyle.Fill;
            listBox.DrawMode = DrawMode.OwnerDrawFixed;
            listBox.IntegralHeight = false;
            listBox.ScrollAlwaysVisible = true;
            listBox.Font = Font;
            listBox.Margin = new Padding(0);
            foreach (Mode m in modes) listBox.Items.Add(m);
            listBox.ItemHeight = Font.Height + 6;
            listBox.DrawItem += ListBoxDrawItem;
            listBox.SelectedIndexChanged += delegate { ApplySelectedMode(); };
            listBox.MouseClick += delegate (object s, MouseEventArgs e)
            {
                // Selecting copies once through SelectedIndexChanged; this also covers a click on
                // the item that is already selected.
                if (e.Button == MouseButtons.Left && listBox.IndexFromPoint(e.Location) >= 0) ApplySelectedMode();
            };

            TableLayoutPanel manual = new TableLayoutPanel();
            manual.AutoSize = true;
            manual.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            manual.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            manual.ColumnCount = 3;
            manual.RowCount = 2;
            manual.Margin = new Padding(0, 8, 0, 8);
            for (int i = 0; i < 3; i++) manual.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            manual.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            manual.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            Label lblW = MakeLabel("가로");
            Label lblH = MakeLabel("세로");
            Label lblHz = MakeLabel("주사율");
            txtW = MakeBox();
            txtH = MakeBox();
            txtHz = MakeBox();
            // Start with the primary monitor's current mode; every box stays editable.
            txtW.Text = curW > 0 ? curW.ToString(CultureInfo.InvariantCulture) : "";
            txtH.Text = curH > 0 ? curH.ToString(CultureInfo.InvariantCulture) : "";
            txtHz.Text = curHz > 0 ? curHz.ToString(CultureInfo.InvariantCulture) : "";
            manual.Controls.Add(lblW, 0, 0);
            manual.Controls.Add(lblH, 1, 0);
            manual.Controls.Add(lblHz, 2, 0);
            manual.Controls.Add(txtW, 0, 1);
            manual.Controls.Add(txtH, 1, 1);
            manual.Controls.Add(txtHz, 2, 1);

            Button btnCreate = new Button();
            btnCreate.Text = "바로가기 만들기";
            btnCreate.AutoSize = true;
            btnCreate.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            btnCreate.Click += delegate { CreateShortcut(); };

            Button btnCancel = new Button();
            btnCancel.Text = "취소";
            btnCancel.AutoSize = true;
            btnCancel.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            btnCancel.Click += delegate { Close(); };

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            buttons.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            buttons.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            buttons.FlowDirection = FlowDirection.RightToLeft; // the first added control sits rightmost
            buttons.Margin = new Padding(0, 8, 0, 0);
            buttons.Controls.Add(btnCancel);
            buttons.Controls.Add(btnCreate);

            AcceptButton = btnCreate;
            CancelButton = btnCancel;

            root.Controls.Add(listBox, 0, 0);
            root.Controls.Add(manual, 0, 1);
            root.Controls.Add(buttons, 0, 2);
            Controls.Add(root);

            // The list takes the remaining space; this height makes it about 10 rows at 96 DPI.
            ClientSize = new Size(480, 378);

            // Item height follows the actual (DPI-scaled) font once the form is shown, and the
            // current resolution is scrolled into view without being selected.
            Shown += delegate
            {
                listBox.ItemHeight = listBox.Font.Height + 6;
                // Grow or shrink the form so the list shows exactly 10 whole rows (no half-cut row).
                Height += 10 * listBox.ItemHeight - listBox.ClientSize.Height;
                for (int i = 0; i < modes.Count; i++)
                {
                    if (IsCurrent(modes[i]))
                    {
                        listBox.TopIndex = i;
                        break;
                    }
                }
            };
        }

        static Label MakeLabel(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            return l;
        }

        static TextBox MakeBox()
        {
            TextBox t = new TextBox();
            t.Dock = DockStyle.Fill;
            return t;
        }

        // A list click only copies the resolution into the boxes. The refresh rate is the primary
        // monitor's current one when that size has it, otherwise the highest rate of that size.
        void ApplySelectedMode()
        {
            int index = listBox.SelectedIndex;
            if (index < 0 || index >= modes.Count) return;
            Mode m = modes[index];
            DEVMODE probe = NewDevMode();
            int hz = FindMode(device, m.W, m.H, curHz, ref probe) ? curHz : m.Hz;
            txtW.Text = m.W.ToString(CultureInfo.InvariantCulture);
            txtH.Text = m.H.ToString(CultureInfo.InvariantCulture);
            txtHz.Text = hz > 0 ? hz.ToString(CultureInfo.InvariantCulture) : "";
        }

        bool IsCurrent(Mode m)
        {
            return m.W == curW && m.H == curH;
        }

        string ItemText(Mode m)
        {
            string text = m.W + "x" + m.H + "  (" + Ratio(m.W, m.H);
            if (IsCurrent(m)) text += ", 현재";
            return text + ")";
        }

        void ListBoxDrawItem(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            Mode m = (Mode)listBox.Items[e.Index];
            bool selected = (e.State & DrawItemState.Selected) != 0;

            Color back = selected ? SystemColors.Highlight : listBox.BackColor;
            Color fore;
            if (selected) fore = SystemColors.HighlightText;
            else if (IsCurrent(m)) fore = Color.Teal;
            else fore = listBox.ForeColor;

            using (SolidBrush brush = new SolidBrush(back))
                e.Graphics.FillRectangle(brush, e.Bounds);

            Rectangle bounds = new Rectangle(e.Bounds.X + 4, e.Bounds.Y, Math.Max(0, e.Bounds.Width - 4), e.Bounds.Height);
            TextRenderer.DrawText(e.Graphics, ItemText(m), listBox.Font, bounds, fore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            if ((e.State & DrawItemState.Focus) != 0) e.DrawFocusRectangle();
        }

        void CreateShortcut()
        {
            // The boxes are the only input: they start at the current mode and can be edited freely.
            int w, h, hz;
            if (!ResolveManual(out w, out h, out hz)) return;

            if (w == curW && h == curH && hz == curHz)
            {
                if (MessageBox.Show(this, "현재 해상도와 같습니다. 이 해상도로 바로가기를 만들까요?", Text,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
            }

            string name = exeBase + " (" + w + "x" + h + "@" + hz + ")";
            string path = Path.Combine(exeDir, name + ".lnk");
            if (File.Exists(path))
            {
                if (MessageBox.Show(this, "이미 있는 바로가기입니다. 덮어쓸까요?", Text,
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;
            }

            object shell = null;
            object link = null;
            try
            {
                // WScript.Shell through late binding (no dynamic / Microsoft.CSharp reference needed).
                Type shellType = Type.GetTypeFromProgID("WScript.Shell");
                if (shellType == null) throw new InvalidOperationException("WScript.Shell을 찾을 수 없습니다.");
                shell = Activator.CreateInstance(shellType);
                link = shellType.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { path });
                Type linkType = link.GetType();
                SetProperty(linkType, link, "TargetPath", exePath);
                SetProperty(linkType, link, "Arguments", "--game " + game + " " + w + "x" + h + "@" + hz);
                SetProperty(linkType, link, "WorkingDirectory", exeDir);
                SetProperty(linkType, link, "IconLocation", exePath + ",0");
                SetProperty(linkType, link, "Description", w + "x" + h + "@" + hz + " 화면으로 실행");
                linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, new object[0]);

                MessageBox.Show(this, "바로가기를 만들었습니다.\n" + name, Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                if (link != null) Marshal.ReleaseComObject(link);
                if (shell != null) Marshal.ReleaseComObject(shell);
            }
        }

        static void SetProperty(Type type, object target, string name, string value)
        {
            type.InvokeMember(name, BindingFlags.SetProperty, null, target, new object[] { value });
        }

        // Manual entry: the first monitor (primary first) that has WxH decides the default Hz.
        bool ResolveManual(out int w, out int h, out int hz)
        {
            w = 0;
            h = 0;
            hz = 0;
            string sw = txtW.Text.Trim();
            string sh = txtH.Text.Trim();
            string shz = txtHz.Text.Trim();
            if (!Regex.IsMatch(sw, "^[0-9]+$") || !Regex.IsMatch(sh, "^[0-9]+$"))
            {
                MessageBox.Show(this, "가로와 세로는 숫자로 입력하세요.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            w = int.Parse(sw);
            h = int.Parse(sh);

            Screen found = null;
            List<int> rates = null;
            foreach (Screen s in OrderedScreens())
            {
                List<int> r = RefreshRates(s.DeviceName, w, h);
                if (r.Count > 0)
                {
                    found = s;
                    rates = r;
                    break;
                }
            }
            if (found == null)
            {
                MessageBox.Show(this, w + "x" + h + " 해상도가 없습니다.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }

            if (shz.Length == 0)
            {
                int scrHz = CurrentHz(found.DeviceName);
                hz = rates.Contains(scrHz) ? scrHz : MaxRate(rates);
            }
            else
            {
                if (!Regex.IsMatch(shz, "^[0-9]+$"))
                {
                    MessageBox.Show(this, "주사율은 숫자로 입력하세요.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return false;
                }
                hz = int.Parse(shz);
            }

            DEVMODE probe = NewDevMode();
            foreach (Screen s in OrderedScreens())
            {
                if (FindMode(s.DeviceName, w, h, hz, ref probe)) return true;
            }
            MessageBox.Show(this, w + "x" + h + "@" + hz + " 해상도가 없습니다.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        static List<int> RefreshRates(string device, int w, int h)
        {
            List<int> rates = new List<int>();
            DEVMODE dm = NewDevMode();
            for (int i = 0; EnumDisplaySettings(device, i, ref dm); i++)
            {
                if (dm.dmPelsWidth == w && dm.dmPelsHeight == h && !rates.Contains(dm.dmDisplayFrequency))
                    rates.Add(dm.dmDisplayFrequency);
            }
            return rates;
        }

        static int CurrentHz(string device)
        {
            DEVMODE dm = NewDevMode();
            return EnumDisplaySettings(device, -1, ref dm) ? dm.dmDisplayFrequency : 0; // -1 = ENUM_CURRENT_SETTINGS
        }

        static int MaxRate(List<int> rates)
        {
            int max = 0;
            foreach (int r in rates)
                if (r > max) max = r;
            return max;
        }
    }
}
