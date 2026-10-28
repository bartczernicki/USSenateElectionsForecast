using USSenateElectionsForecast.Markets;

namespace USSenateElectionsForecast.Markets.Kalshi;

/// <summary>Verified Kalshi contracts for the same 35 Senate races as the Polymarket catalog.</summary>
public static class KalshiRaceCatalog
{
    // Verified through the public Kalshi API on October 28, 2026. Independents remain unaffiliated.
    public static IReadOnlyList<RaceDefinition> All { get; } = Array.AsReadOnly(new[]
    {
        Pair("Alabama", "SENATEAL-26"),
        Pair("Alaska", "SENATEAK-26"),
        Pair("Arkansas", "SENATEAR-26"),
        Pair("Colorado", "SENATECO-26"),
        Pair("Delaware", "SENATEDE-26"),
        Pair("Florida", "SENATEFLS-26", isSpecial: true),
        Pair("Georgia", "SENATEGA-26"),
        Pair("Idaho", "SENATEID-26", independentSuffix: "TACH"),
        Pair("Illinois", "SENATEIL-26"),
        Pair("Iowa", "SENATEIA-26"),
        Pair("Kansas", "SENATEKS-26"),
        // Kalshi's legacy LA ticker identifies Kentucky; the event title and rules confirm the state.
        Pair("Kentucky", "SENATELA-26"),
        Pair("Louisiana", "KXSENATELA-26NOV"),
        Pair("Maine", "SENATEME-26"),
        Pair("Massachusetts", "SENATEMA-26"),
        Pair("Michigan", "SENATEMI-26"),
        Pair("Minnesota", "SENATEMN-26"),
        Pair("Mississippi", "SENATEMS-26"),
        Pair("Montana", "SENATEMT-26", independentSuffix: "IND"),
        Pair("Nebraska", "SENATENE-26", independentSuffix: "DOSB"),
        Pair("New Hampshire", "SENATENH-26"),
        Pair("New Jersey", "SENATENJ-26"),
        Pair("New Mexico", "SENATENM-26"),
        Pair("North Carolina", "SENATENC-26"),
        Pair("Ohio", "SENATEOHS-26", isSpecial: true),
        Pair("Oklahoma", "SENATEOK-26"),
        Pair("Oregon", "SENATEOR-26"),
        Pair("Rhode Island", "SENATERI-26"),
        Pair("South Carolina", "SENATESC-26"),
        Pair("South Dakota", "SENATESD-26", independentSuffix: "BBEN"),
        Pair("Tennessee", "SENATETN-26"),
        Pair("Texas", "SENATETX-26"),
        Pair("Virginia", "SENATEVA-26"),
        Pair("West Virginia", "SENATEWV-26"),
        Pair("Wyoming", "SENATEWY-26")
    });

    private static RaceDefinition Pair(string state, string eventTicker, bool isSpecial = false, string? independentSuffix = null)
    {
        var markets = new List<MarketDefinition>
        {
            Market(eventTicker, "D", Caucus.Democratic),
            Market(eventTicker, "R", Caucus.Republican)
        };
        if (independentSuffix is not null)
            markets.Add(Market(eventTicker, independentSuffix, Caucus.Unknown));
        return new(state, eventTicker, isSpecial, markets.AsReadOnly());
    }

    private static MarketDefinition Market(string eventTicker, string suffix, Caucus caucus)
    {
        string ticker = $"{eventTicker}-{suffix}";
        return new(ticker, ticker, caucus);
    }
}
