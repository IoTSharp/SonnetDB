#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$OutputPath = 'artifacts/nuget-credential-check.json',
    [switch]$Plan
)

$ErrorActionPreference = 'Stop'
$endpoint = 'https://www.nuget.org/api/v2/verifykey/SonnetDB/4.0.0'
if ($Plan) {
    [pscustomobject]@{ method = 'GET'; endpoint = $endpoint; maximumRequests = 1; timeoutSeconds = 30; secretRead = $false }
    return
}

# NuGetGallery VerifyPackageKeyAsync verifies ordinary publishing keys against an
# existing package. It records an audit/telemetry event; it does not upload or
# unlist a package. Temporary PackageVerificationApiKey values are single-use
# credentials and must not be configured as the organization's publishing key.
$report = [ordered]@{
    schemaVersion = 1
    checkedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    runId = $env:GITHUB_RUN_ID
    runAttempt = $env:GITHUB_RUN_ATTEMPT
    event = $env:GITHUB_EVENT_NAME
    workflowCommit = $env:GITHUB_SHA
    workflowRef = $env:GITHUB_REF
    endpoint = $endpoint
    method = 'GET'
    secretPresent = -not [string]::IsNullOrWhiteSpace($env:NUGET_API_KEY)
    httpStatus = $null
    expiryWarning = $false
    classification = 'MISSING_SECRET'
    status = 'NOT_READY'
    packageUploaded = $false
}

$handler = [Net.Http.HttpClientHandler]::new()
$handler.AllowAutoRedirect = $false
$handler.UseCookies = $false
$client = [Net.Http.HttpClient]::new($handler)
$client.Timeout = [TimeSpan]::FromSeconds(30)
$cancellation = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(30))
$request = $null
$response = $null
$stream = $null
try {
    if ($report.secretPresent) {
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get, $endpoint)
        $request.Headers.Add('X-NuGet-ApiKey', $env:NUGET_API_KEY)
        $request.Headers.UserAgent.ParseAdd('SonnetDB-Credential-Check/1.0')
        $response = $client.SendAsync($request, [Net.Http.HttpCompletionOption]::ResponseHeadersRead, $cancellation.Token).GetAwaiter().GetResult()
        $report.httpStatus = [int]$response.StatusCode
        $warnings = @($response.Headers.Warning) -join ' '
        $report.expiryWarning = $warnings -match '(?i)expir'
        $stream = $response.Content.ReadAsStreamAsync($cancellation.Token).GetAwaiter().GetResult()
        $buffer = [byte[]]::new(8193)
        $length = 0
        for ($read = 0; $read -lt 16 -and $length -lt $buffer.Length; $read++) {
            $count = $stream.ReadAsync($buffer, $length, $buffer.Length - $length, $cancellation.Token).GetAwaiter().GetResult()
            if ($count -eq 0) { break }
            $length += $count
        }
        if ($length -ge $buffer.Length -or $read -eq 16) {
            $report.classification = 'RESPONSE_LIMIT'
        }
        else {
            # Only a fixed classification is retained. Never log the raw body,
            # request headers, key, key length, key hash or exception message.
            $body = [Text.Encoding]::UTF8.GetString($buffer, 0, $length)
            $reason = $response.ReasonPhrase
            $report.classification = if ($body -match '(?i)expired' -or $reason -match '(?i)expired') { 'EXPIRED' }
            elseif ($report.expiryWarning) { 'EXPIRY_WARNING' }
            elseif ($report.httpStatus -eq 200) { 'VALID_FOR_EXISTING_PACKAGE' }
            elseif ($report.httpStatus -eq 401) { 'AUTHENTICATION_REJECTED' }
            elseif ($report.httpStatus -eq 403) { 'AUTHORIZATION_REJECTED' }
            elseif ($report.httpStatus -eq 404) { 'PACKAGE_NOT_FOUND' }
            else { 'HTTP_ERROR' }
            if ($report.classification -ceq 'VALID_FOR_EXISTING_PACKAGE') { $report.status = 'PASS' }
        }
    }
}
catch [OperationCanceledException] { $report.classification = 'TIMED_OUT' }
catch [Net.Http.HttpRequestException] { $report.classification = 'TRANSPORT_ERROR' }
catch [ArgumentException] { $report.classification = 'INVALID_SECRET_FORMAT' }
catch [FormatException] { $report.classification = 'INVALID_SECRET_FORMAT' }
catch { $report.classification = 'UNEXPECTED_LOCAL_ERROR' }
finally {
    if ($null -ne $stream) { $stream.Dispose() }
    if ($null -ne $response) { $response.Dispose() }
    if ($null -ne $request) { $request.Dispose() }
    $cancellation.Dispose()
    $client.Dispose()
    $handler.Dispose()
    $path = [IO.Path]::GetFullPath($OutputPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    $json = $report | ConvertTo-Json -Depth 3
    [IO.File]::WriteAllText($path, $json + "`n", [Text.UTF8Encoding]::new($false))
    Write-Output $json
}
if ($report.status -cne 'PASS') { exit 1 }
