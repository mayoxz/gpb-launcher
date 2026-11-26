# Builds one exe per game, named after the game, with the game's icon embedded, using the
# .NET Framework compiler that ships with Windows. It asks for the game (from the Google Play
# Games Start Menu shortcuts) and the target resolution (width x height @ refresh rate), and
# creates a desktop shortcut that runs that exe.
# Saved as UTF-8 with BOM so Windows PowerShell 5.1 reads the Korean text correctly.
$ErrorActionPreference = 'Stop'

try {
    $here = $PSScriptRoot
    $csc  = @("$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe", "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe") |
            Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $csc) { throw 'csc.exe(.NET Framework 4)를 찾을 수 없습니다.' }

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
}
'@

    $device = [System.Windows.Forms.Screen]::PrimaryScreen.DeviceName

    # --- Game choice: Google Play Games puts one Start Menu shortcut per installed game.
    # The shortcut's link target carries the package name, and its icon is embedded into our exe.
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
    # The current mode always comes first as 1).
    $list = @(@($all | Where-Object { Test-Current $_ } | Select-Object -First 1) + @($list | Where-Object { -not (Test-Current $_) }))
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
            if ($iw -notmatch '^\d+$' -or $ih -notmatch '^\d+$') { throw '가로와 세로는 숫자로 입력하세요.' }

            # Same order as the launcher: primary monitor first, then the others.
            # The first monitor that has this width x height decides the default refresh rate.
            $screens = @([System.Windows.Forms.Screen]::AllScreens | Sort-Object { -not $_.Primary })
            $found = $null
            foreach ($s in $screens) {
                $hzs = @([DisplayModes]::Modes($s.DeviceName) | Where-Object { $_ -like "${iw}x${ih}@*" } | ForEach-Object { [int](($_ -split '@')[1]) })
                if ($hzs.Count -gt 0) { $found = [pscustomobject]@{ Device = $s.DeviceName; Hzs = $hzs }; break }
            }
            if (-not $found) { throw "${iw}x${ih} 해상도가 없습니다." }
            $scrHz = [DisplayModes]::Current($found.Device)[2]
            $defHz = if ($found.Hzs -contains $scrHz) { $scrHz } else { ($found.Hzs | Measure-Object -Maximum).Maximum }

            $ihz = (Read-Host "주사율 [$defHz]").Trim(); if (-not $ihz) { $ihz = "$defHz" }
            $pick = $null
            if ($ihz -match '^\d+$') {
                $target = "${iw}x${ih}@${ihz}"
                foreach ($s in $screens) {
                    if ([DisplayModes]::Modes($s.DeviceName) -contains $target) { $pick = [pscustomobject]@{ W = [int]$iw; H = [int]$ih; Hz = [int]$ihz }; break }
                }
            }
            if (-not $pick) { throw "${iw}x${ih}@${ihz} 해상도가 없습니다." }
        }
        else { $pick = $list[[int]$ans - 1] }
        $same = Test-Current $pick
        if ($same) {
            $yn = (Read-Host '현재 해상도와 같습니다. 이 해상도로 바로가기를 만들까요? (Y/N)').Trim()
            if ($yn -notmatch '^[Yy]') { continue }
        }
        $w = $pick.W; $h = $pick.H; $hz = $pick.Hz

        Write-Host ''
        Write-Host "  게임             : $($game.Name)"
        Write-Host "  게임 실행 해상도 : ${w}x${h}@${hz} ($(Format-Ratio $w $h)$(if ($same) { ', 현재와 같음' }))"
        Write-Host "  되돌아갈 해상도  : 실행할 때의 해상도 (지금은 ${curW}x${curH}@${curHz})"
        Write-Host "  바로가기         : 이 폴더\${safeName} (${w}x${h}@${hz})"
        $ok = (Read-Host '맞습니까? (Y/N)').Trim()
        if ($ok -match '^[Yy]') { break }
    }

    # --- Build: one exe per game, named after the game, with the game's icon embedded.
    $exe = Join-Path $here "$safeName.exe"
    $cscArgs = @('/nologo', '/target:winexe', '/optimize+', '/codepage:65001', "/out:$exe", '/r:System.Windows.Forms.dll')
    if ($game.Icon -and (Test-Path $game.Icon)) { $cscArgs += "/win32icon:$($game.Icon)" }
    else { Write-Host '게임 아이콘을 찾지 못해 아이콘 없이 빌드합니다.' -ForegroundColor Yellow }
    # File description (shown in Task Manager and file properties), generated per game.
    $desc = ("$($game.Name) 화면비 런처 (GPB Launcher)").Replace('\', '\\').Replace('"', '\"')
    $infoCs = Join-Path ([IO.Path]::GetTempPath()) "gpb-launcher-info-$PID.cs"
    $info = "using System.Reflection;`n" +
            "[assembly: AssemblyTitle(""$desc"")]`n" +
            "[assembly: AssemblyProduct(""GPB Launcher"")]`n"
    [IO.File]::WriteAllText($infoCs, $info, (New-Object Text.UTF8Encoding $true))
    $cscArgs += (Join-Path $here 'GpbLauncher.cs')
    $cscArgs += $infoCs
    try { & $csc $cscArgs } finally { Remove-Item $infoCs -ErrorAction SilentlyContinue }
    if ($LASTEXITCODE -ne 0) { throw "빌드 실패 (csc 코드 $LASTEXITCODE)" }

    # --- Shortcut in this folder: runs this game's exe and takes the icon embedded in it.
    $lnkPath = Join-Path $here "${safeName} (${w}x${h}@${hz}).lnk"
    $sh = New-Object -ComObject WScript.Shell
    $lnk = $sh.CreateShortcut($lnkPath)
    $lnk.TargetPath = $exe
    $lnk.Arguments = "--game $($game.Package) ${w}x${h}@${hz}"
    $lnk.WorkingDirectory = $here
    $lnk.IconLocation = "$exe,0"
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
