param(
    [Parameter(Mandatory = $true)]
    [string] $OutputPath,
    [string] $WorkPath,
    [string] $ExpectedCommitSha,
    [string] $ExpectedTargetHardwareId,
    [string] $ExpectedTargetHardwareContract
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($PSVersionTable.PSVersion.Major -lt 7) {
    throw 'PowerShell 7 or newer is required.'
}

function Invoke-GitText([string[]] $Arguments) {
    try {
        $result = & git @Arguments 2>$null
        if ($LASTEXITCODE -ne 0) {
            return $null
        }
        return ($result -join "`n").Trim()
    }
    catch {
        return $null
    }
}

function Read-EnvironmentValue([string] $Name, [string] $Fallback) {
    $value = [Environment]::GetEnvironmentVariable($Name)
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $Fallback
    }

    return ("$($value.Trim() -replace '\s+', ' ')")
}

function Invoke-LinuxCommand([string] $Name, [string[]] $Arguments) {
    try {
        $command = Get-Command -Name $Name -CommandType Application -ErrorAction Stop
        $output = @(& $command.Source @Arguments 2>$null)
        if ($LASTEXITCODE -ne 0) {
            return $null
        }

        return ($output -join "`n").Trim()
    }
    catch {
        return $null
    }
}

function Decode-LinuxMountField([string] $Value) {
    if ($null -eq $Value) { return $null }
    return $Value.Replace('\040', ' ').Replace('\011', "`t").Replace('\012', "`n").Replace('\134', '\')
}

function Get-LinuxWorkloadMount([string] $Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $null }
    try { $fullPath = [IO.Path]::GetFullPath($Path) } catch { return $null }
    $json = Invoke-LinuxCommand 'findmnt' @('--json', '--noheadings', '--raw', '--target', $fullPath, '--output', 'TARGET,SOURCE,FSTYPE,MAJ:MIN')
    if ([string]::IsNullOrWhiteSpace($json)) { return $null }
    try {
        $parsed = $json | ConvertFrom-Json -Depth 16
        $fileSystem = @($parsed.filesystems)[0]
        if ($null -eq $fileSystem) { return $null }
        return [ordered]@{
            path = $fullPath
            mountPoint = Decode-LinuxMountField ([string]$fileSystem.target)
            source = Decode-LinuxMountField ([string]$fileSystem.source)
            fileSystem = Decode-LinuxMountField ([string]$fileSystem.fstype)
            deviceId = Decode-LinuxMountField ([string]$fileSystem.'maj:min')
            resolutionSource = 'findmnt'
        }
    }
    catch {
        return $null
    }
}

function Get-LinuxMemoryBytes {
    $memInfo = '/proc/meminfo'
    if (-not (Test-Path -LiteralPath $memInfo -PathType Leaf)) {
        return [long]0
    }

    $line = Get-Content -LiteralPath $memInfo -Encoding utf8 | Where-Object { $_ -match '^MemTotal:\s+(\d+)\s+kB$' } | Select-Object -First 1
    if ($null -eq $line) {
        return [long]0
    }

    if ($line -notmatch '^MemTotal:\s+(\d+)\s+kB$') {
        return [long]0
    }

    return [long]$Matches[1] * 1KB
}

function Get-FileSystemVolumes {
    $volumes = [System.Collections.Generic.List[object]]::new()
    foreach ($drive in Get-PSDrive -PSProvider FileSystem) {
        try {
            $info = [System.IO.DriveInfo]::new($drive.Root)
            $volumes.Add([ordered]@{
                root = $drive.Root
                fileSystem = $info.DriveFormat
                totalBytes = $info.TotalSize
                availableBytes = $info.AvailableFreeSpace
                volumeLabel = $info.VolumeLabel
            })
        }
        catch {
            $volumes.Add([ordered]@{
                root = $drive.Root
                fileSystem = 'UNAVAILABLE'
                totalBytes = -1
                availableBytes = -1
                volumeLabel = $null
            })
        }
    }

    return $volumes
}

$targetStatus = Read-EnvironmentValue 'SONNETDB_M19_TARGET_HARDWARE_STATUS' 'NOT_READY'
$targetId = Read-EnvironmentValue 'SONNETDB_M19_TARGET_HARDWARE_ID' 'UNDECLARED'
$targetContract = Read-EnvironmentValue 'SONNETDB_M19_TARGET_HARDWARE_CONTRACT' 'M19-#125-frozen-target-v1'
$storageModel = Read-EnvironmentValue 'SONNETDB_M19_STORAGE_MODEL' 'UNDECLARED'
$commitSha = Invoke-GitText @('rev-parse', '--verify', 'HEAD')
$worktreeStatus = Invoke-GitText @('status', '--porcelain=v1')

$processors = @()
$physicalMemoryBytes = [long]0
$diskInventory = @()
if ($IsWindows) {
    try {
        $processors = @(Get-CimInstance -ClassName Win32_Processor | ForEach-Object {
            [ordered]@{
                name = $_.Name
                manufacturer = $_.Manufacturer
                cores = $_.NumberOfCores
                logicalProcessors = $_.NumberOfLogicalProcessors
                maxClockSpeedMHz = $_.MaxClockSpeed
                processorId = $_.ProcessorId
            }
        })
    }
    catch {
        $processors = @()
    }
    try {
        $physicalMemoryBytes = [long](Get-CimInstance -ClassName Win32_ComputerSystem).TotalPhysicalMemory
    }
    catch {
        $physicalMemoryBytes = [long]0
    }
    try {
        $diskInventory = @(Get-CimInstance -ClassName Win32_DiskDrive | ForEach-Object {
            [ordered]@{
                model = $_.Model
                serialNumber = $_.SerialNumber
                firmwareRevision = $_.FirmwareRevision
                interfaceType = $_.InterfaceType
                mediaType = $_.MediaType
                sizeBytes = $_.Size
            }
        })
    }
    catch {
        $diskInventory = @()
    }
}
else {
    $physicalMemoryBytes = Get-LinuxMemoryBytes
    try {
        $cpuInfo = Get-Content -LiteralPath '/proc/cpuinfo' -Raw -Encoding utf8
        $modelLine = ($cpuInfo -split "`n" | Where-Object { $_ -match '^(model name|Hardware)\s*:' } | Select-Object -First 1)
        $processors = @([ordered]@{
            name = if ($null -eq $modelLine) { 'UNAVAILABLE' } else { ($modelLine -split ':', 2)[1].Trim() }
            manufacturer = 'UNAVAILABLE'
            cores = $null
            logicalProcessors = [Environment]::ProcessorCount
            maxClockSpeedMHz = $null
            processorId = $null
        })
    }
    catch {
        $processors = @()
    }
    try {
        $lsblk = Get-Command -Name 'lsblk' -CommandType Application -ErrorAction Stop
        $rawInventory = & $lsblk.Source --bytes --json --output NAME,KNAME,PATH,PKNAME,MODEL,SERIAL,REV,SIZE,TYPE,MAJ:MIN,MOUNTPOINTS 2>$null
        if ($LASTEXITCODE -ne 0) {
            throw "lsblk exited with code $LASTEXITCODE."
        }
        $diskInventory = @($rawInventory | ConvertFrom-Json -Depth 16 | Select-Object -ExpandProperty blockdevices)
    }
    catch {
        $diskInventory = @()
    }
}

if ([string]::IsNullOrWhiteSpace($WorkPath)) {
    $WorkPath = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($OutputPath))
}
if ([string]::IsNullOrWhiteSpace($WorkPath)) {
    $WorkPath = (Get-Location).Path
}
$workloadMount = $null
if (-not $IsWindows) {
    $workloadMount = Get-LinuxWorkloadMount $WorkPath
}
if ($null -eq $workloadMount) {
    try {
        $fullWorkPath = [IO.Path]::GetFullPath($WorkPath)
        $workRoot = [IO.Path]::GetPathRoot($fullWorkPath)
    }
    catch {
        $workRoot = 'UNAVAILABLE'
    }
    $workloadMount = [ordered]@{
        path = $WorkPath
        mountPoint = $workRoot
        source = if ($IsWindows) { $workRoot } else { 'UNAVAILABLE' }
        fileSystem = 'UNAVAILABLE'
        deviceId = 'UNAVAILABLE'
        resolutionSource = if ($IsWindows) { 'driveinfo-path-root' } else { 'findmnt-unavailable' }
    }
}

if (-not [string]::IsNullOrWhiteSpace($ExpectedCommitSha) -and -not [string]::Equals($commitSha, $ExpectedCommitSha, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Current HEAD '$commitSha' does not match expected '$ExpectedCommitSha'."
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedTargetHardwareId) -and -not [string]::Equals($targetId, $ExpectedTargetHardwareId, [StringComparison]::Ordinal)) {
    throw "Target hardware id '$targetId' does not match expected '$ExpectedTargetHardwareId'."
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedTargetHardwareContract) -and -not [string]::Equals($targetContract, $ExpectedTargetHardwareContract, [StringComparison]::Ordinal)) {
    throw "Target hardware contract '$targetContract' does not match expected '$ExpectedTargetHardwareContract'."
}

$snapshot = [ordered]@{
    schemaVersion = 1
    capturedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    targetHardware = [ordered]@{
        status = $targetStatus
        id = $targetId
        contract = $targetContract
        declarationSource = 'environment'
        storageModel = $storageModel
    }
    source = [ordered]@{
        commitSha = $commitSha
        worktreeClean = [string]::IsNullOrWhiteSpace($worktreeStatus)
        worktreeStatus = if ([string]::IsNullOrWhiteSpace($worktreeStatus)) { @() } else { @($worktreeStatus -split "`n") }
        remote = Invoke-GitText @('remote', 'get-url', 'origin')
    }
    machine = [ordered]@{
        name = [Environment]::MachineName
        operatingSystem = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
        processArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
        framework = [System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
        processorCount = [Environment]::ProcessorCount
        physicalMemoryBytes = $physicalMemoryBytes
        availableManagedMemoryBytes = [GC]::GetGCMemoryInfo().TotalAvailableMemoryBytes
        processors = $processors
    }
    storage = [ordered]@{
        declaredModel = $storageModel
        declarationSource = 'environment:SONNETDB_M19_STORAGE_MODEL'
        volumes = Get-FileSystemVolumes
        workloadMount = $workloadMount
        disks = $diskInventory
    }
}

$parent = [System.IO.Path]::GetDirectoryName([System.IO.Path]::GetFullPath($OutputPath))
if (-not [string]::IsNullOrWhiteSpace($parent)) {
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
}
$snapshot | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $OutputPath -Encoding utf8NoBOM
$snapshot | ConvertTo-Json -Depth 32
