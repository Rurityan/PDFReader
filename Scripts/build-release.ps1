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
    $pythonExecutable = Join-Path $pythonEnvironment "Scripts\python.exe"
    if ($TargetRuntime -eq "win-x64") {
        & $pythonExecutable -c "import pikepdf, miniaudio"
        if ($LASTEXITCODE -ne 0) {
            throw "The x64 Python environment is missing Acrobat export dependencies. Run: .venv\Scripts\python.exe -m pip install -r Scripts/requirements-ocr.txt"
        }
    } else {
        & $pythonExecutable -c "import cv2, numpy, onnxruntime, pyclipper, fitz"
        if ($LASTEXITCODE -ne 0) {
            throw "The ARM64 Python environment is missing an OCR dependency. Install Scripts/requirements-ocr-arm64.txt and the native wheels from py-libs/win-arm64."
        }
        & $pythonExecutable -c "import pikepdf, miniaudio"
        if ($LASTEXITCODE -ne 0) {
            throw "The ARM64 Python environment is missing Acrobat export dependencies. Install Scripts/requirements-rich-media-arm64.txt and the native wheels from py-libs/win-arm64."
        }
    }

    if (Test-Path $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }
    & $dotnet publish .\PDFReader.csproj -c Release -r $TargetRuntime --self-contained true `
        -p:DebugType=None -p:DebugSymbols=false -o $publishDirectory

    # NuGet's LibVLC package contains binaries for every Windows architecture.
    # Keep only the architecture selected for this release.
    $libVlcRoot = Join-Path $publishDirectory "libvlc"
    if (Test-Path $libVlcRoot) {
        Get-ChildItem $libVlcRoot -Directory | Where-Object { $_.Name -ne $TargetRuntime } |
            Remove-Item -Recurse -Force
    }

    # dotnet publish can still copy native debug symbols and import libraries
    # from third-party packages even when managed symbols are disabled.
    Get-ChildItem $publishDirectory -Recurse -File -Include *.pdb,*.lib |
        Remove-Item -Force
    Get-ChildItem $publishDirectory -File -Filter "MuPDFCore.NativeAssets.*" |
        Where-Object { $_.Name -ne "MuPDFCore.NativeAssets.Win-$($TargetRuntime.Substring(4)).dll" } |
        Remove-Item -Force

    $packagedPython = Join-Path $publishDirectory ".venv"
    # Copy the interpreter and standard library, but never the complete
    # development site-packages tree. It may contain old Paddle/model tooling.
    & robocopy $pythonEnvironment $packagedPython /E /XD site-packages /NFL /NDL /NJH /NJS /NP
    if ($LASTEXITCODE -gt 7) {
        throw "Failed to copy the Python runtime with robocopy (exit code $LASTEXITCODE)."
    }

    # Current workers use this fixed dependency set. Keep transitive runtime
    # packages explicit so a developer's unrelated tools cannot enter a setup.
    $runtimePackages = @(
        "cv2", "numpy", "numpy.libs", "onnxruntime", "pyclipper", "fitz", "pymupdf", "pikepdf",
        "PIL", "lxml", "coloredlogs", "humanfriendly", "flatbuffers", "google",
        "sympy", "mpmath", "packaging", "deprecated", "wrapt", "cffi", "pycparser",
        "typing_extensions"
    )
    $sourceSitePackages = Join-Path $pythonEnvironment "Lib\site-packages"
    $packagedSitePackages = Join-Path $packagedPython "Lib\site-packages"
    New-Item -ItemType Directory -Path $packagedSitePackages -Force | Out-Null
    foreach ($package in $runtimePackages) {
        $sourcePackage = Join-Path $sourceSitePackages $package
        if (Test-Path $sourcePackage) {
            & robocopy $sourcePackage (Join-Path $packagedSitePackages $package) /E /XD __pycache__ tests test mupdf-devel /XF *.pyc *.pyo *.lib /NFL /NDL /NJH /NJS /NP
            if ($LASTEXITCODE -gt 7) {
                throw "Failed to copy Python package $package (exit code $LASTEXITCODE)."
            }
        }
    }

    Get-ChildItem $sourceSitePackages -File | Where-Object {
        $_.Name -match '^(_miniaudio|_cffi_backend).*\.(pyd|py)$' -or $_.Name -eq 'miniaudio.py'
    } | Copy-Item -Destination $packagedSitePackages -Force

    Get-ChildItem (Join-Path $packagedPython "Scripts") -File |
        Where-Object { $_.Name -notin @("python.exe", "pythonw.exe") } |
        Remove-Item -Force

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
