// Trickcal Revive launcher for Google Play Games (beta).
// 1) Remember the monitor's current mode, then switch to the requested resolution
//    (usually a custom resolution made in the GPU control panel)
// 2) Launch the game through its googleplaygames:// link
// 3) When the game window (crosvm.exe) shows up, close the Google Play Games launcher window (client.exe)
// 4) Switch the monitor back to the mode remembered in step 1
//
// Args:          --width <px> --height <px>   (the resolution to launch the game at;
//                without them the resolution is left as is and only steps 2-3 run)
// Optional args: --monitor <id> (default: primary monitor; a stable monitor ID such as
//                  DEL428C#5&168f06e6&4&UID4357, matched against the monitor's device ID, so it
//                  survives \\.\DISPLAYn renumbering)
//                --hz (default: the monitor's current refresh rate)
//                --settle 0 (extra seconds to wait after the game window appears)  --timeout 90
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class TrickcalLauncher
{
    const string GameUri = "googleplaygames://launch/?id=com.epidgames.trickcalrevive&lid=2&pid=1";
    // Google Play Games always installs its program here; only the data folder is user-chosen.
    const string Bootstrapper = @"C:\Program Files\Google\Play Games\Bootstrapper.exe";
    const string Title = "Trickcal Launcher";

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

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    const int DISPLAY_DEVICE_ACTIVE = 0x1;
    const uint EDD_GET_DEVICE_INTERFACE_NAME = 0x1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string dev, uint i, ref DISPLAY_DEVICE dd, uint flags);
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
        string device = Screen.PrimaryScreen.DeviceName;
        string monitor = null;
        int width = 0, height = 0, hz = 0, settle = 0, timeout = 90;
        for (int i = 0; i + 1 < args.Length; i += 2)
        {
            string v = args[i + 1];
            switch (args[i].ToLowerInvariant())
            {
                case "--monitor": monitor = v; break;
                case "--width": width = int.Parse(v); break;
                case "--height": height = int.Parse(v); break;
                case "--hz": hz = int.Parse(v); break;
                case "--settle": settle = int.Parse(v); break;
                case "--timeout": timeout = int.Parse(v); break;
            }
        }

        if (!string.IsNullOrEmpty(monitor))
        {
            device = FindMonitorDevice(monitor);
            if (device == null)
            {
                Error("연결된 모니터 중에 " + monitor + "를 찾지 못했습니다.\nbuild.ps1을 다시 실행해서 바로가기를 새로 만드세요.");
                return 1;
            }
        }

        // No resolution given: keep the current mode and only launch the game.
        bool change = width > 0 || height > 0;
        if (change && (width <= 0 || height <= 0))
        {
            Error("--width와 --height를 함께 지정하세요.\n예: TrickcalLauncher.exe --width 3840 --height 1728");
            return 1;
        }

        // The mode to come back to (a -> b -> a).
        DEVMODE original = NewDevMode();
        if (!EnumDisplaySettings(device, -1, ref original)) // -1 = ENUM_CURRENT_SETTINGS
        {
            Error(device + "의 현재 해상도를 읽지 못했습니다.");
            return 1;
        }
        original.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY;
        if (hz == 0) hz = original.dmDisplayFrequency;
        if (change && original.dmPelsWidth == width && original.dmPelsHeight == height && original.dmDisplayFrequency == hz)
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
                Process.Start(Bootstrapper, "\"" + GameUri + "\"");
            else
                Process.Start(new ProcessStartInfo(GameUri) { UseShellExecute = true });

            DateTime deadline = DateTime.Now.AddSeconds(timeout);
            while (DateTime.Now < deadline && FindWindow("crosvm", null) == IntPtr.Zero)
                Thread.Sleep(100);

            IntPtr game = FindWindow("crosvm", null);
            if (game == IntPtr.Zero)
            {
                message = timeout + "초 안에 게임 창이 뜨지 않았습니다." + (change ? " 해상도를 되돌렸습니다." : "");
                return 2;
            }

            // Google Play Games opens the game on the monitor it was last on, and the ratio follows that monitor.
            string gameScreen = Screen.FromHandle(game).DeviceName;
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

    // \\.\DISPLAYn of the active monitor whose device ID contains the given monitor ID, or null.
    static string FindMonitorDevice(string monitorId)
    {
        for (uint i = 0; i < 64; i++)
        {
            DISPLAY_DEVICE adapter = NewDisplayDevice();
            if (!EnumDisplayDevices(null, i, ref adapter, 0)) break;
            if ((adapter.StateFlags & DISPLAY_DEVICE_ACTIVE) == 0) continue;
            for (uint j = 0; j < 16; j++)
            {
                DISPLAY_DEVICE mon = NewDisplayDevice();
                if (!EnumDisplayDevices(adapter.DeviceName, j, ref mon, EDD_GET_DEVICE_INTERFACE_NAME)) break;
                if ((mon.StateFlags & DISPLAY_DEVICE_ACTIVE) != 0 &&
                    mon.DeviceID != null && mon.DeviceID.IndexOf(monitorId, StringComparison.OrdinalIgnoreCase) >= 0)
                    return adapter.DeviceName;
            }
        }
        return null;
    }

    static DISPLAY_DEVICE NewDisplayDevice()
    {
        DISPLAY_DEVICE dd = new DISPLAY_DEVICE();
        dd.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE));
        return dd;
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
