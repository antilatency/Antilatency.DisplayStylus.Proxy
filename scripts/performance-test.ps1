[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repositoryRoot 'Antilatency.DisplayStylus.Proxy.sln'
$tests = Join-Path $repositoryRoot 'tests\Antilatency.DisplayStylus.Proxy.Tests\Antilatency.DisplayStylus.Proxy.Tests.csproj'

dotnet build $solution --configuration Release --verbosity minimal
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

dotnet test $tests `
    --configuration Release `
    --no-build `
    --filter 'Category=Performance' `
    --logger 'console;verbosity=detailed'
