// Google Play Games (beta) aspect-ratio launcher.
// 1) Remember the monitor's current mode, then switch to the requested resolution
//    (usually a custom resolution made in the GPU control panel)
// 2) Launch the game through its googleplaygames:// link
// 3) When the game window (crosvm.exe) shows up, close the Google Play Games launcher window (client.exe)
// 4) Switch the monitor back to the mode remembered in step 1
//
// Args:          --game <package>            (required; the game's package name, e.g. com.example.game)
//                <W>x<H>[@Hz]                 (the resolution to launch the game at, e.g. 3840x1728@144;
//                also --width <px> --height <px> --hz <rate>; without them the resolution is left as is
//                and only steps 2-3 run)
//                The monitor that supports the resolution is found automatically: the primary monitor
//                is checked first, then the other monitors.
// Optional args: --settle 0 (extra seconds to wait after the game window appears)  --timeout 90
using System;
using System.Diagnostics;
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

        if (string.IsNullOrEmpty(game))
        {
            // Opened directly (e.g. double-clicked) instead of through a shortcut made by build.ps1.
            Error(NoShortcutMessage());
            return 1;
        }
        if (!Regex.IsMatch(game, "^[A-Za-z0-9._]+$"))
        {
            Error("바로가기의 게임 이름(--game)이 올바르지 않습니다: " + game + "\nbuild.ps1을 실행해서 바로가기를 다시 만드세요.");
            return 1;
        }
        string uri = "googleplaygames://launch/?id=" + game + "&lid=1&pid=1";

        // No resolution given: keep the current mode and only launch the game.
        bool change = width > 0 || height > 0;
        if (change && (width <= 0 || height <= 0))
        {
            Error("--width와 --height를 함께 지정하세요.\n예: GpbLauncher.exe --game com.example.game 3840x1728@144");
            return 1;
        }

        // Find a monitor that supports the mode: the primary monitor first, then the others.
        if (change)
        {
            Screen primary = Screen.PrimaryScreen;
            Screen[] screens = Screen.AllScreens;
            Screen[] ordered = new Screen[screens.Length];
            int n = 0;
            ordered[n++] = primary;
            foreach (Screen s in screens)
                if (!string.Equals(s.DeviceName, primary.DeviceName, StringComparison.OrdinalIgnoreCase))
                    ordered[n++] = s;

            bool found = false;
            foreach (Screen s in ordered)
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

    // Explains that this exe runs through its shortcuts, and lists the ones next to it
    // (build.ps1 names them "<exe name> (WxH@Hz).lnk").
    static string NoShortcutMessage()
    {
        string exePath = Application.ExecutablePath;
        string dir = System.IO.Path.GetDirectoryName(exePath);
        string baseName = System.IO.Path.GetFileNameWithoutExtension(exePath);
        StringBuilder list = new StringBuilder();
        try
        {
            foreach (string lnk in System.IO.Directory.GetFiles(dir, baseName + " (*).lnk"))
                list.Append("  ").Append(System.IO.Path.GetFileNameWithoutExtension(lnk)).Append('\n');
        }
        catch (Exception) { }

        string msg = "이 파일은 직접 실행하지 않고, 해상도가 지정된 바로가기로 실행합니다.\n\n";
        if (list.Length > 0)
            msg += "같은 폴더의 바로가기를 실행하세요:\n" + list + "\n다른 해상도가 필요하면 build.ps1로 바로가기를 새로 만드세요.";
        else
            msg += "build.ps1을 실행해서 바로가기를 만드세요.";
        return msg;
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
}
