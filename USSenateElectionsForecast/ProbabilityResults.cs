namespace USSenateElectionsForecast;

public enum CalculationMethod
{
    IndependentMonteCarlo,
    CorrelatedMonteCarlo,
    ConditionalMonteCarlo,
    DirectIndependent,
    DirectCorrelated
}

/// <summary>A probability mass function for the final number of seats, indexed from 0 through 100.</summary>
public sealed class ProbabilitySeatDistribution
{
    private const double MassTolerance = 1e-10;

    public ProbabilitySeatDistribution(IEnumerable<double> probabilities)
    {
        ArgumentNullException.ThrowIfNull(probabilities);
        var values = probabilities.ToArray();
        if (values.Length != 101 || values.Any(value => !double.IsFinite(value) || value < 0 || value > 1 + MassTolerance))
            throw new ArgumentException("A seat distribution requires 101 finite probabilities between zero and one.", nameof(probabilities));
        var mass = values.Sum();
        if (Math.Abs(mass - 1) > MassTolerance)
            throw new ArgumentException("Seat probabilities must sum to one.", nameof(probabilities));
        // Correct floating-point accumulation error without discarding genuine election uncertainty.
        for (int i = 0; i < values.Length; i++) values[i] /= mass;
        Probabilities = Array.AsReadOnly(values);
        Mean = values.Select((value, seats) => value * seats).Sum();
    }

    public IReadOnlyList<double> Probabilities { get; }
    public double Mean { get; }

    public int Percentile(double p)
    {
        if (!double.IsFinite(p) || p is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(p), "Percentile probability must be between zero and one.");
        if (p == 0) return Enumerable.Range(0, 101).First(i => Probabilities[i] > 0);
        if (p == 1) return Enumerable.Range(0, 101).Last(i => Probabilities[i] > 0);
        double cumulative = 0;
        for (int seats = 0; seats < Probabilities.Count; seats++)
        {
            cumulative += Probabilities[seats];
            double roundoff = 8 * 2.220446049250313e-16 * Math.Max(cumulative, p);
            if (Probabilities[seats] > 0 && (cumulative >= p || p - cumulative <= roundoff)) return seats;
        }
        return Enumerable.Range(0, 101).Last(i => Probabilities[i] > 0);
    }

    public double ProbabilityAtLeast(int seats)
    {
        if (seats <= 0) return 1;
        if (seats > 100) return 0;
        return Math.Clamp(Probabilities.Skip(seats).Sum(), 0, 1);
    }

    public static ProbabilitySeatDistribution FromCounts(SeatDistribution distribution)
    {
        ArgumentNullException.ThrowIfNull(distribution);
        double total = distribution.Counts.Sum(count => (long)count);
        return new(distribution.Counts.Select(count => count / total));
    }
}

public sealed record IntegrationDiagnostics(int Order, double Tolerance, double DistributionChange,
    double MassError, double MeanError);

public sealed record ProbabilityResult(
    CalculationMethod Method,
    int? SampleCount,
    double Rho,
    DateTimeOffset SnapshotAt,
    ProbabilitySeatDistribution DemocraticSeats,
    ProbabilitySeatDistribution RepublicanSeats,
    ProbabilitySeatDistribution UnknownSeats,
    IntegrationDiagnostics? Integration = null)
{
    public double DemocraticControlProbability => DemocraticSeats.ProbabilityAtLeast(51);
    public double RepublicanControlProbability => RepublicanSeats.ProbabilityAtLeast(50);
    public double UncertainControlProbability
    {
        get
        {
            double remainder = 1 - DemocraticControlProbability - RepublicanControlProbability;
            if (remainder < -1e-10)
                throw new ArithmeticException("The calculated control probabilities overlap.");
            return Math.Clamp(remainder, 0, 1);
        }
    }

    public static ProbabilityResult FromSimulation(SimulationResult result) => new(
        result.Mode == SimulationMode.Independent ? CalculationMethod.IndependentMonteCarlo : CalculationMethod.CorrelatedMonteCarlo,
        result.TrialCount, result.Rho, result.SnapshotAt,
        ProbabilitySeatDistribution.FromCounts(result.DemocraticSeats),
        ProbabilitySeatDistribution.FromCounts(result.RepublicanSeats),
        ProbabilitySeatDistribution.FromCounts(result.UnknownSeats));
}

public sealed class NumericalConvergenceException(string message) : ArithmeticException(message);
