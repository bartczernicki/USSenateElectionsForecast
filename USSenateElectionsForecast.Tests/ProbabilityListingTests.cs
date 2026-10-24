using System.Globalization;

namespace USSenateElectionsForecast.Tests;

[TestClass]
public sealed class ProbabilityListingTests
{
    [TestMethod]
    public void ListingRanksByTopTwoOutcomeGapAndBreaksTiesByStateName()
    {
        var snapshot = new PredictionSnapshot(1, 2026, new DateTimeOffset(2026, 10, 24, 21, 15, 0, TimeSpan.Zero),
        [
            Race("Texas", false, ("Texas Democrat", 0.49), ("Texas Republican", 0.51)),
            Race("Ohio", true, ("Ohio Republican", 0.75), ("Ohio Democrat", 0.25)),
            Race("Nebraska", false, ("Nebraska Democrat", 0.33), ("Nebraska Independent", 0.33), ("Nebraska Republican", 0.34)),
            Race("Alabama", false, ("Alabama Democrat", 0.25), ("Alabama Republican", 0.75))
        ]);
        using var output = new StringWriter(CultureInfo.InvariantCulture);

        new ConsolePresentation(output).Predictions(snapshot);

        string text = output.ToString();
        StringAssert.Contains(text, " 1) Nebraska | Gap: 1.00 percentage points");
        StringAssert.Contains(text, " 2) Texas | Gap: 2.00 percentage points");
        StringAssert.Contains(text, " 3) Alabama | Gap: 50.00 percentage points");
        StringAssert.Contains(text, " 4) Ohio (special) | Gap: 50.00 percentage points");
        StringAssert.Contains(text, "Predictions retrieved: 2026-10-24 21:15 UTC | Races: 4");
        StringAssert.Contains(text, "Nebraska Independent: 33.00 %");
        StringAssert.Contains(text, "Nebraska Democrat: 33.00 %");
        StringAssert.Contains(text, "Nebraska Republican: 34.00 %");
        Assert.IsLessThan(text.IndexOf("Nebraska Democrat:", StringComparison.Ordinal),
            text.IndexOf("Nebraska Republican:", StringComparison.Ordinal));
        Assert.IsLessThan(text.IndexOf(" 2) Texas", StringComparison.Ordinal),
            text.IndexOf("Nebraska Democrat:", StringComparison.Ordinal));
        foreach (string line in text.ReplaceLineEndings("\n").Split('\n'))
            Assert.IsLessThanOrEqualTo(80, line.Length);
    }

    private static RacePrediction Race(string state, bool isSpecial, params (string Label, double Probability)[] outcomes) =>
        new(state, state.ToLowerInvariant(), isSpecial, null, outcomes.Select((item, index) =>
            new MarketPrediction(index.ToString(CultureInfo.InvariantCulture), $"{state}-{index}", item.Label,
                Caucus.Unknown, item.Probability, item.Probability, null)).ToArray());
}
