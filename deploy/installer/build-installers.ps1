<#
.SYNOPSIS
  Builds the Windows installers for the Dashboard and the Machine app.

.DESCRIPTION
  Publishes the three Windows projects self-contained (no .NET install needed on the PCs) and compiles the two
  Inno Setup scripts. Run on Windows with the .NET 10 SDK and Inno Setup 6 installed:

      powershell -ExecutionPolicy Bypass -File deploy\installer\build-installers.ps1 -Version 1.0.0

  The installers appear in deploy\installer\Output.
#>
param(
    [string]$Version = "0.1.0",
    [string]$InnoSetup = ""
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$publish = Join-Path $PSScriptRoot "publish"
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue

function Publish-Project([string]$project, [string]$target) {
    dotnet publish (Join-Path $root "src\$project\$project.csproj") -c Release -r win-x64 --self-contained true `
        -p:Version=$Version -p:PublishSingleFile=false -p:DebugType=none -o (Join-Path $publish $target)
    if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
}

Publish-Project "TelloGameRoom.Dashboard" "Dashboard"
# The Machine app and its guard service are installed side by side in one folder.
Publish-Project "TelloGameRoom.Machine" "Machine"
Publish-Project "TelloGameRoom.MachineGuard" "Machine"

if (-not $InnoSetup) {
    # Inno Setup can be installed for all users (either Program Files folder) or just for the current user.
    $candidates = @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    )
    $onPath = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($onPath) { $candidates = @($onPath.Source) + $candidates }
    $InnoSetup = $candidates | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}

if (-not $InnoSetup -or -not (Test-Path $InnoSetup)) {
    throw "Inno Setup 6 was not found. Install it with 'winget install JRSoftware.InnoSetup' (or from https://jrsoftware.org/isdl.php), or pass -InnoSetup <path to ISCC.exe>."
}

Write-Host "Using Inno Setup at $InnoSetup"

$env:TELLO_VERSION = $Version
foreach ($script in "Dashboard.iss", "Machine.iss") {
    & $InnoSetup (Join-Path $PSScriptRoot $script)
    if ($LASTEXITCODE -ne 0) { throw "Compiling $script failed." }
}

Write-Host "Installers written to $(Join-Path $PSScriptRoot 'Output')"
