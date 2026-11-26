# Builds one exe per game, named after the game, with the game's icon embedded, using the
# .NET Framework compiler that ships with Windows. It asks for the game (from the Google Play
# Games Start Menu shortcuts) and embeds that game's package name into the exe, so the exe can
# create its own resolution shortcuts when run directly.
# Saved as UTF-8 with BOM so Windows PowerShell 5.1 reads the Korean text correctly.
$ErrorActionPreference = 'Stop'

try {
    $here = $PSScriptRoot
    $csc  = @("$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe", "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe") |
            Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $csc) { throw 'csc.exe(.NET Framework 4)를 찾을 수 없습니다.' }

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

    # --- Build: one exe per game, named after the game, with the game's icon embedded.
    $exe = Join-Path $here "$safeName.exe"
    Write-Host ''
    Write-Host "  게임 : $($game.Name)"
    Write-Host "  만들 파일 : 이 폴더\$safeName.exe"
    $ok = (Read-Host '빌드할까요? (Y/N)').Trim()
    if ($ok -notmatch '^[Yy]') {
        Write-Host '취소했습니다.'
        return
    }

    $cscArgs = @('/nologo', '/target:winexe', '/optimize+', '/codepage:65001', "/out:$exe", '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll')
    if ($game.Icon -and (Test-Path $game.Icon)) { $cscArgs += "/win32icon:$($game.Icon)" }
    else { Write-Host '게임 아이콘을 찾지 못해 아이콘 없이 빌드합니다.' -ForegroundColor Yellow }
    # File description (shown in Task Manager and file properties), generated per game.
    # The package name is embedded too, so the exe can create its own resolution shortcuts.
    $desc = ("$($game.Name) 화면비 런처 (GPB Launcher)").Replace('\', '\\').Replace('"', '\"')
    $infoCs = Join-Path ([IO.Path]::GetTempPath()) "gpb-launcher-info-$PID.cs"
    $info = "using System.Reflection;`n" +
            "[assembly: AssemblyTitle(""$desc"")]`n" +
            "[assembly: AssemblyProduct(""GPB Launcher"")]`n" +
            "[assembly: AssemblyMetadata(""GpbGame"", ""$($game.Package)"")]`n"
    [IO.File]::WriteAllText($infoCs, $info, (New-Object Text.UTF8Encoding $true))
    $cscArgs += (Join-Path $here 'GpbLauncher.cs')
    $cscArgs += $infoCs
    try { & $csc $cscArgs } finally { Remove-Item $infoCs -ErrorAction SilentlyContinue }
    if ($LASTEXITCODE -ne 0) { throw "빌드 실패 (csc 코드 $LASTEXITCODE)" }

    Write-Host ''
    Write-Host "완료: $exe" -ForegroundColor Green
    Write-Host 'exe를 실행해서 바로가기를 만드세요.'
}
catch {
    Write-Host ''
    Write-Host "오류: $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    Read-Host '엔터를 누르면 창이 닫힙니다'
}
