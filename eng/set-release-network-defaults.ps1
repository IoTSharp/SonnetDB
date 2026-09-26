[CmdletBinding()]
param([Parameter(Mandatory)][string]$AppSettingsPath)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$settings = Get-Content -LiteralPath $AppSettingsPath -Raw | ConvertFrom-Json
foreach ($endpoint in $settings.Kestrel.Endpoints.PSObject.Properties) {
    $url = [UriBuilder]::new($endpoint.Value.Url)
    if ($url.Scheme -notin @('http', 'https')) { throw "Unsupported bundle endpoint: $($endpoint.Name)" }
    $url.Host = '127.0.0.1'
    $endpoint.Value.Url = $url.Uri.AbsoluteUri.TrimEnd('/')
}

# Bundles carry documented bootstrap credentials for local setup. No optional
# protocol may open a separate listener or connect to external brokers by default.
foreach ($protocol in @('Mqtt', 'Coap', 'LineProtocolUdp', 'Modbus')) {
    $settings.SonnetDBServer.$protocol.Enabled = $false
}
$settings.SonnetDBServer.Mqtt.ExternalClient.Enabled = $false
$settings.SonnetDBServer.Mqtt.Sparkplug.Enabled = $false
$settings.SonnetDBServer.Coap.Dtls.Enabled = $false

$settings | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $AppSettingsPath -Encoding utf8
Write-Host 'Release Server network defaults: loopback HTTP/Frame; optional protocols disabled.'
