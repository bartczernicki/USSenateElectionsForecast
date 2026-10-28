using System.Text.Json;
using USSenateElectionsForecast.Markets.Kalshi;
using USSenateElectionsForecast.Markets.Polymarket;

namespace USSenateElectionsForecast.Tests;

[TestClass]
public sealed class SourceStoreTests
{
    private string testDirectory = null!;
    private ForecastStore store = null!;

    [TestInitialize]
    public void Initialize()
    {
        testDirectory = Path.Combine(Path.GetTempPath(), "SenateForecastSourceTests", Guid.NewGuid().ToString("N"));
        store = new ForecastStore(Path.Combine(testDirectory, "saved"), Path.Combine(testDirectory, "bundled"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, recursive: true);
    }

    [TestMethod]
    public void ExistingSettingsRetainValuesAndDefaultToPolymarketWithoutRewrite()
    {
        Directory.CreateDirectory(store.DataDirectory);
        const string original = "{\"rho\":0.42,\"trialCount\":123456,\"selectedPredictionFile\":\"polymarket_predictions_20261026.json\"}";
        File.WriteAllText(store.SettingsPath, original);

        var settings = store.LoadSettings();

        Assert.AreEqual(PredictionSource.Polymarket, settings.Source);
        Assert.AreEqual(0.42, settings.Rho);
        Assert.AreEqual(123456, settings.TrialCount);
        Assert.AreEqual("polymarket_predictions_20261026.json", settings.SelectedPredictionFile);
        Assert.AreEqual(original, File.ReadAllText(store.SettingsPath));
    }

    [TestMethod]
    public void KalshiSourceAndExplicitFileRoundTripWithoutChangingCalculationSettings()
    {
        var expected = new AppSettings(0.37, 234567, "kalshi_predictions_20261028.json", PredictionSource.Kalshi);
        store.SaveSettings(expected);

        var restarted = new ForecastStore(store.DataDirectory, store.BundledDataDirectory);

        Assert.AreEqual(expected, restarted.LoadSettings());
        Assert.HasCount(0, Directory.GetFiles(store.DataDirectory, "*.tmp"));
    }

    [TestMethod]
    public void LatestAndFileDiscoveryStayWithinSelectedSourceAcrossBothDirectories()
    {
        var polymarket = TestSnapshots.Load() with { RetrievedAt = Retrieved(29) };
        var kalshi = KalshiSnapshot() with { RetrievedAt = Retrieved(28) };
        WriteSnapshot(store.BundledDataDirectory, "polymarket_predictions_20261029.json", polymarket);
        WriteSnapshot(store.BundledDataDirectory, "kalshi_predictions_20261027.json", kalshi with { RetrievedAt = Retrieved(27) });
        string latestKalshi = store.SaveSnapshot(kalshi);

        Assert.AreEqual("polymarket_predictions_20261029.json", store.LoadPrediction().FileName);
        Assert.AreEqual(latestKalshi, store.LoadPrediction(source: PredictionSource.Kalshi).FileName);
        CollectionAssert.AreEqual(new[] { "kalshi_predictions_20261028.json", "kalshi_predictions_20261027.json" },
            store.ListSnapshots(source: PredictionSource.Kalshi).Select(file => file.FileName).ToArray());
        CollectionAssert.AreEqual(new[] { "polymarket_predictions_20261029.json" },
            store.ListSnapshots().Select(file => file.FileName).ToArray());
        Assert.AreEqual(kalshi.RetrievedAt, store.LoadSnapshot(source: PredictionSource.Kalshi).RetrievedAt);
    }

    [TestMethod]
    public void SameDateRefreshReplacesOnlyItsOwnSourceFile()
    {
        string polymarket = store.SaveSnapshot(TestSnapshots.Load() with { RetrievedAt = Retrieved(28) });
        byte[] originalPolymarket = File.ReadAllBytes(store.GetSnapshotPath(polymarket));
        string kalshi = store.SaveSnapshot(KalshiSnapshot());
        var replacement = KalshiSnapshot() with { RetrievedAt = Retrieved(28).AddMinutes(10) };

        Assert.AreEqual("kalshi_predictions_20261028.json", kalshi);
        Assert.AreEqual(kalshi, store.SaveSnapshot(replacement));
        Assert.AreEqual(replacement.RetrievedAt, store.LoadSnapshot(source: PredictionSource.Kalshi).RetrievedAt);
        CollectionAssert.AreEqual(originalPolymarket, File.ReadAllBytes(store.GetSnapshotPath(polymarket)));
        Assert.HasCount(2, Directory.GetFiles(store.DataDirectory, "*_predictions_*.json"));
        Assert.HasCount(0, Directory.GetFiles(store.DataDirectory, "*.tmp"));
    }

    [TestMethod]
    public void KalshiSaveUsesActualUtcDate()
    {
        var snapshot = KalshiSnapshot() with
        {
            RetrievedAt = new DateTimeOffset(2026, 10, 28, 22, 30, 0, TimeSpan.FromHours(-4))
        };

        Assert.AreEqual("kalshi_predictions_20261029.json", store.SaveSnapshot(snapshot));
        Assert.AreEqual(snapshot.RetrievedAt, store.LoadSnapshot(source: PredictionSource.Kalshi).RetrievedAt);
    }

    [TestMethod]
    public void MissingOrCorruptKalshiDataNeverFallsBackToPolymarket()
    {
        store.SaveSnapshot(TestSnapshots.Load() with { RetrievedAt = Retrieved(28) });
        Assert.ThrowsExactly<InvalidDataException>(() => store.LoadPrediction(source: PredictionSource.Kalshi));
        File.WriteAllText(store.GetSnapshotPath("kalshi_predictions_20261028.json"), "{broken");
        var warnings = new List<string>();

        Assert.ThrowsExactly<InvalidDataException>(() => store.LoadPrediction(warn: warnings.Add, source: PredictionSource.Kalshi));
        Assert.IsNotEmpty(warnings);
        Assert.HasCount(0, store.ListSnapshots(source: PredictionSource.Kalshi));
        Assert.HasCount(35, store.LoadSnapshot().Races);
    }

    [TestMethod]
    public void MissingExplicitKalshiFileFallsBackOnlyWithinKalshiWithoutChangingSettings()
    {
        store.SaveSnapshot(TestSnapshots.Load() with { RetrievedAt = Retrieved(29) });
        string latest = store.SaveSnapshot(KalshiSnapshot());
        const string missing = "kalshi_predictions_20261027.json";
        var settings = new AppSettings(0.27, 54321, missing, PredictionSource.Kalshi);
        store.SaveSettings(settings);
        string original = File.ReadAllText(store.SettingsPath);
        var warnings = new List<string>();

        Assert.AreEqual(latest, store.LoadPrediction(missing, warnings.Add, source: PredictionSource.Kalshi).FileName);
        Assert.IsNotEmpty(warnings);
        Assert.AreEqual(original, File.ReadAllText(store.SettingsPath));
        Assert.ThrowsExactly<InvalidDataException>(() => store.LoadPrediction(missing,
            fallbackToLatest: false, source: PredictionSource.Kalshi));
    }

    [TestMethod]
    public void ProviderInFilenameMustMatchItsSnapshotContents()
    {
        WriteSnapshot(store.BundledDataDirectory, "kalshi_predictions_20261028.json", TestSnapshots.Load());
        WriteSnapshot(store.BundledDataDirectory, "polymarket_predictions_20261028.json", KalshiSnapshot());
        var warnings = new List<string>();

        Assert.HasCount(0, store.ListSnapshots(warnings.Add));
        Assert.HasCount(0, store.ListSnapshots(warnings.Add, PredictionSource.Kalshi));
        Assert.IsNotEmpty(warnings);
        Assert.ThrowsExactly<InvalidDataException>(() => store.LoadSnapshot());
        Assert.ThrowsExactly<InvalidDataException>(() => store.LoadSnapshot(source: PredictionSource.Kalshi));
    }

    [TestMethod]
    public void ValidationRejectsMixedProvidersAndExplicitProviderMismatchBeforeSaving()
    {
        var kalshi = KalshiSnapshot();
        var polymarket = TestSnapshots.Load();
        var mixed = kalshi with { Races = kalshi.Races.Skip(1).Append(polymarket.Races[0]).ToArray() };

        SnapshotValidator.Validate(kalshi);
        SnapshotValidator.Validate(kalshi, PredictionSource.Kalshi);
        Assert.ThrowsExactly<InvalidDataException>(() => SnapshotValidator.Validate(mixed));
        Assert.ThrowsExactly<InvalidDataException>(() => SnapshotValidator.Validate(kalshi, PredictionSource.Polymarket));
        Assert.ThrowsExactly<InvalidDataException>(() => SnapshotValidator.Validate(polymarket, PredictionSource.Kalshi));
        Assert.ThrowsExactly<InvalidDataException>(() => store.SaveSnapshot(mixed));
        Assert.ThrowsExactly<InvalidDataException>(() => store.SaveSnapshot(kalshi, PredictionSource.Polymarket));
        Assert.IsFalse(Directory.Exists(store.DataDirectory));
    }

    [TestMethod]
    public void HistoricalPolymarketFilenameCanDifferFromUtcRetrievalDate()
    {
        var historical = TestSnapshots.Load() with
        {
            RetrievedAt = new DateTimeOffset(2026, 10, 27, 0, 36, 0, TimeSpan.Zero)
        };
        WriteSnapshot(store.BundledDataDirectory, "polymarket_predictions_20261026.json", historical);

        Assert.AreEqual(historical.RetrievedAt, store.LoadPrediction("polymarket_predictions_20261026.json").Snapshot.RetrievedAt);
    }

    [TestMethod]
    public void InvalidSourceOrCrossSourceFileSelectionDoesNotReplaceSavedSettings()
    {
        var original = new AppSettings(0.18, 123456);
        store.SaveSettings(original);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => store.SaveSettings(original with { Source = (PredictionSource)123 }));
        Assert.ThrowsExactly<ArgumentException>(() => store.SaveSettings(original with { SelectedPredictionFile = "kalshi_predictions_20261028.json" }));
        Assert.ThrowsExactly<ArgumentException>(() => store.SaveSettings(original with
        {
            Source = PredictionSource.Kalshi, SelectedPredictionFile = "polymarket_predictions_20261028.json"
        }));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => store.ListSnapshots(source: (PredictionSource)123));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => store.LoadPrediction(source: (PredictionSource)123));
        Assert.AreEqual(original, store.LoadSettings());
    }

    [TestMethod]
    public void EquivalentProviderProbabilitiesProduceIdenticalResultsInEveryCalculationMethod()
    {
        var kalshi = KalshiSnapshot();
        var polymarket = TestSnapshots.WithBlocProbabilities(TestSnapshots.Load(), race => (0.6, 0, 0.4));
        var simulation = new SimulationEngine();
        foreach (var mode in new[] { SimulationMode.Independent, SimulationMode.Correlated })
        {
            var expected = simulation.Run(polymarket, mode, 1_000, 0.12, new Random(2026));
            var actual = simulation.Run(kalshi, mode, 1_000, 0.12, new Random(2026));
            CollectionAssert.AreEqual(expected.DemocraticSeats.Counts, actual.DemocraticSeats.Counts);
            CollectionAssert.AreEqual(expected.RepublicanSeats.Counts, actual.RepublicanSeats.Counts);
            CollectionAssert.AreEqual(expected.UnknownSeats.Counts, actual.UnknownSeats.Counts);
        }

        var probability = new ProbabilityCalculationEngine();
        AssertSameDistributions(probability.CalculateIndependent(polymarket), probability.CalculateIndependent(kalshi));
        AssertSameDistributions(probability.CalculateCorrelated(polymarket, 0.12), probability.CalculateCorrelated(kalshi, 0.12));
        AssertSameDistributions(probability.RunConditional(polymarket, 1_000, 0.12, new Random(2026)),
            probability.RunConditional(kalshi, 1_000, 0.12, new Random(2026)));
    }

    private static PredictionSnapshot KalshiSnapshot() => new(PolymarketCatalog.SchemaVersion, PolymarketCatalog.ElectionYear, Retrieved(28),
        KalshiRaceCatalog.All.Select(definition => new RacePrediction(definition.State, definition.EventSlug,
            definition.IsSpecial, null, definition.Markets.Select(market =>
            {
                double price = market.Caucus switch { Caucus.Democratic => 0.6, Caucus.Republican => 0.4, _ => 0 };
                return new MarketPrediction(market.MarketId, market.MarketSlug, market.MarketId,
                    market.Caucus, price, price, null);
            }).ToArray())).ToArray());

    private static DateTimeOffset Retrieved(int day) => new(2026, 10, day, 12, 0, 0, TimeSpan.Zero);

    private static void WriteSnapshot(string directory, string fileName, PredictionSnapshot snapshot)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, fileName), JsonSerializer.Serialize(snapshot, ForecastJson.Options));
    }

    private static void AssertSameDistributions(ProbabilityResult expected, ProbabilityResult actual)
    {
        foreach (var (before, after) in new[]
        {
            (expected.DemocraticSeats, actual.DemocraticSeats),
            (expected.RepublicanSeats, actual.RepublicanSeats),
            (expected.UnknownSeats, actual.UnknownSeats)
        })
        {
            for (int seats = 0; seats <= 100; seats++)
                Assert.AreEqual(before.Probabilities[seats], after.Probabilities[seats], 1e-13, $"Probability for {seats} seats.");
        }
    }
}
