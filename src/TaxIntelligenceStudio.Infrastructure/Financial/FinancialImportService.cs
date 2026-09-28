using System.Globalization;
using System.Text.RegularExpressions;
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.WordExtractor;

namespace TaxIntelligenceStudio.Infrastructure.Financial;

public sealed partial class FinancialImportService(IPdfOcrService? pdfOcrService = null)
    : IFinancialImportService
{
    private static readonly string[] DateHeaders =
        ["date", "transaction date", "posted date", "posting date", "run date", "settlement date"];
    private static readonly string[] DescriptionHeaders =
        ["action", "description", "memo", "name", "merchant", "details", "transaction"];
    private static readonly string[] AmountHeaders =
        ["amount", "amount $", "transaction amount", "net amount", "total"];
    private static readonly string[] DebitHeaders = ["debit", "withdrawal", "charge"];
    private static readonly string[] CreditHeaders = ["credit", "deposit", "payment"];

    public async Task<FinancialImportResult> ImportAsync(
        IReadOnlyCollection<string> filePaths,
        IProgress<FinancialImportProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var transactions = new List<FinancialTransaction>();
        var files = new List<FinancialFileImport>();
        var processedFiles = 0;
        foreach (var filePath in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("Financial input file was not found.", filePath);
            }

            var extension = Path.GetExtension(filePath).ToLowerInvariant();
            progress?.Report(new FinancialImportProgress(
                processedFiles,
                filePaths.Count,
                Path.GetFileName(filePath),
                transactions.Count,
                $"Reading {extension.TrimStart('.').ToUpperInvariant()} file"));
            IReadOnlyCollection<FinancialTransaction> imported;
            try
            {
                imported = extension switch
                {
                    ".csv" => await ImportCsvAsync(filePath, cancellationToken),
                    ".pdf" => await ImportPdfAsync(filePath, progress, cancellationToken),
                    ".xlsx" or ".xlsm" => ImportWorkbook(filePath),
                    ".txt" => await ImportTextAsync(filePath, cancellationToken),
                    _ => throw new NotSupportedException(
                        $"The financial file type '{extension}' is not supported.")
                };
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                files.Add(new FinancialFileImport(
                    Path.GetFileName(filePath),
                    extension,
                    0,
                    false,
                    exception.Message));
                processedFiles++;
                progress?.Report(new FinancialImportProgress(
                    processedFiles,
                    filePaths.Count,
                    Path.GetFileName(filePath),
                    transactions.Count,
                    "File could not be parsed"));
                continue;
            }

            transactions.AddRange(imported);
            files.Add(new FinancialFileImport(
                Path.GetFileName(filePath),
                extension,
                imported.Count,
                imported.Count > 0,
                imported.Count > 0
                    ? $"Parsed {imported.Count:N0} financial/tax record(s)."
                    : "No supported tax fields or transaction rows matched. Review the document layout."));
            processedFiles++;
            progress?.Report(new FinancialImportProgress(
                processedFiles,
                filePaths.Count,
                Path.GetFileName(filePath),
                transactions.Count,
                imported.Count > 0 ? "File parsed" : "No supported rows found"));
        }

        return new FinancialImportResult(transactions, files);
    }

    private static async Task<IReadOnlyCollection<FinancialTransaction>> ImportCsvAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var results = new List<FinancialTransaction>();
        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            useAsync: true);
        using var reader = new StreamReader(stream);
        using var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            BadDataFound = null,
            HeaderValidated = null,
            MissingFieldFound = null,
            DetectDelimiter = true,
            TrimOptions = TrimOptions.Trim
        });

        Dictionary<string, string>? headerLookup = null;
        for (var index = 0; index < 25 && await csv.ReadAsync(); index++)
        {
            var candidateHeaders = csv.Parser.Record ?? [];
            if (!LooksLikeHeader(candidateHeaders))
            {
                continue;
            }

            if (!csv.ReadHeader())
            {
                return results;
            }

            headerLookup = BuildHeaderLookup(csv.HeaderRecord ?? []);
            break;
        }

        if (headerLookup is null)
        {
            return results;
        }

        var row = csv.Parser.Row;
        while (await csv.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            row++;
            var description = BuildDescription(
                GetFirst(csv, headerLookup, DescriptionHeaders),
                GetFirst(csv, headerLookup, ["description"]),
                GetFirst(csv, headerLookup, ["symbol"]));
            var amount = ParseAmount(GetFirst(csv, headerLookup, AmountHeaders));

            if (amount is null)
            {
                var debit = ParseAmount(GetFirst(csv, headerLookup, DebitHeaders));
                var credit = ParseAmount(GetFirst(csv, headerLookup, CreditHeaders));
                amount = credit ?? (debit is null ? null : -Math.Abs(debit.Value));
            }

            if (amount is null || amount == 0 || string.IsNullOrWhiteSpace(description))
            {
                continue;
            }

            results.Add(new FinancialTransaction(
                ParseDate(GetFirst(csv, headerLookup, DateHeaders)),
                description,
                amount.Value,
                Path.GetFileName(filePath),
                row));
        }

        return results;
    }

    private async Task<IReadOnlyCollection<FinancialTransaction>> ImportPdfAsync(
        string filePath,
        IProgress<FinancialImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var extracted = await Task.Run(() =>
        {
            var results = new List<FinancialTransaction>();
            using var document = PdfDocument.Open(filePath);
            var row = 0;
            var pageNumber = 0;
            var extractedText = false;
            foreach (var page in document.GetPages())
            {
                pageNumber++;
                cancellationToken.ThrowIfCancellationRequested();
                var words = NearestNeighbourWordExtractor.Instance.GetWords(page.Letters).ToList();
                extractedText |= words.Count > 0;
                var lines = BuildPdfLines(words).ToList();
                var isCreditCardStatement =
                    Path.GetFileName(filePath).Contains("VISA", StringComparison.OrdinalIgnoreCase) ||
                    (lines.Any(line => line.Contains("Credit Card", StringComparison.OrdinalIgnoreCase)) &&
                     lines.Any(line => line.Contains("Transactions", StringComparison.OrdinalIgnoreCase)));
                foreach (var line in lines)
                {
                    row++;
                    var match = TransactionLinePattern().Match(line);
                    if (!match.Success)
                    {
                        AddTaxSummaryLine(results, filePath, row, line);
                        continue;
                    }

                    var amount = ParseAmount(match.Groups["amount"].Value);
                    if (amount is null || amount == 0)
                    {
                        continue;
                    }

                    var description = WhitespacePattern().Replace(
                        match.Groups["description"].Value.Trim(),
                        " ");
                    results.Add(new FinancialTransaction(
                        ParseDate(match.Groups["date"].Value),
                        description,
                        ApplyStatementSign(description, amount.Value, isCreditCardStatement),
                        Path.GetFileName(filePath),
                        row,
                        IsInformational: isCreditCardStatement &&
                            ContainsAny(description, "payment", "credit", "refund")));
                }

                AddForm1098Facts(results, filePath, words, pageNumber);
                AddRecognizedTaxFacts(results, filePath, new OcrPageText(pageNumber, lines));
                AddFidelityFacts(results, filePath, pageNumber, lines);
            }

            return new PdfTextExtraction(NormalizeRecords(results), extractedText);
        }, cancellationToken);

        if (extracted.HasText && extracted.Transactions.Count > 0)
        {
            return extracted.Transactions;
        }

        if (pdfOcrService is null)
        {
            throw new InvalidDataException(
                "This PDF contains no extractable text. Local OCR is unavailable in this application context.");
        }

        var ocrProgress = new Progress<PdfOcrProgress>(update =>
            progress?.Report(new FinancialImportProgress(
                update.ProcessedPages,
                Math.Max(update.TotalPages, 1),
                Path.GetFileName(filePath),
                extracted.Transactions.Count,
                update.Stage)));
        var pages = await pdfOcrService.RecognizeAsync(filePath, ocrProgress, cancellationToken);
        var results = extracted.Transactions.ToList();
        foreach (var page in pages)
        {
            AddRecognizedTaxFacts(results, filePath, page);
        }

        var normalized = NormalizeRecords(results);
        if (normalized.Count == 0)
        {
            throw new InvalidDataException(
                "Local OCR completed, but no supported tax-form fields or transaction rows were recognized. Review the scanned document manually.");
        }

        return normalized;
    }

    private static IReadOnlyCollection<FinancialTransaction> ImportWorkbook(string filePath)
    {
        var results = new List<FinancialTransaction>();
        using var workbook = new XLWorkbook(filePath);
        foreach (var sheet in workbook.Worksheets)
        {
            var used = sheet.RangeUsed();
            if (used is null)
            {
                continue;
            }

            var headerRow = used.RowsUsed()
                .Take(25)
                .FirstOrDefault(row => LooksLikeHeader(
                    row.Cells().Select(cell => cell.GetFormattedString())));
            if (headerRow is null)
            {
                continue;
            }

            var headerLookup = headerRow.Cells()
                .Where(cell => !cell.IsEmpty())
                .GroupBy(cell => NormalizeHeader(cell.GetString()))
                .ToDictionary(
                    group => group.Key,
                    group => group.First().Address.ColumnNumber,
                    StringComparer.OrdinalIgnoreCase);

            foreach (var row in used.RowsUsed().Where(row => row.RowNumber() > headerRow.RowNumber()))
            {
                var description = BuildDescription(
                    GetFirst(row, headerLookup, DescriptionHeaders),
                    GetFirst(row, headerLookup, ["description"]),
                    GetFirst(row, headerLookup, ["symbol"]));
                var amount = ParseAmount(GetFirst(row, headerLookup, AmountHeaders));
                if (amount is null)
                {
                    var debit = ParseAmount(GetFirst(row, headerLookup, DebitHeaders));
                    var credit = ParseAmount(GetFirst(row, headerLookup, CreditHeaders));
                    amount = credit ?? (debit is null ? null : -Math.Abs(debit.Value));
                }

                if (amount is null || amount == 0 || string.IsNullOrWhiteSpace(description))
                {
                    continue;
                }

                results.Add(new FinancialTransaction(
                    ParseDate(GetFirst(row, headerLookup, DateHeaders)),
                    description,
                    amount.Value,
                    Path.GetFileName(filePath),
                    row.RowNumber()));
            }
        }

        return results;
    }

    private static async Task<IReadOnlyCollection<FinancialTransaction>> ImportTextAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        var results = new List<FinancialTransaction>();
        var lines = await File.ReadAllLinesAsync(filePath, cancellationToken);
        for (var index = 0; index < lines.Length; index++)
        {
            var match = TransactionLinePattern().Match(lines[index]);
            var amount = match.Success ? ParseAmount(match.Groups["amount"].Value) : null;
            if (!match.Success || amount is null || amount == 0)
            {
                continue;
            }

            results.Add(new FinancialTransaction(
                ParseDate(match.Groups["date"].Value),
                match.Groups["description"].Value.Trim(),
                ApplyStatementSign(match.Groups["description"].Value, amount.Value),
                Path.GetFileName(filePath),
                index + 1));
        }

        return results;
    }

    private static string? GetFirst(
        CsvReader csv,
        IReadOnlyDictionary<string, string> headers,
        IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (headers.TryGetValue(NormalizeHeader(candidate), out var actual))
            {
                var value = csv.GetField(actual);
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static string? GetFirst(
        IXLRangeRow row,
        IReadOnlyDictionary<string, int> headers,
        IEnumerable<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (headers.TryGetValue(NormalizeHeader(candidate), out var column))
            {
                var value = row.Cell(column).GetFormattedString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }
        }

        return null;
    }

    private static DateOnly? ParseDate(string? value)
    {
        return DateTime.TryParse(value, CultureInfo.CurrentCulture, DateTimeStyles.AllowWhiteSpaces, out var date) ||
               DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out date)
            ? DateOnly.FromDateTime(date)
            : null;
    }

    private static decimal? ParseAmount(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        var negative = normalized.StartsWith('(') && normalized.EndsWith(')');
        negative |= normalized.EndsWith("-", StringComparison.Ordinal);
        normalized = normalized
            .Replace("$", string.Empty, StringComparison.Ordinal)
            .Replace(",", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("(", string.Empty, StringComparison.Ordinal)
            .Replace(")", string.Empty, StringComparison.Ordinal)
            .Replace("CR", string.Empty, StringComparison.OrdinalIgnoreCase)
            .TrimEnd('-')
            .Trim();

        if (!decimal.TryParse(
                normalized,
                NumberStyles.Number | NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var amount))
        {
            return null;
        }

        return negative ? -Math.Abs(amount) : amount;
    }

    [GeneratedRegex(
        @"^(?<date>\d{1,2}[/-]\d{1,2}(?:[/-]\d{2,4})?)\s+(?<description>.+?)\s+(?<amount>\(?-?\$?[\d,]+\.\d{2}\)?(?:\s*CR|-)?)(?:\s+\$?[\d,]+\.\d{2})?$",
        RegexOptions.CultureInvariant)]
    private static partial Regex TransactionLinePattern();

    private static Dictionary<string, string> BuildHeaderLookup(IEnumerable<string> headers) =>
        headers
            .Where(header => !string.IsNullOrWhiteSpace(header))
            .GroupBy(NormalizeHeader)
            .ToDictionary(
                group => group.Key,
                group => group.First(),
                StringComparer.OrdinalIgnoreCase);

    private static bool LooksLikeHeader(IEnumerable<string> values)
    {
        var headers = values.Select(NormalizeHeader).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return DateHeaders.Any(candidate => headers.Contains(NormalizeHeader(candidate))) &&
               (AmountHeaders.Concat(DebitHeaders).Concat(CreditHeaders)
                   .Any(candidate => headers.Contains(NormalizeHeader(candidate)))) &&
               DescriptionHeaders.Any(candidate => headers.Contains(NormalizeHeader(candidate)));
    }

    private static string NormalizeHeader(string value) =>
        NonAlphaNumericPattern().Replace(value.Trim().ToLowerInvariant(), string.Empty);

    private static string BuildDescription(params string?[] values) =>
        string.Join(
            " | ",
            values.Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase));

    private static decimal ApplyStatementSign(
        string description,
        decimal amount,
        bool isCreditCardStatement = false)
    {
        if (isCreditCardStatement)
        {
            return ContainsAny(description, "payment", "credit", "refund", "reversal")
                ? Math.Abs(amount)
                : -Math.Abs(amount);
        }

        if (amount < 0)
        {
            return amount;
        }

        if (ContainsAny(
                description,
                "payment",
                "credit",
                "refund",
                "reversal",
                "cashback",
                "cash back"))
        {
            return amount;
        }

        return ContainsAny(
            description,
            "purchase",
            "debit",
            "fee",
            "interest charge",
            "withdrawal",
            "advance")
            ? -amount
            : amount;
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static void AddTaxSummaryLine(
        ICollection<FinancialTransaction> results,
        string filePath,
        int row,
        string line,
        int? taxYear = null)
    {
        var match = TaxSummaryLinePattern().Match(line);
        if (!match.Success)
        {
            return;
        }

        var description = WhitespacePattern().Replace(
            match.Groups["description"].Value.Trim(),
            " ");
        var amount = ParseAmount(match.Groups["amount"].Value);
        if (amount is null || amount == 0)
        {
            return;
        }

        if (ContainsAny(description, "real estate taxes", "mortgage interest", "points paid"))
        {
            amount = -Math.Abs(amount.Value);
        }

        if (results.Any(item =>
                item.Description.Equals(description, StringComparison.OrdinalIgnoreCase) &&
                item.Amount == amount.Value))
        {
            return;
        }

        var isPropertyTax = ContainsAny(description, "real estate taxes", "property tax");
        results.Add(new FinancialTransaction(
            null,
            $"Statement summary: {description}",
            amount.Value,
            Path.GetFileName(filePath),
            row,
            isPropertyTax ? "Property tax statement" : null,
            isPropertyTax ? "Tax" : null,
            false,
            taxYear,
            RecognitionConfidence: isPropertyTax ? 0.90m : null));
    }

    private static void AddForm1098Facts(
        ICollection<FinancialTransaction> results,
        string filePath,
        IReadOnlyCollection<UglyToad.PdfPig.Content.Word> words,
        int row)
    {
        var rows = words
            .GroupBy(word => Math.Round(word.BoundingBox.Bottom / 2d) * 2d)
            .Select(group => new
            {
                Bottom = group.Key,
                Text = string.Join(
                    " ",
                    group.OrderBy(word => word.BoundingBox.Left).Select(word => word.Text))
            })
            .Select(row => new
            {
                row.Bottom,
                Text = WhitespacePattern().Replace(row.Text, " ")
            })
            .ToList();
        var labelRow = rows.FirstOrDefault(item =>
            item.Text.Contains("Mortgage interest received", StringComparison.OrdinalIgnoreCase) &&
            item.Text.Contains("Outstanding mortgage principal", StringComparison.OrdinalIgnoreCase));
        if (labelRow is null)
        {
            return;
        }

        var amount = words
            .Where(word =>
                word.BoundingBox.Bottom < labelRow.Bottom &&
                word.BoundingBox.Bottom >= labelRow.Bottom - 35 &&
                CurrencyAmountPattern().IsMatch(word.Text))
            .OrderBy(word => word.BoundingBox.Left)
            .Select(word => ParseAmount(word.Text))
            .FirstOrDefault(value => value is > 0);
        if (amount is null)
        {
            return;
        }

        const string description = "Form 1098 mortgage interest paid";
        if (results.Any(item =>
                item.Description == description &&
                item.Amount == -amount.Value))
        {
            return;
        }

        results.Add(new FinancialTransaction(
            null,
            "1098 Box 1: Mortgage interest received",
            -amount.Value,
            Path.GetFileName(filePath),
            row,
            "1098",
            "1",
            RecognitionConfidence: 0.90m));
    }

    private static void AddRecognizedTaxFacts(
        ICollection<FinancialTransaction> results,
        string filePath,
        OcrPageText page)
    {
        var lines = page.Lines
            .Select(line => WhitespacePattern().Replace(line, " ").Trim())
            .Where(line => line.Length > 0)
            .ToList();
        var pageText = string.Join(" ", lines);
        var form = DetectTaxForm(pageText);
        var taxYear = FindTaxYear(pageText);
        AddAnnualMortgageStatementFacts(
            results,
            filePath,
            page,
            pageText,
            taxYear);

        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            AddTaxSummaryLine(results, filePath, page.PageNumber, line, taxYear);
            foreach (var field in TaxFormFields)
            {
                if (!field.Forms.Contains(form, StringComparer.OrdinalIgnoreCase) ||
                    !field.Labels.Any(label => LineMatchesField(line, label, form)))
                {
                    continue;
                }

                var amount = FindPositionedAmount(page.PositionedLines, line, field) ??
                             FindFollowingAmount(lines, index, form, field);
                if (amount is null || amount == 0)
                {
                    continue;
                }

                var signedAmount = field.IsDeduction ? -Math.Abs(amount.Value) : Math.Abs(amount.Value);
                if (results.Any(item =>
                        item.TaxForm == form &&
                        item.TaxBox == field.Box &&
                        item.Amount == signedAmount))
                {
                    continue;
                }

                results.Add(new FinancialTransaction(
                    null,
                    $"{form} Box {field.Box}: {field.Description}",
                    signedAmount,
                    Path.GetFileName(filePath),
                    page.PageNumber,
                    form,
                    field.Box,
                    field.IsInformational,
                    taxYear,
                    0.75m));
            }
        }
    }

    private static void AddAnnualMortgageStatementFacts(
        ICollection<FinancialTransaction> results,
        string filePath,
        OcrPageText page,
        string pageText,
        int? taxYear)
    {
        if (!pageText.Contains(
                "Annual Tax and Interest Statement",
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var field = new TaxFormField(
            ["Property tax statement"],
            "Tax",
            "Real estate taxes paid",
            ["real estate taxes paid"],
            true,
            false);
        var labelLine = page.Lines.FirstOrDefault(line =>
            line.Contains("Real Estate Taxes Paid", StringComparison.OrdinalIgnoreCase));
        var amount = labelLine is null
            ? null
            : FindPositionedAmount(page.PositionedLines, labelLine, field);
        if (amount is null or 0)
        {
            return;
        }

        results.Add(new FinancialTransaction(
            null,
            "Property tax statement Box Tax: Real estate taxes paid",
            -Math.Abs(amount.Value),
            Path.GetFileName(filePath),
            page.PageNumber,
            "Property tax statement",
            "Tax",
            false,
            taxYear,
            0.85m));
    }

    private static bool LineMatchesField(string line, string label, string form)
    {
        var normalizedLine = WhitespacePattern().Replace(line, " ").Trim();
        var normalizedLabel = WhitespacePattern().Replace(label, " ").Trim();
        if (normalizedLine.Equals(normalizedLabel, StringComparison.OrdinalIgnoreCase) ||
            Regex.IsMatch(
                normalizedLine,
                $@"^\d+[a-z]?\s+{Regex.Escape(normalizedLabel)}\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            return true;
        }

        return form == "Property tax statement" &&
               normalizedLine.Contains(normalizedLabel, StringComparison.OrdinalIgnoreCase) &&
               !normalizedLine.StartsWith("Box ", StringComparison.OrdinalIgnoreCase);
    }

    private static decimal? FindFollowingAmount(
            IReadOnlyList<string> lines,
            int labelIndex,
            string form,
            TaxFormField field)
    {
        var label = field.Labels.First(item => LineMatchesField(lines[labelIndex], item, form));
        var labelPosition = lines[labelIndex].IndexOf(label, StringComparison.OrdinalIgnoreCase);
        if (labelPosition >= 0)
        {
            var sameLineAmount = FindAmount(lines[labelIndex][(labelPosition + label.Length)..]);
            if (sameLineAmount is not null)
            {
                return sameLineAmount;
            }
        }

        for (var offset = 1; offset <= 4 && labelIndex + offset < lines.Count; offset++)
        {
            var candidate = lines[labelIndex + offset];
            if (TaxFormFields
                .Where(item => item.Forms.Contains(form, StringComparer.OrdinalIgnoreCase))
                .SelectMany(item => item.Labels)
                .Any(labelText => LineMatchesField(candidate, labelText, form)))
            {
                break;
            }

            var amount = FindAmount(candidate);
            if (amount is not null)
            {
                return amount;
            }
        }

        return null;
    }

    private static decimal? FindPositionedAmount(
            IReadOnlyCollection<OcrTextLine>? positionedLines,
            string labelText,
            TaxFormField field)
    {
        if (positionedLines is null)
        {
            return null;
        }

        var labelLine = positionedLines.FirstOrDefault(item =>
            item.Text.Equals(labelText, StringComparison.OrdinalIgnoreCase));
        if (labelLine is null)
        {
            return null;
        }

        var candidates = positionedLines
            .Select(line => new
            {
                Line = line,
                Amount = FindAmount(line.Text),
                VerticalDistance = Math.Abs(
                    (line.Y + line.Height / 2d) -
                    (labelLine.Y + labelLine.Height / 2d))
            })
            .Where(item =>
                item.Amount is not null &&
                !ReferenceEquals(item.Line, labelLine) &&
                item.Line.X >= labelLine.X - 40 &&
                (item.VerticalDistance <= Math.Max(45, labelLine.Height * 1.75) ||
                 (item.Line.Y >= labelLine.Y &&
                  item.Line.Y - labelLine.Y <= 150 &&
                  Math.Abs(item.Line.X - labelLine.X) <= 200)))
            .OrderBy(item => item.VerticalDistance)
            .ThenBy(item => Math.Abs(item.Line.X - (labelLine.X + labelLine.Width)))
            .ToList();
        return candidates.FirstOrDefault()?.Amount;
    }

    private static void AddFidelityFacts(
            ICollection<FinancialTransaction> results,
            string filePath,
            int pageNumber,
            IReadOnlyCollection<string> sourceLines)
    {
        var lines = sourceLines
            .Select(line => WhitespacePattern().Replace(line, " ").Trim())
            .Where(line => line.Length > 0)
            .ToList();
        var pageText = string.Join(" ", lines);
        if (!pageText.Contains("YEAR-END INVESTMENT REPORT", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var taxYear = FindTaxYear(pageText);
        if (pageText.Contains("Income Summary", StringComparison.OrdinalIgnoreCase))
        {
            AddFidelitySummaryField(results, filePath, pageNumber, lines, taxYear, "Dividends");
            AddFidelitySummaryField(results, filePath, pageNumber, lines, taxYear, "Interest");
            AddFidelitySummaryField(results, filePath, pageNumber, lines, taxYear, "Tax-deferred");
            AddFidelitySummaryField(results, filePath, pageNumber, lines, taxYear, "Tax-free");
        }

        if (!pageText.Contains("Contributions Made in", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var contributionLine = lines.FirstOrDefault(line =>
            Regex.IsMatch(
                line,
                @"^20\d{2}\s+Contributions\b",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        var amount = contributionLine is null ? null : FindAmount(contributionLine);
        if (amount is null or 0)
        {
            return;
        }

        var (form, box, description) =
            pageText.Contains("HEALTH SAVINGS ACCOUNT", StringComparison.OrdinalIgnoreCase)
                ? ("5498-SA", "2", "HSA contributions")
                : pageText.Contains("ROTH IRA", StringComparison.OrdinalIgnoreCase)
                    ? ("5498", "10", "Roth IRA contributions")
                    : ("5498", "1", "IRA contributions");
        results.Add(new FinancialTransaction(
            null,
            $"{form} Box {box}: {description}",
            Math.Abs(amount.Value),
            Path.GetFileName(filePath),
            pageNumber,
            form,
            box,
            true,
            taxYear,
            0.82m));
    }

    private static void AddFidelitySummaryField(
            ICollection<FinancialTransaction> results,
            string filePath,
            int pageNumber,
            IReadOnlyCollection<string> lines,
            int? taxYear,
            string label)
    {
        var line = lines.FirstOrDefault(item =>
            item.StartsWith(label + " ", StringComparison.OrdinalIgnoreCase));
        var amount = line is null ? null : FindAmount(line);
        if (amount is null or 0)
        {
            return;
        }

        results.Add(new FinancialTransaction(
            null,
            $"Fidelity year-end summary: {label}",
            Math.Abs(amount.Value),
            Path.GetFileName(filePath),
            pageNumber,
            "Fidelity year-end summary",
            label,
            true,
            taxYear,
            0.70m));
    }

    private static int? FindTaxYear(string text)
    {
        var match = TaxYearPattern().Match(text);
        return match.Success && int.TryParse(match.Value, out var year)
            ? year
            : null;
    }

    private static string DetectTaxForm(string text)
    {
        var normalized = text.ToUpperInvariant();

        string[] forms =
        [
            "1099-SA", "5498-SA", "1099-INT", "1099-DIV", "1099-NEC", "1099-MISC",
            "1099-R", "1099-K", "1099-B", "1098-E", "1098-T", "1095-A", "SSA-1099",
            "SCHEDULE K-1", "FORM W-2", "FORM 1098", "FORM 5498"
        ];
        var detected = forms.FirstOrDefault(normalized.Contains);
        if (detected is null &&
            Regex.IsMatch(normalized, @"\b1098\s+(?:FORM|MORTGAGE)|\bFORM\s+1098\b"))
        {
            detected = "FORM 1098";
        }

        if (detected is null &&
            ContainsAny(normalized, "PROPERTY TAX", "REAL ESTATE TAX", "AD VALOREM"))
        {
            return "Property tax statement";
        }

        return detected switch
        {
            "FORM W-2" => "W-2",
            "FORM 1098" => "1098",
            "FORM 5498" => "5498",
            "SCHEDULE K-1" => "Schedule K-1",
            null => "Unclassified tax document",
            _ => detected
        };
    }

    private static decimal? FindAmount(string text)
    {
        var matches = EmbeddedCurrencyAmountPattern().Matches(text);
        return matches.Count == 0
            ? null
            : ParseAmount(matches[0].Groups["amount"].Value);
    }

    private static List<FinancialTransaction> NormalizeRecords(
        IEnumerable<FinancialTransaction> source)
    {
        var records = source
            .GroupBy(item => item.TaxForm is null
                ? $"summary|{item.Description}|{item.Amount}"
                : item.TaxForm == "1099-MISC"
                    ? $"tax|{item.SourceRow}|{item.TaxForm}|{item.TaxBox}|{item.Amount}"
                    : $"tax|{item.TaxForm}|{item.TaxBox}|{item.Amount}")
            .Select(group =>
            {
                var preferred = group
                    .OrderByDescending(item => item.RecognitionConfidence ?? 0)
                    .ThenBy(item => item.SourceRow)
                    .First();
                return preferred with
                {
                    TaxYear = group.Select(item => item.TaxYear)
                        .FirstOrDefault(year => year is not null),
                    RecognitionConfidence = group.Max(item => item.RecognitionConfidence)
                };
            })
            .ToList();

        var structuredAmounts = records
            .Where(item => item.TaxForm is not null)
            .Select(item => item.Amount)
            .ToHashSet();
        records.RemoveAll(item =>
            item.TaxForm is null &&
            item.Description.StartsWith("Statement summary:", StringComparison.OrdinalIgnoreCase) &&
            structuredAmounts.Contains(item.Amount));
        return records;
    }

    private static readonly TaxFormField[] TaxFormFields =
    [
        new(["W-2"], "1", "Wages, tips, other compensation", ["wages tips other compensation"], false, false),
        new(["W-2"], "2", "Federal income tax withheld", ["federal income tax withheld"], false, true),
        new(["1099-INT"], "1", "Interest income", ["interest income"], false, false),
        new(["1099-INT"], "4", "Federal income tax withheld", ["federal income tax withheld"], false, true),
        new(["1099-DIV"], "1a", "Total ordinary dividends", ["total ordinary dividends"], false, false),
        new(["1099-DIV"], "1b", "Qualified dividends", ["qualified dividends"], false, true),
        new(["1099-B"], "1d", "Proceeds", ["proceeds"], false, false),
        new(["1099-B"], "1e", "Cost or other basis", ["cost or other basis"], true, true),
        new(["1099-NEC"], "1", "Nonemployee compensation", ["nonemployee compensation"], false, false),
        new(["1099-MISC"], "1", "Rents", ["rents"], false, false),
        new(["1099-MISC"], "2", "Royalties", ["royalties"], false, false),
        new(["1099-K"], "1a", "Gross amount of payment card transactions", ["gross amount"], false, false),
        new(["1099-R"], "1", "Gross distribution", ["gross distribution"], false, false),
        new(["1099-R"], "2a", "Taxable amount", ["taxable amount"], false, false),
        new(["1098"], "1", "Mortgage interest received", ["mortgage interest received"], true, false),
        new(["1098-E"], "1", "Student loan interest received", ["student loan interest received"], true, false),
        new(["1098-T"], "1", "Payments received for qualified tuition", ["payments received for qualified tuition"], true, false),
        new(["1095-A"], "33A", "Annual enrollment premium", ["annual totals", "enrollment premiums"], false, true),
        new(["1099-SA"], "1", "Gross distribution", ["gross distribution"], false, false),
        new(["5498-SA"], "2", "Total HSA contributions", ["total contributions made"], false, true),
        new(["SSA-1099"], "5", "Net benefits", ["net benefits"], false, false),
        new(["Schedule K-1"], "1", "Ordinary business income", ["ordinary business income"], false, false),
        new(["Property tax statement"], "Tax", "Real estate or property tax", ["property tax", "real estate tax", "total tax"], true, false)
    ];

    private sealed record TaxFormField(
        string[] Forms,
        string Box,
        string Description,
        string[] Labels,
        bool IsDeduction,
        bool IsInformational);

    private sealed record PdfTextExtraction(
        IReadOnlyCollection<FinancialTransaction> Transactions,
        bool HasText);

    private static IEnumerable<string> BuildPdfLines(
        IReadOnlyCollection<UglyToad.PdfPig.Content.Word> words) =>
        words.GroupBy(word => Math.Round(word.BoundingBox.Bottom / 2d) * 2d)
            .OrderByDescending(group => group.Key)
            .Select(group => string.Join(
                " ",
                group.OrderBy(word => word.BoundingBox.Left)
                    .Select(word => word.Text)))
            .Where(line => !string.IsNullOrWhiteSpace(line));

    [GeneratedRegex(@"[^a-z0-9]")]
    private static partial Regex NonAlphaNumericPattern();

    [GeneratedRegex(
        @"^(?<description>real\s+estate\s+taxes\s+paid|mortgage\s+interest(?:\s+paid)?|ordinary\s+dividends|qualified\s+dividends|dividends|taxable\s+interest|interest|short[- ]term\s+capital\s+gains?|long[- ]term\s+capital\s+gains?|capital\s+gains?)\s+\$?(?<amount>[\d,]+\.\d{2})$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TaxSummaryLinePattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(@"^\$?[\d,]+\.\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyAmountPattern();

    [GeneratedRegex(@"(?<amount>\(?\$?\s*[\d,]+(?:\s\d{3})*\.\d{2}\)?)", RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedCurrencyAmountPattern();

    [GeneratedRegex(@"\b20(?:1\d|2\d)\b", RegexOptions.CultureInvariant)]
    private static partial Regex TaxYearPattern();
}
