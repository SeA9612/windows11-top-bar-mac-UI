param([switch]$FrameworkDependent)
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    dotnet run --project validation/TopBar/TopBarTests.csproj -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Top bar tests failed.' }
    $publishDir = if ($FrameworkDependent) { 'artifacts/topbar-small' } else { 'artifacts/topbar-portable' }
    $selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
    dotnet publish src/TopBar/TopBar.csproj -c Release -r win-x64 --self-contained $selfContained -p:DebugType=None -p:DebugSymbols=false -o $publishDir
    if ($LASTEXITCODE -ne 0) { throw 'Top bar build failed.' }
} finally { Pop-Location }
