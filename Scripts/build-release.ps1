param(
    [string]$PythonEnvironmentPath = "",
    [switch]$BuildInstaller
)

$ErrorActionPreference = "Stop"
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$publishDirectory = Join-Path $projectRoot "publish\win-x64"

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
    dotnet publish .\PDFReader.csproj -c Release -r win-x64 --self-contained true `
        -p:DebugType=None -p:DebugSymbols=false -o $publishDirectory

    $packagedPython = Join-Path $publishDirectory ".venv"
    if (Test-Path $packagedPython) {
        Remove-Item -LiteralPath $packagedPython -Recurse -Force
    }
    Copy-Item -LiteralPath $pythonEnvironment -Destination $packagedPython -Recurse -Force

    if ($BuildInstaller) {
        $iscc = Get-Command ISCC.exe -ErrorAction SilentlyContinue
        if ($null -eq $iscc) {
            $knownCompilerPaths = @(
                "${env:ProgramFiles}\Inno Setup 7\ISCC.exe",
                "${env:ProgramFiles}\Inno Setup 6\ISCC.exe",
                "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
                "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
            )
            $knownCompilerPath = $knownCompilerPaths | Where-Object { Test-Path $_ } | Select-Object -First 1
            if ($knownCompilerPath) {
                $iscc = Get-Item -LiteralPath $knownCompilerPath
            }
        }
        if ($null -eq $iscc) {
            throw "Inno Setup compiler ISCC.exe was not found in PATH."
        }
        & $iscc.Source .\Installer\PDFReader.iss
    }
}
finally {
    Pop-Location
}
