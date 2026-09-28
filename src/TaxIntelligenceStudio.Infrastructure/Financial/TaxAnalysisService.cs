using TaxIntelligenceStudio.Application;
using TaxIntelligenceStudio.Domain;

namespace TaxIntelligenceStudio.Infrastructure.Financial;

public sealed class TaxAnalysisService(
    TimeProvider timeProvider,
    IAuthoritativeSourceVerifier? sourceVerifier = null) : ITaxAnalysisService
{
    private static readonly Uri Form1040 = new("https://www.irs.gov/forms-pubs/about-form-1040");
    private static readonly Uri ScheduleA = new("https://www.irs.gov/forms-pubs/about-schedule-a-form-1040");
    private static readonly Uri ScheduleB = new("https://www.irs.gov/forms-pubs/about-schedule-b-form-1040");
    private static readonly Uri ScheduleC = new("https://www.irs.gov/forms-pubs/about-schedule-c-form-1040");
    private static readonly Uri ScheduleD = new("https://www.irs.gov/forms-pubs/about-schedule-d-form-1040");
    private static readonly Uri ScheduleE = new("https://www.irs.gov/forms-pubs/about-schedule-e-form-1040");
    private static readonly Uri Form8949 = new("https://www.irs.gov/forms-pubs/about-form-8949");
    private static readonly Uri Form8889 = new("https://www.irs.gov/forms-pubs/about-form-8889");

    public async Task<TaxAnalysisResult> AnalyzeAsync(
        IReadOnlyCollection<FinancialTransaction> transactions,
        IProgress<TaxAnalysisProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        progress?.Report(new TaxAnalysisProgress(
            5,
            $"Classifying {transactions.Count:N0} financial record(s)"));
        var classified = transactions
            .Select(transaction => new ClassifiedTransaction(transaction, Classify(transaction)))
            .ToList();

        progress?.Report(new TaxAnalysisProgress(30, "Building Form 1040 and schedule mappings"));
        var mappings = classified
            .GroupBy(item => item.Rule)
            .Select(group => new TaxMapping(
                group.Key.Category,
                group.Sum(item => item.Transaction.Amount),
                group.Key.Form,
                group.Key.Destination,
                group.Key.Rationale,
                group.Key.SourceUrl,
                group.Key.Confidence))
            .OrderBy(mapping => mapping.Form)
            .ThenBy(mapping => mapping.Category)
            .ToList();

        progress?.Report(new TaxAnalysisProgress(45, "Evaluating evidence-backed optimization candidates"));
        var opportunities = BuildOpportunities(classified);
        if (sourceVerifier is not null)
        {
            var sourceUrls = mappings.Select(mapping => mapping.SourceUrl)
                .Concat(opportunities.Select(opportunity => opportunity.SourceUrl))
                .Distinct()
                .ToList();
            var verifications = new List<VerifiedSource>();
            for (var index = 0; index < sourceUrls.Count; index++)
            {
                var url = sourceUrls[index];
                progress?.Report(new TaxAnalysisProgress(
                    50 + (45d * index / Math.Max(sourceUrls.Count, 1)),
                    $"Verifying IRS source {index + 1:N0} of {sourceUrls.Count:N0}: {GetPublicationName(url)}"));
                verifications.Add(await sourceVerifier.VerifyAsync(
                    new AuthoritativeSource(
                        url,
                        GetPublicationName(url),
                        null,
                        null,
                        null),
                    cancellationToken));
            }

            var failed = verifications.Where(source => !source.IsVerified).ToList();
            if (failed.Count > 0)
            {
                throw new InvalidOperationException(
                    $"{GuidanceResponse.VerificationFailureMessage} " +
                    string.Join(
                        " | ",
                        failed.Select(source =>
                            $"{source.Source.SourceUrl}: {source.FailureReason}")));
            }
        }

        var result = new TaxAnalysisResult(
            transactions,
            mappings,
            opportunities,
            transactions.Where(item => !item.IsInformational && item.Amount > 0).Sum(item => item.Amount),
            Math.Abs(transactions.Where(item => !item.IsInformational && item.Amount < 0).Sum(item => item.Amount)),
            timeProvider.GetUtcNow());

        progress?.Report(new TaxAnalysisProgress(
            100,
            $"Analysis complete: {mappings.Count:N0} mapping(s), {opportunities.Count:N0} review item(s)"));
        return result;
    }

    private static ClassificationRule Classify(FinancialTransaction transaction)
    {
        var description = transaction.Description.ToLowerInvariant();

        if (transaction.TaxForm == "Fidelity year-end summary" &&
            transaction.TaxBox == "Dividends")
        {
            return new("Estimated dividends", "Schedule B / Form 1040", "Reconcile to Form 1099-DIV",
                "The year-end statement is preliminary supporting evidence; use the final tax form for filing.",
                ScheduleB,
                0.65m);
        }

        if (transaction.TaxForm == "Fidelity year-end summary" &&
            transaction.TaxBox == "Interest")
        {
            return new("Estimated taxable interest", "Schedule B / Form 1040", "Reconcile to Form 1099-INT",
                "The year-end statement is preliminary supporting evidence; use the final tax form for filing.",
                ScheduleB,
                0.65m);
        }

        if (transaction.IsInformational)
        {
            return new(
                $"{transaction.TaxForm} informational field",
                transaction.TaxForm ?? "Supporting workpaper",
                $"Box {transaction.TaxBox ?? "field"} - reconciliation only",
                "This field is retained for return preparation but is not added to income or deductions.",
                Form1040,
                0.90m);
        }

        if (transaction.TaxForm == "1099-NEC")
        {
            return new("Nonemployee compensation", "Schedule C", "Gross receipts",
                "Reconcile Form 1099-NEC Box 1 to business books and Schedule C.", ScheduleC, 0.92m);
        }

        if (transaction.TaxForm == "1099-R")
        {
            return new("Retirement distribution", "Form 1040", "IRA or pension distributions",
                "Reconcile gross and taxable distributions with Form 1099-R and distribution codes.", Form1040, 0.90m);
        }

        if (transaction.TaxForm == "SSA-1099")
        {
            return new("Social Security benefits", "Form 1040", "Social Security benefits",
                "Use Form SSA-1099 and the applicable benefits worksheet to determine the taxable amount.", Form1040, 0.90m);
        }

        if (ContainsAny(description, "w-2", "w2 ", "salary", "payroll", "paycheck"))
        {
            return new("Wages", "Form 1040", "Wages, salaries, tips",
                "Potential wage income; reconcile to Form W-2 before filing.", Form1040, 0.80m);
        }

        if (ContainsAny(description, "interest", "int payment", "savings interest"))
        {
            return new("Taxable interest", "Schedule B / Form 1040", "Interest income",
                "Potential interest income; reconcile to Forms 1099-INT.", ScheduleB, 0.85m);
        }

        if (ContainsAny(description, "dividend", "div reinvest", "qualified div"))
        {
            return new("Dividends", "Schedule B / Form 1040", "Dividend income",
                "Potential dividend income; reconcile to Forms 1099-DIV.", ScheduleB, 0.85m);
        }

        if (ContainsAny(description, "stock", "broker", "security", "crypto", "coinbase",
                "fidelity", "schwab", "etrade"))
        {
            return new("Capital transaction", "Form 8949 / Schedule D",
                "Sales and other dispositions of capital assets",
                "Basis and proceeds must be reconciled to brokerage records and Forms 1099-B.",
                Form8949,
                0.70m);
        }

        if (ContainsAny(description, "rent", "tenant", "property management"))
        {
            return new("Rental activity", "Schedule E", "Supplemental income or loss",
                "Potential rental activity; classify income and ordinary/necessary expenses.",
                ScheduleE,
                0.70m);
        }

        if (ContainsAny(description, "hsa", "health savings"))
        {
            return new("HSA activity", "Form 8889", "HSA contributions or distributions",
                "Reconcile HSA activity to Forms 1099-SA and 5498-SA.", Form8889, 0.85m);
        }

        if (ContainsAny(
                description,
                "participant contribution",
                "partic contr",
                "401k",
                "401(k)",
                "ira contribution",
                "retirement contribution"))
        {
            return new("Retirement contribution activity", "Form 1040 / Schedule 1",
                "Retirement contributions requiring plan and W-2 reconciliation",
                "Do not deduct payroll deferrals again. Reconcile contribution type, employer plan reporting, and any separately deductible IRA contribution.",
                Form1040,
                0.72m);
        }

        if (ContainsAny(description, "charity", "donation", "church", "nonprofit"))
        {
            return new("Charitable contribution candidate", "Schedule A",
                "Gifts to charity",
                "Potential itemized deduction subject to substantiation and eligibility limits.",
                ScheduleA,
                0.72m);
        }

        if (ContainsAny(description, "medical", "hospital", "doctor", "pharmacy", "dental"))
        {
            return new("Medical expense candidate", "Schedule A",
                "Medical and dental expenses",
                "Potential itemized expense subject to adjusted-gross-income limitations.",
                ScheduleA,
                0.65m);
        }

        if (ContainsAny(description, "mortgage interest", "property tax", "real estate tax"))
        {
            return new("Home deduction candidate", "Schedule A",
                "Taxes or qualified home mortgage interest",
                "Potential itemized deduction; reconcile to Forms 1098 and tax statements.",
                ScheduleA,
                0.75m);
        }

        if (ContainsAny(description, "client", "invoice", "consulting", "freelance",
                "business", "office", "software", "advertising"))
        {
            return new("Business activity", "Schedule C",
                transaction.Amount >= 0 ? "Gross receipts" : "Business expense candidate",
                "Potential sole-proprietor activity requiring business-purpose substantiation.",
                ScheduleC,
                0.65m);
        }

        return transaction.Amount >= 0
            ? new("Unclassified inflow", "Form 1040 / Schedule 1", "Income requiring review",
                "Determine whether this inflow is taxable, a transfer, loan, refund, or other item.",
                Form1040,
                0.35m)
            : new("Unclassified outflow", "Supporting workpaper", "Expense requiring review",
                "No deduction is claimed without business purpose, eligibility, and substantiation.",
                Form1040,
                0.25m);
    }

    private static IReadOnlyCollection<TaxOpportunity> BuildOpportunities(
        IReadOnlyCollection<ClassifiedTransaction> classified)
    {
        var opportunities = new List<TaxOpportunity>();
        AddOpportunity(
            opportunities,
            classified,
            "Charitable contribution candidate",
            "Compare itemized deductions",
            "Documented charitable contributions may support Schedule A when itemizing produces a better allowed result than the standard deduction.",
            ScheduleA);
        AddOpportunity(
            opportunities,
            classified,
            "Medical expense candidate",
            "Review deductible medical expenses",
            "Eligible unreimbursed medical expenses may contribute to Schedule A after applicable limitations.",
            ScheduleA);
        AddOpportunity(
            opportunities,
            classified,
            "Business activity",
            "Review ordinary and necessary business expenses",
            "Substantiated business expenses may reduce Schedule C net profit; personal expenses must be excluded.",
            ScheduleC);

        var capitalLoss = classified
            .Where(item => item.Rule.Category == "Capital transaction" &&
                           item.Transaction.Amount < 0)
            .Sum(item => Math.Abs(item.Transaction.Amount));
        if (capitalLoss > 0)
        {
            opportunities.Add(new TaxOpportunity(
                "Review capital losses and carryovers",
                "Reconcile basis, realized losses, wash sales, and prior-year carryovers before applying Schedule D limitations.",
                capitalLoss,
                ScheduleD,
                0.60m));
        }

        return opportunities;
    }

    private static void AddOpportunity(
        ICollection<TaxOpportunity> opportunities,
        IEnumerable<ClassifiedTransaction> classified,
        string category,
        string title,
        string explanation,
        Uri sourceUrl)
    {
        var amount = classified
            .Where(item => item.Rule.Category == category && item.Transaction.Amount < 0)
            .Sum(item => Math.Abs(item.Transaction.Amount));
        if (amount > 0)
        {
            opportunities.Add(new TaxOpportunity(
                title,
                explanation,
                amount,
                sourceUrl,
                0.65m));
        }
    }

    private static bool ContainsAny(string value, params string[] terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

    private static string GetPublicationName(Uri url)
    {
        var slug = url.Segments.LastOrDefault()?.Trim('/') ?? "IRS guidance";
        return slug.Replace('-', ' ');
    }

    private sealed record ClassificationRule(
        string Category,
        string Form,
        string Destination,
        string Rationale,
        Uri SourceUrl,
        decimal Confidence);

    private sealed record ClassifiedTransaction(
        FinancialTransaction Transaction,
        ClassificationRule Rule);
}
