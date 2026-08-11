param(
    [string]$PythonEnvironmentPath = "",
    [ValidateSet("win-x64", "win-arm64")]
    [string]$RuntimeIdentifier = "win-x64",
    [switch]$BuildInstaller
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$publishDirectory = Join-Path $projectRoot "publish\$RuntimeIdentifier"

if ([string]::IsNullOrWhiteSpace($PythonEnvironmentPath)) {
    $PythonEnvironmentPath = Join-Path $projectRoot ".venv"
}
$pythonEnvironment = (Resolve-Path $PythonEnvironmentPath).Path
if (-not (Test-Path (Join-Path $pythonEnvironment "Scripts\python.exe"))) {
    throw "Python environment is missing Scripts\python.exe: $pythonEnvironment"
}

Push-Location $projectRoot
try {
    if (Test-Path $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }
    dotnet restore
    dotnet publish .\PDFReader.csproj -c Release -r $RuntimeIdentifier --self-contained true `
        -p:DebugType=None -p:DebugSymbols=false -o $publishDirectory

    $packagedPython = Join-Path $publishDirectory ".venv"
    if (Test-Path $packagedPython) {
        Remove-Item -LiteralPath $packagedPython -Recurse -Force
    }
    Copy-Item -LiteralPath $pythonEnvironment -Destination $packagedPython -Recurse -Force

    if ($BuildInstaller) {
        $isccPath = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Path
        if ([string]::IsNullOrWhiteSpace($isccPath)) {
            $knownCompilerPaths = @(
                "${env:ProgramFiles}\Inno Setup 7\ISCC.exe",
                "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
                "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
                "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
            )
            $isccPath = $knownCompilerPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
        }
        if ([string]::IsNullOrWhiteSpace($isccPath)) {
            throw "Inno Setup compiler ISCC.exe was not found in PATH."
        }
        $installerArchitecture = if ($RuntimeIdentifier -eq "win-arm64") { "arm64" } else { "x64compatible" }
        $installerPublishDirectory = "..\publish\$RuntimeIdentifier"
        & $isccPath "/DPublishDir=$installerPublishDirectory" "/DTargetArchitectures=$installerArchitecture" .\Installer\PDFReader.iss
    }
}
finally {
    Pop-Location
}
