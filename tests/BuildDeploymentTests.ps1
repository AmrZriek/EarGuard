# Exercises the real build/deployment script with workspace-only files and fake process/startup commands.
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$fixture = Join-Path $repo ('archive\deployment-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $fixture | Out-Null
Copy-Item -LiteralPath (Join-Path $repo 'build.ps1') -Destination $fixture
Copy-Item -LiteralPath (Join-Path $repo 'src') -Destination $fixture -Recurse
$testState = @{ Target = $null; Stopped = @(); Started = @() }
$testState.Target = Join-Path $fixture 'profile\EarGuard\EarGuard.exe'
New-Item -ItemType Directory -Path (Split-Path $testState.Target) -Force | Out-Null
Set-Content -LiteralPath $testState.Target -Value 'previous executable'
$testState.Stopped = @()
$testState.Started = @()

function Get-ScheduledTask { param($TaskName) [PSCustomObject]@{ TaskName = $TaskName } }
function Get-CimInstance {
    param($ClassName, $Filter)
    [PSCustomObject]@{ ProcessId = 123; ExecutablePath = $testState.Target; CommandLine = ('"' + $testState.Target + '" --tray') }
    [PSCustomObject]@{ ProcessId = 456; ExecutablePath = 'C:\Unrelated\EarGuard.exe'; CommandLine = 'unrelated' }
}
function Get-Process {
    param($Id)
    $process = [PSCustomObject]@{ Path = $testState.Target }
    Add-Member -InputObject $process -MemberType ScriptMethod -Name WaitForExit -Value { }
    $process
}
function Stop-Process { param($Id, [switch]$Force) $testState.Stopped += $Id }
function Start-Process { param($FilePath, $WindowStyle, $WorkingDirectory, $ArgumentList) $testState.Started += [PSCustomObject]@{ Path = $FilePath; Arguments = $ArgumentList } }
function Check($condition, $message) { if (-not $condition) { throw $message } }

$previousLocalAppData = $env:LOCALAPPDATA
$previousLocation = Get-Location
try {
    $env:LOCALAPPDATA = Join-Path $fixture 'profile'
    $build = Join-Path $fixture 'build.ps1'
    $locked = [IO.File]::Open($testState.Target, 'Open', 'Read', 'Read')
    $failed = $false
    try { & $build -NoTest -Deploy -Compiler Roslyn *> (Join-Path $fixture 'failed-replacement.log') }
    catch { $failed = $true; $_ | Out-String | Add-Content -LiteralPath (Join-Path $fixture "expected-failures.log") }
    finally { $locked.Dispose() }
    Check $failed 'Locked replacement must fail.'
    Check ((Get-Content -LiteralPath $testState.Target -Raw).Trim() -eq 'previous executable') 'Failed replacement changed the old binary.'
    Check ($testState.Stopped.Count -eq 1 -and $testState.Stopped[0] -eq 123) 'Only the matching instance may be stopped.'
    Check ($testState.Started.Count -eq 1 -and $testState.Started[0].Path -eq $testState.Target -and $testState.Started[0].Arguments -eq '--tray') 'Failed deployment must restore exactly its prior instance and arguments.'

    $testState.Stopped = @(); $testState.Started = @()
    & $build -NoTest -Deploy -Compiler Roslyn *> (Join-Path $fixture 'successful-replacement.log')
    Check ($LASTEXITCODE -eq 0 -and $testState.Stopped.Count -eq 1 -and $testState.Started.Count -eq 1) 'Successful deployment must restore the stopped instance.'
    $hash = (Get-FileHash -LiteralPath $testState.Target).Hash

    $testState.Stopped = @(); $testState.Started = @()
    & $build -NoTest -Deploy -Compiler Roslyn *> (Join-Path $fixture 'unchanged.log')
    Check ($LASTEXITCODE -eq 0 -and $testState.Stopped.Count -eq 0 -and $testState.Started.Count -eq 0) 'Unchanged deployment must not stop or restart anything.'

    Set-Content -LiteralPath (Join-Path $fixture 'src\Program.cs') -Value 'deliberately invalid C#'
    $failed = $false
    try { & $build -NoTest -Deploy -Compiler Roslyn *> (Join-Path $fixture 'failed-compilation.log') }
    catch { $failed = $true; $_ | Out-String | Add-Content -LiteralPath (Join-Path $fixture "expected-failures.log") }
    Check $failed 'Invalid source must fail compilation.'
    Check ($testState.Stopped.Count -eq 0 -and $testState.Started.Count -eq 0) 'Failed compilation must leave running instances alone.'
    Check ((Get-FileHash -LiteralPath $testState.Target).Hash -eq $hash) 'Failed compilation must preserve the installed bytes.'
    $lastBuild = Get-ChildItem -LiteralPath (Join-Path $fixture 'archive\builds') -Directory |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $failureLog = Join-Path $lastBuild.FullName 'build.log'
    Check (Test-Path -LiteralPath $failureLog) 'Failed builds must retain a diagnostic log.'
    Check ((Get-Content -LiteralPath $failureLog -Raw) -match 'BUILD FAILED:.*compilation failed') 'The retained log must explain the build failure.'
    Write-Host "[PASS] Deployment isolation, failure recovery, unchanged build and failed compilation. Artifacts: $fixture"
} finally {
    $env:LOCALAPPDATA = $previousLocalAppData
    Set-Location $previousLocation
}
