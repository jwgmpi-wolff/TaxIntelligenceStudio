<#
.SYNOPSIS
    Exports Azure retail prices to a CSV file in the collector container.

.DESCRIPTION
    Queries the public Azure Retail Prices API and writes every returned meter
    to a CSV file. The API needs no credential and no Azure subscription.

    Part numbers: the retail prices API does not publish them. A part number is
    the identifier that appears on an Azure invoice and in the price sheet, so
    it has to come from one of those. Supply a price sheet or invoice CSV with
    -PriceSheetPath, or let the script download the price sheet for you with
    -BillingAccountId / -BillingProfileId (this requires a billing role, see
    below). Every row is then joined to it on meterId and gains a partNumber
    column. Without either source the partNumber column is still written, but
    empty, so the CSV schema never changes.

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

.PARAMETER PriceSheetPath
    Price sheet or invoice CSV (or a .zip containing one) that supplies part
    numbers. Any export with a meter id column and a part number column works;
    the header names are matched loosely, so 'MeterID' / 'Meter ID' / 'meterId'
    and 'PartNumber' / 'Part Number' / 'Part Num' are all accepted.

.PARAMETER BillingAccountId
    Download the price sheet instead of supplying a file. Requires
    -BillingProfileId for Microsoft Customer Agreement accounts, and a billing
    role that permits price sheet download (Billing Profile Reader or higher).
    List accounts with: az billing account list

.PARAMETER BillingProfileId
    Billing profile for the price sheet download. Required with
    -BillingAccountId on a Microsoft Customer Agreement.
    List profiles with: az billing profile list --account-name <id>

.PARAMETER InvoiceId
    Download the price sheet for one specific invoice rather than the current
    open period. Use this to reproduce exactly what an invoice was billed at.

.PARAMETER OutputPath
    CSV file to write. Defaults to C:\temp\azure_retail_prices.csv.

.PARAMETER ResultsPath
    Collector container that receives the run log and the run result file.
    Defaults to C:\temp\TaxIntelligenceStudio.

.EXAMPLE
    .\scripts\Get-AzureRetailPrices.ps1 -ServiceName 'Virtual Machines' -ArmRegionName 'eastus'

.EXAMPLE
    .\scripts\Get-AzureRetailPrices.ps1 -ServiceName 'Virtual Machines' -PriceSheetPath C:\temp\pricesheet.csv

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

    [string]$PriceSheetPath,

    [string]$BillingAccountId,

    [string]$BillingProfileId,

    [string]$InvoiceId,

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

function Write-Note {
    param([string]$Message)
    Write-Host "    $Message" -ForegroundColor Yellow
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

# --- Part numbers -----------------------------------------------------------
#
# The retail prices API does not publish part numbers. They live on the invoice
# and in the price sheet, keyed by meter id, so a price sheet is the only way to
# attach them to retail prices.

# Header names vary between the EA price sheet, the MCA price sheet, and invoice
# exports, so columns are matched on their letters alone.
function Find-Column {
    param(
        [Parameter(Mandatory)][string[]]$Available,
        [Parameter(Mandatory)][string[]]$Candidates
    )

    foreach ($candidate in $Candidates) {
        $normalized = $candidate -replace '[^a-z0-9]', ''
        foreach ($column in $Available) {
            if (($column.ToLowerInvariant() -replace '[^a-z0-9]', '') -eq $normalized) {
                return $column
            }
        }
    }

    return $null
}

# Downloads the price sheet for a billing account, profile, or single invoice.
# The API is asynchronous: it returns an operation to poll, then a download URL.
function Get-PriceSheetDownload {
    param(
        [Parameter(Mandatory)][string]$Account,
        [string]$Profile,
        [string]$Invoice,
        [Parameter(Mandatory)][string]$Destination
    )

    if (-not (Get-Command az -ErrorAction SilentlyContinue)) {
        throw 'Downloading a price sheet needs the Azure CLI. Install it from https://aka.ms/installazurecli, or pass -PriceSheetPath instead.'
    }

    $token = (& az account get-access-token --resource https://management.azure.com --query accessToken -o tsv 2>$null)
    if (-not $token) {
        throw 'Not signed in to Azure. Run "az login", or pass -PriceSheetPath instead.'
    }

    $scope = "https://management.azure.com/providers/Microsoft.Billing/billingAccounts/$Account"
    if ($Profile) { $scope += "/billingProfiles/$Profile" }

    $uri = if ($Invoice) {
        "$scope/invoices/$Invoice/pricesheet/download?api-version=2020-05-01"
    }
    elseif ($Profile) {
        "$scope/providers/Microsoft.CostManagement/pricesheets/default/download?api-version=2022-06-01-preview"
    }
    else {
        "$scope/pricesheet/download?api-version=2020-05-01"
    }

    $headers = @{ Authorization = "Bearer $token" }

    try {
        $response = Invoke-WebRequest -Uri $uri -Method Post -Headers $headers -ErrorAction Stop
    }
    catch {
        $status = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { 0 }
        if ($status -eq 401 -or $status -eq 403) {
            throw ("Azure refused the price sheet download for billing account '$Account'. " +
                'Downloading a price sheet requires a billing role such as Billing Profile Reader, ' +
                'which an ordinary subscription owner does not have. Ask a billing administrator to ' +
                'grant the role or to send you the price sheet, then pass it with -PriceSheetPath.')
        }
        throw "The price sheet download request failed: $($_.Exception.Message)"
    }

    # 202 Accepted: poll the operation until it produces a download URL.
    $operation = $response.Headers['Location']
    if ($operation -is [array]) { $operation = $operation[0] }

    $downloadUrl = $null
    if ($response.StatusCode -eq 200 -and $response.Content) {
        $downloadUrl = ($response.Content | ConvertFrom-Json).properties.downloadUrl
    }

    $deadline = (Get-Date).AddMinutes(10)
    while (-not $downloadUrl -and $operation -and (Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 10
        $poll = Invoke-WebRequest -Uri $operation -Headers $headers -ErrorAction Stop
        if ($poll.StatusCode -eq 200 -and $poll.Content) {
            $downloadUrl = ($poll.Content | ConvertFrom-Json).properties.downloadUrl
        }
        Write-Detail 'Waiting for the price sheet to be generated...'
    }

    if (-not $downloadUrl) {
        throw 'The price sheet was not ready within 10 minutes. Download it from the Azure portal and pass -PriceSheetPath.'
    }

    Invoke-WebRequest -Uri $downloadUrl -OutFile $Destination -ErrorAction Stop
    return $Destination
}

# Builds a meter id -> part number lookup from a price sheet or invoice export.
function Import-PartNumberMap {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        throw "Price sheet not found at '$Path'."
    }

    $csvPath = $Path
    $extracted = $null

    if ([IO.Path]::GetExtension($Path) -eq '.zip') {
        $extracted = Join-Path ([IO.Path]::GetTempPath()) ('pricesheet-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
        Expand-Archive -LiteralPath $Path -DestinationPath $extracted -Force
        $inner = Get-ChildItem -LiteralPath $extracted -Recurse -Filter '*.csv' | Select-Object -First 1
        if (-not $inner) {
            throw "'$Path' does not contain a CSV price sheet."
        }
        $csvPath = $inner.FullName
    }

    try {
        $rows = @(Import-Csv -LiteralPath $csvPath)
        if ($rows.Count -eq 0) {
            throw "The price sheet at '$Path' has no rows."
        }

        $columns = $rows[0].PSObject.Properties.Name
        $meterColumn = Find-Column -Available $columns -Candidates @('meterId', 'meter id', 'meterid')
        $partColumn = Find-Column -Available $columns -Candidates @(
            'partNumber', 'part number', 'partNum', 'part num', 'part')

        if (-not $meterColumn) {
            throw ("The price sheet at '$Path' has no meter id column, so it cannot be joined to retail prices. " +
                "Columns found: $($columns -join ', ')")
        }
        if (-not $partColumn) {
            throw ("The price sheet at '$Path' has no part number column. Enterprise Agreement price sheets and " +
                "invoice exports include one; some Microsoft Customer Agreement exports do not. " +
                "Columns found: $($columns -join ', ')")
        }

        $map = @{}
        foreach ($row in $rows) {
            $meter = $row.$meterColumn
            $part = $row.$partColumn
            if ($meter -and $part) {
                $map[$meter.ToString().Trim().ToLowerInvariant()] = $part.ToString().Trim()
            }
        }

        if ($map.Count -eq 0) {
            throw "The price sheet at '$Path' produced no meter-to-part-number pairs."
        }

        Write-Detail "Part numbers: $($map.Count) meters from '$meterColumn' / '$partColumn'"
        return $map
    }
    finally {
        if ($extracted -and (Test-Path -LiteralPath $extracted)) {
            Remove-Item -LiteralPath $extracted -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
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
$partNumberMap = $null
$partNumberSource = $null
$matchedParts = 0

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
            partNumberSource   = $partNumberSource
            partNumbersKnown   = if ($partNumberMap) { $partNumberMap.Count } else { 0 }
            partNumbersMatched = $matchedParts
            partNumbersMissing = [Math]::Max(0, $RecordCount - $matchedParts)
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

    # Part numbers come from a price sheet, so resolve that first: a bad path or
    # a missing billing role should fail before a long export, not after it.
    $downloadedSheet = $null
    try {
        if ($PriceSheetPath) {
            Write-Step 'Loading part numbers from the price sheet'
            $partNumberMap = Import-PartNumberMap -Path ([System.Environment]::ExpandEnvironmentVariables($PriceSheetPath))
            $partNumberSource = $PriceSheetPath
        }
        elseif ($BillingAccountId) {
            Write-Step 'Downloading the price sheet for part numbers'
            $downloadedSheet = Join-Path $CollectorContainer ("pricesheet-" + (Get-Date).ToString('yyyyMMdd-HHmmss') + '.csv')
            Get-PriceSheetDownload -Account $BillingAccountId -Profile $BillingProfileId `
                -Invoice $InvoiceId -Destination $downloadedSheet | Out-Null
            $partNumberMap = Import-PartNumberMap -Path $downloadedSheet
            $partNumberSource = $downloadedSheet
        }
        else {
            Write-Note 'No price sheet supplied, so partNumber will be written empty. Part numbers come from your invoice or price sheet: pass -PriceSheetPath, or -BillingAccountId to download one.'
        }
    }
    finally {
        if ($downloadedSheet -and -not $partNumberMap -and (Test-Path -LiteralPath $downloadedSheet)) {
            Remove-Item -LiteralPath $downloadedSheet -Force -ErrorAction SilentlyContinue
        }
    }

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

    # Attach the part number from the price sheet. partNumber leads the row so
    # it lines up with how invoices are read, and is always present even when no
    # price sheet was supplied, so the CSV schema never changes.
    $rows = [System.Collections.Generic.List[object]]::new()
    foreach ($record in $records) {
        $partNumber = ''
        if ($partNumberMap -and $record.meterId) {
            $key = $record.meterId.ToString().Trim().ToLowerInvariant()
            if ($partNumberMap.ContainsKey($key)) {
                $partNumber = $partNumberMap[$key]
                $matchedParts++
            }
        }

        $rows.Add(($record | Select-Object @{ Name = 'partNumber'; Expression = { $partNumber } }, *))
    }

    if ($partNumberMap) {
        Write-Detail "Part numbers matched: $matchedParts of $($records.Count) records"
        if ($matchedParts -eq 0) {
            Write-Note 'No meter matched the price sheet. Check that the price sheet covers the same services and period as this query.'
        }
    }

    $outputDirectory = Split-Path -Path $OutputPath -Parent
    if ($outputDirectory -and -not (Test-Path -LiteralPath $outputDirectory)) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }

    Write-Step "Writing $($records.Count) records"
    try {
        $rows | Export-Csv $OutputPath -NoTypeInformation
    }
    catch [System.IO.IOException] {
        throw ("Could not write '$OutputPath' because another process has it open " +
            '(Excel is the usual cause). Close it, or pass a different -OutputPath, and re-run.')
    }

    $sizeKb = [math]::Round(((Get-Item -LiteralPath $OutputPath).Length / 1KB), 1)
    Write-Detail "CSV: $OutputPath ($sizeKb KB)"

    $partSummary = if ($partNumberMap) { " $matchedParts carry a part number." } else { '' }
    Write-CollectedResult -Status 'succeeded' `
        -Message "Exported $($records.Count) records to $OutputPath.$partSummary" `
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
if ($partNumberMap) {
    Write-Host "  Parts:   $matchedParts of $($records.Count) rows matched from $partNumberSource"
}
else {
    Write-Host '  Parts:   partNumber column written empty (no price sheet supplied)'
}
Write-Host "  Results: $CollectorContainer (collector container)"

if ($CollectorLog) { try { Stop-Transcript | Out-Null } catch { } }
