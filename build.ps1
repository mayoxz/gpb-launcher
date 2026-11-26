# Builds GpbLauncher.exe with the .NET Framework compiler that ships with Windows,
# asks for the monitor, the game (from the Google Play Games Start Menu shortcuts), and the
# target resolution (width x height @ refresh rate), and creates a desktop shortcut that uses them.
# Saved as UTF-8 with BOM so Windows PowerShell 5.1 reads the Korean text correctly.
$ErrorActionPreference = 'Stop'

try {
    $here = $PSScriptRoot
    $csc  = @("$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe", "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe") |
            Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $csc) { throw 'csc.exe(.NET Framework 4)를 찾을 수 없습니다.' }
    $exe  = Join-Path $here 'GpbLauncher.exe'

    Add-Type -AssemblyName System.Windows.Forms
    Add-Type @'
using System; using System.Runtime.InteropServices;
public static class DisplayModes {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DEVMODE {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra; public int dmFields;
        public int dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels; public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string dev, int i, ref DEVMODE dm);
    static DEVMODE New() { var d = new DEVMODE(); d.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE)); return d; }
    public static int[] Current(string dev) { var d = New(); EnumDisplaySettings(dev, -1, ref d); return new[] { d.dmPelsWidth, d.dmPelsHeight, d.dmDisplayFrequency }; }
    // Every distinct width x height @ refresh rate the monitor offers, as "WxH@Hz".
    public static System.Collections.Generic.List<string> Modes(string dev) {
        var r = new System.Collections.Generic.List<string>(); var d = New();
        for (int i = 0; EnumDisplaySettings(dev, i, ref d); i++) {
            string s = d.dmPelsWidth + "x" + d.dmPelsHeight + "@" + d.dmDisplayFrequency; if (!r.Contains(s)) r.Add(s);
        }
        return r;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAY_DEVICE {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string dev, uint i, ref DISPLAY_DEVICE dd, uint flags);
    static DISPLAY_DEVICE NewDD() { var d = new DISPLAY_DEVICE(); d.cb = Marshal.SizeOf(typeof(DISPLAY_DEVICE)); return d; }
    // Active monitors as "\.\DISPLAYn|monitor name|stable id|1 if primary".
    // The stable id is the part of the device path between "DISPLAY#" and "#{", e.g. DEL428C#5&168f06e6&4&UID4357.
    public static System.Collections.Generic.List<string> Monitors() {
        var r = new System.Collections.Generic.List<string>();
        for (uint i = 0; i < 64; i++) {
            var a = NewDD(); if (!EnumDisplayDevices(null, i, ref a, 0)) break;
            if ((a.StateFlags & 1) == 0) continue;
            for (uint j = 0; j < 16; j++) {
                var m = NewDD(); if (!EnumDisplayDevices(a.DeviceName, j, ref m, 1)) break;
                if ((m.StateFlags & 1) == 0 || string.IsNullOrEmpty(m.DeviceID)) continue;
                string id = m.DeviceID;
                int s = id.IndexOf("DISPLAY#", StringComparison.OrdinalIgnoreCase); if (s >= 0) id = id.Substring(s + 8);
                int e = id.IndexOf("#{"); if (e >= 0) id = id.Substring(0, e);
                r.Add(a.DeviceName + "|" + m.DeviceString + "|" + id + "|" + ((a.StateFlags & 4) != 0 ? "1" : "0"));
                break;
            }
        }
        return r;
    }
}
'@

    # --- Monitor choice. The shortcut stores the stable monitor id, not \.\DISPLAYn, which can be renumbered.
    $mons = @([DisplayModes]::Monitors() | ForEach-Object {
        $p = $_ -split '\|'; $c = [DisplayModes]::Current($p[0])
        [pscustomobject]@{ Device = $p[0]; Name = $p[1]; Id = $p[2]; Primary = ($p[3] -eq '1'); Mode = "$($c[0])x$($c[1])@$($c[2])" }
    } | Sort-Object @{ e = 'Primary'; Descending = $true })
    if ($mons.Count -eq 0) { throw '연결된 모니터를 찾지 못했습니다.' }
    if ($mons.Count -eq 1) {
        $mon = $mons[0]
        Write-Host "모니터      : $($mon.Name)"
    }
    else {
        Write-Host '모니터:'
        for ($i = 0; $i -lt $mons.Count; $i++) {
            $m = $mons[$i]
            Write-Host ('  {0,3})  {1}  {2}{3}' -f ($i + 1), $m.Name, $m.Mode, $(if ($m.Primary) { '  (주 모니터)' } else { '' }))
        }
        while ($true) {
            $ans = (Read-Host '모니터 번호 (엔터 = 주 모니터)').Trim()
            if (-not $ans) { $mon = $mons[0]; break }
            if ($ans -match '^\d+$' -and [int]$ans -ge 1 -and [int]$ans -le $mons.Count) { $mon = $mons[[int]$ans - 1]; break }
            Write-Host '목록의 번호를 입력하세요.' -ForegroundColor Yellow
        }
        Write-Host ''
    }
    $device = $mon.Device

    # --- Game choice: Google Play Games puts one Start Menu shortcut per installed game.
    # The shortcut's link target carries the package name, and its icon can be copied for our own shortcut.
    $games = @()
    $dirs = @(
        (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Google Play Games'),
        (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Google Play Games')
    )
    foreach ($dir in $dirs) {
        if (-not (Test-Path $dir)) { continue }
        foreach ($f in Get-ChildItem $dir -Filter '*.lnk' -ErrorAction SilentlyContinue) {
            $raw = [Text.Encoding]::Unicode.GetString([IO.File]::ReadAllBytes($f.FullName))
            $m = [regex]::Match($raw, 'googleplaygames://launch/\?id=([A-Za-z0-9._]+)')
            if (-not $m.Success) { continue }
            $pkg = $m.Groups[1].Value
            if ($games | Where-Object { $_.Package -eq $pkg }) { continue }
            $im = [regex]::Match($raw, '[A-Za-z]:\\[^\x00]*?\.ico')
            $ico = if ($im.Success) { $im.Value } else { $null }
            $games += [pscustomobject]@{ Package = $pkg; Name = $f.BaseName; Icon = $ico }
        }
    }
    $games = @($games | Sort-Object Name)
    if ($games.Count -eq 0) { throw '구플베 게임 바로가기를 찾지 못했습니다. 구플베에서 게임을 설치하면 시작 메뉴에 바로가기가 생깁니다.' }
    if ($games.Count -eq 1) {
        $game = $games[0]
        Write-Host "게임        : $($game.Name)"
    }
    else {
        Write-Host '게임:'
        for ($i = 0; $i -lt $games.Count; $i++) {
            Write-Host ('  {0,3})  {1}' -f ($i + 1), $games[$i].Name)
        }
        while ($true) {
            $ans = (Read-Host '게임 번호').Trim()
            if ($ans -match '^\d+$' -and [int]$ans -ge 1 -and [int]$ans -le $games.Count) { $game = $games[[int]$ans - 1]; break }
            Write-Host '목록의 번호를 입력하세요.' -ForegroundColor Yellow
        }
        Write-Host ''
    }
    $safeName = $game.Name
    foreach ($c in [IO.Path]::GetInvalidFileNameChars()) { $safeName = $safeName.Replace([string]$c, '') }

    # --- Resolution choice (width x height @ refresh rate).
    # The launcher itself remembers the current mode at run time and returns to it (a -> b -> a).
    $cur = [DisplayModes]::Current($device)
    $curW = $cur[0]; $curH = $cur[1]; $curHz = $cur[2]

    # Ratio as "x:9" (20:9, 21.33:9, 20.5:9), never reduced to odd pairs like 41:18.
    function Format-Ratio([int]$w, [int]$h) { ([math]::Round($w * 9.0 / $h, 2)).ToString('0.##', [Globalization.CultureInfo]::InvariantCulture) + ':9' }

    $all = [DisplayModes]::Modes($device) | ForEach-Object {
        $p = $_ -split '[x@]'; [pscustomobject]@{ W = [int]$p[0]; H = [int]$p[1]; Hz = [int]$p[2] }
    } | Sort-Object @{ e = 'W'; Descending = $true }, @{ e = 'H'; Descending = $true }, @{ e = 'Hz'; Descending = $true }
    function Test-Current($m) { $m.W -eq $curW -and $m.H -eq $curH -and $m.Hz -eq $curHz }

    Write-Host "현재 해상도 : ${curW}x${curH}@${curHz} ($(Format-Ratio $curW $curH))" -ForegroundColor Cyan

    # One line per resolution, at its highest refresh rate ($all is sorted Hz-descending within each size).
    # Other refresh rates go through manual entry.
    $list = @($all | Where-Object { $_.W -eq $curW -or $_.H -eq $curH } |
              Group-Object { "$($_.W)x$($_.H)" } | ForEach-Object { $_.Group[0] })
    while ($true) {
        Write-Host ''
        Write-Host "바꿀 해상도:"
        for ($i = 0; $i -lt $list.Count; $i++) {
            $m = $list[$i]
            $info = if (Test-Current $m) { "($(Format-Ratio $m.W $m.H), 현재)" } else { "($(Format-Ratio $m.W $m.H))" }
            Write-Host ('  {0,3}) {1,5} x {2,-5} @{3,-4} {4}' -f ($i + 1), $m.W, $m.H, $m.Hz, $info)
        }
        $manual = $list.Count + 1
        Write-Host ('  {0,3})  직접 입력' -f $manual)
        $ans = (Read-Host '번호 입력').Trim()
        if ($ans -notmatch '^\d+$' -or [int]$ans -lt 1 -or [int]$ans -gt $manual) { Write-Host '목록의 번호를 입력하세요.' -ForegroundColor Yellow; continue }
        if ([int]$ans -eq $manual) {
            $iw = (Read-Host '가로').Trim()
            $ih = (Read-Host '세로').Trim()
            $ihz = (Read-Host "주사율 [$curHz]").Trim(); if (-not $ihz) { $ihz = "$curHz" }
            $pick = $null
            if ($iw -match '^\d+$' -and $ih -match '^\d+$' -and $ihz -match '^\d+$') {
                $pick = $all | Where-Object { $_.W -eq [int]$iw -and $_.H -eq [int]$ih -and $_.Hz -eq [int]$ihz } | Select-Object -First 1
            }
            if (-not $pick) { throw "${iw}x${ih}@${ihz} 해상도가 이 모니터에 없습니다. (GPU 제어판에서 사용자 지정 해상도를 먼저 만드세요)" }
            if (Test-Current $pick) { throw '현재 해상도와 같습니다.' }
        }
        else { $pick = $list[[int]$ans - 1] }
        if (Test-Current $pick) { Write-Host '현재 해상도와 같습니다. 다른 해상도를 고르세요.' -ForegroundColor Yellow; continue }
        $w = $pick.W; $h = $pick.H; $hz = $pick.Hz

        Write-Host ''
        Write-Host "  게임             : $($game.Name)"
        Write-Host "  모니터           : $($mon.Name)"
        Write-Host "  게임 실행 해상도 : ${w}x${h}@${hz} ($(Format-Ratio $w $h))"
        Write-Host "  되돌아갈 해상도  : 실행할 때의 해상도 (지금은 ${curW}x${curH}@${curHz})"
        Write-Host "  바로가기         : 바탕화면\${safeName} (${w}x${h}@${hz})"
        $ok = (Read-Host '맞습니까? (Y/N)').Trim()
        if ($ok -match '^[Yy]') { break }
    }

    # --- Build
    & $csc /nologo /target:winexe /optimize+ /codepage:65001 "/out:$exe" /r:System.Windows.Forms.dll (Join-Path $here 'GpbLauncher.cs')
    if ($LASTEXITCODE -ne 0) { throw "빌드 실패 (csc 코드 $LASTEXITCODE)" }

    # --- Shortcut icon: the game's icon copied from its Start Menu shortcut, if it has one.
    $iconLocation = "$exe,0"
    if ($game.Icon -and (Test-Path $game.Icon)) {
        $iconsDir = Join-Path $here 'icons'
        if (-not (Test-Path $iconsDir)) { New-Item -ItemType Directory -Path $iconsDir | Out-Null }
        $iconFile = Join-Path $iconsDir "$($game.Package).ico"
        Copy-Item $game.Icon $iconFile -Force
        $iconLocation = "$iconFile,0"
    }

    # --- Desktop shortcut
    $lnkPath = Join-Path ([Environment]::GetFolderPath('Desktop')) "${safeName} (${w}x${h}@${hz}).lnk"
    $sh = New-Object -ComObject WScript.Shell
    $lnk = $sh.CreateShortcut($lnkPath)
    $lnk.TargetPath = $exe
    $lnk.Arguments = "--game $($game.Package) --width $w --height $h --hz $hz"
    if ($mons.Count -gt 1) { $lnk.Arguments += " --monitor $($mon.Id)" }
    $lnk.WorkingDirectory = $here
    $lnk.IconLocation = $iconLocation
    $lnk.Description = "${w}x${h}@${hz} 화면으로 실행"
    $lnk.Save()

    Write-Host ''
    Write-Host "완료: $exe" -ForegroundColor Green
    Write-Host "바로가기: $lnkPath" -ForegroundColor Green
}
catch {
    Write-Host ''
    Write-Host "오류: $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    Read-Host '엔터를 누르면 창이 닫힙니다'
}
