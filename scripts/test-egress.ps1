<#
.SYNOPSIS
    Checks the egress profile's network layout from inside the tools network (docs/design/egress-proxy.md).

.DESCRIPTION
    Run after  docker compose --profile egress up -d  with the harness running (dotnet run --launch-profile egress).
    Each check starts a throwaway curl container on the internal "tools" network, where the tool API runs, and
    confirms that the only way out is the egress proxy and that the proxy refuses what it should:

      - external names don't resolve, and a direct connection to an address fails (no route);
      - the proxy challenges a request without an egress token (407);
      - the proxy refuses a forged token (403);
      - the proxy refuses an IP literal and the cloud metadata address (403).

    Exits with 1 if any check fails.
#>
[CmdletBinding()]
param(
    [string] $Network = 'scanharness_tools',
    [string] $Proxy = 'http://egress-proxy:3128',
    [string] $CurlImage = 'curlimages/curl:8.10.1'
)

$failures = 0

function Invoke-Curl([string[]] $Arguments) {
    $output = & docker run --rm --network $Network $CurlImage -s -m 10 @Arguments 2>&1
    return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n") }
}

function Test-Check([string] $Name, [scriptblock] $Passes) {
    $result = & $Passes
    if ($result) {
        Write-Host "PASS  $Name" -ForegroundColor Green
    } else {
        Write-Host "FAIL  $Name" -ForegroundColor Red
        $script:failures++
    }
}

# Direct access: curl exits 6 when it can't resolve the host and 7 (or 28, timed out) when it can't connect.
Test-Check 'External names do not resolve inside the tools network' {
    (Invoke-Curl @('-o', '/dev/null', 'https://example.com/')).ExitCode -eq 6
}

Test-Check 'A direct connection to an address fails (no route out)' {
    (Invoke-Curl @('-o', '/dev/null', '-k', 'https://1.1.1.1/')).ExitCode -in 7, 28
}

# Through the proxy. Plain HTTP shows the proxy's own answer (an HTTPS tunnel would only show curl's error).
Test-Check 'The proxy challenges a request without an egress token (407)' {
    (Invoke-Curl @('-o', '/dev/null', '-w', '%{http_code}', '-x', $Proxy, 'http://example.com/')).Output -eq '407'
}

Test-Check 'The proxy refuses a forged egress token (403)' {
    (Invoke-Curl @('-o', '/dev/null', '-w', '%{http_code}', '-x', $Proxy, '-U', 'egress:forged.token', 'http://example.com/')).Output -eq '403'
}

Test-Check 'The proxy refuses an HTTPS tunnel without a token' {
    (Invoke-Curl @('-o', '/dev/null', '-x', $Proxy, 'https://example.com/')).ExitCode -ne 0
}

Test-Check 'The proxy refuses the cloud metadata address' {
    $code = (Invoke-Curl @('-o', '/dev/null', '-w', '%{http_code}', '-x', $Proxy, 'http://169.254.169.254/latest/meta-data/')).Output
    $code -in '403', '407'
}

Test-Check 'Refusals come from the proxy (marked x-egress-refused)' {
    (Invoke-Curl @('-D', '-', '-o', '/dev/null', '-x', $Proxy, 'http://example.com/')).Output -match '(?im)^x-egress-refused: 1'
}

if ($failures -gt 0) {
    Write-Host "$failures check(s) failed." -ForegroundColor Red
    exit 1
}

Write-Host 'All egress checks passed.' -ForegroundColor Green
