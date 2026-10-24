using System.Text.Json;

namespace USSenateElectionsForecast.Tests;

[TestClass]
public sealed class StoreTests
{
    private string directory = null!;
    private ForecastStore store = null!;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(Path.GetTempPath(), "SenateForecastTests", Guid.NewGuid().ToString("N"));
        store = new ForecastStore(directory, Path.Combine(AppContext.BaseDirectory, "Data", "bundled-predictions.json"));
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
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
        foreach (var expected in new[] { new AppSettings(0, 1_000), new AppSettings(0.99, 10_000_000) })
        {
            store.SaveSettings(expected);
            Assert.AreEqual(expected, store.LoadSettings());
        }
        Assert.HasCount(0, Directory.GetFiles(directory, "*.tmp"));
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
        var expected = store.LoadSnapshot() with { RetrievedAt = DateTimeOffset.UtcNow };
        store.SaveSnapshot(expected);
        var actual = store.LoadSnapshot();
        Assert.AreEqual(expected.RetrievedAt, actual.RetrievedAt);
        Assert.AreEqual(JsonSerializer.Serialize(expected, ForecastJson.Options), JsonSerializer.Serialize(actual, ForecastJson.Options));
        Assert.ThrowsExactly<InvalidDataException>(() => store.SaveSnapshot(expected with { Races = expected.Races.Skip(1).ToArray() }));
        Assert.AreEqual(expected.RetrievedAt, store.LoadSnapshot().RetrievedAt);
    }

    [TestMethod]
    public void InvalidCacheFallsBackToBundleWithWarning()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(store.SnapshotPath, "null");
        var warnings = new List<string>();
        Assert.HasCount(35, store.LoadSnapshot(warnings.Add).Races);
        Assert.HasCount(1, warnings);
        Assert.AreEqual("null", File.ReadAllText(store.SnapshotPath));
    }

    [TestMethod]
    public void MissingBundleAndCacheReturnActionableError()
    {
        var missing = new ForecastStore(directory, Path.Combine(directory, "missing.json"));
        var error = Assert.ThrowsExactly<InvalidDataException>(() => missing.LoadSnapshot());
        StringAssert.Contains(error.Message, "Update Predictions from Polymarket");
    }

    [TestMethod]
    public void FailedDiskReplacementLeavesNoTemporaryFilesAndBundleStillWorks()
    {
        var snapshot = store.LoadSnapshot();
        Directory.CreateDirectory(store.SnapshotPath);
        var error = Assert.Throws<Exception>(() => store.SaveSnapshot(snapshot));
        Assert.IsTrue(error is IOException or UnauthorizedAccessException);
        Assert.HasCount(0, Directory.GetFiles(directory, "*.tmp"));
        Assert.HasCount(35, store.LoadSnapshot().Races);
    }
}
