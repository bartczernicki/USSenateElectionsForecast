namespace USSenateElectionsForecast.Tests;

[TestClass]
public sealed class ProbabilityCalculationTests
{
    private readonly ProbabilityCalculationEngine engine = new();

    [TestMethod]
    public void IndependentAggregationMatchesEnumeratedJointOutcomesIncludingUnknownWinners()
    {
        var source = TestSnapshots.Load();
        var uncertain = new Dictionary<string, (double Democratic, double Unknown, double Republican)>
        {
            ["Nebraska"] = (0.2, 0.3, 0.5),
            ["Texas"] = (0.65, 0, 0.35),
            ["Alaska"] = (0.4, 0.2, 0.4)
        };
        var certainDemocraticStates = source.Races.Where(race => !uncertain.ContainsKey(race.State))
            .Take(15).Select(race => race.State).ToHashSet();
        var snapshot = TestSnapshots.WithBlocProbabilities(source, race =>
            uncertain.TryGetValue(race.State, out var probabilities) ? probabilities
                : certainDemocraticStates.Contains(race.State) ? (1, 0, 0) : (0, 0, 1));
        var combinations = uncertain.Values.ToArray();
        var democratic = new double[101];
        var republican = new double[101];
        var unknown = new double[101];
        double democraticControl = 0, republicanControl = 0, uncertainControl = 0;

        void Enumerate(int index, int d, int r, int u, double probability)
        {
            if (index == combinations.Length)
            {
                democratic[d] += probability;
                republican[r] += probability;
                unknown[u] += probability;
                if (d >= 51) democraticControl += probability;
                else if (r >= 50) republicanControl += probability;
                else uncertainControl += probability;
                return;
            }
            var race = combinations[index];
            if (race.Democratic > 0) Enumerate(index + 1, d + 1, r, u, probability * race.Democratic);
            if (race.Republican > 0) Enumerate(index + 1, d, r + 1, u, probability * race.Republican);
            if (race.Unknown > 0) Enumerate(index + 1, d, r, u + 1, probability * race.Unknown);
        }

        Enumerate(0, 49, 48, 0, 1);
        var result = engine.CalculateIndependent(snapshot);

        AssertDistribution(democratic, result.DemocraticSeats, 1e-13);
        AssertDistribution(republican, result.RepublicanSeats, 1e-13);
        AssertDistribution(unknown, result.UnknownSeats, 1e-13);
        Assert.AreEqual(democraticControl, result.DemocraticControlProbability, 1e-13);
        Assert.AreEqual(republicanControl, result.RepublicanControlProbability, 1e-13);
        Assert.AreEqual(uncertainControl, result.UncertainControlProbability, 1e-13);
        AssertValidResult(result);
    }

    [TestMethod]
    public void DeterministicWinnersPreserveTieBreakAndUnknownControlRules()
    {
        foreach (var expected in new[]
        {
            (DemocraticWins: 17, UnknownWins: 0, Democratic: 51, Republican: 49, DControl: 1.0, RControl: 0.0, UControl: 0.0),
            (DemocraticWins: 16, UnknownWins: 0, Democratic: 50, Republican: 50, DControl: 0.0, RControl: 1.0, UControl: 0.0),
            (DemocraticWins: 16, UnknownWins: 1, Democratic: 50, Republican: 49, DControl: 0.0, RControl: 0.0, UControl: 1.0)
        })
        {
            var snapshot = TestSnapshots.Certain(expected.DemocraticWins, expected.UnknownWins);
            foreach (var result in new[]
            {
                engine.CalculateIndependent(snapshot),
                engine.RunConditional(snapshot, 4, 0.12, new Random(123)),
                engine.CalculateCorrelated(snapshot, 0.12)
            })
            {
                Assert.AreEqual(1, result.DemocraticSeats.Probabilities[expected.Democratic], 1e-13);
                Assert.AreEqual(1, result.RepublicanSeats.Probabilities[expected.Republican], 1e-13);
                Assert.AreEqual(1, result.UnknownSeats.Probabilities[expected.UnknownWins], 1e-13);
                Assert.AreEqual(expected.DControl, result.DemocraticControlProbability, 1e-13);
                Assert.AreEqual(expected.RControl, result.RepublicanControlProbability, 1e-13);
                Assert.AreEqual(expected.UControl, result.UncertainControlProbability, 1e-13);
                AssertValidResult(result);
            }
        }
    }

    [TestMethod]
    public void ZeroRhoUsesExactIndependentDistributionsAndDoesNotDrawRandomNumbers()
    {
        var snapshot = TestSnapshots.Load();
        var independent = engine.CalculateIndependent(snapshot);
        var conditional = engine.RunConditional(snapshot, 1_000, 0, new ThrowingRandom());
        var correlated = engine.CalculateCorrelated(snapshot, 0);

        Assert.AreEqual(CalculationMethod.DirectIndependent, independent.Method);
        Assert.AreEqual(CalculationMethod.ConditionalMonteCarlo, conditional.Method);
        Assert.AreEqual(CalculationMethod.DirectCorrelated, correlated.Method);
        Assert.AreEqual(0, conditional.SampleCount.GetValueOrDefault());
        Assert.IsNull(independent.SampleCount);
        Assert.IsNull(correlated.SampleCount);
        Assert.IsNull(correlated.Integration);
        foreach (var result in new[] { conditional, correlated })
        {
            AssertDistribution(independent.DemocraticSeats.Probabilities, result.DemocraticSeats, 0);
            AssertDistribution(independent.RepublicanSeats.Probabilities, result.RepublicanSeats, 0);
            AssertDistribution(independent.UnknownSeats.Probabilities, result.UnknownSeats, 0);
            Assert.AreEqual(0, result.Rho);
        }
    }

    [TestMethod]
    public void DirectBundledForecastsMatchVerifiedControlProbabilitiesAndAnalyticMeans()
    {
        var snapshot = TestSnapshots.Load();
        var independent = engine.CalculateIndependent(snapshot);
        var correlated = engine.CalculateCorrelated(snapshot, 0.12);

        Assert.AreEqual(0.7251380212401005, independent.DemocraticControlProbability, 1e-12);
        Assert.AreEqual(0.2109043207808110, independent.RepublicanControlProbability, 1e-12);
        Assert.AreEqual(0.06395765797908845, independent.UncertainControlProbability, 1e-12);
        Assert.AreEqual(0.6706393816288958, correlated.DemocraticControlProbability, 1e-10);
        Assert.AreEqual(0.2888881307918616, correlated.RepublicanControlProbability, 1e-10);
        Assert.AreEqual(0.0404724875792426, correlated.UncertainControlProbability, 1e-10);
        foreach (var result in new[] { independent, correlated })
        {
            Assert.AreEqual(51.43299143730692, result.DemocraticSeats.Mean, 1e-8);
            Assert.AreEqual(48.15424890182513, result.RepublicanSeats.Mean, 1e-8);
            Assert.AreEqual(0.4127596608679432, result.UnknownSeats.Mean, 1e-8);
            Assert.AreEqual(snapshot.RetrievedAt, result.SnapshotAt);
            Assert.IsNull(result.SampleCount);
            AssertValidResult(result);
        }
        Assert.IsNull(independent.Integration);
        AssertConverged(correlated);
    }

    [TestMethod]
    public void CorrelatedTwoCoinRacesMatchTheAnalyticGaussianJointProbability()
    {
        const double rho = 0.6;
        var source = TestSnapshots.Load();
        var states = source.Races.Take(2).Select(race => race.State).ToHashSet();
        var snapshot = TestSnapshots.WithBlocProbabilities(source, race => states.Contains(race.State)
            ? (0.5, 0, 0.5) : (0, 0, 1));

        var result = engine.CalculateCorrelated(snapshot, rho);

        double bothSame = 0.25 + Math.Asin(rho) / (2 * Math.PI);
        Assert.AreEqual(bothSame, result.DemocraticSeats.Probabilities[34], 1e-10);
        Assert.AreEqual(1 - 2 * bothSame, result.DemocraticSeats.Probabilities[35], 1e-10);
        Assert.AreEqual(bothSame, result.DemocraticSeats.Probabilities[36], 1e-10);
        Assert.AreEqual(35, result.DemocraticSeats.Mean, 1e-8);
        Assert.AreEqual(1, result.UnknownSeats.Probabilities[0], 1e-13);
        AssertConverged(result);
        AssertValidResult(result);
    }

    [TestMethod]
    public void DenseBalancedRacesAtPointNineNineRequireFullDistributionConvergence()
    {
        var snapshot = TestSnapshots.WithBlocProbabilities(TestSnapshots.Load(), _ => (0.5, 0, 0.5));

        var result = engine.CalculateCorrelated(snapshot, 0.99);

        AssertConverged(result);
        Assert.AreEqual(8192, result.Integration!.Order);
        Assert.AreEqual(51.5, result.DemocraticSeats.Mean, 1e-8);
        Assert.AreEqual(48.5, result.RepublicanSeats.Mean, 1e-8);
        Assert.AreEqual(0, result.UnknownSeats.Mean, 1e-13);
        for (int wins = 0; wins <= 35; wins++)
            Assert.AreEqual(result.DemocraticSeats.Probabilities[34 + wins],
                result.DemocraticSeats.Probabilities[34 + 35 - wins], 1e-10);
        AssertValidResult(result);
    }

    [TestMethod]
    public void OneConditionalSampleContributesAFullDistributionInsteadOfOneExpectedSeatValue()
    {
        var source = TestSnapshots.Load();
        var states = source.Races.Take(2).Select(race => race.State).ToHashSet();
        var snapshot = TestSnapshots.WithBlocProbabilities(source, race => states.Contains(race.State)
            ? (0.5, 0, 0.5) : (0, 0, 1));

        var result = engine.RunConditional(snapshot, 1, 0.12, new Random(123));

        Assert.AreEqual(CalculationMethod.ConditionalMonteCarlo, result.Method);
        Assert.AreEqual(1, result.SampleCount.GetValueOrDefault());
        Assert.IsGreaterThan(0.0, result.DemocraticSeats.Probabilities[34]);
        Assert.IsGreaterThan(0.0, result.DemocraticSeats.Probabilities[35]);
        Assert.IsGreaterThan(0.0, result.DemocraticSeats.Probabilities[36]);
        Assert.IsNull(result.Integration);
        AssertValidResult(result);
    }

    [TestMethod]
    public void SeededConditionalEstimatesAgreeStatisticallyWithDirectCalculation()
    {
        var snapshot = TestSnapshots.Load();
        var direct = engine.CalculateCorrelated(snapshot, 0.12);
        var conditional = engine.RunConditional(snapshot, 20_000, 0.12, new Random(8675309));

        Assert.AreEqual(20_000, conditional.SampleCount.GetValueOrDefault());
        Assert.AreEqual(direct.DemocraticControlProbability, conditional.DemocraticControlProbability, 0.02);
        Assert.AreEqual(direct.RepublicanControlProbability, conditional.RepublicanControlProbability, 0.02);
        Assert.AreEqual(direct.UncertainControlProbability, conditional.UncertainControlProbability, 0.02);
        Assert.AreEqual(direct.DemocraticSeats.Mean, conditional.DemocraticSeats.Mean, 0.08);
        Assert.AreEqual(direct.RepublicanSeats.Mean, conditional.RepublicanSeats.Mean, 0.08);
        Assert.AreEqual(direct.UnknownSeats.Mean, conditional.UnknownSeats.Mean, 0.03);
        AssertDistribution(direct.DemocraticSeats.Probabilities, conditional.DemocraticSeats, 0.012);
        AssertDistribution(direct.RepublicanSeats.Probabilities, conditional.RepublicanSeats, 0.012);
        AssertDistribution(direct.UnknownSeats.Probabilities, conditional.UnknownSeats, 0.012);
        AssertValidResult(conditional);
    }

    [TestMethod]
    public void ConditionalSamplingIsReproducibleForTheSameSeed()
    {
        var snapshot = TestSnapshots.Load();
        var first = engine.RunConditional(snapshot, 100, 0.6, new Random(815));
        var second = engine.RunConditional(snapshot, 100, 0.6, new Random(815));
        AssertDistribution(first.DemocraticSeats.Probabilities, second.DemocraticSeats, 0);
        AssertDistribution(first.RepublicanSeats.Probabilities, second.RepublicanSeats, 0);
        AssertDistribution(first.UnknownSeats.Probabilities, second.UnknownSeats, 0);
    }

    [TestMethod]
    public void IntegrationLimitProducesAnExplicitNonconvergenceError()
    {
        var limited = new ProbabilityCalculationEngine(maximumIntegrationOrder: 32);

        var error = Assert.ThrowsExactly<NumericalConvergenceException>(() =>
            limited.CalculateCorrelated(TestSnapshots.Load(), 0.12));

        Assert.IsFalse(string.IsNullOrWhiteSpace(error.Message));
    }

    [TestMethod]
    public void InvalidInputsAreRejectedBeforeCalculation()
    {
        var snapshot = TestSnapshots.Load();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => engine.RunConditional(snapshot, 0, 0.12));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => engine.RunConditional(snapshot, 10_000_001, 0.12));
        foreach (double rho in new[] { -0.01, 1, double.NaN, double.PositiveInfinity })
        {
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => engine.RunConditional(snapshot, 1, rho));
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => engine.CalculateCorrelated(snapshot, rho));
        }
        foreach (int maximumOrder in new[] { 0, 31, 48, 16_384 })
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ProbabilityCalculationEngine(maximumOrder));
        foreach (double tolerance in new[] { 0, -1, double.NaN, double.PositiveInfinity })
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new ProbabilityCalculationEngine(8192, tolerance));
        var incomplete = snapshot with { Races = snapshot.Races.Skip(1).ToArray() };
        Assert.ThrowsExactly<InvalidDataException>(() => engine.CalculateIndependent(incomplete));
        Assert.ThrowsExactly<InvalidDataException>(() => engine.CalculateCorrelated(incomplete, 0.12));
        Assert.ThrowsExactly<InvalidDataException>(() => engine.RunConditional(incomplete, 1, 0.12));
    }

    [TestMethod]
    public void AlreadyCanceledTokenPreventsEveryCalculationMode()
    {
        var snapshot = TestSnapshots.Load();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => engine.CalculateIndependent(snapshot, cancellation.Token));
        Assert.ThrowsExactly<OperationCanceledException>(() => engine.CalculateCorrelated(snapshot, 0.12, cancellation.Token));
        Assert.ThrowsExactly<OperationCanceledException>(() => engine.RunConditional(snapshot, 1_000, 0.12,
            cancellationToken: cancellation.Token));
    }

    [TestMethod]
    public void CancellationDuringConditionalSamplingDoesNotReturnPartialResults()
    {
        using var cancellation = new CancellationTokenSource();
        var random = new CancelingRandom(cancellation);

        Assert.ThrowsExactly<OperationCanceledException>(() => engine.RunConditional(TestSnapshots.Load(), 1_000,
            0.12, random, cancellation.Token));

        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.IsGreaterThan(0, random.Draws);
    }

    private static void AssertDistribution(IReadOnlyList<double> expected, ProbabilitySeatDistribution actual, double tolerance)
    {
        Assert.HasCount(101, actual.Probabilities);
        for (int seats = 0; seats < 101; seats++)
            Assert.AreEqual(expected[seats], actual.Probabilities[seats], tolerance, $"Probability for {seats} seats.");
    }

    private static void AssertConverged(ProbabilityResult result)
    {
        Assert.IsNotNull(result.Integration);
        Assert.IsLessThanOrEqualTo(8192, result.Integration.Order);
        Assert.AreEqual(1e-10, result.Integration.Tolerance);
        Assert.IsLessThanOrEqualTo(1e-10, result.Integration.DistributionChange);
        Assert.IsLessThanOrEqualTo(1e-10, result.Integration.MassError);
        Assert.IsLessThanOrEqualTo(1e-8, result.Integration.MeanError);
    }

    private static void AssertValidResult(ProbabilityResult result)
    {
        foreach (var distribution in new[] { result.DemocraticSeats, result.RepublicanSeats, result.UnknownSeats })
        {
            Assert.AreEqual(1, distribution.Probabilities.Sum(), 1e-12);
            foreach (double probability in distribution.Probabilities)
                Assert.IsTrue(double.IsFinite(probability) && probability is >= 0 and <= 1);
        }
        Assert.AreEqual(100, result.DemocraticSeats.Mean + result.RepublicanSeats.Mean + result.UnknownSeats.Mean, 1e-8);
        Assert.AreEqual(1, result.DemocraticControlProbability + result.RepublicanControlProbability
            + result.UncertainControlProbability, 1e-12);
    }

    private sealed class ThrowingRandom : Random
    {
        public override double NextDouble() => throw new InvalidOperationException("The exact rho-zero calculation must not draw random numbers.");
    }

    private sealed class CancelingRandom(CancellationTokenSource cancellation) : Random(2026)
    {
        public int Draws { get; private set; }
        public override double NextDouble()
        {
            Draws++;
            cancellation.Cancel();
            return base.NextDouble();
        }
    }
}
