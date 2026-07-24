[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$project = Join-Path $repositoryRoot 'src\Antilatency.DisplayStylus.Proxy.Server\Antilatency.DisplayStylus.Proxy.Server.csproj'
$productionRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\production'))
$expectedPrefix = $repositoryRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
$packages = @(
    [pscustomobject]@{
        Name = 'Self-contained'
        PublishProfile = 'win-x64'
        SelfContained = $true
        PublishDirectory = [System.IO.Path]::GetFullPath((Join-Path $productionRoot 'win-x64'))
        ArchivePath = [System.IO.Path]::GetFullPath(
            (Join-Path $productionRoot 'Antilatency.DisplayStylus.Proxy-win-x64.zip'))
    },
    [pscustomobject]@{
        Name = 'Framework-dependent'
        PublishProfile = 'win-x64-framework-dependent'
        SelfContained = $false
        PublishDirectory = [System.IO.Path]::GetFullPath(
            (Join-Path $productionRoot 'win-x64-framework-dependent'))
        ArchivePath = [System.IO.Path]::GetFullPath(
            (Join-Path $productionRoot 'Antilatency.DisplayStylus.Proxy-win-x64-framework-dependent.zip'))
    }
)

$targets = @($productionRoot)
$targets += $packages | ForEach-Object { $_.PublishDirectory }
$targets += $packages | ForEach-Object { $_.ArchivePath }
foreach ($target in $targets) {
    if (-not $target.StartsWith($expectedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Production target is outside the repository: $target"
    }
}

foreach ($package in $packages) {
    if (Test-Path -LiteralPath $package.PublishDirectory) {
        Remove-Item -LiteralPath $package.PublishDirectory -Recurse -Force
    }
    if (Test-Path -LiteralPath $package.ArchivePath) {
        Remove-Item -LiteralPath $package.ArchivePath -Force
    }
    New-Item -ItemType Directory -Path $package.PublishDirectory -Force | Out-Null
}

$dotnetRoot = Split-Path -Parent (Get-Command dotnet -ErrorAction Stop).Source
$dotnetNotices = @{
    'LICENSE.txt' = 'DOTNET-LICENSE.txt'
    'ThirdPartyNotices.txt' = 'DOTNET-THIRD-PARTY-NOTICES.txt'
}
$requiredPublishedFiles = @(
    'AntilatencyAltEnvironmentAdditionalMarkers.dll',
    'AntilatencyAltEnvironmentRectangle.dll',
    'AntilatencyAltEnvironmentSelector.dll',
    'AntilatencyAltEnvironmentSides.dll',
    'AntilatencyAltTracking.dll',
    'AntilatencyDeviceNetwork.dll',
    'AntilatencyHardwareExtensionInterface.dll',
    'AntilatencyPhysicalConfigurableEnvironment.dll',
    'AntilatencySDK-LICENSE.txt',
    'appsettings.json'
)

foreach ($package in $packages) {
    $selfContained = $package.SelfContained.ToString().ToLowerInvariant()
    $publishSucceeded = $false
    for ($attempt = 1; $attempt -le 3; $attempt++) {
        dotnet publish $project `
            --configuration Release `
            --runtime win-x64 `
            --self-contained $selfContained `
            --output $package.PublishDirectory `
            -p:PublishProfile=$($package.PublishProfile)
        if ($LASTEXITCODE -eq 0) {
            $publishSucceeded = $true
            break
        }
        if ($attempt -lt 3) {
            Start-Sleep -Milliseconds 750
        }
    }
    if (-not $publishSucceeded) {
        throw "$($package.Name) dotnet publish failed after three attempts."
    }

    # Project-reference symbols and the IIS native module are not used by this
    # standalone Kestrel process. Keep the deployable directory production-only.
    Get-ChildItem -LiteralPath $package.PublishDirectory -Filter '*.pdb' -File |
        Remove-Item -Force
    $iisModule = Join-Path $package.PublishDirectory 'aspnetcorev2_inprocess.dll'
    if (Test-Path -LiteralPath $iisModule) {
        Remove-Item -LiteralPath $iisModule -Force
    }

    $packageReadme = Join-Path $package.PublishDirectory 'README.txt'
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'packaging\PRODUCTION_README.txt') `
        -Destination $packageReadme
    if (-not $package.SelfContained) {
        [System.IO.File]::AppendAllText(
            $packageReadme,
            "`r`nRequires the .NET 10 ASP.NET Core Runtime (x64).`r`n",
            [System.Text.UTF8Encoding]::new($false))
    }
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'packaging\run-proxy.cmd') `
        -Destination (Join-Path $package.PublishDirectory 'run-proxy.cmd')

    foreach ($notice in $dotnetNotices.GetEnumerator()) {
        $source = Join-Path $dotnetRoot $notice.Key
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Required .NET redistribution notice is missing: $source"
        }
        Copy-Item -LiteralPath $source `
            -Destination (Join-Path $package.PublishDirectory $notice.Value)
    }

    $executable = Join-Path $package.PublishDirectory 'Antilatency.DisplayStylus.Proxy.exe'
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "Published proxy executable was not created: $executable"
    }
    foreach ($requiredFile in $requiredPublishedFiles) {
        $requiredPath = Join-Path $package.PublishDirectory $requiredFile
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "$($package.Name) package is missing required file: $requiredFile"
        }
    }

    $checksumLines = Get-ChildItem -LiteralPath $package.PublishDirectory -File |
        Sort-Object Name |
        ForEach-Object {
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            "$hash *$($_.Name)"
        }
    [System.IO.File]::WriteAllLines(
        (Join-Path $package.PublishDirectory 'SHA256SUMS.txt'),
        [string[]]$checksumLines,
        [System.Text.UTF8Encoding]::new($false))

    $archiveCreated = $false
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        try {
            Compress-Archive `
                -Path (Join-Path $package.PublishDirectory '*') `
                -DestinationPath $package.ArchivePath `
                -CompressionLevel Optimal `
                -ErrorAction Stop
            $archiveCreated = $true
            break
        }
        catch {
            if (Test-Path -LiteralPath $package.ArchivePath) {
                Remove-Item -LiteralPath $package.ArchivePath -Force
            }
            if ($attempt -eq 5) {
                throw
            }
            # Antivirus and indexing services can briefly hold a newly published
            # executable. Wait for that transient handle to close.
            Start-Sleep -Milliseconds 750
        }
    }
    if (-not $archiveCreated) {
        throw "$($package.Name) ZIP archive was not created."
    }

    $publishedFiles = Get-ChildItem -LiteralPath $package.PublishDirectory -File
    $nativeLibraries = $publishedFiles | Where-Object {
        $_.Extension -eq '.dll' -and $_.Name -in $requiredPublishedFiles
    }
    $archive = Get-Item -LiteralPath $package.ArchivePath

    Write-Host ''
    Write-Host "$($package.Name) executable: $executable"
    Write-Host "Native Antilatency DLLs: $($nativeLibraries.Count)"
    Write-Host "Package files: $($publishedFiles.Count)"
    Write-Host "ZIP archive: $($package.ArchivePath) ($([math]::Round($archive.Length / 1MB, 2)) MiB)"
}
