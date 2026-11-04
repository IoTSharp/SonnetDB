<#
.SYNOPSIS
以独占 PowerShell 7 子进程捕获现场脚本及其原生 SSH 子进程的完整标准输出。
.DESCRIPTION
调用方继续使用批准的 SSH stdin wrapper。本包装只负责进程所有权、期限、非空 JSON 和不覆盖回执；
原生子进程继承的 stdout 不会经过父脚本的 PowerShell 对象管道，必须在进程边界重定向。
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ScriptPath,
    [Parameter(Mandatory)][string]$OutputPath,
    [ValidateRange(1, 600)][int]$TimeoutSeconds = 430
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'PowerShell 7 required' }
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$fieldScriptPath = [IO.Path]::GetFullPath($ScriptPath)
$receiptPath = [IO.Path]::GetFullPath($OutputPath)
if (-not [IO.File]::Exists($fieldScriptPath)) { throw 'The requested script is missing.' }
if ([IO.File]::Exists($receiptPath)) { throw 'Receipt already exists; choose a new output path.' }
if (-not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($receiptPath))) { throw 'Receipt parent directory is missing.' }

$startInfo = [Diagnostics.ProcessStartInfo]::new('C:\Program Files\PowerShell\7\pwsh.exe')
$startInfo.UseShellExecute = $false
$startInfo.CreateNoWindow = $true
$startInfo.RedirectStandardOutput = $true
$startInfo.RedirectStandardError = $true
$startInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
$startInfo.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
$fieldArgs = @('-NoProfile', '-File', $fieldScriptPath)
foreach ($item in $fieldArgs) { $startInfo.ArgumentList.Add($item) }
$captureProcess = [Diagnostics.Process]::new()
$captureProcess.StartInfo = $startInfo
$captureStarted = $false
try {
    if (-not $captureProcess.Start()) { throw 'Could not start the owned field script.' }
    $captureStarted = $true
    $identity = [ordered]@{
        pid = $captureProcess.Id
        parentPid = $PID
        startedAt = $captureProcess.StartTime.ToUniversalTime().ToString('O')
        program = $startInfo.FileName
        argv = $fieldArgs
    }
    $stdoutTask = $captureProcess.StandardOutput.ReadToEndAsync()
    $stderrTask = $captureProcess.StandardError.ReadToEndAsync()
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $completed = $false
    # 最多每秒检查一次，同时保留迭代次数及墙钟上限；异常/取消走 finally 回收独占进程树。
    for ($attempt = 0; $attempt -lt $TimeoutSeconds -and $timer.Elapsed.TotalSeconds -lt $TimeoutSeconds; $attempt++) {
        if ($captureProcess.WaitForExit(1000)) { $completed = $true; break }
    }
    if (-not $completed) { throw 'Owned field script exceeded its process deadline.' }
    $stdout = $stdoutTask.WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
    $stderr = $stderrTask.WaitAsync([TimeSpan]::FromSeconds(10)).GetAwaiter().GetResult()
    if ([string]::IsNullOrWhiteSpace($stdout) -or $stdout.Length -gt 1048576) { throw 'Field stdout is empty or exceeds its capture budget.' }
    $null = ConvertFrom-Json -InputObject $stdout -Depth 32 -ErrorAction Stop
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes($stdout)
    $stream = [IO.File]::Open($receiptPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $stream.Write($bytes); $stream.Flush($true) }
    finally { $stream.Dispose() }
    $identity.outputPath = $receiptPath
    $identity.bytes = $bytes.Length
    $identity.sha256 = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($bytes))
    $identity.exitCode = $captureProcess.ExitCode
    if ($stderr.Length -gt 0) { [Console]::Error.Write($stderr) }
    $identity | ConvertTo-Json -Depth 4 -Compress
    exit $captureProcess.ExitCode
}
finally {
    if ($captureStarted -and -not $captureProcess.HasExited) {
        $captureProcess.Kill($true)
        $null = $captureProcess.WaitForExit(5000)
    }
    $captureProcess.Dispose()
}
