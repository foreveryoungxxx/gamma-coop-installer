param(
    [switch]$Headless,
    [int]$Frames = 0,
    [switch]$Simulate
)

$root = $PSScriptRoot
$code = 0
try {
    $src = 'Tui.cs', 'Engine.cs', 'Coop.cs', 'InstallUi.cs', 'Safety.cs', 'Progress.cs', 'Finder.cs', 'Hub.cs', 'Integrity.cs', 'UpdateGuard.cs', 'SimTests.cs' | ForEach-Object { Join-Path $root $_ }
    Add-Type -Path $src -ReferencedAssemblies 'System.Core', 'System.Management', 'System.Web.Extensions', 'System.IO.Compression', 'System.IO.Compression.FileSystem', 'System.Windows.Forms'
    $code = [GammaCoop.App]::Run($root, [bool]$Headless, $Frames, [bool]$Simulate)
}
catch {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $logs = Join-Path $root 'logs'
    if (-not (Test-Path $logs)) { New-Item -ItemType Directory -Path $logs | Out-Null }
    $logFile = Join-Path $logs "gamma_coop_$stamp.log"
    $text = ($_ | Out-String) + "`r`n" + $_.ScriptStackTrace
    [System.IO.File]::AppendAllText($logFile, "$(Get-Date -Format 'HH:mm:ss.fff')  ОШИБКА: $text`r`n", (New-Object System.Text.UTF8Encoding($true)))
    Write-Host ''
    Write-Host 'Ой, установщик упал с ошибкой:' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Yellow
    Write-Host "Подробности записаны в файл: $logFile"
    $code = 1
    if (-not $Headless) {
        Write-Host 'Нажми любую клавишу, чтобы закрыть окно...'
        try { $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown') } catch { Start-Sleep -Seconds 10 }
    }
}
exit $code
