#requires -Version 7.2
param([Parameter(Mandatory)][string]$Report)
$ErrorActionPreference = 'Stop'
[xml]$trx = Get-Content -LiteralPath $Report -Raw
$counts = $trx.TestRun.ResultSummary.Counters
$passed = [int]$counts.passed
$failed = [int]$counts.failed
$total = [int]$counts.total
$executed = [int]$counts.executed
$skipped = $total - $executed
$summary = "Offline tests: total=$total, passed=$passed, failed=$failed, skipped=$skipped"
Write-Host $summary
if ($env:GITHUB_STEP_SUMMARY) { Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY -Value $summary }
if ($total -le 0 -or $passed -le 0 -or $failed -ne 0 -or $executed -ne $passed -or $trx.TestRun.ResultSummary.outcome -ne 'Completed') {
    throw 'A nonempty, successful offline test report is required.'
}
