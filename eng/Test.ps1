[CmdletBinding()]
param(
    [ValidateSet("All", "Dsh", "Audio", "Voice")]
    [string]$Suite = "All",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [string]$PythonPath = "python",
    [string]$NodePath = "node",
    [switch]$NoRestore
)

$ErrorActionPreference = "Stop"
$TestRepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$RunDshTests = $Suite -in @("All", "Dsh")
$RunAudioTests = $Suite -in @("All", "Audio")
$RunVoiceTests = $Suite -in @("All", "Voice")

function Assert-TestExecutable {
    param([Parameter(Mandatory = $true)][string]$Executable)
    if (-not (Get-Command -Name $Executable -CommandType Application, ExternalScript -ErrorAction SilentlyContinue)) {
        throw "Required executable was not found: $Executable"
    }
}

function Invoke-TestCommand {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][string]$Executable,
        [string[]]$Arguments = @()
    )
    Write-Host "[test] $Name"
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE."
    }
}

function Invoke-DotNetRegression {
    param(
        [Parameter(Mandatory = $true)][string]$Project,
        [string[]]$ProbeArguments = @(),
        [switch]$NoBuild
    )
    $RunArguments = @("run", "--project", (Join-Path $TestRepoRoot $Project), "--configuration", $Configuration)
    if ($NoRestore) { $RunArguments += "--no-restore" }
    if ($NoBuild) { $RunArguments += "--no-build" }
    if ($ProbeArguments.Count -gt 0) { $RunArguments += "--"; $RunArguments += $ProbeArguments }
    Invoke-TestCommand -Name ($Project + " " + ($ProbeArguments -join " ")) -Executable "dotnet" -Arguments $RunArguments
}

# Preflight only; dependencies are never installed and no real device or DSH task is started.
if ($RunDshTests -or $RunAudioTests) { Assert-TestExecutable "dotnet" }
if ($RunDshTests) {
    Assert-TestExecutable $NodePath
    Assert-TestExecutable "npm"
    $NodeVersion = & $NodePath -p "process.versions.node"
    if ($LASTEXITCODE -ne 0 -or [int](($NodeVersion -split "\.")[0]) -lt 20) {
        throw "DSH regressions require Node.js 20 or later."
    }
    $PluginDependency = Join-Path $TestRepoRoot "integrations/deepseek-harness/halo-pixelbar-tools/node_modules/@deepseek-ai/schemastery/package.json"
    if (-not (Test-Path -LiteralPath $PluginDependency -PathType Leaf)) {
        throw "Plugin dependencies are missing. Run pnpm --dir integrations/deepseek-harness/halo-pixelbar-tools install --frozen-lockfile first."
    }
}
if ($RunVoiceTests) {
    Assert-TestExecutable $PythonPath
    Invoke-TestCommand -Name "voice test dependency: numpy" -Executable $PythonPath -Arguments @("-B", "-c", "import numpy")
}

Push-Location -LiteralPath $TestRepoRoot
try {
    if ($RunDshTests) {
        Invoke-DotNetRegression "tests/dsh-session-service-tests/SessionServiceTests.csproj"
        Invoke-DotNetRegression "tests/dsh-task-routing-tests/TaskRoutingTests.csproj"
        Invoke-DotNetRegression "tests/dsh-spoken-interaction-tests/SpokenInteractionTests.csproj"
        $DshUiProject = "tests/dsh-ui-tests/DshUiTests.csproj"
        Invoke-DotNetRegression $DshUiProject
        foreach ($Probe in @("--page", "--tasks", "--voice", "--resources")) {
            Invoke-DotNetRegression -Project $DshUiProject -ProbeArguments @($Probe) -NoBuild
        }
        Invoke-DotNetRegression "tests/resource-lifecycle-tests/ResourceLifecycleTests.csproj"
        Invoke-DotNetRegression -Project "tests/resource-lifecycle-tests/ResourceLifecycleTests.csproj" -ProbeArguments @("--hidden-start") -NoBuild

        $BridgeDirectory = Join-Path $TestRepoRoot "integrations/deepseek-harness/halo-session-bridge"
        foreach ($Source in @("index.js", "bridge-core.js")) {
            Invoke-TestCommand -Name "session bridge syntax: $Source" -Executable $NodePath -Arguments @("--check", (Join-Path $BridgeDirectory $Source))
        }
        Invoke-TestCommand -Name "DSH session bridge regressions" -Executable $NodePath -Arguments @("--test", (Join-Path $TestRepoRoot "tests/dsh-session-bridge/bridge.test.mjs"))

        $PluginDirectory = Join-Path $TestRepoRoot "integrations/deepseek-harness/halo-pixelbar-tools"
        foreach ($Source in @("index.js", "scripts/pipe-smoke-test.mjs", "scripts/package-check.mjs")) {
            Invoke-TestCommand -Name "PixelBar plugin syntax: $Source" -Executable $NodePath -Arguments @("--check", (Join-Path $PluginDirectory $Source))
        }
        Invoke-TestCommand -Name "PixelBar plugin schemas and package contents" -Executable $NodePath -Arguments @((Join-Path $PluginDirectory "scripts/package-check.mjs"))
    }

    if ($RunAudioTests) {
        Invoke-DotNetRegression "tests/audio-control-tests/AudioControlTests.csproj"
        Invoke-DotNetRegression "tests/audio-endpoint-tests/AudioEndpointTests.csproj"
        Invoke-DotNetRegression "tests/audio-command-tests/AudioCommandTests.csproj"
        Invoke-DotNetRegression "tests/audio-ui-tests/AudioUiTests.csproj"
    }

    if ($RunVoiceTests) {
        Invoke-TestCommand -Name "voice worker regressions" -Executable $PythonPath -Arguments @("-B", (Join-Path $TestRepoRoot "tests/voice-agent/test_voice_agent_host.py"))
    }
    Write-Host "All requested regression suites passed: $Suite."
}
finally {
    Pop-Location
}
