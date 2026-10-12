<#
.SYNOPSIS
    Converts ExecutiveSummary.html to ExecutiveSummary.pdf using Microsoft Edge (or Chrome) in headless mode.

.EXAMPLE
    .\Build-ExecutiveSummary.ps1
    .\Build-ExecutiveSummary.ps1 -Html .\ExecutiveSummary.html -Output .\ExecutiveSummary.pdf
#>
param(
    [string]$Html = (Join-Path $PSScriptRoot "ExecutiveSummary.html"),
    [string]$Output = (Join-Path $PSScriptRoot "ExecutiveSummary.pdf")
)

$ErrorActionPreference = "Stop"

$browser = @(
    "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe",
    "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
    "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $browser) { throw "Microsoft Edge or Google Chrome is required to build the PDF." }

$htmlPath = (Resolve-Path $Html).Path
$outputPath = [System.IO.Path]::GetFullPath($Output)
$profileDir = Join-Path $env:TEMP "exec-summary-pdf-profile"

if (Test-Path $outputPath) { Remove-Item $outputPath -Force }

Start-Process -FilePath $browser -Wait -WindowStyle Hidden -ArgumentList @(
    "--headless",
    "--disable-gpu",
    "--no-first-run",
    "--disable-sync",
    "--disable-background-networking",
    "--disable-extensions",
    "--log-level=3",
    "--user-data-dir=`"$profileDir`"",
    "--no-pdf-header-footer",
    "--print-to-pdf-no-header",
    "--print-to-pdf=`"$outputPath`"",
    "`"file:///$($htmlPath -replace '\\', '/')`""
)

if (-not (Test-Path $outputPath)) { throw "The PDF was not created." }
Write-Host "Created $outputPath"
