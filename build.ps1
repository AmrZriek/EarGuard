# EarGuard Build Script (.NET Framework 4.8 / Windows)
param(
    [switch]$NoTest,
    [switch]$Debug,
    [switch]$BuildOnly,
    [switch]$Deploy,
    [switch]$HardwareTest,
    [switch]$Pause,
    [ValidateSet('Auto', 'Roslyn', 'Framework')][string]$Compiler = 'Auto'
)

$ErrorActionPreference = "Stop"

$baseDir = $PSScriptRoot
Set-Location $baseDir
$outputDir = Join-Path $baseDir ('archive\builds\' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$logPath = Join-Path $outputDir 'build.log'

# Explorer's Run with PowerShell opens a temporary console. Keep its result visible.
$pauseAtEnd = $Pause.IsPresent
if (-not $pauseAtEnd -and [Environment]::GetCommandLineArgs() -notcontains '-NonInteractive') {
    $launchProcess = Get-CimInstance Win32_Process -Filter "ProcessId = $PID" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($launchProcess.ParentProcessId) {
        $parentProcess = Get-Process -Id $launchProcess.ParentProcessId -ErrorAction SilentlyContinue
        $pauseAtEnd = $parentProcess.ProcessName -eq 'explorer'
    }
}

Start-Transcript -LiteralPath $logPath | Out-Null
try {
Write-Host "=================================================" -ForegroundColor Cyan
Write-Host "           EarGuard Build Pipeline               " -ForegroundColor Cyan
Write-Host "=================================================" -ForegroundColor Cyan

$roslynCsc = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"
$frameworkCsc = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if ($BuildOnly -and $Deploy) { throw '-BuildOnly and -Deploy cannot be combined.' }
if ($HardwareTest -and ($BuildOnly -or $NoTest)) { throw '-HardwareTest requires tests and cannot be combined with -BuildOnly.' }
$csc = if ($Compiler -eq 'Framework') { $frameworkCsc } elseif ($Compiler -eq 'Roslyn' -or (Test-Path $roslynCsc)) { $roslynCsc } else { $frameworkCsc }
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

    $testSources = Get-ChildItem -Path "$baseDir\src\Config\*.cs", "$baseDir\src\Audio\*.cs", "$baseDir\src\Tray\*.cs", "$baseDir\src\UI\*.cs", "$baseDir\tests\*.cs" | Select-Object -ExpandProperty FullName

    $testArgs = @(
        "/nologo",
        "/t:exe",
        "/langversion:5",
        "/out:$outputDir\TestRunner.exe"
    ) + $deterministicArg + $references + $testSources

    & $csc $testArgs | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Error "TestRunner compilation failed."
        exit 1
    }

    Write-Host "Executing tests..." -ForegroundColor Yellow
    $runnerArgs = if ($HardwareTest) { @('--hardware') } else { @() }
    & "$outputDir\TestRunner.exe" $runnerArgs | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Tests failed!"
        exit 1
    }

    # Retain test binaries and isolated artifacts for review.
} else {
    Write-Host "[2/5] Skipping tests (-NoTest specified)." -ForegroundColor Yellow
}

# Step 2: Build Application
Write-Host "[3/5] Building EarGuard.exe..." -ForegroundColor Cyan

$appSources = Get-ChildItem -Path "$baseDir\src\Config\*.cs", "$baseDir\src\Audio\*.cs", "$baseDir\src\Tray\*.cs", "$baseDir\src\UI\*.cs", "$baseDir\src\Program.cs" | Select-Object -ExpandProperty FullName

$targetType = "/t:winexe"
$opt = if ($Debug) { "/debug+" } else { "/optimize+", "/debug-" }

# Compile into a fresh directory so a failed build cannot affect a running binary.
$iconArg = if (Test-Path "$baseDir\EarGuard.ico") { @("/win32icon:$baseDir\EarGuard.ico") } else { @() }
$appArgs = @(
    "/nologo",
    $targetType,
    "/highentropyva+",
    "/langversion:5",
    "/out:$outputDir\EarGuard.exe"
) + $deterministicArg + $iconArg + $opt + $references + $appSources

& $csc $appArgs | Out-Host
if ($LASTEXITCODE -ne 0) {
    Write-Error "EarGuard.exe compilation failed."
    exit 1
}

# Step 3: Verification
Write-Host "[4/5] Verifying binary output..." -ForegroundColor Cyan
$exePath = "$outputDir\EarGuard.exe"
if (Test-Path $exePath) {
    $fileItem = Get-Item $exePath
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    $fileStream = [System.IO.File]::OpenRead($exePath)
    $hashBytes = $sha256.ComputeHash($fileStream)
    $fileStream.Close()
    $hashString = [System.BitConverter]::ToString($hashBytes).Replace("-", "").ToUpperInvariant()
    $sizeKiB = [Math]::Round($fileItem.Length / 1024, 2)

    if ($Deploy) {
        $installedExe = Join-Path $env:LOCALAPPDATA 'EarGuard\EarGuard.exe'
        $registered = $null -ne (Get-ScheduledTask -TaskName 'EarGuardStartup' -ErrorAction SilentlyContinue)
        if (-not $registered) {
            $registered = $null -ne (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'EarGuard' -ErrorAction SilentlyContinue)
        }
        if (-not $registered) { throw 'Startup is not registered; no installed copy was changed.' }
        $needsCopy = -not (Test-Path -LiteralPath $installedExe)
        if (-not $needsCopy) { $needsCopy = (Get-FileHash -LiteralPath $installedExe).Hash -ne $hashString }
        if ($needsCopy) {
            $installDir = Split-Path $installedExe -Parent
            New-Item -ItemType Directory -Path $installDir -Force | Out-Null
            $staged = Join-Path $installDir ('EarGuard.' + [Guid]::NewGuid().ToString('N') + '.tmp')
            Copy-Item -LiteralPath $exePath -Destination $staged
            if ((Get-FileHash -LiteralPath $staged).Hash -ne $hashString) { throw 'Staged executable verification failed.' }
            # Capture the original process and arguments once, before stopping any instance.
            $instances = @(Get-CimInstance Win32_Process -Filter "Name = 'EarGuard.exe'" |
                Where-Object { $_.ExecutablePath -eq $installedExe })
            $stopped = @()
            try {
                foreach ($instance in $instances) {
                    $process = Get-Process -Id $instance.ProcessId -ErrorAction Stop
                    if ($process.Path -ne $installedExe) { throw 'Process identity changed before deployment.' }
                    $launchArgs = $instance.CommandLine -replace '^\s*("[^"]+"|\S+)\s*', ''
                    Stop-Process -Id $instance.ProcessId -Force -ErrorAction Stop
                    $stopped += [PSCustomObject]@{ Path = $instance.ExecutablePath; Arguments = $launchArgs }
                    $process.WaitForExit()
                }
                if (Test-Path -LiteralPath $installedExe) {
                    [IO.File]::Replace($staged, $installedExe, $installedExe + '.' + [Guid]::NewGuid().ToString('N') + '.bak')
                } else { [IO.File]::Move($staged, $installedExe) }
            } finally {
                foreach ($instance in $stopped) {
                    $start = @{ FilePath = $instance.Path; WindowStyle = 'Hidden'; WorkingDirectory = $installDir }
                    if ($instance.Arguments) { $start.ArgumentList = $instance.Arguments }
                    Start-Process @start
                }
            }
            Write-Host "  Installed: $installedExe" -ForegroundColor Green
        } else { Write-Host '  Installed copy already current; no process stopped.' -ForegroundColor Green }
    } else {
        Write-Host '[5/5] Build only: running processes and installed executable unchanged.' -ForegroundColor Cyan
    }

    Write-Host ""
    Write-Host "=================================================" -ForegroundColor Green
    Write-Host "  BUILD SUCCESSFUL!" -ForegroundColor Green
    Write-Host "=================================================" -ForegroundColor Green
    Write-Host "  Binary:      $exePath"
    Write-Host "  The repository-root EarGuard.exe is not replaced. Run or copy the Binary path above."
    Write-Host "  Size:        $sizeKiB KiB"
    Write-Host "  SHA256:      $hashString"
    Write-Host "  Target:      .NET Framework 4.8 (Single Portable .exe)"
    Write-Host "  Zero external dependencies. Ready to distribute."
    Write-Host "=================================================" -ForegroundColor Green
} else {
    Write-Error "EarGuard.exe was not created."
    exit 1
}
} catch {
    Write-Host ("BUILD FAILED: " + $_.Exception.Message) -ForegroundColor Red
    throw
} finally {
    Write-Host "Build log: $logPath"
    Stop-Transcript | Out-Null
    if ($pauseAtEnd) { [void](Read-Host 'Press Enter to close this window') }
}
