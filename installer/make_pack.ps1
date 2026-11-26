param(
    [string]$Root = '',
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
if (-not $OutDir) { $OutDir = Split-Path -Parent $here }

try {
    Add-Type -Path (Join-Path $here 'Coop.cs'), (Join-Path $here 'Safety.cs'), (Join-Path $here 'Finder.cs') -ReferencedAssemblies 'System.Core', 'System.Web.Extensions', 'System.IO.Compression', 'System.IO.Compression.FileSystem'
    [GammaCoop.Finder]::AppDir = $here

    if (-not $Root) {
        Write-Host 'Ищу папку с игрой (там, где лежат GAMMA и ANOMALY)...'
        $roots = @([GammaCoop.Finder]::FindGammaRoots())
        if ($roots.Count -eq 1) {
            $Root = $roots[0]
        }
        elseif ($roots.Count -gt 1) {
            for ($i = 0; $i -lt $roots.Count; $i++) { Write-Host ("  [{0}] {1}" -f ($i + 1), $roots[$i]) }
            $n = Read-Host 'Какую взять? Номер'
            $idx = 0
            if ([int]::TryParse($n, [ref]$idx) -and $idx -ge 1 -and $idx -le $roots.Count) { $Root = $roots[$idx - 1] }
            else { $Root = $roots[0] }
        }
        else {
            $ans = Read-Host 'Не нашёл. Папка с игрой хоста (там лежат GAMMA и ANOMALY)'
            $Root = $ans.Trim().Trim('"')
        }
    }
    if (-not [GammaCoop.Finder]::IsGammaRoot($Root)) { throw "В папке '$Root' нет GAMMA\ModOrganizer.exe и ANOMALY\bin. Укажи папку, где лежат GAMMA и ANOMALY." }
    if (-not (Test-Path -LiteralPath $OutDir)) { New-Item -ItemType Directory -Path $OutDir | Out-Null }

    $out = Join-Path $OutDir ([GammaCoop.Coop]::NewBuildName())
    Write-Host "Беру файлы из: $Root (только чтение)"
    Write-Host "Собираю сборку: $out"
    $log = [Action[string]]{ param($s) Write-Host $s }
    $res = [GammaCoop.Coop]::Build($Root, $out, $log, (Join-Path $here 'passport')).Split('|')
    $size = (Get-Item -LiteralPath $out).Length
    $info = [GammaCoop.Coop]::Probe($out)
    Write-Host ''
    Write-Host ("Готово: {0} файлов, {1:N1} МБ в архиве." -f $res[0], ($size / 1MB)) -ForegroundColor Green
    Write-Host ("Моды в сборке ({0}): {1}" -f $info.Mods.Count, ($info.Mods -join ', '))
    Write-Host ("Отправь друзьям файл {0} (например через LocalSend)." -f (Split-Path -Leaf $out))
    exit 0
}
catch {
    Write-Host ''
    Write-Host 'Не получилось собрать сборку:' -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Yellow
    exit 1
}
