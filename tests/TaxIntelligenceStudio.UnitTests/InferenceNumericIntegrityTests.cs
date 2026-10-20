using TaxIntelligenceStudio.Domain;
using TaxIntelligenceStudio.Infrastructure.Inference;

namespace TaxIntelligenceStudio.UnitTests;

public sealed class InferenceNumericIntegrityTests
{
    private static readonly ModelChatMessage[] Messages =
    [
        new("user", "Synthetic case: earnings $348000, rent $15000, expenses $18000 " +
            "this year, monthly benefit $2400, property taxes $10000 and $8000."),
        new("assistant", "You have $3,480,000."),
        new("user", "Show the breakdown.")
    ];

    [Theory]
    [InlineData("You have $3,480,000 in employer earnings.")]
    [InlineData("Your rent is $15.00.")]
    [InlineData("Your rent is $1,500.")]
    [InlineData("Passive income is $50,0OO0.")]
    [InlineData("Condo rent is $15,00/month and expenses $18,00/year.")]
    [InlineData("Property taxes are $10,0 and $8,0.")]
    public void ReportedCorruptions_AreRejected(string content) =>
        Assert.Throws<InvalidDataException>(() => InferenceNumericIntegrity.Validate(Messages, content));

    [Fact]
    public void CorrectAmountsAndOrdinaryDerivedAmounts_ArePreserved()
    {
        const string content = "Earnings $348,000; rent $15,000; expenses $18,000. " +
            "$15,000 - $18,000 = -$3,000 before depreciation; $2,400 x 12 = $28,800.";
        InferenceNumericIntegrity.Validate(Messages, content);
    }

    [Fact]
    public void Reminder_UsesOnlyExactUserAmountsNotEarlierAssistantErrors()
    {
        var reminder = InferenceNumericIntegrity.CreateFactReminder(Messages);
        Assert.Contains("$348000", reminder);
        Assert.Contains("$15000", reminder);
        Assert.Contains("$2400", reminder);
        Assert.DoesNotContain("$3,480,000", reminder);
        Assert.Contains("annual rent into monthly", reminder);
    }

    [Fact]
    public void LegitimateScaleCalculations_AreConservativelyFlaggedNotSilentlyCorrected() =>
        Assert.Throws<InvalidDataException>(() =>
            InferenceNumericIntegrity.Validate(Messages, "An illustrative tenth of $15,000 is $1,500."));
}
