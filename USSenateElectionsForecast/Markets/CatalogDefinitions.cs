namespace USSenateElectionsForecast.Markets;

public record MarketDefinition(string MarketId, string MarketSlug, Caucus Caucus);

public record RaceDefinition(
    string State,
    string EventSlug,
    bool IsSpecial,
    IReadOnlyList<MarketDefinition> Markets);
