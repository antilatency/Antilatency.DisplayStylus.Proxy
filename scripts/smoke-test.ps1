[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$serverProject = Join-Path $repositoryRoot 'tests\Antilatency.DisplayStylus.Proxy.TestHost\Antilatency.DisplayStylus.Proxy.TestHost.csproj'
$serverDll = Join-Path $repositoryRoot 'tests\Antilatency.DisplayStylus.Proxy.TestHost\bin\Release\net10.0\Antilatency.DisplayStylus.Proxy.TestHost.dll'
$smokeRoot = Join-Path $repositoryRoot 'artifacts\smoke'
$baseUrl = 'http://127.0.0.1:48192'
$stdoutPath = Join-Path $smokeRoot 'server.stdout.log'
$stderrPath = Join-Path $smokeRoot 'server.stderr.log'

function Read-BinarySnapshot([byte[]]$payload) {
    $stream = [IO.MemoryStream]::new($payload, $false)
    $reader = [IO.BinaryReader]::new($stream, [Text.UTF8Encoding]::new($false, $true), $false)
    try {
        if ($reader.ReadUInt32() -ne 0x50534441) { throw 'Invalid binary snapshot magic.' }
        if ($reader.ReadUInt16() -ne 2) { throw 'Unsupported binary snapshot version.' }
        $flags = $reader.ReadUInt16()
        if (($flags -band 0xfffe) -ne 0) { throw 'Unsupported binary snapshot flags.' }
        $sequence = $reader.ReadInt64()
        $null = $reader.ReadInt64()
        $null = $reader.ReadUInt32()
        $driver = Read-BinaryString $reader

        $nodes = @()
        if (($flags -band 1) -ne 0) {
            $nodeCount = Read-BoundedCount $reader 65536 'node'
            $nodes = [object[]]::new($nodeCount)
            for ($nodeIndex = 0; $nodeIndex -lt $nodeCount; $nodeIndex++) {
                $nodeId = $reader.ReadUInt32()
                $parentId = if ($reader.ReadBoolean()) { $reader.ReadUInt32() } else { $null }
                $status = Read-BinaryString $reader
                $physicalPath = Read-BinaryString $reader
                $propertyCount = Read-BoundedCount $reader 65536 'property'
                $properties = @{}
                for ($propertyIndex = 0; $propertyIndex -lt $propertyCount; $propertyIndex++) {
                    $propertyKey = Read-BinaryString $reader
                    $propertyValue = Read-BinaryString $reader
                    $properties[$propertyKey] = $propertyValue
                }
                $nodes[$nodeIndex] = [pscustomobject]@{
                    Id = $nodeId
                    ParentId = $parentId
                    Status = $status
                    PhysicalPath = $physicalPath
                    Properties = $properties
                }
            }
        }

        if ($reader.ReadBoolean()) {
            $null = $reader.ReadBoolean()
            if ($reader.ReadBoolean()) { $null = $reader.ReadUInt32() }
            $null = $reader.ReadUInt32()
            $null = $reader.ReadUInt32()
            for ($index = 0; $index -lt 13; $index++) { $null = $reader.ReadSingle() }
        }

        $stylusCount = Read-BoundedCount $reader 4096 'stylus'
        for ($stylusIndex = 0; $stylusIndex -lt $stylusCount; $stylusIndex++) {
            $null = Read-BinaryString $reader
            $null = $reader.ReadUInt32()
            $null = $reader.ReadUInt32()
            $null = $reader.ReadBoolean()
            $null = $reader.ReadBoolean()
            for ($index = 0; $index -lt 13; $index++) { $null = $reader.ReadSingle() }
            $null = Read-BinaryString $reader
            $null = $reader.ReadSingle()
        }

        if ($stream.Position -ne $stream.Length) { throw 'Binary snapshot contains trailing bytes.' }
        return [pscustomobject]@{
            Sequence = $sequence
            Driver = $driver
            Nodes = $nodes
            StylusCount = $stylusCount
        }
    }
    finally {
        $reader.Dispose()
    }
}

function Read-BinaryString([IO.BinaryReader]$reader) {
    $length = $reader.ReadInt32()
    if ($length -lt 0 -or $length -gt 1048576) { throw "Invalid binary string length $length." }
    $bytes = $reader.ReadBytes($length)
    if ($bytes.Length -ne $length) { throw 'Truncated binary string.' }
    return [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
}

function Read-BoundedCount([IO.BinaryReader]$reader, [int]$maximum, [string]$name) {
    $count = $reader.ReadInt32()
    if ($count -lt 0 -or $count -gt $maximum) { throw "Invalid $name count $count." }
    return $count
}

New-Item -ItemType Directory -Force -Path $smokeRoot | Out-Null

dotnet build $serverProject --configuration Release --verbosity quiet
$serverProcess = Start-Process `
    -FilePath 'dotnet' `
    -ArgumentList @($serverDll) `
    -PassThru `
    -WindowStyle Hidden `
    -RedirectStandardOutput $stdoutPath `
    -RedirectStandardError $stderrPath

try {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    $health = $null
    while ($null -eq $health -and [DateTime]::UtcNow -lt $deadline) {
        try {
            $health = Invoke-RestMethod -Uri ($baseUrl + '/health') -TimeoutSec 1
        }
        catch {
            Start-Sleep -Milliseconds 100
        }
    }
    if ($null -eq $health) {
        throw "Server did not become healthy. See $stderrPath"
    }

    $http = [System.Net.WebClient]::new()
    $initialBytes = $http.DownloadData($baseUrl + '/api/v2/snapshot')
    $initial = Read-BinarySnapshot $initialBytes

    $secondStdoutPath = Join-Path $smokeRoot 'second.stdout.log'
    $secondInstance = Start-Process `
        -FilePath 'dotnet' `
        -ArgumentList @($serverDll) `
        -PassThru `
        -WindowStyle Hidden `
        -RedirectStandardOutput $secondStdoutPath `
        -RedirectStandardError (Join-Path $smokeRoot 'second.stderr.log')
    if (!$secondInstance.WaitForExit(5000)) {
        Stop-Process -Id $secondInstance.Id
        throw 'A second proxy instance unexpectedly kept running on the fixed port.'
    }
    $secondInstance.WaitForExit()
    $secondInstance.Refresh()
    if ((Get-Content -LiteralPath $secondStdoutPath -Raw) -notmatch 'already running') {
        throw 'A second proxy instance did not report the running server correctly.'
    }

    $leaseA = Invoke-RestMethod `
        -Method Post `
        -ContentType 'application/json' `
        -Body '{"clientId":"smoke-a","durationSeconds":30}' `
        -Uri ($baseUrl + '/api/v1/lease/acquire')

    try {
        Invoke-RestMethod `
            -Method Post `
            -ContentType 'application/json' `
            -Body '{"clientId":"smoke-b","durationSeconds":30}' `
            -Uri ($baseUrl + '/api/v1/lease/acquire') `
            -ErrorAction Stop | Out-Null
        $secondWriterStatus = 200
    }
    catch {
        $secondWriterStatus = [int]$_.Exception.Response.StatusCode
    }

    $writeBody = @{
        leaseId = $leaseA.lease.leaseId
        value = 'SmokeTag'
    } | ConvertTo-Json -Compress
    Invoke-RestMethod `
        -Method Put `
        -ContentType 'application/json' `
        -Body $writeBody `
        -Uri ($baseUrl + '/api/v1/nodes/2/properties/Tag') | Out-Null

    Start-Sleep -Milliseconds 100
    $updatedBytes = $http.DownloadData($baseUrl + '/api/v2/snapshot')
    $updated = Read-BinarySnapshot $updatedBytes

    $webSocketUrl = $baseUrl.Replace('http://', 'ws://') + '/api/v2/stream'
    $socket = [System.Net.WebSockets.ClientWebSocket]::new()
    try {
        $null = $socket.ConnectAsync([Uri]$webSocketUrl, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
        $buffer = [byte[]]::new(65536)
        $segment = [ArraySegment[byte]]::new($buffer)
        $message = [IO.MemoryStream]::new()
        do {
            $received = $socket.ReceiveAsync($segment, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
            if ($received.MessageType -ne [System.Net.WebSockets.WebSocketMessageType]::Binary) {
                throw "Expected a binary WebSocket snapshot, got $($received.MessageType)."
            }
            $message.Write($buffer, 0, $received.Count)
        } while (!$received.EndOfMessage)
        $streamed = Read-BinarySnapshot $message.ToArray()
        $message.Dispose()
    }
    finally {
        $socket.Dispose()
    }

    if ($health.status -ne 'ok') { throw 'Health endpoint failed.' }
    if ($updated.Nodes[1].Properties['Tag'] -ne 'SmokeTag') { throw 'Write command was not applied.' }
    if ($secondWriterStatus -ne 423) { throw "Expected HTTP 423 for the second writer, got $secondWriterStatus." }
    if ($streamed.Sequence -le 0) { throw 'WebSocket did not return a snapshot.' }

    [pscustomobject]@{
        BaseUrl = $baseUrl
        Driver = $health.driver
        InitialSequence = $initial.Sequence
        UpdatedSequence = $updated.Sequence
        WebSocketSequence = $streamed.Sequence
        Styluses = $updated.StylusCount
        SecondInstanceRejected = $true
        SecondWriterStatus = $secondWriterStatus
        UpdatedTag = $updated.Nodes[1].Properties['Tag']
    } | Format-List
}
finally {
    if ($null -ne $http) {
        $http.Dispose()
    }
    if (!$serverProcess.HasExited) {
        Stop-Process -Id $serverProcess.Id
    }
    $serverProcess.WaitForExit()
}
