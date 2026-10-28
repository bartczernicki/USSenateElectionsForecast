using USSenateElectionsForecast.Markets;
using USSenateElectionsForecast.Markets.Kalshi;
using USSenateElectionsForecast.Markets.Polymarket;

namespace USSenateElectionsForecast;

public static class SnapshotValidator
{
    public const double MinimumRawPriceSum = 0.90;
    public const double MaximumRawPriceSum = 1.10;
    private const double Tolerance = 1e-9;

    public static void Validate(PredictionSnapshot snapshot) => Validate(snapshot, GetSource(snapshot));

    public static PredictionSource GetSource(PredictionSnapshot snapshot)
    {
        if (snapshot?.Races is null || snapshot.Races.Count == 0)
            throw new InvalidDataException("A complete forecast requires all 35 Senate races, including the Florida and Ohio special elections.");
        foreach (var source in Enum.GetValues<PredictionSource>())
        {
            var catalog = Catalog(source);
            if (snapshot.Races.All(race => race is not null && catalog.Any(definition =>
                definition.State == race.State && definition.EventSlug == race.EventSlug)))
                return source;
        }
        throw new InvalidDataException("The predictions contain mixed or unrecognized market sources. Use a complete Polymarket or Kalshi snapshot.");
    }

    public static void Validate(PredictionSnapshot snapshot, PredictionSource source)
    {
        var catalog = Catalog(source);
        if (snapshot is null || snapshot.SchemaVersion != PolymarketCatalog.SchemaVersion || snapshot.ElectionYear != PolymarketCatalog.ElectionYear)
            throw new InvalidDataException("The predictions must use schema version 1 and election year 2026.");
        if (snapshot.RetrievedAt == default)
            throw new InvalidDataException("The predictions have no retrieval timestamp.");
        if (snapshot.Races is null || snapshot.Races.Count != catalog.Count)
            throw new InvalidDataException("A complete forecast requires all 35 Senate races, including the Florida and Ohio special elections.");

        var states = new HashSet<string>(StringComparer.Ordinal);
        var marketIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var race in snapshot.Races)
        {
            if (race is null || !states.Add(race.State))
                throw new InvalidDataException("The predictions contain a missing or duplicate race.");
            var definition = catalog.SingleOrDefault(x => x.State == race.State)
                ?? throw new InvalidDataException($"Unexpected Senate race: {race.State}.");
            ValidateRace(race, definition);
            foreach (var outcome in race.Outcomes)
                if (!marketIds.Add(outcome.MarketId))
                    throw new InvalidDataException($"Duplicate market ID {outcome.MarketId} across races.");
        }
    }

    private static IReadOnlyList<RaceDefinition> Catalog(PredictionSource source) => source switch
    {
        PredictionSource.Polymarket => PolymarketCatalog.All,
        PredictionSource.Kalshi => KalshiRaceCatalog.All,
        _ => throw new ArgumentOutOfRangeException(nameof(source), "Select Polymarket or Kalshi.")
    };

    internal static void ValidateRace(RacePrediction race, RaceDefinition definition)
    {
        if (race.State != definition.State || race.EventSlug != definition.EventSlug || race.IsSpecial != definition.IsSpecial)
            throw new InvalidDataException($"The {definition.State} race does not match the verified 2026 catalog.");
        if (race.Outcomes is null || race.Outcomes.Count != definition.Markets.Count)
            throw new InvalidDataException($"The {definition.State} predictions are incomplete or include an unexpected contract. Review the race catalog before updating.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        double rawSum = 0;
        double normalizedSum = 0;
        foreach (var outcome in race.Outcomes)
        {
            if (outcome is null || string.IsNullOrWhiteSpace(outcome.MarketId) || !ids.Add(outcome.MarketId))
                throw new InvalidDataException($"The {definition.State} predictions contain a missing or duplicate market ID.");
            var expected = definition.Markets.SingleOrDefault(x => x.MarketId == outcome.MarketId);
            if (expected is null || outcome.MarketSlug != expected.MarketSlug || outcome.Caucus != expected.Caucus)
                throw new InvalidDataException($"Unrecognized contract or caucus mapping for {definition.State} market {outcome.MarketId}. Review the race catalog before updating.");
            if (string.IsNullOrWhiteSpace(outcome.Label))
                throw new InvalidDataException($"Market {outcome.MarketId} has no outcome label.");
            if (!IsProbability(outcome.RawYesPrice) || !IsProbability(outcome.Probability))
                throw new InvalidDataException($"Market {outcome.MarketId} has a non-finite probability or a value outside 0–1.");
            rawSum += outcome.RawYesPrice;
            normalizedSum += outcome.Probability;
        }

        ValidateRawSum(rawSum, definition.State);
        if (Math.Abs(normalizedSum - 1) > Tolerance || race.Outcomes.Any(x => Math.Abs(x.Probability - x.RawYesPrice / rawSum) > Tolerance))
            throw new InvalidDataException($"The {definition.State} probabilities must sum to 1 and equal the normalized source prices.");
    }

    internal static void ValidateRawSum(double rawSum, string state)
    {
        if (!double.IsFinite(rawSum) || rawSum < MinimumRawPriceSum - Tolerance || rawSum > MaximumRawPriceSum + Tolerance)
            throw new InvalidDataException($"The {state} Yes prices total {rawSum:F4}; expected 0.90–1.10. The source may be incomplete or its contracts may have changed. Review the race catalog before updating.");
    }

    private static bool IsProbability(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
}
