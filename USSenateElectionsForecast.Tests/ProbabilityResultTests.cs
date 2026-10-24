namespace USSenateElectionsForecast.Tests;

[TestClass]
public sealed class ProbabilityResultTests
{
    [TestMethod]
    public void WeightedDistributionUsesProbabilityMassForMeansPercentilesAndInclusiveTails()
    {
        var probabilities = new double[101];
        probabilities[50] = 0.025;
        probabilities[51] = 0.2;
        probabilities[55] = 0.725;
        probabilities[56] = 0.05;
        var distribution = new ProbabilitySeatDistribution(probabilities);

        Assert.AreEqual(54.125, distribution.Mean, 1e-12);
        Assert.AreEqual(50, distribution.Percentile(0));
        Assert.AreEqual(50, distribution.Percentile(0.025));
        Assert.AreEqual(51, distribution.Percentile(0.05));
        Assert.AreEqual(51, distribution.Percentile(0.225));
        Assert.AreEqual(55, distribution.Percentile(0.95));
        Assert.AreEqual(56, distribution.Percentile(0.951));
        Assert.AreEqual(56, distribution.Percentile(1));
        Assert.AreEqual(0.975, distribution.ProbabilityAtLeast(51), 1e-14);
        Assert.AreEqual(0.775, distribution.ProbabilityAtLeast(55), 1e-14);
        Assert.AreEqual(0.05, distribution.ProbabilityAtLeast(56), 1e-14);
        Assert.AreEqual(0, distribution.ProbabilityAtLeast(57));
        Assert.AreEqual(1, distribution.ProbabilityAtLeast(0));
        Assert.AreEqual(0, distribution.ProbabilityAtLeast(101));
    }

    [TestMethod]
    public void TinyQuantileDoesNotSelectAnEmptyBinOrIgnoreItsThreshold()
    {
        var probabilities = new double[101];
        probabilities[50] = 1e-20;
        probabilities[51] = 1;
        var distribution = new ProbabilitySeatDistribution(probabilities);
        Assert.AreEqual(50, distribution.Percentile(0));
        Assert.AreEqual(51, distribution.Percentile(1e-18));
    }

    [TestMethod]
    public void CountAdapterPreservesExistingMonteCarloMeansAndPercentiles()
    {
        var counts = new int[101];
        counts[49] = 5;
        counts[50] = 20;
        counts[51] = 70;
        counts[52] = 5;
        var original = new SeatDistribution(counts);
        var distribution = ProbabilitySeatDistribution.FromCounts(original);
        Assert.AreEqual(original.Mean, distribution.Mean, 1e-12);
        foreach (double p in new[] { 0, 0.05, 0.25, 0.5, 0.95, 1 })
            Assert.AreEqual(original.Percentile(p), distribution.Percentile(p));

        var snapshot = TestSnapshots.Certain(democraticWins: 16, unknownWins: 1);
        var simulation = new SimulationEngine().Run(snapshot, SimulationMode.Independent, 5, 0.12);
        var result = ProbabilityResult.FromSimulation(simulation);
        Assert.AreEqual(CalculationMethod.IndependentMonteCarlo, result.Method);
        Assert.AreEqual(5, result.SampleCount);
        Assert.AreEqual(simulation.DemocraticControlTrials / 5.0, result.DemocraticControlProbability);
        Assert.AreEqual(simulation.RepublicanControlTrials / 5.0, result.RepublicanControlProbability);
        Assert.AreEqual(simulation.UncertainControlTrials / 5.0, result.UncertainControlProbability);
    }

    [TestMethod]
    public void DistributionCopiesItsInputAndExposesReadOnlyProbabilities()
    {
        var probabilities = new double[101];
        probabilities[51] = 1;
        var distribution = new ProbabilitySeatDistribution(probabilities);
        probabilities[51] = 0;
        Assert.AreEqual(1, distribution.Probabilities[51]);
        Assert.ThrowsExactly<NotSupportedException>(() => ((IList<double>)distribution.Probabilities)[51] = 0);
    }

    [TestMethod]
    public void DistributionNormalizesRoundoffButRejectsInvalidMassAndValues()
    {
        var values = new double[101];
        values[51] = 1 + 1e-12;
        Assert.AreEqual(1, new ProbabilitySeatDistribution(values).Probabilities[51]);
        values[51] = 0.9;
        Assert.ThrowsExactly<ArgumentException>(() => new ProbabilitySeatDistribution(values));
        foreach (double invalid in new[] { double.NaN, double.PositiveInfinity, -0.01, 1.01 })
        {
            values[51] = invalid;
            Assert.ThrowsExactly<ArgumentException>(() => new ProbabilitySeatDistribution(values));
        }
        Assert.ThrowsExactly<ArgumentException>(() => new ProbabilitySeatDistribution(new double[100]));
        values[51] = 1;
        var distribution = new ProbabilitySeatDistribution(values);
        foreach (double p in new[] { -0.01, 1.01, double.NaN, double.PositiveInfinity })
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => distribution.Percentile(p));
    }
}
