using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.EntityFrameworkCore;
using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;
using TaxIntelligenceStudio.Infrastructure.Persistence;
using TaxIntelligenceStudio.Infrastructure.Inference;
using UglyToad.PdfPig;

namespace TaxIntelligenceStudio.Infrastructure.Knowledge;

public sealed partial class IrsKnowledgeIngestionService(
    IHttpClientFactory httpClientFactory,
    TaxIntelligenceDbContext dbContext,
    TimeProvider timeProvider,
    ModelProviderOptions modelProviderOptions) : IIrsKnowledgeIngestionService
{
    private static readonly Uri Root = new("https://www.irs.gov/");
    private static readonly Uri DefaultSitemap = new("https://www.irs.gov/sitemap.xml");

    public async Task<CrawlResult> CrawlAsync(
        int maximumDocuments,
        IProgress<CrawlProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (modelProviderOptions.OfflineMode)
        {
            throw new InvalidOperationException(
                "IRS source refresh is unavailable while offline mode is enabled.");
        }

        if (maximumDocuments < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumDocuments),
                "Maximum documents must be zero for all documents or a positive number.");
        }

        var client = httpClientFactory.CreateClient("LiveGovernment");
        var robots = await LoadRobotsAsync(client, cancellationToken);
        var limit = maximumDocuments == 0 ? int.MaxValue : maximumDocuments;
        var urls = await DiscoverUrlsAsync(client, robots.Sitemaps, limit, cancellationToken);
        var discoveredUrls = new HashSet<Uri>(urls);
        var pendingUrls = new LinkedList<Uri>(urls);

        var processed = 0;
        var added = 0;
        var updated = 0;
        var unchanged = 0;
        var failed = 0;

        while (pendingUrls.Count > 0 && processed < limit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = pendingUrls.First!.Value;
            pendingUrls.RemoveFirst();
            if (!robots.IsAllowed(url.AbsolutePath))
            {
                continue;
            }

            try
            {
                var fetched = await FetchDocumentAsync(client, url, cancellationToken);
                foreach (var link in fetched.Links.Where(IsSupportedDocument))
                {
                    if (!robots.IsAllowed(link.AbsolutePath) || !discoveredUrls.Add(link))
                    {
                        continue;
                    }

                    if (link.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
                    {
                        pendingUrls.AddFirst(link);
                    }
                    else
                    {
                        pendingUrls.AddLast(link);
                    }
                }

                var now = timeProvider.GetUtcNow();
                var current = await dbContext.KnowledgeDocuments
                    .SingleOrDefaultAsync(
                        document => document.SourceUrl == url.AbsoluteUri && document.IsCurrent,
                        cancellationToken);

                if (current is not null &&
                    string.Equals(current.ContentHash, fetched.Hash, StringComparison.Ordinal))
                {
                    current.LastVerifiedAt = now;
                    unchanged++;
                }
                else
                {
                    if (current is not null)
                    {
                        current.IsCurrent = false;
                        updated++;
                    }
                    else
                    {
                        added++;
                    }

                    dbContext.KnowledgeDocuments.Add(new KnowledgeDocument
                    {
                        SourceUrl = url.AbsoluteUri,
                        Title = fetched.Title,
                        Content = fetched.Content,
                        ContentHash = fetched.Hash,
                        ContentType = fetched.ContentType,
                        Version = (current?.Version ?? 0) + 1,
                        IsCurrent = true,
                        RetrievedAt = now,
                        LastVerifiedAt = now
                    });
                }

                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception) when (
                exception is HttpRequestException or
                InvalidDataException or
                System.Xml.XmlException)
            {
                failed++;
            }

            processed++;
            progress?.Report(new CrawlProgress(
                discoveredUrls.Count,
                processed,
                added,
                updated,
                unchanged,
                failed,
                url.AbsoluteUri));

            if (robots.CrawlDelay > TimeSpan.Zero)
            {
                await Task.Delay(robots.CrawlDelay, timeProvider, cancellationToken);
            }
        }

        return new CrawlResult(
            discoveredUrls.Count,
            processed,
            added,
            updated,
            unchanged,
            failed,
            timeProvider.GetUtcNow());
    }

    private static async Task<RobotsPolicy> LoadRobotsAsync(
        HttpClient client,
        CancellationToken cancellationToken)
    {
        using var request = CreateLiveRequest(new Uri(Root, "robots.txt"));
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return RobotsPolicy.Parse(text);
    }

    private static async Task<List<Uri>> DiscoverUrlsAsync(
        HttpClient client,
        IReadOnlyCollection<Uri> declaredSitemaps,
        int limit,
        CancellationToken cancellationToken)
    {
        var documents = new HashSet<Uri>();
        var visitedSitemaps = new HashSet<Uri>();
        var pending = new Queue<Uri>(
            declaredSitemaps.Count > 0 ? declaredSitemaps : [DefaultSitemap]);

        while (pending.Count > 0 && documents.Count < limit)
        {
            var sitemap = pending.Dequeue();
            if (!visitedSitemaps.Add(sitemap) || !IsIrsUri(sitemap))
            {
                continue;
            }

            using var request = CreateLiveRequest(sitemap);
            using var response = await client.SendAsync(request, cancellationToken);
            if (response.StatusCode != HttpStatusCode.OK)
            {
                continue;
            }

            var xml = await response.Content.ReadAsStringAsync(cancellationToken);
            var document = XDocument.Parse(xml, LoadOptions.None);
            var locations = document
                .Descendants()
                .Where(element => element.Name.LocalName == "loc")
                .Select(element => Uri.TryCreate(element.Value.Trim(), UriKind.Absolute, out var uri) ? uri : null)
                .Where(uri => uri is not null && IsIrsUri(uri))
                .Cast<Uri>();

            if (document.Root?.Name.LocalName == "sitemapindex")
            {
                foreach (var location in locations)
                {
                    pending.Enqueue(location);
                }
            }
            else
            {
                foreach (var location in locations)
                {
                    documents.Add(location);
                    if (documents.Count >= limit)
                    {
                        break;
                    }
                }
            }
        }

        return documents.OrderBy(uri => uri.AbsoluteUri, StringComparer.Ordinal).ToList();
    }

    private static async Task<FetchedDocument> FetchDocumentAsync(
        HttpClient client,
        Uri url,
        CancellationToken cancellationToken)
    {
        using var request = CreateLiveRequest(url);
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length == 0)
        {
            throw new InvalidDataException("The IRS source returned an empty document.");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        string content;
        string title;

        if (mediaType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) ||
            url.AbsolutePath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            using var pdf = PdfDocument.Open(bytes);
            content = string.Join(
                Environment.NewLine,
                pdf.GetPages().Select(page => page.Text));
            title = Path.GetFileNameWithoutExtension(url.AbsolutePath);
            if (string.IsNullOrWhiteSpace(content))
            {
                throw new InvalidDataException("No extractable text was found in the IRS PDF.");
            }

            return new FetchedDocument(
                title,
                content,
                Convert.ToHexString(SHA256.HashData(bytes)),
                mediaType,
                []);
        }

        var html = Encoding.UTF8.GetString(bytes);
        title = ExtractTitle(html, url);
        content = ExtractText(html);

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidDataException("No extractable text was found in the IRS source.");
        }

        return new FetchedDocument(
            title,
            content,
            Convert.ToHexString(SHA256.HashData(bytes)),
            mediaType,
            ExtractLinks(html, url));
    }

    private static HttpRequestMessage CreateLiveRequest(Uri uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.CacheControl = new()
        {
            NoCache = true,
            NoStore = true,
            MaxAge = TimeSpan.Zero
        };
        request.Headers.Pragma.ParseAdd("no-cache");
        request.Headers.UserAgent.ParseAdd("TaxIntelligenceStudio/1.0 (+local IRS corpus builder)");
        return request;
    }

    private static bool IsIrsUri(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        (uri.IdnHost.Equals("irs.gov", StringComparison.OrdinalIgnoreCase) ||
         uri.IdnHost.Equals("www.irs.gov", StringComparison.OrdinalIgnoreCase));

    private static bool IsSupportedDocument(Uri uri)
    {
        if (!IsIrsUri(uri))
        {
            return false;
        }

        var extension = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
        return extension is not (".css" or ".js" or ".jpg" or ".jpeg" or ".png" or
            ".gif" or ".svg" or ".ico" or ".zip" or ".gz" or ".mp4" or ".webm");
    }

    private static string ExtractTitle(string html, Uri url)
    {
        var match = TitlePattern().Match(html);
        return match.Success
            ? WebUtility.HtmlDecode(match.Groups["title"].Value.Trim())
            : url.AbsolutePath.Trim('/').Replace('-', ' ');
    }

    private static string ExtractText(string html)
    {
        var withoutScripts = ScriptAndStylePattern().Replace(html, " ");
        var withoutTags = HtmlTagPattern().Replace(withoutScripts, " ");
        return WhitespacePattern().Replace(WebUtility.HtmlDecode(withoutTags), " ").Trim();
    }

    private static IReadOnlyCollection<Uri> ExtractLinks(string html, Uri baseUri)
    {
        var links = new HashSet<Uri>();
        foreach (Match match in HrefPattern().Matches(html))
        {
            var value = WebUtility.HtmlDecode(match.Groups["href"].Value);
            if (Uri.TryCreate(baseUri, value, out var link))
            {
                var builder = new UriBuilder(link) { Fragment = string.Empty };
                if (IsIrsUri(builder.Uri))
                {
                    links.Add(builder.Uri);
                }
            }
        }

        return links.ToList();
    }

    [GeneratedRegex(@"<title\b[^>]*>(?<title>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitlePattern();

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptAndStylePattern();

    [GeneratedRegex(@"<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex HtmlTagPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();

    [GeneratedRegex(
        @"\bhref\s*=\s*[""'](?<href>[^""'#]+)[""']",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HrefPattern();

    private sealed record FetchedDocument(
        string Title,
        string Content,
        string Hash,
        string ContentType,
        IReadOnlyCollection<Uri> Links);

    private sealed record RobotsPolicy(
        IReadOnlyCollection<RobotsRule> Rules,
        IReadOnlyCollection<Uri> Sitemaps,
        TimeSpan CrawlDelay)
    {
        public bool IsAllowed(string path)
        {
            var matchingRule = Rules
                .Where(rule => rule.IsMatch(path))
                .OrderByDescending(rule => rule.Pattern.Length)
                .ThenByDescending(rule => rule.Allow)
                .FirstOrDefault();
            return matchingRule?.Allow ?? true;
        }

        public static RobotsPolicy Parse(string robotsText)
        {
            var rules = new List<RobotsRule>();
            var sitemaps = new List<Uri>();
            var appliesToAll = false;
            var delay = TimeSpan.FromMilliseconds(250);

            foreach (var rawLine in robotsText.Split('\n'))
            {
                var line = rawLine.Split('#', 2)[0].Trim();
                if (line.Length == 0 || !line.Contains(':'))
                {
                    continue;
                }

                var parts = line.Split(':', 2);
                var name = parts[0].Trim();
                var value = parts[1].Trim();

                if (name.Equals("User-agent", StringComparison.OrdinalIgnoreCase))
                {
                    appliesToAll = value == "*";
                }
                else if (appliesToAll &&
                         (name.Equals("Disallow", StringComparison.OrdinalIgnoreCase) ||
                          name.Equals("Allow", StringComparison.OrdinalIgnoreCase)) &&
                         value.Length > 0)
                {
                    rules.Add(new RobotsRule(
                        value,
                        name.Equals("Allow", StringComparison.OrdinalIgnoreCase)));
                }
                else if (appliesToAll &&
                         name.Equals("Crawl-delay", StringComparison.OrdinalIgnoreCase) &&
                         decimal.TryParse(value, System.Globalization.NumberStyles.Number,
                             System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                {
                    delay = TimeSpan.FromSeconds(decimal.ToDouble(seconds));
                }
                else if (name.Equals("Sitemap", StringComparison.OrdinalIgnoreCase) &&
                         Uri.TryCreate(value, UriKind.Absolute, out var sitemap) &&
                         IsIrsUri(sitemap))
                {
                    sitemaps.Add(sitemap);
                }
            }

            return new RobotsPolicy(rules, sitemaps, delay);
        }
    }

    private sealed record RobotsRule(string Pattern, bool Allow)
    {
        public bool IsMatch(string path)
        {
            var endAnchored = Pattern.EndsWith('$');
            var pattern = endAnchored ? Pattern[..^1] : Pattern;
            var expression = "^" + Regex.Escape(pattern).Replace(@"\*", ".*");
            if (endAnchored)
            {
                expression += "$";
            }

            return Regex.IsMatch(
                path,
                expression,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }
    }
}
