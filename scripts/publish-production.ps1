[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$project = Join-Path $repositoryRoot 'src\Antilatency.DisplayStylus.Proxy.Server\Antilatency.DisplayStylus.Proxy.Server.csproj'
$productionRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts\production'))
$publishDirectory = [System.IO.Path]::GetFullPath((Join-Path $productionRoot 'win-x64'))
$archivePath = [System.IO.Path]::GetFullPath((Join-Path $productionRoot 'Antilatency.DisplayStylus.Proxy-win-x64.zip'))
$expectedPrefix = $repositoryRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar

foreach ($target in @($productionRoot, $publishDirectory, $archivePath)) {
    if (-not $target.StartsWith($expectedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Production target is outside the repository: $target"
    }
}

if (Test-Path -LiteralPath $publishDirectory) {
    Remove-Item -LiteralPath $publishDirectory -Recurse -Force
}
if (Test-Path -LiteralPath $archivePath) {
    Remove-Item -LiteralPath $archivePath -Force
}
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

$publishSucceeded = $false
for ($attempt = 1; $attempt -le 3; $attempt++) {
    dotnet publish $project `
        --configuration Release `
        --runtime win-x64 `
        --self-contained true `
        --output $publishDirectory `
        -p:PublishProfile=win-x64
    if ($LASTEXITCODE -eq 0) {
        $publishSucceeded = $true
        break
    }
    if ($attempt -lt 3) {
        Start-Sleep -Milliseconds 750
    }
}
if (-not $publishSucceeded) {
    throw 'dotnet publish failed after three attempts.'
}

# Project-reference symbols and the IIS native module are not used by this
# standalone Kestrel process. Keep the deployable directory production-only.
Get-ChildItem -LiteralPath $publishDirectory -Filter '*.pdb' -File |
    Remove-Item -Force
$iisModule = Join-Path $publishDirectory 'aspnetcorev2_inprocess.dll'
if (Test-Path -LiteralPath $iisModule) {
    Remove-Item -LiteralPath $iisModule -Force
}

Copy-Item -LiteralPath (Join-Path $repositoryRoot 'packaging\PRODUCTION_README.txt') `
    -Destination (Join-Path $publishDirectory 'README.txt')
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'packaging\run-proxy.cmd') `
    -Destination (Join-Path $publishDirectory 'run-proxy.cmd')

$dotnetRoot = Split-Path -Parent (Get-Command dotnet -ErrorAction Stop).Source
$dotnetNotices = @{
    'LICENSE.txt' = 'DOTNET-LICENSE.txt'
    'ThirdPartyNotices.txt' = 'DOTNET-THIRD-PARTY-NOTICES.txt'
}
foreach ($notice in $dotnetNotices.GetEnumerator()) {
    $source = Join-Path $dotnetRoot $notice.Key
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Required .NET redistribution notice is missing: $source"
    }
    Copy-Item -LiteralPath $source `
        -Destination (Join-Path $publishDirectory $notice.Value)
}

$executable = Join-Path $publishDirectory 'Antilatency.DisplayStylus.Proxy.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "Published proxy executable was not created: $executable"
}

$checksumLines = Get-ChildItem -LiteralPath $publishDirectory -File |
    Sort-Object Name |
    ForEach-Object {
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash *$($_.Name)"
    }
[System.IO.File]::WriteAllLines(
    (Join-Path $publishDirectory 'SHA256SUMS.txt'),
    [string[]]$checksumLines,
    [System.Text.UTF8Encoding]::new($false))

$archiveCreated = $false
for ($attempt = 1; $attempt -le 5; $attempt++) {
    try {
        Compress-Archive `
            -Path (Join-Path $publishDirectory '*') `
            -DestinationPath $archivePath `
            -CompressionLevel Optimal `
            -ErrorAction Stop
        $archiveCreated = $true
        break
    }
    catch {
        if (Test-Path -LiteralPath $archivePath) {
            Remove-Item -LiteralPath $archivePath -Force
        }
        if ($attempt -eq 5) {
            throw
        }
        # Antivirus and indexing services can briefly hold a newly published
        # self-contained executable. Wait for that transient handle to close.
        Start-Sleep -Milliseconds 750
    }
}
if (-not $archiveCreated) {
    throw 'Production ZIP archive was not created.'
}

$publishedFiles = Get-ChildItem -LiteralPath $publishDirectory -File
$nativeLibraries = $publishedFiles | Where-Object { $_.Name -like 'Antilatency*.dll' }
$archive = Get-Item -LiteralPath $archivePath

Write-Host ''
Write-Host "Production executable: $executable"
Write-Host "Native Antilatency DLLs: $($nativeLibraries.Count)"
Write-Host "Package files: $($publishedFiles.Count)"
Write-Host "ZIP archive: $archivePath ($([math]::Round($archive.Length / 1MB, 2)) MiB)"
