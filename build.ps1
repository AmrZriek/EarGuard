# EarGuard Build Script (.NET Framework 4.8 / Windows)
param(
    [switch]$NoTest,
    [switch]$Debug
)

$ErrorActionPreference = "Stop"

Write-Host "=================================================" -ForegroundColor Cyan
Write-Host "           EarGuard Build Pipeline               " -ForegroundColor Cyan
Write-Host "=================================================" -ForegroundColor Cyan

$baseDir = $PSScriptRoot
Set-Location $baseDir

$roslynCsc = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
$frameworkCsc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

$csc = if (Test-Path $roslynCsc) { $roslynCsc } else { $frameworkCsc }
if (-not (Test-Path $csc)) {
    Write-Error "Could not locate C# compiler."
    exit 1
}

# Roslyn honours /deterministic+ (reproducible binaries); the in-box .NET Framework compiler
# predates it and rejects the flag outright, so it is only passed when it is actually supported.
$deterministicArg = @()
if ($csc -eq $roslynCsc) { $deterministicArg = @("/deterministic+") }
Write-Host "[1/4] Compiler located: $csc" -ForegroundColor Green

$netDir = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$wpfDir = "$netDir\WPF"

$references = @(
    "/r:$netDir\mscorlib.dll",
    "/r:$netDir\System.dll",
    "/r:$netDir\System.Core.dll",
    "/r:$netDir\System.Web.Extensions.dll",
    "/r:$netDir\System.Drawing.dll",
    "/r:$netDir\System.Windows.Forms.dll",
    "/r:$wpfDir\WindowsBase.dll",
    "/r:$wpfDir\PresentationCore.dll",
    "/r:$wpfDir\PresentationFramework.dll",
    "/r:$netDir\System.Xaml.dll"
)

# Step 1: Run Unit Tests
if (-not $NoTest) {
    Write-Host "[2/4] Building and executing test suite..." -ForegroundColor Cyan

    $testSources = Get-ChildItem -Path "$baseDir\src\Config\*.cs", "$baseDir\src\Audio\*.cs", "$baseDir\src\Tray\TrayIconRecovery.cs", "$baseDir\tests\*.cs" | Select-Object -ExpandProperty FullName

    $testArgs = @(
        "/nologo",
        "/t:exe",
        "/out:$baseDir\TestRunner.exe"
    ) + $deterministicArg + $references + $testSources

    & $csc $testArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Error "TestRunner compilation failed."
        exit 1
    }

    Write-Host "Executing tests..." -ForegroundColor Yellow
    & "$baseDir\TestRunner.exe"
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Tests failed!"
        exit 1
    }

    # Clean up test binary to recycle bin
    Add-Type -AssemblyName Microsoft.VisualBasic
    Start-Sleep -Milliseconds 200
    foreach ($file in @("$baseDir\TestRunner.exe", "$baseDir\TestRunner.pdb")) {
        if (Test-Path $file) {
            for ($attempt = 0; $attempt -lt 5; $attempt++) {
                try {
                    [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile($file, 'OnlyErrorDialogs', 'SendToRecycleBin')
                    break
                } catch {
                    Start-Sleep -Milliseconds 200
                }
            }
        }
    }
} else {
    Write-Host "[2/4] Skipping tests (-NoTest specified)." -ForegroundColor Yellow
}

# Step 2: Build Application
Write-Host "[3/4] Building EarGuard.exe..." -ForegroundColor Cyan

$appSources = Get-ChildItem -Path "$baseDir\src\Config\*.cs", "$baseDir\src\Audio\*.cs", "$baseDir\src\Tray\*.cs", "$baseDir\src\UI\*.cs", "$baseDir\src\Program.cs" | Select-Object -ExpandProperty FullName

$targetType = "/t:winexe"
$opt = if ($Debug) { "/debug+" } else { "/optimize+", "/debug-" }

$iconArg = if (Test-Path "$baseDir\EarGuard.ico") { @("/win32icon:$baseDir\EarGuard.ico") } else { @() }
$appArgs = @(
    "/nologo",
    $targetType,
    "/highentropyva+",
    "/out:$baseDir\EarGuard.exe"
) + $deterministicArg + $iconArg + $opt + $references + $appSources

& $csc $appArgs
if ($LASTEXITCODE -ne 0) {
    Write-Error "EarGuard.exe compilation failed."
    exit 1
}

# Step 3: Verification
Write-Host "[4/4] Verifying binary output..." -ForegroundColor Cyan
$exePath = "$baseDir\EarGuard.exe"
if (Test-Path $exePath) {
    $fileItem = Get-Item $exePath
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    $fileStream = [System.IO.File]::OpenRead($exePath)
    $hashBytes = $sha256.ComputeHash($fileStream)
    $fileStream.Close()
    $hashString = [System.BitConverter]::ToString($hashBytes).Replace("-", "").ToUpperInvariant()
    $sizeKb = [Math]::Round($fileItem.Length / 1024, 2)

    Write-Host ""
    Write-Host "=================================================" -ForegroundColor Green
    Write-Host "  BUILD SUCCESSFUL!" -ForegroundColor Green
    Write-Host "=================================================" -ForegroundColor Green
    Write-Host "  Binary:      $exePath"
    Write-Host "  Size:        $sizeKb KB"
    Write-Host "  SHA256:      $hashString"
    Write-Host "  Target:      .NET Framework 4.8 (Single Portable .exe)"
    Write-Host "  Zero external dependencies. Ready to distribute."
    Write-Host "=================================================" -ForegroundColor Green
} else {
    Write-Error "EarGuard.exe was not created."
    exit 1
}
