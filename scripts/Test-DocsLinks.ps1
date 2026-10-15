#requires -Version 7.2
param()
$ErrorActionPreference = 'Stop'
& node (Join-Path $PSScriptRoot 'docs-links/check.mjs')
if ($LASTEXITCODE -ne 0) { throw "Markdown link check failed (exit $LASTEXITCODE). Run npm ci at the repository root if dependencies are missing." }
