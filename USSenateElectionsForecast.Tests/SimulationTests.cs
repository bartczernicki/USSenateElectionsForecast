using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace USSenateElectionsForecast.Tests;

[TestClass]
public sealed class SimulationTests
{
    private readonly SimulationEngine _engine = new();

    [TestMethod]
    public void CertainRacesProduceExactSeatsInBothModes()
    {
        var snapshot = TestSnapshots.Certain(democraticWins: 17);
        foreach (var mode in new[] { SimulationMode.Independent, SimulationMode.Correlated })
        {
            var result = _engine.Run(snapshot, mode, 25, 0.99, new Random(123));
            Assert.AreEqual(25, result.DemocraticSeats.Counts[51]);
            Assert.AreEqual(25, result.RepublicanSeats.Counts[49]);
            Assert.AreEqual(25, result.UnknownSeats.Counts[0]);
            Assert.AreEqual(25, result.DemocraticControlTrials);
            Assert.AreEqual(0, result.RepublicanControlTrials);
            Assert.AreEqual(0, result.UncertainControlTrials);
            Assert.AreEqual(snapshot.RetrievedAt, result.SnapshotAt);
        }
    }

    [TestMethod]
    public void RepublicanVicePresidentBreaksFiftyFiftyTie()
    {
        var result = _engine.Run(TestSnapshots.Certain(democraticWins: 16), SimulationMode.Independent, 1, 0.12);
        Assert.AreEqual(50, result.DemocraticSeats.Mean);
        Assert.AreEqual(50, result.RepublicanSeats.Mean);
        Assert.AreEqual(1, result.RepublicanControlTrials);
        Assert.AreEqual(0, result.UncertainControlTrials);
    }

    [TestMethod]
    public void UnaffiliatedWinnerCanLeaveControlUncertain()
    {
        var result = _engine.Run(TestSnapshots.Certain(democraticWins: 16, unknownWins: 1),
            SimulationMode.Correlated, 10, 0.12, new Random(123));
        Assert.AreEqual(50, result.DemocraticSeats.Mean);
        Assert.AreEqual(49, result.RepublicanSeats.Mean);
        Assert.AreEqual(1, result.UnknownSeats.Mean);
        Assert.AreEqual(10, result.UncertainControlTrials);
        Assert.AreEqual(0, result.DemocraticControlTrials + result.RepublicanControlTrials);
    }

    [TestMethod]
    public void FiftyRepublicansControlDespiteUnaffiliatedWinner()
    {
        var result = _engine.Run(TestSnapshots.Certain(democraticWins: 15, unknownWins: 1),
            SimulationMode.Independent, 1, 0.12);
        Assert.AreEqual(49, result.DemocraticSeats.Mean);
        Assert.AreEqual(50, result.RepublicanSeats.Mean);
        Assert.AreEqual(1, result.RepublicanControlTrials);
    }

    [TestMethod]
    public void HistogramsAndControlCountsConserveAllTrialsAndSeats()
    {
        const int trials = 20_000;
        foreach (var mode in new[] { SimulationMode.Independent, SimulationMode.Correlated })
        {
            var result = _engine.Run(TestSnapshots.Load(), mode, trials, 0.12, new Random(2026));
            Assert.AreEqual(trials, result.DemocraticSeats.Counts.Sum());
            Assert.AreEqual(trials, result.RepublicanSeats.Counts.Sum());
            Assert.AreEqual(trials, result.UnknownSeats.Counts.Sum());
            Assert.AreEqual(trials, result.DemocraticControlTrials + result.RepublicanControlTrials + result.UncertainControlTrials);
            Assert.AreEqual(100, result.DemocraticSeats.Mean + result.RepublicanSeats.Mean + result.UnknownSeats.Mean, 1e-10);
        }
    }

    [TestMethod]
    public void BothModesPreserveDemocraticAndUnaffiliatedMarginals()
    {
        var source = TestSnapshots.Certain();
        var unknownRace = source.Races.First(race => race.Outcomes.Any(market => market.Caucus == Caucus.Unknown)
            && race.Outcomes.Any(market => market.Caucus == Caucus.Republican));
        var democraticRace = source.Races.First(race => race.State != unknownRace.State
            && race.Outcomes.Any(market => market.Caucus == Caucus.Democratic)
            && race.Outcomes.Any(market => market.Caucus == Caucus.Republican));
        var snapshot = TestSnapshots.WithBlocProbabilities(source, race => race.State == democraticRace.State
            ? (0.3, 0, 0.7) : race.State == unknownRace.State ? (0, 0.2, 0.8) : (0, 0, 1));

        foreach (var mode in new[] { SimulationMode.Independent, SimulationMode.Correlated })
        {
            var result = _engine.Run(snapshot, mode, 100_000, 0.6, new Random(641));
            Assert.AreEqual(34.3, result.DemocraticSeats.Mean, 0.008);
            Assert.AreEqual(0.2, result.UnknownSeats.Mean, 0.008);
            Assert.AreEqual(65.5, result.RepublicanSeats.Mean, 0.012);
        }
    }

    [TestMethod]
    public void ZeroRhoMatchesIndependentSimulationForSameSeed()
    {
        var snapshot = TestSnapshots.Load();
        var independent = _engine.Run(snapshot, SimulationMode.Independent, 10_000, 0.99, new Random(55));
        var correlated = _engine.Run(snapshot, SimulationMode.Correlated, 10_000, 0, new Random(55));
        Assert.AreEqual(0, independent.Rho);
        Assert.AreEqual(0, correlated.Rho);
        CollectionAssert.AreEqual(independent.DemocraticSeats.Counts, correlated.DemocraticSeats.Counts);
        CollectionAssert.AreEqual(independent.RepublicanSeats.Counts, correlated.RepublicanSeats.Counts);
        CollectionAssert.AreEqual(independent.UnknownSeats.Counts, correlated.UnknownSeats.Counts);
    }

    [TestMethod]
    public void CorrelatedCoinRacesMatchAnalyticJointProbabilityAndWidenDistribution()
    {
        const double rho = 0.6;
        const int trials = 150_000;
        var source = TestSnapshots.Certain();
        var states = source.Races.Where(race => race.Outcomes.Any(market => market.Caucus == Caucus.Democratic)
            && race.Outcomes.Any(market => market.Caucus == Caucus.Republican)).Take(2).Select(race => race.State).ToHashSet();
        Assert.HasCount(2, states);
        var snapshot = TestSnapshots.WithBlocProbabilities(source, race => states.Contains(race.State) ? (0.5, 0, 0.5) : (0, 0, 1));
        var correlated = _engine.Run(snapshot, SimulationMode.Correlated, trials, rho, new Random(983));
        var independent = _engine.Run(snapshot, SimulationMode.Independent, trials, 0, new Random(983));
        var expectedBothRepublican = 0.25 + Math.Asin(rho) / (2 * Math.PI);

        Assert.AreEqual(expectedBothRepublican, correlated.DemocraticSeats.Counts[34] / (double)trials, 0.005);
        Assert.AreEqual(35, correlated.DemocraticSeats.Mean, 0.008);
        Assert.AreEqual(35, independent.DemocraticSeats.Mean, 0.008);
        Assert.IsGreaterThan(Variance(independent.DemocraticSeats) + 0.15, Variance(correlated.DemocraticSeats));
    }

    [TestMethod]
    public void SameSeedReproducesCorrelatedResults()
    {
        var snapshot = TestSnapshots.Load();
        var first = _engine.Run(snapshot, SimulationMode.Correlated, 1_000, 0.12, new Random(185));
        var second = _engine.Run(snapshot, SimulationMode.Correlated, 1_000, 0.12, new Random(185));
        CollectionAssert.AreEqual(first.DemocraticSeats.Counts, second.DemocraticSeats.Counts);
        CollectionAssert.AreEqual(first.RepublicanSeats.Counts, second.RepublicanSeats.Counts);
        CollectionAssert.AreEqual(first.UnknownSeats.Counts, second.UnknownSeats.Counts);
    }

    [TestMethod]
    public void InvalidInputsAndIncompleteSnapshotAreRejected()
    {
        var snapshot = TestSnapshots.Load();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _engine.Run(snapshot, SimulationMode.Independent, 0, 0.12));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _engine.Run(snapshot, SimulationMode.Independent, 10_000_001, 0.12));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _engine.Run(snapshot, SimulationMode.Correlated, 1, double.NaN));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _engine.Run(snapshot, SimulationMode.Correlated, 1, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _engine.Run(snapshot, (SimulationMode)99, 1, 0.12));
        Assert.ThrowsExactly<InvalidDataException>(() => _engine.Run(snapshot with { Races = snapshot.Races.Skip(1).ToArray() }, SimulationMode.Independent, 1, 0.12));
    }

    [TestMethod]
    public void CanceledSimulationDoesNotReturnPartialResults()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.ThrowsExactly<OperationCanceledException>(() => _engine.Run(TestSnapshots.Load(),
            SimulationMode.Correlated, 1_000, 0.12, cancellationToken: cancellation.Token));
    }

    [TestMethod]
    public void SeatPercentilesUseEmpiricalInverseCdfIncludingEndpoints()
    {
        var counts = new int[101];
        counts[50] = 1;
        counts[51] = 2;
        counts[53] = 1;
        var distribution = new SeatDistribution(counts);
        Assert.AreEqual(51.25, distribution.Mean);
        Assert.AreEqual(50, distribution.Percentile(0));
        Assert.AreEqual(50, distribution.Percentile(0.25));
        Assert.AreEqual(51, distribution.Percentile(0.5));
        Assert.AreEqual(51, distribution.Percentile(0.75));
        Assert.AreEqual(53, distribution.Percentile(1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => distribution.Percentile(double.NaN));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => distribution.Percentile(50));
    }

    private static double Variance(SeatDistribution distribution) => distribution.Counts
        .Select((count, seats) => count * Math.Pow(seats - distribution.Mean, 2)).Sum() / distribution.Counts.Sum();
}

internal static class TestSnapshots
{
    public static PredictionSnapshot Load() => JsonSerializer.Deserialize<PredictionSnapshot>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "polymarket_predictions_20261024.json")), ForecastJson.Options)
        ?? throw new InvalidDataException("The bundled test snapshot was empty.");

    public static PredictionSnapshot Certain(int democraticWins = 0, int unknownWins = 0)
    {
        var source = Load();
        var unknownStates = source.Races.Where(race => race.Outcomes.Any(market => market.Caucus == Caucus.Unknown))
            .Take(unknownWins).Select(race => race.State).ToHashSet();
        var democraticStates = source.Races.Where(race => !unknownStates.Contains(race.State)
            && race.Outcomes.Any(market => market.Caucus == Caucus.Democratic))
            .Take(democraticWins).Select(race => race.State).ToHashSet();
        if (unknownStates.Count != unknownWins || democraticStates.Count != democraticWins)
            throw new InvalidOperationException("The catalog cannot supply the requested certain-win test fixture.");
        return WithBlocProbabilities(source, race => democraticStates.Contains(race.State)
            ? (1, 0, 0) : unknownStates.Contains(race.State) ? (0, 1, 0) : (0, 0, 1));
    }

    public static PredictionSnapshot WithBlocProbabilities(PredictionSnapshot source,
        Func<RacePrediction, (double Democratic, double Unknown, double Republican)> probabilities)
    {
        return source with
        {
            Races = source.Races.Select(race =>
            {
                var desired = probabilities(race);
                var allocated = new HashSet<Caucus>();
                var outcomes = race.Outcomes.Select(market =>
                {
                    var probability = allocated.Add(market.Caucus) ? market.Caucus switch
                    {
                        Caucus.Democratic => desired.Democratic,
                        Caucus.Unknown => desired.Unknown,
                        Caucus.Republican => desired.Republican,
                        _ => throw new InvalidOperationException("Unexpected caucus in test fixture.")
                    } : 0;
                    return market with { RawYesPrice = probability, Probability = probability };
                }).ToArray();
                if (Math.Abs(outcomes.Sum(market => market.Probability) - 1) > 1e-10)
                    throw new InvalidOperationException($"Race {race.State} cannot support the requested test probabilities.");
                return race with { Outcomes = outcomes };
            }).ToArray()
        };
    }
}
