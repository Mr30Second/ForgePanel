$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "ForgePanel\ForgePanel.csproj"
if (-not (Test-Path $project)) { throw "Project file not found: $project" }
dotnet publish $project -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
if ($LASTEXITCODE -ne 0) { throw "Build failed." }
$exe = Join-Path $root "ForgePanel\bin\Release\net8.0-windows\win-x64\publish\ForgePanel.exe"
Write-Host "Build completed."
Write-Host $exe
if (Test-Path $exe) { Start-Process $exe } else { Write-Warning "EXE not found." }
