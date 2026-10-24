namespace USSenateElectionsForecast;

public enum Caucus { Democratic, Unknown, Republican }

public enum SimulationMode { Independent, Correlated }

public sealed record AppSettings(double Rho = 0.12, int TrialCount = 100_000)
{
    public void Validate()
    {
        if (!double.IsFinite(Rho) || Rho is < 0 or > 0.99)
            throw new ArgumentOutOfRangeException(nameof(Rho), "Rho must be between 0.00 and 0.99.");
        if (TrialCount is < 1_000 or > 10_000_000)
            throw new ArgumentOutOfRangeException(nameof(TrialCount), "Simulation count must be between 1,000 and 10,000,000.");
    }
}

public sealed record MarketPrediction(
    string MarketId,
    string MarketSlug,
    string Label,
    Caucus Caucus,
    double RawYesPrice,
    double Probability,
    DateTimeOffset? UpdatedAt);

public sealed record RacePrediction(
    string State,
    string EventSlug,
    bool IsSpecial,
    DateTimeOffset? SourceUpdatedAt,
    IReadOnlyList<MarketPrediction> Outcomes)
{
    public double ProbabilityFor(Caucus caucus) => Outcomes.Where(x => x.Caucus == caucus).Sum(x => x.Probability);
}

public sealed record PredictionSnapshot(
    int SchemaVersion,
    int ElectionYear,
    DateTimeOffset RetrievedAt,
    IReadOnlyList<RacePrediction> Races);
