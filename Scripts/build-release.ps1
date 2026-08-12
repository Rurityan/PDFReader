param(
    [string]$PythonEnvironmentPath = "",
    [ValidateSet("win-x64", "win-arm64", "both")]
    [string]$RuntimeIdentifier = "win-x64",
    [string]$DotnetPath = "",
    [switch]$BuildInstaller
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$dotnet = if ([string]::IsNullOrWhiteSpace($DotnetPath)) {
    (Get-Command dotnet.exe -ErrorAction Stop).Source
} else {
    (Resolve-Path $DotnetPath).Path
}
if (-not ((& $dotnet --list-sdks) -match '^10\.')) {
    throw "A .NET 10 SDK is required. Use -DotnetPath to specify dotnet.exe."
}

function Publish-Runtime {
    param([string]$TargetRuntime)

    $publishDirectory = Join-Path $projectRoot "publish\$TargetRuntime"
    $environmentPath = $PythonEnvironmentPath
    if ([string]::IsNullOrWhiteSpace($environmentPath)) {
        $environmentPath = if ($TargetRuntime -eq "win-arm64") {
            Join-Path $projectRoot ".venv-arm64"
        } else {
            Join-Path $projectRoot ".venv"
        }
    }
    $pythonEnvironment = (Resolve-Path $environmentPath).Path
    if (-not (Test-Path (Join-Path $pythonEnvironment "Scripts\python.exe"))) {
        throw "Python environment is missing Scripts\python.exe: $pythonEnvironment"
    }

    if (Test-Path $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }
    & $dotnet publish .\PDFReader.csproj -c Release -r $TargetRuntime --self-contained true `
        -p:DebugType=None -p:DebugSymbols=false -o $publishDirectory

    $packagedPython = Join-Path $publishDirectory ".venv"
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
        $installerArchitecture = if ($TargetRuntime -eq "win-arm64") { "arm64" } else { "x64compatible" }
        $installerPublishDirectory = "..\publish\$TargetRuntime"
        & $isccPath "/DPublishDir=$installerPublishDirectory" "/DTargetArchitectures=$installerArchitecture" "/DTargetRuntime=$TargetRuntime" .\Installer\PDFReader.iss
    }
}

Push-Location $projectRoot
try {
    & $dotnet restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }
    if ($RuntimeIdentifier -eq "both") {
        Publish-Runtime "win-x64"
        Publish-Runtime "win-arm64"
    } else {
        Publish-Runtime $RuntimeIdentifier
    }
} finally {
    Pop-Location
}
