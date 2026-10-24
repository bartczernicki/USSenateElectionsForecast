using MathNet.Numerics.Distributions;

namespace USSenateElectionsForecast;

public sealed class SimulationEngine
{
    public SimulationResult Run(
        PredictionSnapshot snapshot,
        SimulationMode mode,
        int trialCount,
        double rho,
        Random? random = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (trialCount is < 1 or > 10_000_000)
            throw new ArgumentOutOfRangeException(nameof(trialCount), "Trial count must be between 1 and 10,000,000.");
        if (!double.IsFinite(rho) || rho is < 0 or > 0.99)
            throw new ArgumentOutOfRangeException(nameof(rho), "Rho must be between 0.00 and 0.99.");
        cancellationToken.ThrowIfCancellationRequested();
        SnapshotValidator.Validate(snapshot);

        var races = snapshot.Races.Select(RaceThresholds.FromPrediction).ToArray();
        var democraticCounts = new int[101];
        var republicanCounts = new int[101];
        var unknownCounts = new int[101];
        int democraticControl = 0, republicanControl = 0, uncertainControl = 0;
        var generator = random ?? Random.Shared;
        var effectiveRho = mode == SimulationMode.Correlated ? rho : 0;
        var nationalWeight = Math.Sqrt(effectiveRho);
        var raceWeight = Math.Sqrt(1 - effectiveRho);

        for (var trial = 0; trial < trialCount; trial++)
        {
            if ((trial & 255) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            var democraticSeats = RaceCatalog.DemocraticBaseline;
            var republicanSeats = RaceCatalog.RepublicanBaseline;
            var unknownSeats = 0;
            var nationalShock = effectiveRho > 0 ? Normal.Sample(generator, 0, 1) * nationalWeight : 0;

            foreach (var race in races)
            {
                Caucus winner;
                if (race.CertainWinner is { } certainWinner)
                {
                    winner = certainWinner;
                }
                else if (effectiveRho == 0)
                {
                    var draw = generator.NextDouble();
                    winner = draw < race.DemocraticCumulative ? Caucus.Democratic
                        : draw < race.UnknownCumulative ? Caucus.Unknown
                        : Caucus.Republican;
                }
                else
                {
                    // Fixed D / unaffiliated / R order makes a positive national shock favor Republicans.
                    var score = nationalShock + raceWeight * Normal.Sample(generator, 0, 1);
                    winner = score < race.DemocraticThreshold ? Caucus.Democratic
                        : score < race.UnknownThreshold ? Caucus.Unknown
                        : Caucus.Republican;
                }

                switch (winner)
                {
                    case Caucus.Democratic: democraticSeats++; break;
                    case Caucus.Republican: republicanSeats++; break;
                    default: unknownSeats++; break;
                }
            }

            democraticCounts[democraticSeats]++;
            republicanCounts[republicanSeats]++;
            unknownCounts[unknownSeats]++;

            // A Republican vice president breaks a 50-50 tie. Unaffiliated winners are not assigned a caucus.
            if (republicanSeats >= 50) republicanControl++;
            else if (democraticSeats >= 51) democraticControl++;
            else uncertainControl++;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new SimulationResult(mode, trialCount, effectiveRho, snapshot.RetrievedAt,
            new SeatDistribution(democraticCounts), new SeatDistribution(republicanCounts),
            new SeatDistribution(unknownCounts), democraticControl, republicanControl, uncertainControl);
    }

    private readonly record struct RaceThresholds(
        double DemocraticCumulative,
        double UnknownCumulative,
        double DemocraticThreshold,
        double UnknownThreshold,
        Caucus? CertainWinner)
    {
        public static RaceThresholds FromPrediction(RacePrediction prediction)
        {
            var democratic = prediction.ProbabilityFor(Caucus.Democratic);
            var unknown = prediction.ProbabilityFor(Caucus.Unknown);
            var republican = prediction.ProbabilityFor(Caucus.Republican);
            // Normalize the validated aggregate once more to absorb harmless floating-point summation error.
            var total = democratic + unknown + republican;
            democratic /= total;
            unknown /= total;
            republican /= total;
            var secondCumulative = Math.Min(1, democratic + unknown);
            Caucus? certainWinner = democratic == 1 ? Caucus.Democratic
                : unknown == 1 ? Caucus.Unknown
                : republican == 1 ? Caucus.Republican
                : null;
            return new(democratic, secondCumulative, Quantile(democratic), Quantile(secondCumulative), certainWinner);
        }

        private static double Quantile(double probability) => probability <= 0 ? double.NegativeInfinity
            : probability >= 1 ? double.PositiveInfinity
            : Normal.InvCDF(0, 1, probability);
    }

}

public sealed record SimulationResult(
    SimulationMode Mode,
    int TrialCount,
    double Rho,
    DateTimeOffset SnapshotAt,
    SeatDistribution DemocraticSeats,
    SeatDistribution RepublicanSeats,
    SeatDistribution UnknownSeats,
    int DemocraticControlTrials,
    int RepublicanControlTrials,
    int UncertainControlTrials);

public sealed class SeatDistribution
{
    private readonly long _total;

    public SeatDistribution(int[] counts)
    {
        ArgumentNullException.ThrowIfNull(counts);
        if (counts.Length != 101 || counts.Any(count => count < 0))
            throw new ArgumentException("A seat distribution requires 101 nonnegative counts.", nameof(counts));
        Counts = (int[])counts.Clone();
        _total = Counts.Sum(count => (long)count);
        if (_total == 0)
            throw new ArgumentException("A seat distribution requires at least one trial.", nameof(counts));
        Mean = Counts.Select((count, seats) => (long)count * seats).Sum() / (double)_total;
    }

    public int[] Counts { get; }
    public double Mean { get; }

    /// <summary>Returns the empirical inverse CDF at a probability in the inclusive range [0, 1].</summary>
    public int Percentile(double p)
    {
        if (!double.IsFinite(p) || p is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(p), "Percentile probability must be between 0 and 1.");
        var target = Math.Max(1, (long)Math.Ceiling(p * _total));
        long cumulative = 0;
        for (var seats = 0; seats < Counts.Length; seats++)
        {
            cumulative += Counts[seats];
            if (cumulative >= target) return seats;
        }
        throw new InvalidOperationException("The seat distribution has been modified after construction.");
    }
}
