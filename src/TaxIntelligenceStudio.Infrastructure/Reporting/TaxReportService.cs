using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;

namespace TaxIntelligenceStudio.Infrastructure.Reporting;

public sealed class TaxReportService(IAuthoritativeSourceVerifier sourceVerifier)
    : ITaxReportService
{
    static TaxReportService()
    {
        if (OperatingSystem.IsWindows())
        {
            GlobalFontSettings.UseWindowsFontsUnderWindows = true;
        }
    }

    public async Task ExportAsync(
        TaxAnalysisResult analysis,
        string outputPath,
        ReportFormat format,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var verifiedSources = await VerifySourcesAsync(analysis, cancellationToken);
        var lines = BuildReportLines(analysis, verifiedSources);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);

        switch (format)
        {
            case ReportFormat.Pdf:
                WritePdf(outputPath, lines);
                break;
            case ReportFormat.Word:
                WriteWord(outputPath, lines);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format), format, null);
        }
    }

    private async Task<IReadOnlyCollection<VerifiedSource>> VerifySourcesAsync(
        TaxAnalysisResult analysis,
        CancellationToken cancellationToken)
    {
        var urls = analysis.Mappings.Select(mapping => mapping.SourceUrl)
            .Concat(analysis.Opportunities.Select(opportunity => opportunity.SourceUrl))
            .Distinct()
            .ToList();
        if (urls.Count == 0)
        {
            throw new InvalidOperationException(GuidanceResponse.VerificationFailureMessage);
        }

        var tasks = urls.Select(url => sourceVerifier.VerifyAsync(
            new AuthoritativeSource(url, GetPublicationName(url), null, null, null),
            cancellationToken));
        var sources = await Task.WhenAll(tasks);
        if (sources.Any(source => !source.IsVerified))
        {
            throw new InvalidOperationException(GuidanceResponse.VerificationFailureMessage);
        }

        return sources;
    }

    private static string GetPublicationName(Uri url)
    {
        var segment = url.Segments.LastOrDefault()?.Trim('/') ?? "IRS source";
        return segment.Replace('-', ' ');
    }

    private static IReadOnlyCollection<string> BuildReportLines(
        TaxAnalysisResult analysis,
        IReadOnlyCollection<VerifiedSource> sources)
    {
        var lines = new List<string>
        {
            "TAX INTELLIGENCE STUDIO - FEDERAL TAX ANALYSIS WORKPAPER",
            $"Generated: {DateTimeOffset.Now:u}",
            $"Transactions reviewed: {analysis.Transactions.Count}",
            $"Potential inflows: {analysis.TotalIncome:C}",
            $"Potential outflows: {analysis.TotalExpenses:C}",
            string.Empty,
            "IMPORTANT",
            GuidanceResponse.Disclaimer,
            "Amounts are candidates derived from imported financial exports. They are not a completed return and must be reconciled to W-2, 1099, K-1, brokerage basis, and other tax documents.",
            string.Empty,
            "FORM AND SCHEDULE ROADMAP"
        };

        var step = 1;
        foreach (var mapping in analysis.Mappings)
        {
            lines.Add(
                $"Step {step++}: {mapping.Category} ({mapping.Amount:C}) -> {mapping.Form} -> {mapping.ReportingDestination}");
            lines.Add($"  Basis: {mapping.Rationale}");
            lines.Add($"  Confidence: {mapping.Confidence:P0}");
            lines.Add($"  Source: {mapping.SourceUrl}");
        }

        lines.Add(string.Empty);
        lines.Add("POTENTIAL TAX OPTIMIZATION REVIEW ITEMS");
        if (analysis.Opportunities.Count == 0)
        {
            lines.Add("No optimization item could be supported from the imported transaction descriptions.");
        }
        else
        {
            foreach (var opportunity in analysis.Opportunities)
            {
                lines.Add($"{opportunity.Title} - candidate amount {opportunity.PotentialAmount:C}");
                lines.Add($"  {opportunity.Explanation}");
                lines.Add($"  Confidence: {opportunity.Confidence:P0}");
                lines.Add($"  Source: {opportunity.SourceUrl}");
            }
        }

        lines.Add(string.Empty);
        lines.Add("TRANSACTION WORKPAPER");
        foreach (var transaction in analysis.Transactions.OrderBy(item => item.Date))
        {
            lines.Add(
                $"{transaction.Date?.ToString("yyyy-MM-dd") ?? "No date"} | {transaction.Amount,12:C} | {transaction.Description} | {transaction.SourceFile}:{transaction.SourceRow}");
        }

        lines.Add(string.Empty);
        lines.Add("LIVE-VERIFIED IRS SOURCES");
        foreach (var source in sources)
        {
            lines.Add(source.Source.SourceUrl.AbsoluteUri);
            lines.Add($"  {source.Title}");
            lines.Add($"  Verified: {source.VerifiedAt:u}; SHA-256: {source.ContentHash}");
        }

        return lines;
    }

    private static void WritePdf(string outputPath, IReadOnlyCollection<string> lines)
    {
        using var document = new PdfDocument();
        document.Info.Title = "Tax Intelligence Studio Analysis";

        PdfPage? page = null;
        XGraphics? graphics = null;
        var font = new XFont("Arial", 9);
        var headingFont = new XFont("Arial", 14, XFontStyleEx.Bold);
        const double margin = 42;
        var y = margin;

        foreach (var line in lines)
        {
            var isHeading = line.Length > 0 &&
                            line == line.ToUpperInvariant() &&
                            line.Any(char.IsLetter);
            var selectedFont = isHeading ? headingFont : font;
            var wrappedLines = Wrap(line, isHeading ? 78 : 112);

            foreach (var wrappedLine in wrappedLines)
            {
                if (page is null || graphics is null || y > page.Height.Point - margin)
                {
                    graphics?.Dispose();
                    page = document.AddPage();
                    page.Size = PdfSharp.PageSize.Letter;
                    graphics = XGraphics.FromPdfPage(page);
                    y = margin;
                }

                graphics.DrawString(
                    wrappedLine,
                    selectedFont,
                    XBrushes.Black,
                    new XRect(margin, y, page.Width.Point - (2 * margin), 20),
                    XStringFormats.TopLeft);
                y += isHeading ? 22 : 14;
            }
        }

        graphics?.Dispose();
        document.Save(outputPath);
    }

    private static void WriteWord(string outputPath, IReadOnlyCollection<string> lines)
    {
        using var document = WordprocessingDocument.Create(
            outputPath,
            WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        mainPart.Document = new Document(new Body());
        var body = mainPart.Document.Body!;

        foreach (var line in lines)
        {
            var isHeading = line.Length > 0 &&
                            line == line.ToUpperInvariant() &&
                            line.Any(char.IsLetter);
            var runProperties = new RunProperties();
            if (isHeading)
            {
                runProperties.Append(new Bold(), new FontSize { Val = "28" });
            }

            body.Append(new Paragraph(
                new Run(
                    runProperties,
                    new Text(line) { Space = SpaceProcessingModeValues.Preserve })));
        }

        mainPart.Document.Save();
    }

    private static IEnumerable<string> Wrap(string value, int width)
    {
        if (string.IsNullOrEmpty(value))
        {
            yield return string.Empty;
            yield break;
        }

        var remaining = value;
        while (remaining.Length > width)
        {
            var breakAt = remaining.LastIndexOf(' ', width);
            if (breakAt <= 0)
            {
                breakAt = width;
            }

            yield return remaining[..breakAt].TrimEnd();
            remaining = remaining[breakAt..].TrimStart();
        }

        yield return remaining;
    }
}
