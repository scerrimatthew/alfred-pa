using Alfred.Functions.Services.Gmail;
using Xunit;

namespace Alfred.Functions.Tests;

public class LabelNamesTests
{
    [Theory]
    [InlineData("weekly-plan", "Alfred/School/Weekly Plan")]
    [InlineData("homework", "Alfred/School/Homework")]
    [InlineData("other", "Alfred/School/Other")]
    [InlineData("OUTING", "Alfred/School/OUTING")] // only the first letter is upper-cased, rest is preserved
    public void ForSchool_NestsUnderAlfredSchoolAndHumanizesSlug(string slug, string expected)
    {
        Assert.Equal(expected, LabelNames.ForSchool(slug));
    }

    [Theory]
    [InlineData("payment-request", "Payment Request")]
    [InlineData("invoice", "Invoice")]
    [InlineData("personal-reply", "Personal Reply")]
    [InlineData("  delivery  ", "Delivery")]
    [InlineData("two words", "Two Words")]
    public void ForPersonal_IsBareHumanizedCategory(string slug, string expected)
    {
        Assert.Equal(expected, LabelNames.ForPersonal(slug));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    public void ForPersonal_BlankSlug_FallsBackToOther(string slug)
    {
        Assert.Equal("Other", LabelNames.ForPersonal(slug));
    }

    [Fact]
    public void PersonalCategoryLabels_CoverEveryTriageCategory()
    {
        // The triage prompt offers exactly these categories; recategorization looks the old
        // label up in this set, so each humanized category must be present.
        string[] triageCategories =
        [
            "invoice", "payment-request", "personal-reply", "appointment", "financial",
            "official", "security", "delivery", "notification", "other"
        ];

        foreach (var category in triageCategories)
        {
            Assert.Contains(LabelNames.ForPersonal(category), LabelNames.PersonalCategoryLabels);
        }
    }

    [Fact]
    public void PersonalCategoryLabels_MatchCaseInsensitively()
    {
        Assert.Contains("payment request", LabelNames.PersonalCategoryLabels);
        Assert.Contains("INVOICE", LabelNames.PersonalCategoryLabels);
    }
}
