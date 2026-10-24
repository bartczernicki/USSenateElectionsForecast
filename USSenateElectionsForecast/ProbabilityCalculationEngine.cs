using System.Globalization;
using MathNet.Numerics.Distributions;
using MathNet.Numerics.Integration;

namespace USSenateElectionsForecast;

/// <summary>Aggregates race probabilities while retaining the uncertainty in the national factor.</summary>
public sealed class ProbabilityCalculationEngine
{
    private const int InitialIntegrationOrder = 32;
    private const int DefaultMaximumIntegrationOrder = 8192;
    private const double DefaultConvergenceTolerance = 1e-10;
    private const double MeanTolerance = 1e-8;
    private readonly int _maximumIntegrationOrder;
    private readonly double _convergenceTolerance;

    public ProbabilityCalculationEngine() : this(DefaultMaximumIntegrationOrder) { }

    internal ProbabilityCalculationEngine(int maximumIntegrationOrder, double convergenceTolerance = DefaultConvergenceTolerance)
    {
        if (maximumIntegrationOrder is < InitialIntegrationOrder or > DefaultMaximumIntegrationOrder
            || (maximumIntegrationOrder & (maximumIntegrationOrder - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(maximumIntegrationOrder), "The integration limit must be a power of two from 32 through 8192.");
        if (!double.IsFinite(convergenceTolerance) || convergenceTolerance <= 0)
            throw new ArgumentOutOfRangeException(nameof(convergenceTolerance), "The convergence tolerance must be positive and finite.");
        _maximumIntegrationOrder = maximumIntegrationOrder;
        _convergenceTolerance = convergenceTolerance;
    }

    public ProbabilityResult RunConditional(
        PredictionSnapshot snapshot,
        int sampleCount,
        double rho,
        Random? random = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (sampleCount is < 1 or > 10_000_000)
            throw new ArgumentOutOfRangeException(nameof(sampleCount), "National-factor sample count must be between 1 and 10,000,000.");
        ValidateRho(rho);
        var races = Prepare(snapshot, cancellationToken);
        var workspace = new ConditionalWorkspace(races, rho);
        if (rho == 0)
        {
            workspace.EvaluateMarginals();
            cancellationToken.ThrowIfCancellationRequested();
            return CreateResult(snapshot, CalculationMethod.ConditionalMonteCarlo, 0, 0, workspace.Distributions);
        }

        var generator = random ?? Random.Shared;
        var accumulator = new DistributionAccumulator(races.Length + 1);
        for (int sample = 0; sample < sampleCount; sample++)
        {
            if ((sample & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
            workspace.EvaluateFactor(Normal.Sample(generator, 0, 1));
            accumulator.Add(workspace.Distributions, 1);
        }

        cancellationToken.ThrowIfCancellationRequested();
        return CreateResult(snapshot, CalculationMethod.ConditionalMonteCarlo, sampleCount, rho,
            accumulator.Average(sampleCount));
    }

    public ProbabilityResult CalculateIndependent(PredictionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var races = Prepare(snapshot, cancellationToken);
        var workspace = new ConditionalWorkspace(races, 0);
        workspace.EvaluateMarginals();
        cancellationToken.ThrowIfCancellationRequested();
        return CreateResult(snapshot, CalculationMethod.DirectIndependent, null, 0, workspace.Distributions);
    }

    public ProbabilityResult CalculateCorrelated(
        PredictionSnapshot snapshot,
        double rho,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateRho(rho);
        var races = Prepare(snapshot, cancellationToken);
        var workspace = new ConditionalWorkspace(races, rho);
        if (rho == 0)
        {
            workspace.EvaluateMarginals();
            cancellationToken.ThrowIfCancellationRequested();
            return CreateResult(snapshot, CalculationMethod.DirectCorrelated, null, 0, workspace.Distributions);
        }

        var expectedMeans = new[]
        {
            RaceCatalog.DemocraticBaseline + races.Sum(race => race.Democratic),
            races.Sum(race => race.Unknown),
            RaceCatalog.RepublicanBaseline + races.Sum(race => race.Republican)
        };
        double[][]? previous = null;
        int consecutiveConverged = 0;
        double distributionChange = double.PositiveInfinity;
        double massError = double.PositiveInfinity;
        double meanError = double.PositiveInfinity;

        for (int order = InitialIntegrationOrder; order <= _maximumIntegrationOrder; order *= 2)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // The omitted standard-normal tail outside [-9, 9] is approximately 2.26e-19.
            var rule = new GaussLegendreRule(-9, 9, order);
            var nodes = rule.Abscissas;
            var weights = rule.Weights;
            var accumulator = new DistributionAccumulator(races.Length + 1);
            for (int node = 0; node < order; node++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                workspace.EvaluateFactor(nodes[node]);
                accumulator.Add(workspace.Distributions, weights[node] * Normal.PDF(0, 1, nodes[node]));
            }

            var current = accumulator.Values;
            // Assess raw integrals before the result distributions correct floating-point mass error.
            massError = MaximumMassError(current);
            meanError = MaximumMeanError(current, expectedMeans);
            distributionChange = previous is null ? double.PositiveInfinity : MaximumL1Change(current, previous);
            bool converged = previous is not null
                && distributionChange <= _convergenceTolerance
                && massError <= Math.Min(_convergenceTolerance, DefaultConvergenceTolerance)
                && meanError <= MeanTolerance;
            consecutiveConverged = converged ? consecutiveConverged + 1 : 0;
            if (consecutiveConverged >= 2)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var diagnostics = new IntegrationDiagnostics(order, _convergenceTolerance, distributionChange, massError, meanError);
                return CreateResult(snapshot, CalculationMethod.DirectCorrelated, null, rho, current, diagnostics);
            }
            previous = current;
        }

        cancellationToken.ThrowIfCancellationRequested();
        throw new NumericalConvergenceException(string.Create(CultureInfo.InvariantCulture,
            $"The correlated probability calculation did not converge within {_maximumIntegrationOrder:N0} integration nodes. " +
            $"Last distribution change: {distributionChange:G4}; mass error: {massError:G4}; mean error: {meanError:G4} seats. No direct result was produced."));
    }

    private static RaceInputs[] Prepare(PredictionSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SnapshotValidator.Validate(snapshot);
        return snapshot.Races.Select(race =>
        {
            double democratic = race.ProbabilityFor(Caucus.Democratic);
            double unknown = race.ProbabilityFor(Caucus.Unknown);
            double republican = race.ProbabilityFor(Caucus.Republican);
            double total = democratic + unknown + republican;
            democratic /= total;
            unknown /= total;
            republican /= total;
            return new RaceInputs(democratic, unknown, republican, Quantile(democratic), Quantile(Math.Min(1, democratic + unknown)));
        }).ToArray();
    }

    private static void ValidateRho(double rho)
    {
        if (!double.IsFinite(rho) || rho is < 0 or > 0.99)
            throw new ArgumentOutOfRangeException(nameof(rho), "Rho must be between 0.00 and 0.99.");
    }

    private static double Quantile(double probability) => probability <= 0 ? double.NegativeInfinity
        : probability >= 1 ? double.PositiveInfinity : Normal.InvCDF(0, 1, probability);

    private static ProbabilityResult CreateResult(PredictionSnapshot snapshot, CalculationMethod method,
        int? sampleCount, double rho, double[][] distributions, IntegrationDiagnostics? integration = null) =>
        new(method, sampleCount, rho, snapshot.RetrievedAt,
            Shift(distributions[0], RaceCatalog.DemocraticBaseline),
            Shift(distributions[2], RaceCatalog.RepublicanBaseline),
            Shift(distributions[1], 0), integration);

    private static ProbabilitySeatDistribution Shift(double[] contestedSeats, int baseline)
    {
        var finalSeats = new double[101];
        Array.Copy(contestedSeats, 0, finalSeats, baseline, contestedSeats.Length);
        return new ProbabilitySeatDistribution(finalSeats);
    }

    private static double MaximumMassError(double[][] distributions) =>
        distributions.Max(distribution => Math.Abs(distribution.Sum() - 1));

    private static double MaximumMeanError(double[][] distributions, double[] expectedMeans)
    {
        double error = 0;
        for (int bloc = 0; bloc < distributions.Length; bloc++)
        {
            int baseline = bloc == 0 ? RaceCatalog.DemocraticBaseline : bloc == 2 ? RaceCatalog.RepublicanBaseline : 0;
            double mean = distributions[bloc].Select((probability, seats) => probability * (seats + baseline)).Sum();
            error = Math.Max(error, Math.Abs(mean - expectedMeans[bloc]));
        }
        return error;
    }

    private static double MaximumL1Change(double[][] current, double[][] previous)
    {
        double maximum = 0;
        for (int bloc = 0; bloc < current.Length; bloc++)
        {
            double change = 0;
            for (int seats = 0; seats < current[bloc].Length; seats++)
                change += Math.Abs(current[bloc][seats] - previous[bloc][seats]);
            maximum = Math.Max(maximum, change);
        }
        return maximum;
    }

    private readonly record struct RaceInputs(double Democratic, double Unknown, double Republican,
        double DemocraticThreshold, double UnknownThreshold);

    private sealed class ConditionalWorkspace(RaceInputs[] races, double rho)
    {
        private readonly double _nationalWeight = Math.Sqrt(rho);
        private readonly double _residualWeight = Math.Sqrt(1 - rho);
        private readonly double[][] _raceProbabilities = [new double[races.Length], new double[races.Length], new double[races.Length]];
        public double[][] Distributions { get; } = [new double[races.Length + 1], new double[races.Length + 1], new double[races.Length + 1]];

        public void EvaluateMarginals()
        {
            for (int i = 0; i < races.Length; i++)
            {
                _raceProbabilities[0][i] = races[i].Democratic;
                _raceProbabilities[1][i] = races[i].Unknown;
                _raceProbabilities[2][i] = races[i].Republican;
            }
            Aggregate();
        }

        public void EvaluateFactor(double factor)
        {
            double nationalShock = _nationalWeight * factor;
            for (int i = 0; i < races.Length; i++)
            {
                double lower = (races[i].DemocraticThreshold - nationalShock) / _residualWeight;
                double upper = (races[i].UnknownThreshold - nationalShock) / _residualWeight;
                double democratic = Normal.CDF(0, 1, lower);
                double republican = Normal.CDF(0, 1, -upper);
                // Use the survival functions when both bounds are positive to retain small middle probabilities.
                double unknown = lower > 0
                    ? Normal.CDF(0, 1, -lower) - republican
                    : Normal.CDF(0, 1, upper) - democratic;
                _raceProbabilities[0][i] = democratic;
                _raceProbabilities[1][i] = Math.Clamp(unknown, 0, 1);
                _raceProbabilities[2][i] = republican;
            }
            Aggregate();
        }

        private void Aggregate()
        {
            foreach (var distribution in Distributions)
            {
                Array.Clear(distribution);
                distribution[0] = 1;
            }
            for (int race = 0; race < races.Length; race++)
            {
                for (int bloc = 0; bloc < Distributions.Length; bloc++)
                {
                    double probability = _raceProbabilities[bloc][race];
                    double complement = 1 - probability;
                    var distribution = Distributions[bloc];
                    for (int seats = race + 1; seats > 0; seats--)
                        distribution[seats] = complement * distribution[seats] + probability * distribution[seats - 1];
                    distribution[0] *= complement;
                }
            }
        }
    }

    private sealed class DistributionAccumulator(int size)
    {
        private readonly double[][] _compensation = [new double[size], new double[size], new double[size]];
        public double[][] Values { get; } = [new double[size], new double[size], new double[size]];

        public void Add(double[][] distributions, double weight)
        {
            for (int bloc = 0; bloc < Values.Length; bloc++)
            {
                for (int seats = 0; seats < size; seats++)
                {
                    double adjusted = weight * distributions[bloc][seats] - _compensation[bloc][seats];
                    double updated = Values[bloc][seats] + adjusted;
                    _compensation[bloc][seats] = (updated - Values[bloc][seats]) - adjusted;
                    Values[bloc][seats] = updated;
                }
            }
        }

        public double[][] Average(int count) => Values.Select(distribution => distribution.Select(value => value / count).ToArray()).ToArray();
    }
}
