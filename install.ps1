# Установка / переустановка InteractiveMenu.
#   irm https://raw.githubusercontent.com/Reuz0201/InteractiveMenu/main/install.ps1 | iex
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repo  = 'Reuz0201/InteractiveMenu'
$asset = 'InteractiveMenu-win-x64.zip'
$dir   = Join-Path $env:LOCALAPPDATA 'Programs\InteractiveMenu'

Write-Host ''
Write-Host '  InteractiveMenu' -ForegroundColor Magenta
Write-Host ''

# --- .NET Desktop Runtime 8+
$hasRuntime = $false
try {
    $runtimes = & dotnet --list-runtimes 2>$null
    $hasRuntime = [bool]($runtimes | Where-Object { $_ -match '^Microsoft\.WindowsDesktop\.App (\d+)\.' -and [int]$Matches[1] -ge 8 })
} catch { }

if (-not $hasRuntime) {
    Write-Host '  Нужен .NET Desktop Runtime 8 — ставлю через winget...' -ForegroundColor Yellow
    try {
        winget install --id Microsoft.DotNet.DesktopRuntime.8 -e --accept-source-agreements --accept-package-agreements --silent
    } catch {
        Write-Host '  Не получилось. Скачай вручную: https://dotnet.microsoft.com/download/dotnet/8.0' -ForegroundColor Red
        return
    }
}

# --- последний релиз
Write-Host '  Ищу последнюю версию...'
$release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'InteractiveMenu-Installer' }
$file = $release.assets | Where-Object { $_.name -eq $asset } | Select-Object -First 1
if (-not $file) { throw "В последнем релизе нет $asset" }

$zip = Join-Path $env:TEMP 'InteractiveMenu-install.zip'
Write-Host "  Качаю $($release.tag_name)..."
Invoke-WebRequest $file.browser_download_url -OutFile $zip -UseBasicParsing

# --- закрываем запущенное приложение
foreach ($proc in @(Get-Process InteractiveMenu -ErrorAction SilentlyContinue)) {
    try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop } catch {
        Write-Host '  InteractiveMenu запущен от администратора — закрой его (трей → Выход) и запусти установку ещё раз.' -ForegroundColor Red
        return
    }
}
Start-Sleep -Milliseconds 500

# --- распаковка
New-Item -ItemType Directory -Force -Path $dir | Out-Null
Expand-Archive -Path $zip -DestinationPath $dir -Force
Remove-Item $zip -ErrorAction SilentlyContinue

# --- ярлык в меню «Пуск»
$programs = [Environment]::GetFolderPath('Programs')
$shell = New-Object -ComObject WScript.Shell
$lnk = $shell.CreateShortcut((Join-Path $programs 'InteractiveMenu.lnk'))
$lnk.TargetPath = Join-Path $dir 'InteractiveMenu.exe'
$lnk.WorkingDirectory = $dir
$lnk.Save()

Write-Host "  Установлено в $dir" -ForegroundColor Green
Write-Host '  Запускаю...'
Start-Process (Join-Path $dir 'InteractiveMenu.exe')
Write-Host ''
Write-Host '  Готово! Зажми Alt + Q, чтобы открыть меню. Настройки — двойной клик по иконке в трее.' -ForegroundColor Green
Write-Host ''
