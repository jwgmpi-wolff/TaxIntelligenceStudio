<#
.SYNOPSIS
    Exports Azure retail prices to a CSV file in the collector container.

.DESCRIPTION
    Queries the public Azure Retail Prices API and writes every returned meter
    to a CSV file. The API needs no credential and no Azure subscription.

    Results are written to the collector container, the single local folder that
    every result produced by this repository is collected to
    (C:\temp\TaxIntelligenceStudio by default). The CSV itself defaults to
    C:\temp\azure_retail_prices.csv so it is easy to open by hand.

    The API returns 100 items per page, so a broad query returns hundreds of
    thousands of rows and takes a long time. Narrow the result set with -Filter,
    -ServiceName, -ArmRegionName, or -MaxRecords.

.PARAMETER Filter
    Raw OData filter passed to the API, for example:
    "serviceName eq 'Virtual Machines' and armRegionName eq 'eastus'".
    Takes precedence over -ServiceName and -ArmRegionName.

.PARAMETER ServiceName
    Restrict to a single service, for example 'Virtual Machines'.

.PARAMETER ArmRegionName
    Restrict to a single Azure region, for example 'eastus'.

.PARAMETER PriceType
    Restrict to a price type: Consumption, Reservation, or DevTestConsumption.

.PARAMETER CurrencyCode
    Currency for the returned prices. Defaults to USD.

.PARAMETER MaxRecords
    Stop after this many rows. 0 (the default) retrieves everything the filter
    matches.

.PARAMETER OutputPath
    CSV file to write. Defaults to C:\temp\azure_retail_prices.csv.

.PARAMETER ResultsPath
    Collector container that receives the run log and the run result file.
    Defaults to C:\temp\TaxIntelligenceStudio.

.EXAMPLE
    .\scripts\Get-AzureRetailPrices.ps1 -ServiceName 'Virtual Machines' -ArmRegionName 'eastus'

.EXAMPLE
    .\scripts\Get-AzureRetailPrices.ps1 -Filter "serviceName eq 'Azure OpenAI'" -CurrencyCode USD

.EXAMPLE
    .\scripts\Get-AzureRetailPrices.ps1 -MaxRecords 500
#>
[CmdletBinding()]
param(
    [string]$Filter,

    [string]$ServiceName,

    [string]$ArmRegionName,

    [ValidateSet('Consumption', 'Reservation', 'DevTestConsumption')]
    [string]$PriceType,

    [ValidatePattern('^[A-Za-z]{3}$')]
    [string]$CurrencyCode = 'USD',

    [ValidateRange(0, [int]::MaxValue)]
    [int]$MaxRecords = 0,

    [string]$OutputPath = 'C:\temp\azure_retail_prices.csv',

    [string]$ResultsPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$ApiRoot = 'https://prices.azure.com/api/retail/prices'
$ApiVersion = '2023-01-01-preview'

# ---------------------------------------------------------------------------
# Collector container
#
# The single local folder that every result from this script is collected to:
# the run log and a machine-readable run result file. The CSV is written to
# -OutputPath (C:\temp\azure_retail_prices.csv by default) and is recorded in
# the run result so the two are always linked.
# ---------------------------------------------------------------------------
$CollectorContainer = if ($ResultsPath) {
    [System.Environment]::ExpandEnvironmentVariables($ResultsPath)
}
else {
    'C:\temp\TaxIntelligenceStudio'
}

function Write-Step {
    param([string]$Message)
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Detail {
    param([string]$Message)
    Write-Host "    $Message" -ForegroundColor Green
}

function Build-Filter {
    if ($Filter) { return $Filter }

    $clauses = @()
    if ($ServiceName) { $clauses += "serviceName eq '$($ServiceName -replace "'", "''")'" }
    if ($ArmRegionName) { $clauses += "armRegionName eq '$($ArmRegionName -replace "'", "''")'" }
    if ($PriceType) { $clauses += "priceType eq '$PriceType'" }

    if ($clauses.Count -eq 0) { return $null }
    return ($clauses -join ' and ')
}

# --- Collector container ----------------------------------------------------

$CollectorLog = $null
$CollectorResult = $null
try {
    New-Item -ItemType Directory -Path $CollectorContainer -Force | Out-Null
    $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
    $CollectorLog = Join-Path $CollectorContainer "azure-retail-prices-$stamp.log"
    $CollectorResult = Join-Path $CollectorContainer "azure-retail-prices-result-$stamp.json"
    Start-Transcript -Path $CollectorLog -Force | Out-Null
}
catch {
    # Collecting the run must never prevent the export.
    Write-Warning "Could not open the collector container at ${CollectorContainer}: $($_.Exception.Message)"
    $CollectorLog = $null
}

$effectiveFilter = Build-Filter

function Write-CollectedResult {
    param([string]$Status, [string]$Message, [int]$RecordCount, [int]$PageCount)

    if (-not $CollectorResult) { return }

    try {
        [pscustomobject]@{
            collected          = (Get-Date).ToString('o')
            action             = 'azure-retail-prices'
            status             = $Status
            message            = $Message
            filter             = $effectiveFilter
            currencyCode       = $CurrencyCode
            maxRecords         = $MaxRecords
            recordCount        = $RecordCount
            pageCount          = $PageCount
            csvPath            = $OutputPath
            collectorContainer = $CollectorContainer
            log                = $CollectorLog
            computer           = $env:COMPUTERNAME
            user               = "$env:USERDOMAIN\$env:USERNAME"
        } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $CollectorResult -Encoding UTF8
    }
    catch {
        Write-Warning "Could not write the run result: $($_.Exception.Message)"
    }
}

# --- Export -----------------------------------------------------------------

$records = [System.Collections.Generic.List[object]]::new()
$pages = 0

try {
    Write-Step 'Exporting Azure retail prices'
    Write-Detail "Collector container: $CollectorContainer"
    Write-Detail "Currency: $CurrencyCode"
    Write-Detail "Filter: $(if ($effectiveFilter) { $effectiveFilter } else { '(none - retrieving every meter, this takes a while)' })"

    $query = "api-version=$ApiVersion&currencyCode='$CurrencyCode'"
    if ($effectiveFilter) {
        $query += '&$filter=' + [Uri]::EscapeDataString($effectiveFilter)
    }
    $uri = "${ApiRoot}?$query"

    while ($uri) {
        $response = Invoke-RestMethod -Uri $uri -Method Get -ErrorAction Stop
        $pages++

        foreach ($item in @($response.Items)) {
            $records.Add($item)
            if ($MaxRecords -gt 0 -and $records.Count -ge $MaxRecords) { break }
        }

        Write-Detail "Page $pages - $($records.Count) records"

        if ($MaxRecords -gt 0 -and $records.Count -ge $MaxRecords) { break }

        $uri = if ($response.PSObject.Properties.Name -contains 'NextPageLink') {
            $response.NextPageLink
        }
        else {
            $null
        }
    }

    if ($records.Count -eq 0) {
        throw "The API returned no meters for that filter. Check -Filter, -ServiceName, and -ArmRegionName."
    }

    $outputDirectory = Split-Path -Path $OutputPath -Parent
    if ($outputDirectory -and -not (Test-Path -LiteralPath $outputDirectory)) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }

    Write-Step "Writing $($records.Count) records"
    try {
        $records | Export-Csv $OutputPath -NoTypeInformation
    }
    catch [System.IO.IOException] {
        throw ("Could not write '$OutputPath' because another process has it open " +
            '(Excel is the usual cause). Close it, or pass a different -OutputPath, and re-run.')
    }

    $sizeKb = [math]::Round(((Get-Item -LiteralPath $OutputPath).Length / 1KB), 1)
    Write-Detail "CSV: $OutputPath ($sizeKb KB)"

    Write-CollectedResult -Status 'succeeded' `
        -Message "Exported $($records.Count) records to $OutputPath." `
        -RecordCount $records.Count -PageCount $pages
}
catch {
    Write-CollectedResult -Status 'failed' -Message $_.Exception.Message `
        -RecordCount $records.Count -PageCount $pages
    if ($CollectorLog) { try { Stop-Transcript | Out-Null } catch { } }
    throw
}

Write-Host ''
Write-Host "Azure retail prices exported." -ForegroundColor Green
Write-Host "  CSV:     $OutputPath"
Write-Host "  Results: $CollectorContainer (collector container)"

if ($CollectorLog) { try { Stop-Transcript | Out-Null } catch { } }
