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
Write-Host "[1/5] Compiler located: $csc" -ForegroundColor Green

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
    Write-Host "[2/5] Building and executing test suite..." -ForegroundColor Cyan

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
    Write-Host "[2/5] Skipping tests (-NoTest specified)." -ForegroundColor Yellow
}

# Step 2: Build Application
Write-Host "[3/5] Building EarGuard.exe..." -ForegroundColor Cyan

$appSources = Get-ChildItem -Path "$baseDir\src\Config\*.cs", "$baseDir\src\Audio\*.cs", "$baseDir\src\Tray\*.cs", "$baseDir\src\UI\*.cs", "$baseDir\src\Program.cs" | Select-Object -ExpandProperty FullName

$targetType = "/t:winexe"
$opt = if ($Debug) { "/debug+" } else { "/optimize+", "/debug-" }

# Stop a running instance before compiling into the repository.
#
# The compiler opens the output file for writing, and a running EarGuard holds its own executable
# open. Without this the build fails with CS2012 and, worse, can leave a truncated EarGuard.exe
# behind - which then breaks logon startup with "file not found" until someone rebuilds. Stopping
# first makes the build reproducible whether or not EarGuard is currently running.
$runningBeforeBuild = Get-Process -Name 'EarGuard' -ErrorAction SilentlyContinue
if ($runningBeforeBuild) {
    Write-Host "  Stopping the running EarGuard instance so the binary can be replaced..." -ForegroundColor Yellow
    $runningBeforeBuild | Stop-Process -Force
    Start-Sleep -Milliseconds 700
}

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
Write-Host "[4/5] Verifying binary output..." -ForegroundColor Cyan
$exePath = "$baseDir\EarGuard.exe"
if (Test-Path $exePath) {
    $fileItem = Get-Item $exePath
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    $fileStream = [System.IO.File]::OpenRead($exePath)
    $hashBytes = $sha256.ComputeHash($fileStream)
    $fileStream.Close()
    $hashString = [System.BitConverter]::ToString($hashBytes).Replace("-", "").ToUpperInvariant()
    $sizeKb = [Math]::Round($fileItem.Length / 1024, 2)

    # Step 3: Refresh the installed copy logon startup launches
    #
    # 'Start with Windows' registers a scheduled task against a stable per-user copy of the executable
    # (ApplicationInstaller), not against this working tree. Rebuilding here therefore has to refresh
    # that copy, otherwise the machine keeps running the previous build at logon while the repository
    # holds a newer one. Only ever touched when the task is already registered - a user who has not
    # enabled startup gets no stray files installed behind their back.
    Write-Host "[5/5] Refreshing the installed copy used by logon startup..." -ForegroundColor Cyan

    $installedExe = Join-Path $env:LOCALAPPDATA "EarGuard\EarGuard.exe"
    $registered = $false
    try {
        $task = Get-ScheduledTask -TaskName "EarGuardStartup" -ErrorAction SilentlyContinue
        if ($task) { $registered = $true }
    } catch { }
    if (-not $registered) {
        try {
            $runValue = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'EarGuard' -ErrorAction SilentlyContinue
            if ($runValue) { $registered = $true }
        } catch { }
    }

    if (-not $registered) {
        Write-Host "  Startup is not registered; leaving no installed copy behind." -ForegroundColor Yellow
    } elseif (-not (Test-Path $exePath)) {
        Write-Host "  Skipped: no built executable to install." -ForegroundColor Yellow
    } else {
        try {
            # Replacing a running binary fails, so stop the tray instance first and restart it after.
            $running = Get-Process -Name 'EarGuard' -ErrorAction SilentlyContinue
            $wasRunning = $null -ne $running
            if ($wasRunning) {
                $running | Stop-Process -Force
                Start-Sleep -Milliseconds 700
            }

            $installDir = Split-Path $installedExe -Parent
            if (-not (Test-Path $installDir)) { New-Item -ItemType Directory -Path $installDir -Force | Out-Null }

            # Compare before copying so a no-op rebuild leaves the installed file's timestamp alone.
            $needsCopy = $true
            if (Test-Path $installedExe) {
                $sourceHash = (Get-FileHash $exePath -Algorithm SHA256).Hash
                $targetHash = (Get-FileHash $installedExe -Algorithm SHA256).Hash
                if ($sourceHash -eq $targetHash) { $needsCopy = $false }
            }

            if ($needsCopy) {
                Copy-Item $exePath $installedExe -Force
                Write-Host "  Installed: $installedExe" -ForegroundColor Green
            } else {
                Write-Host "  Installed copy already current." -ForegroundColor Green
            }

            if ($wasRunning) {
                Start-Process $installedExe -ArgumentList '--tray'
                Write-Host "  Restarted EarGuard from the installed location." -ForegroundColor Green
            }
        } catch {
            Write-Host "  Could not refresh the installed copy: $($_.Exception.Message)" -ForegroundColor Yellow
            Write-Host "  The running instance keeps its current registration." -ForegroundColor Yellow
        }
    }

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
