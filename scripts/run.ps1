[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repositoryRoot 'src\Antilatency.DisplayStylus.Proxy.Server\Antilatency.DisplayStylus.Proxy.Server.csproj'

dotnet run --project $project --configuration Release
