param(
    [string]$Out = ""
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$panelProj = Join-Path $root "ForgePanel\ForgePanel.csproj"
$setupProj = Join-Path $root "ForgePanel.Setup\ForgePanel.Setup.csproj"
$payloadDir = Join-Path $root "ForgePanel\bin\SetupPayload"
$setupBin = Join-Path $root "ForgePanel.Setup\bin\Release\net472\ForgePanel-Setup.exe"
if (-not $Out) { $Out = Join-Path $root "dist\ForgePanel-Setup.exe" }

# Путь к MSBuild зависит от версии Visual Studio, поэтому перебираем оба расположения
# и все редакции. vswhere на этой машине отдаёт пустой ответ на нужный шаблон, так что
# он здесь только запасной вариант.
function Find-MsBuild
{
    $patterns = @(
        "C:\Program Files\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\amd64\MSBuild.exe",
        "C:\Program Files (x86)\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\amd64\MSBuild.exe"
    )
    foreach ($pattern in $patterns)
    {
        $hit = Get-ChildItem -Path $pattern -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }

    $vswhere = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
    if (Test-Path $vswhere)
    {
        $found = & $vswhere -latest -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\MSBuild.exe"
        if ($found) { return ($found -split "`r?`n")[0] }
    }

    throw "MSBuild не найден. Запустите скрипт из «Developer PowerShell for Visual Studio» или установите Visual Studio с компонентом MSBuild."
}

$msbuild = Find-MsBuild
Write-Host "MSBuild: $msbuild"

Write-Host ""
Write-Host "[1/3] Публикую панель (self-contained .NET 8)..."
# dotnet publish здесь не подходит: на этой машине он падает внутри NuGet
# («Value cannot be null. (Parameter 'path1')»), а MSBuild с /t:Restore;Publish работает.
& $msbuild $panelProj /t:"Restore;Publish" /p:Configuration=Release /p:RuntimeIdentifier=win-x64 `
    /p:SelfContained=true /p:PublishSingleFile=false /p:DebugType=none /p:DebugSymbols=false `
    "/p:PublishDir=$payloadDir\" /nologo /v:minimal
if ($LASTEXITCODE -ne 0) { throw "Публикация панели не удалась (код $LASTEXITCODE)." }

Write-Host ""
Write-Host "[2/3] Собираю мастер (net472, он же деинсталлятор)..."
& $msbuild $setupProj /t:Rebuild /p:Configuration=Release /nologo /v:minimal
if ($LASTEXITCODE -ne 0) { throw "Сборка мастера не удалась (код $LASTEXITCODE)." }

Write-Host ""
Write-Host "[3/3] Упаковываю payload внутрь мастера..."
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Out) | Out-Null
if (Test-Path $Out) { Remove-Item $Out -Force }

# Мастер — приложение без консоли, поэтому нужен Start-Process с -Wait: иначе PowerShell
# не дождётся упаковки и проверит не тот код возврата.
$pack = Start-Process -FilePath $setupBin -ArgumentList @(
    "/pack", "/payload", $payloadDir, "/stub", $setupBin, "/out", $Out, "/exclude", "*.pdb"
) -Wait -PassThru
if ($pack.ExitCode -ne 0) { throw "Упаковка не удалась (код $($pack.ExitCode)). Подробности в '$Out.log'." }

$log = "$Out.log"
if (Test-Path $log) { Get-Content $log -Encoding UTF8 | Write-Host }

$size = (Get-Item $Out).Length / 1MB
Write-Host ""
Write-Host ("Готово: {0}  ({1:N1} МБ)" -f $Out, $size)
Write-Host "Скрипт ничего не устанавливает: мастер запускается двойным кликом по этому файлу."
