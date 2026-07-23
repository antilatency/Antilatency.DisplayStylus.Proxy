[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$externalRoot = Join-Path $repositoryRoot 'external'
$runtimeSdkPath = Join-Path $externalRoot 'AntilatencySdk-4.6.0-Unity'
$runtimeSdkBranch = 'subset-22a756f0fb8b2c7a4dbd47324fdc939eb60f8372'
$runtimeSdkCommit = 'bc77d08b02a96df4bc3c69bd6caab317acc70f54'
$nativeSdkPath = Join-Path $externalRoot 'AntilatencySdk-4.6.0-Native'
$nativeSdkBranch = 'subset-77c480a75f113ec3fcb27bdae376668debc5b0d7'
$nativeSdkCommit = '13ddab8c175961e38f67e3d7aa0fe497940a264b'
$sdkRepository = 'https://github.com/AntilatencySDK/Release_4.6.0.git'
$displayStylusSdkPath = Join-Path $externalRoot 'Antilatency.DisplayStylus.Unity.SDK'
$displayStylusSdkRepository = 'https://github.com/antilatency/Antilatency.DisplayStylus.Unity.SDK.git'
$displayStylusSdkTag = '2.0.0'
$displayStylusSdkCommit = 'bc9f8dd9c4e288038e6cbbc66939a9418517aef0'
$unityTestProjectTemplate = Join-Path $repositoryRoot 'tests\UnityTestProjectTemplate'
$unityTestProjectPath = Join-Path $externalRoot 'Antilatency.DisplayStylus.Unity.TestProject'

New-Item -ItemType Directory -Force -Path $externalRoot | Out-Null

function Initialize-SparseSdkClone(
    [string]$Path,
    [string]$Branch,
    [string]$Commit,
    [string[]]$SparseDirectories) {
    if (!(Test-Path -LiteralPath (Join-Path $Path '.git'))) {
        git clone --filter=blob:none --sparse --no-checkout $sdkRepository $Path
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to clone the Antilatency SDK repository."
        }
    }

    git -C $Path fetch --depth 1 origin "refs/heads/$Branch"
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to fetch Antilatency SDK branch $Branch."
    }

    $fetchedCommit = git -C $Path rev-parse FETCH_HEAD
    if ($LASTEXITCODE -ne 0 -or $fetchedCommit -ne $Commit) {
        throw "Antilatency SDK branch $Branch no longer resolves to the pinned commit $Commit."
    }

    git -C $Path sparse-checkout set @SparseDirectories
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to configure sparse checkout for $Path."
    }

    git -C $Path checkout --detach $Commit
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to check out Antilatency SDK commit $Commit."
    }
}

function Initialize-DisplayStylusSdkClone {
    if (!(Test-Path -LiteralPath (Join-Path $displayStylusSdkPath '.git'))) {
        git clone --filter=blob:none --no-checkout `
            $displayStylusSdkRepository `
            $displayStylusSdkPath
        if ($LASTEXITCODE -ne 0) {
            throw 'Failed to clone the Antilatency Display Stylus Unity SDK repository.'
        }
    }

    git -C $displayStylusSdkPath fetch --depth 1 origin "refs/tags/$displayStylusSdkTag"
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to fetch Display Stylus Unity SDK tag $displayStylusSdkTag."
    }

    $fetchedCommit = git -C $displayStylusSdkPath rev-parse 'FETCH_HEAD^{commit}'
    if ($LASTEXITCODE -ne 0 -or $fetchedCommit -ne $displayStylusSdkCommit) {
        throw "Display Stylus Unity SDK tag $displayStylusSdkTag does not resolve to the pinned commit $displayStylusSdkCommit."
    }

    git -C $displayStylusSdkPath checkout --detach $displayStylusSdkCommit
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to check out Display Stylus Unity SDK commit $displayStylusSdkCommit."
    }
}

Initialize-SparseSdkClone `
    -Path $runtimeSdkPath `
    -Branch $runtimeSdkBranch `
    -Commit $runtimeSdkCommit `
    -SparseDirectories @('Runtime/Api', 'Runtime/Modules', 'Plugins/Windows/x64')
Initialize-SparseSdkClone `
    -Path $nativeSdkPath `
    -Branch $nativeSdkBranch `
    -Commit $nativeSdkCommit `
    -SparseDirectories @('Api', 'Bin/WindowsDesktop/x64')
Initialize-DisplayStylusSdkClone

$requiredRuntime = Join-Path $runtimeSdkPath 'Plugins\Windows\x64\AntilatencyAltEnvironmentSides.dll'
$requiredUnityModule = Join-Path $runtimeSdkPath 'Runtime\Modules\DeviceNetwork\DeviceNetwork.cs'
$requiredApi = Join-Path $nativeSdkPath 'Api\Antilatency.PhysicalConfigurableEnvironment.cs'
$requiredDisplayStylusSdk = Join-Path $displayStylusSdkPath 'Runtime\Connection\DisplayStylusConnection.cs'
if (
    !(Test-Path -LiteralPath $requiredRuntime) -or
    !(Test-Path -LiteralPath $requiredUnityModule) -or
    !(Test-Path -LiteralPath $requiredApi) -or
    !(Test-Path -LiteralPath $requiredDisplayStylusSdk)) {
    throw 'An external SDK checkout is incomplete. Delete the affected external directory and rerun bootstrap.'
}

if (!(Test-Path -LiteralPath $unityTestProjectTemplate)) {
    throw "Unity test project template is missing: $unityTestProjectTemplate"
}

New-Item -ItemType Directory -Force -Path $unityTestProjectPath | Out-Null
Get-ChildItem -LiteralPath $unityTestProjectTemplate | Copy-Item `
    -Destination $unityTestProjectPath `
    -Recurse `
    -Force

$unityPackagesLock = Join-Path $unityTestProjectPath 'Packages\packages-lock.json'
if (Test-Path -LiteralPath $unityPackagesLock) {
    Remove-Item -LiteralPath $unityPackagesLock
}

dotnet restore (Join-Path $repositoryRoot 'Antilatency.DisplayStylus.Proxy.sln')
Write-Host "Dependencies and Unity test project are ready."
