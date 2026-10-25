using System.Text;
using System.Text.Json;

namespace USSenateElectionsForecast.Tests;

[TestClass]
public sealed class StoreTests
{
    private string testDirectory = null!;
    private string directory = null!;
    private ForecastStore store = null!;

    [TestInitialize]
    public void Initialize()
    {
        testDirectory = Path.Combine(Path.GetTempPath(), "SenateForecastTests", Guid.NewGuid().ToString("N"));
        directory = Path.Combine(testDirectory, "saved");
        string bundled = Path.Combine(testDirectory, "bundled");
        Directory.CreateDirectory(bundled);
        const string fixtureFile = "polymarket_predictions_20261024.json";
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Data", fixtureFile), Path.Combine(bundled, fixtureFile));
        store = new ForecastStore(directory, bundled);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(testDirectory)) Directory.Delete(testDirectory, recursive: true);
    }

    [TestMethod]
    public void FreshInstallationUsesBundleWithoutCreatingSettingsOrMakingRequests()
    {
        Assert.AreEqual(new AppSettings(), store.LoadSettings());
        var snapshot = store.LoadSnapshot();
        Assert.HasCount(35, snapshot.Races);
        SnapshotValidator.Validate(snapshot);
        Assert.IsFalse(Directory.Exists(directory));
    }

    [TestMethod]
    public void SettingsRoundTripIncludingBoundaries()
    {
        foreach (var expected in new[] { new AppSettings(0, 1_000), new AppSettings(0.99, 10_000_000, "polymarket_predictions_20261024.json") })
        {
            store.SaveSettings(expected);
            Assert.AreEqual(expected, store.LoadSettings());
        }
        Assert.HasCount(0, Directory.GetFiles(directory, "*.tmp"));
    }

    [TestMethod]
    public void ExistingSettingsDefaultToLatestWithoutBeingRewritten()
    {
        Directory.CreateDirectory(directory);
        const string original = "{\"rho\":0.42,\"trialCount\":1000}";
        File.WriteAllText(store.SettingsPath, original);
        Assert.AreEqual(new AppSettings(0.42, 1_000), store.LoadSettings());
        Assert.AreEqual(original, File.ReadAllText(store.SettingsPath));
    }

    [TestMethod]
    public void InvalidSettingsDoNotOverwriteSavedConfiguration()
    {
        var expected = new AppSettings(0.25, 50_000);
        store.SaveSettings(expected);
        foreach (var settings in new[] { new AppSettings(double.NaN), new AppSettings(double.PositiveInfinity),
                     new AppSettings(-0.01), new AppSettings(1), new AppSettings(0.12, 999), new AppSettings(0.12, 10_000_001) })
            Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => store.SaveSettings(settings));
        Assert.AreEqual(expected, store.LoadSettings());
    }

    [TestMethod]
    public void SelectedPredictionMustBeAFilenameWithAValidDate()
    {
        store.SaveSettings(new AppSettings());
        foreach (string fileName in new[] { "", "../polymarket_predictions_20261024.json", "Data/polymarket_predictions_20261024.json",
                     "polymarket_predictions_20260230.json", "polymarket_predictions_20261024.json.tmp", "predictions.json" })
        {
            Assert.ThrowsExactly<ArgumentException>(() => store.SaveSettings(new AppSettings(SelectedPredictionFile: fileName)));
            Assert.ThrowsExactly<ArgumentException>(() => store.GetSnapshotPath(fileName));
        }
        Assert.AreEqual(new AppSettings(), store.LoadSettings());
    }

    [TestMethod]
    public void CorruptSettingsUseDefaultsWithWarning()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(store.SettingsPath, "{broken");
        var warnings = new List<string>();
        Assert.AreEqual(new AppSettings(), store.LoadSettings(warnings.Add));
        Assert.HasCount(1, warnings);
        File.WriteAllText(store.SettingsPath, "{\"rho\":1.5,\"trialCount\":1000}");
        Assert.AreEqual(new AppSettings(), store.LoadSettings(warnings.Add));
        Assert.HasCount(2, warnings);
    }

    [TestMethod]
    public void ValidSavedSnapshotTakesPrecedenceAndSurvivesInvalidReplacement()
    {
        var expected = store.LoadSnapshot() with { RetrievedAt = Retrieved(26) };
        store.SaveSnapshot(expected);
        var actual = store.LoadSnapshot();
        Assert.AreEqual(expected.RetrievedAt, actual.RetrievedAt);
        Assert.AreEqual(JsonSerializer.Serialize(expected, ForecastJson.Options), JsonSerializer.Serialize(actual, ForecastJson.Options));
        Assert.ThrowsExactly<InvalidDataException>(() => store.SaveSnapshot(expected with { Races = expected.Races.Skip(1).ToArray() }));
        Assert.AreEqual(expected.RetrievedAt, store.LoadSnapshot().RetrievedAt);
    }

    [TestMethod]
    public void InvalidLegacyCacheFallsBackToBundleWithWarning()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(store.LegacySnapshotPath, "null");
        var warnings = new List<string>();
        Assert.HasCount(35, store.LoadSnapshot(warnings.Add).Races);
        Assert.HasCount(1, warnings);
        Assert.AreEqual("null", File.ReadAllText(store.LegacySnapshotPath));
    }

    [TestMethod]
    public void MissingBundleAndCacheReturnActionableError()
    {
        var missing = new ForecastStore(directory, Path.Combine(directory, "missing"));
        var error = Assert.ThrowsExactly<InvalidDataException>(() => missing.LoadSnapshot());
        StringAssert.Contains(error.Message, "Update Predictions from Polymarket");
    }

    [TestMethod]
    public void FailedDiskReplacementLeavesNoTemporaryFilesAndBundleStillWorks()
    {
        var snapshot = store.LoadSnapshot();
        Directory.CreateDirectory(store.GetSnapshotPath("polymarket_predictions_20261024.json"));
        var error = Assert.Throws<Exception>(() => store.SaveSnapshot(snapshot));
        Assert.IsTrue(error is IOException or UnauthorizedAccessException);
        Assert.HasCount(0, Directory.GetFiles(directory, "*.tmp"));
        Assert.HasCount(35, store.LoadSnapshot().Races);
    }

    [TestMethod]
    public void DiscoveryCombinesDirectoriesAndUsesNewestDateRatherThanModificationTime()
    {
        string bundled = Path.Combine(directory, "bundled");
        string saved = Path.Combine(directory, "saved");
        WriteSnapshot(bundled, "polymarket_predictions_20261024.json", TestSnapshots.Load());
        string newer = WriteSnapshot(saved, "polymarket_predictions_20261026.json", TestSnapshots.Certain() with { RetrievedAt = Retrieved(26) });
        File.SetLastWriteTimeUtc(newer, new DateTime(2020, 1, 1));
        var combined = new ForecastStore(saved, bundled);

        CollectionAssert.AreEqual(new[] { "polymarket_predictions_20261026.json", "polymarket_predictions_20261024.json" },
            combined.ListSnapshots().Select(x => x.FileName).ToArray());
        Assert.AreEqual(new DateOnly(2026, 10, 26), combined.ListSnapshots()[0].Date);
        Assert.AreEqual("polymarket_predictions_20261026.json", combined.LoadPrediction().FileName);
        Assert.AreEqual(Retrieved(26), combined.LoadSnapshot().RetrievedAt);
    }

    [TestMethod]
    public void ExplicitSelectionSurvivesRestartAndLaterDatesUntilLatestIsSelected()
    {
        string older = store.SaveSnapshot(TestSnapshots.Certain() with { RetrievedAt = Retrieved(23) });
        store.SaveSettings(new AppSettings(SelectedPredictionFile: older));
        string newest = store.SaveSnapshot(TestSnapshots.Load() with { RetrievedAt = Retrieved(26) });

        var restarted = new ForecastStore(directory, store.BundledDataDirectory);
        Assert.AreEqual(older, restarted.LoadPrediction(restarted.LoadSettings().SelectedPredictionFile).FileName);
        Assert.AreEqual(Retrieved(23), restarted.LoadPrediction(older).Snapshot.RetrievedAt);
        restarted.SaveSettings(restarted.LoadSettings() with { SelectedPredictionFile = null });
        Assert.AreEqual(newest, restarted.LoadPrediction(restarted.LoadSettings().SelectedPredictionFile).FileName);
    }

    [TestMethod]
    public void ValidDataDirectoryCopyWinsForDuplicateDateAndInvalidCopyFallsBackToBundle()
    {
        var cached = TestSnapshots.Certain() with { RetrievedAt = Retrieved(24) };
        string fileName = store.SaveSnapshot(cached);
        Assert.HasCount(1, store.ListSnapshots());
        Assert.AreEqual(cached.RetrievedAt, store.LoadPrediction(fileName).Snapshot.RetrievedAt);

        File.WriteAllText(store.GetSnapshotPath(fileName), "{broken");
        var warnings = new List<string>();
        var fallback = store.LoadPrediction(fileName, warnings.Add);
        Assert.AreEqual(TestSnapshots.Load().RetrievedAt, fallback.Snapshot.RetrievedAt);
        Assert.HasCount(1, warnings);
        Assert.HasCount(1, store.ListSnapshots());
    }

    [TestMethod]
    public void CorruptAndInvalidDateFilesAreSkippedWithWarnings()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(store.GetSnapshotPath("polymarket_predictions_20261027.json"), "null");
        WriteSnapshot(directory, "polymarket_predictions_20260230.json", TestSnapshots.Load());
        File.WriteAllText(store.GetSnapshotPath("polymarket_predictions_20261026.json"),
            JsonSerializer.Serialize(TestSnapshots.Load() with { Races = [] }, ForecastJson.Options));
        var warnings = new List<string>();
        Assert.AreEqual("polymarket_predictions_20261024.json", store.LoadPrediction(warn: warnings.Add).FileName);
        Assert.HasCount(3, warnings);
        Assert.HasCount(1, store.ListSnapshots());
    }

    [TestMethod]
    public void MissingOrCorruptRememberedSelectionFallsBackWithoutChangingSettings()
    {
        foreach (bool corrupt in new[] { false, true })
        {
            const string selected = "polymarket_predictions_20261026.json";
            store.SaveSettings(new AppSettings(SelectedPredictionFile: selected));
            string original = File.ReadAllText(store.SettingsPath);
            if (corrupt) File.WriteAllText(store.GetSnapshotPath(selected), "null");
            var warnings = new List<string>();
            Assert.AreEqual("polymarket_predictions_20261024.json", store.LoadPrediction(selected, warnings.Add).FileName);
            Assert.IsTrue(warnings.Any(x => x.Contains("Using Latest available.", StringComparison.Ordinal)));
            Assert.AreEqual(original, File.ReadAllText(store.SettingsPath));
            Assert.ThrowsExactly<InvalidDataException>(() => store.LoadPrediction(selected, fallbackToLatest: false));
        }
    }

    [TestMethod]
    public void SavingUsesUtcDateAndReplacesOnlyThatDate()
    {
        var original = TestSnapshots.Load();
        string older = store.SaveSnapshot(original);
        byte[] originalBytes = File.ReadAllBytes(store.GetSnapshotPath(older));
        var first = original with { RetrievedAt = new DateTimeOffset(2026, 10, 26, 22, 30, 0, TimeSpan.FromHours(-4)) };
        string newest = store.SaveSnapshot(first);
        Assert.AreEqual("polymarket_predictions_20261027.json", newest);
        var replacement = TestSnapshots.Certain() with { RetrievedAt = Retrieved(27) };
        Assert.AreEqual(newest, store.SaveSnapshot(replacement));
        Assert.AreEqual(replacement.RetrievedAt, store.LoadPrediction(newest).Snapshot.RetrievedAt);
        CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(store.GetSnapshotPath(older)));
        Assert.HasCount(2, Directory.GetFiles(directory, "polymarket_predictions_*.json"));
        Assert.HasCount(0, Directory.GetFiles(directory, "*.tmp"));
    }

    [TestMethod]
    public void LegacyMigrationPreservesBomWhitespaceAndOriginalBytes()
    {
        Directory.CreateDirectory(directory);
        var expected = TestSnapshots.Load() with { RetrievedAt = Retrieved(26) };
        string content = "\r\n  " + JsonSerializer.Serialize(expected, ForecastJson.Options) + "\r\n ";
        byte[] original = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(content)).ToArray();
        File.WriteAllBytes(store.LegacySnapshotPath, original);

        var selected = store.LoadPrediction();
        Assert.AreEqual("polymarket_predictions_20261026.json", selected.FileName);
        Assert.AreEqual(expected.RetrievedAt, selected.Snapshot.RetrievedAt);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(store.LegacySnapshotPath));
        CollectionAssert.AreEqual(original, File.ReadAllBytes(store.GetSnapshotPath(selected.FileName)));
        Assert.HasCount(0, Directory.GetFiles(directory, "*.tmp"));
    }

    [TestMethod]
    public void LegacyMigrationDoesNotOverwriteExistingDatedSnapshot()
    {
        string existing = store.SaveSnapshot(TestSnapshots.Certain() with { RetrievedAt = Retrieved(26) });
        byte[] original = File.ReadAllBytes(store.GetSnapshotPath(existing));
        WriteSnapshot(directory, "polymarket_predictions.json", TestSnapshots.Load() with { RetrievedAt = Retrieved(26).AddHours(1) });

        Assert.AreEqual(Retrieved(26), store.LoadPrediction().Snapshot.RetrievedAt);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(store.GetSnapshotPath(existing)));
        Assert.IsTrue(File.Exists(store.LegacySnapshotPath));
    }

    [TestMethod]
    public void FailedLegacyArchiveKeepsOriginalSnapshotUsableAndLeavesNoTemporaryFiles()
    {
        var legacy = TestSnapshots.Certain() with { RetrievedAt = Retrieved(26) };
        WriteSnapshot(directory, "polymarket_predictions.json", legacy);
        Directory.CreateDirectory(store.GetSnapshotPath("polymarket_predictions_20261026.json"));
        var warnings = new List<string>();

        Assert.AreEqual(legacy.RetrievedAt, store.LoadPrediction(warn: warnings.Add).Snapshot.RetrievedAt);
        Assert.HasCount(1, warnings);
        Assert.IsTrue(File.Exists(store.LegacySnapshotPath));
        Assert.HasCount(0, Directory.GetFiles(directory, "*.tmp"));
    }

    [TestMethod]
    public void PreviousDatedFilenameAndRememberedSelectionUseCanonicalNameWithoutChangingBytes()
    {
        Directory.CreateDirectory(directory);
        var snapshot = TestSnapshots.Certain() with { RetrievedAt = Retrieved(23) };
        string oldPath = Path.Combine(directory, "predictions_20261023.json");
        byte[] bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(
            "\r\n " + JsonSerializer.Serialize(snapshot, ForecastJson.Options) + "\r\n")).ToArray();
        File.WriteAllBytes(oldPath, bytes);
        const string settings = "{\"rho\":0.42,\"trialCount\":1000,\"selectedPredictionFile\":\"predictions_20261023.json\"}";
        File.WriteAllText(store.SettingsPath, settings);

        var restarted = new ForecastStore(directory, store.BundledDataDirectory);
        AppSettings saved = restarted.LoadSettings();
        var selected = restarted.LoadPrediction(saved.SelectedPredictionFile);

        Assert.AreEqual(0.42, saved.Rho);
        Assert.AreEqual("polymarket_predictions_20261023.json", selected.FileName);
        Assert.AreEqual(snapshot.RetrievedAt, selected.Snapshot.RetrievedAt);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(oldPath));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(store.GetSnapshotPath(selected.FileName)));
        Assert.AreEqual(settings, File.ReadAllText(store.SettingsPath));
        CollectionAssert.AreEqual(new[] { "polymarket_predictions_20261024.json", selected.FileName },
            restarted.ListSnapshots().Select(file => file.FileName).ToArray());
    }

    [TestMethod]
    public void PreviousUndatedFilenameMigratesWithoutDeletingOriginal()
    {
        var snapshot = TestSnapshots.Certain() with { RetrievedAt = Retrieved(26) };
        WriteSnapshot(directory, "predictions.json", snapshot);
        byte[] bytes = File.ReadAllBytes(store.PreviousLegacySnapshotPath);

        var selected = store.LoadPrediction();

        Assert.AreEqual("polymarket_predictions_20261026.json", selected.FileName);
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(store.PreviousLegacySnapshotPath));
        CollectionAssert.AreEqual(bytes, File.ReadAllBytes(store.GetSnapshotPath(selected.FileName)));
    }

    [TestMethod]
    public void CanonicalDatedFileWinsOverPreviousNameAndCorruptCanonicalCanFallBack()
    {
        var canonical = TestSnapshots.Load() with { RetrievedAt = Retrieved(26) };
        var previous = TestSnapshots.Certain() with { RetrievedAt = Retrieved(26).AddHours(1) };
        string name = store.SaveSnapshot(canonical);
        string canonicalPath = store.GetSnapshotPath(name);
        byte[] original = File.ReadAllBytes(canonicalPath);
        string previousPath = WriteSnapshot(directory, "predictions_20261026.json", previous);
        byte[] previousBytes = File.ReadAllBytes(previousPath);

        Assert.AreEqual(canonical.RetrievedAt, store.LoadPrediction(name).Snapshot.RetrievedAt);
        CollectionAssert.AreEqual(original, File.ReadAllBytes(canonicalPath));
        Assert.HasCount(2, store.ListSnapshots());

        File.WriteAllText(canonicalPath, "{broken");
        var warnings = new List<string>();
        var fallback = store.LoadPrediction(name, warnings.Add);
        Assert.AreEqual(previous.RetrievedAt, fallback.Snapshot.RetrievedAt);
        Assert.AreEqual(name, fallback.FileName);
        Assert.IsNotEmpty(warnings);
        Assert.AreEqual("{broken", File.ReadAllText(canonicalPath));
        CollectionAssert.AreEqual(previousBytes, File.ReadAllBytes(previousPath));
    }

    private static DateTimeOffset Retrieved(int day) => new(2026, 10, day, 12, 0, 0, TimeSpan.Zero);

    private static string WriteSnapshot(string targetDirectory, string fileName, PredictionSnapshot snapshot)
    {
        Directory.CreateDirectory(targetDirectory);
        string path = Path.Combine(targetDirectory, fileName);
        File.WriteAllText(path, JsonSerializer.Serialize(snapshot, ForecastJson.Options));
        return path;
    }
}
