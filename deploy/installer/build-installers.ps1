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
    [string]$InnoSetup = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
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

if (-not (Test-Path $InnoSetup)) {
    throw "Inno Setup was not found at '$InnoSetup'. Install Inno Setup 6 or pass -InnoSetup."
}

$env:TELLO_VERSION = $Version
foreach ($script in "Dashboard.iss", "Machine.iss") {
    & $InnoSetup (Join-Path $PSScriptRoot $script)
    if ($LASTEXITCODE -ne 0) { throw "Compiling $script failed." }
}

Write-Host "Installers written to $(Join-Path $PSScriptRoot 'Output')"
