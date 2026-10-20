using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;

namespace TaxIntelligenceStudio.Infrastructure.Reporting;

public sealed partial class InferenceReportService(TimeProvider timeProvider) : IInferenceReportService
{
    static InferenceReportService()
    {
        if (OperatingSystem.IsWindows())
        {
            GlobalFontSettings.UseWindowsFontsUnderWindows = true;
        }
    }

    public async Task ExportAsync(
        IReadOnlyList<ModelInferenceTurn> turns,
        string outputPath,
        ReportFormat format,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(turns);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (turns.Count == 0 || turns.Any(turn => string.IsNullOrWhiteSpace(turn.Response.Content)))
        {
            throw new ArgumentException("At least one completed model response is required.", nameof(turns));
        }
        if (format is not (ReportFormat.Pdf or ReportFormat.Word))
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }
        var blocks = BuildBlocks(turns);
        var fullPath = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        var temporaryPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await Task.Run(() =>
            {
                if (format == ReportFormat.Pdf)
                {
                    WritePdf(temporaryPath, blocks, cancellationToken);
                }
                else
                {
                    WriteWord(temporaryPath, blocks, cancellationToken);
                }
            }, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private List<Block> BuildBlocks(IReadOnlyList<ModelInferenceTurn> turns)
    {
        var blocks = new List<Block>
        {
            new("Tax Intelligence Studio - Inference Results", 1),
            new($"Exported: {timeProvider.GetUtcNow():u}"),
            new("AI-generated tax research, not a verified tax opinion. Review calculations, " +
                "citations and current law with a qualified tax professional before relying on the result."),
            new("This export may contain private taxpayer information and attachment excerpts. Store and share it securely.")
        };
        for (var index = 0; index < turns.Count; index++)
        {
            var turn = turns[index];
            blocks.Add(new($"Response {index + 1}", 1));
            blocks.Add(new($"Provider: {turn.Response.ProviderId} | Model: {turn.Response.ModelId}"));
            blocks.Add(new($"Source mode: {turn.Response.SourceDataMode} | " +
                $"Elapsed: {turn.Response.Elapsed.TotalSeconds:F1}s | Finish reason: {turn.Response.FinishReason}"));
            blocks.Add(new($"Used cached sources: {(turn.Response.UsedCachedSources ? "Yes" : "No")}"));
            blocks.Add(new("Question / supplied context", 2));
            blocks.AddRange(ParseText(turn.Prompt));
            blocks.Add(new("Model response", 2));
            blocks.AddRange(ParseText(turn.Response.Content));
            blocks.Add(new("Cited source URLs (not reverified by export)", 2));
            blocks.AddRange(turn.Response.Citations.Count == 0
                ? [new Block("No exact supplied-source URL citations were recorded.")]
                : turn.Response.Citations.Select(url => new Block(url)));
            blocks.Add(new("Warnings", 2));
            blocks.AddRange(turn.Response.Warnings.Count == 0
                ? [new Block("No additional provider warnings recorded. This does not establish accuracy.")]
                : turn.Response.Warnings.Select(warning => new Block(warning)));
        }
        return blocks;
    }

    private static IEnumerable<Block> ParseText(string text)
    {
        var code = false;
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
            {
                code = !code;
                continue;
            }
            if (code)
            {
                yield return new Block(line, Code: true);
                continue;
            }
            var heading = HeadingPattern().Match(line);
            var content = heading.Success ? heading.Groups[1].Value : line;
            content = BoldPattern().Replace(content, "$1");
            yield return new Block(content, heading.Success ? 2 : 0);
        }
    }

    private static void WriteWord(string path, IReadOnlyList<Block> blocks, CancellationToken token)
    {
        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var main = document.AddMainDocumentPart();
        main.Document = new Document(new Body());
        var body = main.Document.Body!;
        foreach (var block in blocks)
        {
            token.ThrowIfCancellationRequested();
            var properties = new RunProperties
            {
                RunFonts = new RunFonts { Ascii = block.Code ? "Courier New" : "Arial", HighAnsi = block.Code ? "Courier New" : "Arial" },
                FontSize = new FontSize { Val = block.Heading == 1 ? "32" : block.Heading == 2 ? "26" : "22" }
            };
            if (block.Heading > 0)
            {
                properties.Bold = new Bold();
            }
            var paragraphProperties = new ParagraphProperties
            {
                SpacingBetweenLines = new SpacingBetweenLines { After = block.Heading > 0 ? "140" : "80" }
            };
            if (block.Heading > 0)
            {
                paragraphProperties.KeepNext = new KeepNext();
                paragraphProperties.OutlineLevel = new OutlineLevel { Val = block.Heading - 1 };
            }
            body.Append(new Paragraph(paragraphProperties,
                new Run(properties, new Text(block.Text) { Space = SpaceProcessingModeValues.Preserve })));
        }
        body.Append(new SectionProperties(
            new PageSize { Width = 12240, Height = 15840 },
            new PageMargin { Top = 900, Bottom = 900, Left = 900, Right = 900 }));
        main.Document.Save();
    }

    private static void WritePdf(string path, IReadOnlyList<Block> blocks, CancellationToken token)
    {
        using var document = new PdfDocument();
        document.Info.Title = "Tax Intelligence Studio - Inference Results";
        const double margin = 45;
        PdfPage? page = null;
        XGraphics? graphics = null;
        var y = margin;
        try
        {
            foreach (var block in blocks)
            {
                token.ThrowIfCancellationRequested();
                var size = block.Heading == 1 ? 16 : block.Heading == 2 ? 13 : 10;
                var font = new XFont(block.Code ? "Courier New" : "Arial", size,
                    block.Heading > 0 ? XFontStyleEx.Bold : XFontStyleEx.Regular);
                var lineHeight = size * 1.45;
                foreach (var line in Wrap(block.Text, font, 612 - 2 * margin))
                {
                    token.ThrowIfCancellationRequested();
                    if (page is null || y + lineHeight > page.Height.Point - margin - 16)
                    {
                        graphics?.Dispose();
                        page = document.AddPage();
                        page.Size = PdfSharp.PageSize.Letter;
                        graphics = XGraphics.FromPdfPage(page);
                        graphics.DrawString($"Tax Intelligence Studio | Page {document.PageCount}",
                            new XFont("Arial", 8), XBrushes.Gray,
                            new XRect(margin, page.Height.Point - margin, page.Width.Point - 2 * margin, 12),
                            XStringFormats.TopLeft);
                        y = margin;
                    }
                    graphics!.DrawString(line, font, XBrushes.Black,
                        new XRect(margin, y, page.Width.Point - 2 * margin, lineHeight),
                        XStringFormats.TopLeft);
                    y += lineHeight;
                }
                y += block.Heading > 0 ? 6 : 3;
            }
        }
        finally
        {
            graphics?.Dispose();
        }
        document.Save(path);
    }

    private static IEnumerable<string> Wrap(string text, XFont font, double width)
    {
        if (text.Length == 0)
        {
            yield return string.Empty;
            yield break;
        }
        using var measurement = XGraphics.CreateMeasureContext(
            new XSize(612, 792), XGraphicsUnit.Point, XPageDirection.Downwards);
        var remaining = text;
        while (remaining.Length > 0)
        {
            var low = 1;
            var high = remaining.Length;
            while (low < high)
            {
                var middle = (low + high + 1) / 2;
                if (measurement.MeasureString(remaining[..middle], font).Width <= width)
                {
                    low = middle;
                }
                else
                {
                    high = middle - 1;
                }
            }
            var count = low;
            if (count < remaining.Length)
            {
                var space = remaining.LastIndexOf(' ', count - 1, count);
                if (space > 0)
                {
                    count = space;
                }
                if (char.IsHighSurrogate(remaining[count - 1]) && count < remaining.Length &&
                    char.IsLowSurrogate(remaining[count]))
                {
                    count = count > 1 ? count - 1 : 2;
                }
            }
            yield return remaining[..count];
            remaining = remaining[count..].TrimStart();
        }
    }

    private sealed record Block(string Text, int Heading = 0, bool Code = false);

    [GeneratedRegex(@"^\s*#{1,6}\s+(.+)$")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"\*\*(.+?)\*\*")]
    private static partial Regex BoldPattern();
}
