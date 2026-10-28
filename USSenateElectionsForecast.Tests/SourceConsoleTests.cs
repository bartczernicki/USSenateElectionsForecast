using System.Globalization;
using System.Net;
using System.Text.Json;
using USSenateElectionsForecast.Markets;
using USSenateElectionsForecast.Markets.Kalshi;
using USSenateElectionsForecast.Markets.Polymarket;

namespace USSenateElectionsForecast.Tests;

[TestClass]
public sealed class SourceConsoleTests
{
    [TestMethod]
    public async Task PolymarketRemainsDefaultWhenNewerKalshiDataIsAvailable()
    {
        using var scenario = new Scenario();

        await scenario.RunAsync("0\n");

        StringAssert.Contains(scenario.Text, "- Prediction source: Polymarket");
        StringAssert.Contains(scenario.Text, $"Predictions file: {scenario.PolymarketFile}");
        Assert.DoesNotContain($"Predictions file: {scenario.KalshiFile}", scenario.Text);
        Assert.IsEmpty(scenario.Handler.Requests);
        Assert.IsFalse(File.Exists(scenario.Store.SettingsPath));
    }

    [TestMethod]
    public async Task SourceSwitchClearsSpecificFilePersistsAndUsesKalshiForForecastsAfterRestart()
    {
        using var scenario = new Scenario();
        scenario.Store.SaveSettings(new AppSettings(0.2, 1_000, scenario.PolymarketFile));

        await scenario.RunAsync("5\n6\n2\n\n4\n\n0\n1\n\n0\n");

        AppSettings settings = scenario.Store.LoadSettings();
        Assert.AreEqual(PredictionSource.Kalshi, settings.Source);
        Assert.IsNull(settings.SelectedPredictionFile);
        Assert.AreEqual(0.2, settings.Rho);
        Assert.AreEqual(1_000, settings.TrialCount);
        StringAssert.Contains(scenario.Text, $"Selected prediction source: Kalshi. Using {scenario.KalshiFile}.");
        StringAssert.Contains(scenario.Text, "1) Update Predictions (Polymarket)");
        StringAssert.Contains(scenario.Text, "4) List Current US Senate Probabilities (Polymarket)");
        StringAssert.Contains(scenario.Text, $"5) Select Predictions File ({scenario.PolymarketFile})");
        StringAssert.Contains(scenario.Text, "6) Select Prediction Source (Polymarket)");
        StringAssert.Contains(scenario.Text, "1) Update Predictions (Kalshi)");
        StringAssert.Contains(scenario.Text, "4) List Current US Senate Probabilities (Kalshi)");
        StringAssert.Contains(scenario.Text, $"5) Select Predictions File ({scenario.KalshiFile})");
        StringAssert.Contains(scenario.Text, "6) Select Prediction Source (Kalshi)");
        StringAssert.Contains(scenario.Text, "Probability of >=60 Dem Seats: 100.00 %");

        int offset = scenario.Text.Length;
        await scenario.RunAsync("1\n\n0\n");
        string restarted = scenario.Text[offset..];
        StringAssert.Contains(restarted, "- Prediction source: Kalshi");
        StringAssert.Contains(restarted, $"Predictions file: {scenario.KalshiFile}");
        StringAssert.Contains(restarted, "Probability of >=60 Dem Seats: 100.00 %");
        Assert.IsEmpty(scenario.Handler.Requests);
    }

    [TestMethod]
    public async Task FilePickerListsOnlyActiveSourceWithoutDownloading()
    {
        using var scenario = new Scenario();
        scenario.Store.SaveSettings(new AppSettings(Source: PredictionSource.Kalshi));

        await scenario.RunAsync("5\n5\n0\n0\n0\n");

        StringAssert.Contains(scenario.Text, $"2) {scenario.KalshiFile} (active)");
        StringAssert.Contains(scenario.Text, "4) List Current US Senate Probabilities (Kalshi)");
        StringAssert.Contains(scenario.Text, $"5) Select Predictions File ({scenario.KalshiFile})");
        StringAssert.Contains(scenario.Text, "6) Select Prediction Source (Kalshi)");
        Assert.DoesNotContain(scenario.PolymarketFile, scenario.Text);
        Assert.IsEmpty(scenario.Handler.Requests);
    }

    [TestMethod]
    public async Task MissingSourceSnapshotPreservesSettingsAndActiveForecast()
    {
        using var scenario = new Scenario(includeKalshi: false);
        scenario.Store.SaveSettings(new AppSettings(0, 1_000, scenario.PolymarketFile));
        string settingsBefore = File.ReadAllText(scenario.Store.SettingsPath);

        await scenario.RunAsync("5\n6\n2\n\n0\n1\n\n0\n");

        Assert.AreEqual(settingsBefore, File.ReadAllText(scenario.Store.SettingsPath));
        StringAssert.Contains(scenario.Text, "Could not select prediction source:");
        StringAssert.Contains(scenario.Text, "The previous predictions and source are still selected.");
        Assert.DoesNotContain("- Prediction source: Kalshi", scenario.Text);
        StringAssert.Contains(scenario.Text, "Probability of >=60 Dem Seats: 0.00 %");
        Assert.IsEmpty(scenario.Handler.Requests);
    }

    [TestMethod]
    public async Task SourceSwitchSettingsSaveFailurePreservesPreviousSnapshot()
    {
        using var scenario = new Scenario();
        Directory.CreateDirectory(scenario.Store.SettingsPath);

        await scenario.RunAsync("5\n6\n2\n\n4\n\n0\n0\n");

        StringAssert.Contains(scenario.Text, "Could not select prediction source:");
        Assert.DoesNotContain("- Prediction source: Kalshi", scenario.Text);
        Assert.DoesNotContain($"Predictions file: {scenario.KalshiFile}", scenario.Text);
        StringAssert.Contains(scenario.Text, $"Predictions file: {scenario.PolymarketFile}");
        Assert.IsEmpty(scenario.Handler.Requests);
    }

    [TestMethod]
    public async Task FailedKalshiRefreshPreservesSpecificSelectionAndCallsOnlyKalshi()
    {
        using var scenario = new Scenario();
        string older = scenario.Store.SaveSnapshot(KalshiSnapshot(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero)));
        scenario.Store.SaveSettings(new AppSettings(0, 1_000, older, PredictionSource.Kalshi));
        string settingsBefore = File.ReadAllText(scenario.Store.SettingsPath);
        string predictionsBefore = File.ReadAllText(scenario.Store.GetSnapshotPath(older));

        await scenario.RunAsync("5\n1\n\n0\n0\n");

        Assert.AreEqual(settingsBefore, File.ReadAllText(scenario.Store.SettingsPath));
        Assert.AreEqual(predictionsBefore, File.ReadAllText(scenario.Store.GetSnapshotPath(older)));
        StringAssert.Contains(scenario.Text, "Updating all 35 Senate races from Kalshi...");
        StringAssert.Contains(scenario.Text, "The previous predictions are still selected.");
        Assert.IsGreaterThan(0, scenario.Handler.Requests.Count);
        Assert.IsTrue(scenario.Handler.Requests.All(uri => uri.Host == "external-api.kalshi.com"));
    }

    [TestMethod]
    public async Task SuccessfulKalshiRefreshReturnsToLatestAndPreservesPolymarketData()
    {
        using var scenario = new Scenario(successfulRefresh: true);
        string older = scenario.Store.SaveSnapshot(KalshiSnapshot(new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero)));
        scenario.Store.SaveSettings(new AppSettings(0, 1_000, older, PredictionSource.Kalshi));
        string polymarketBefore = File.ReadAllText(scenario.Store.GetSnapshotPath(scenario.PolymarketFile));
        DateTimeOffset began = DateTimeOffset.UtcNow;

        await scenario.RunAsync("5\n1\n\n4\n\n0\n0\n");

        AppSettings settings = scenario.Store.LoadSettings();
        Assert.AreEqual(PredictionSource.Kalshi, settings.Source);
        Assert.IsNull(settings.SelectedPredictionFile);
        PredictionSnapshot refreshed = scenario.Store.LoadSnapshot(source: PredictionSource.Kalshi);
        Assert.IsGreaterThanOrEqualTo(began, refreshed.RetrievedAt);
        Assert.HasCount(35, refreshed.Races);
        Assert.IsTrue(refreshed.Races.All(race => race.EventSlug == KalshiRaceCatalog.All.Single(definition => definition.State == race.State).EventSlug));
        Assert.AreEqual(polymarketBefore, File.ReadAllText(scenario.Store.GetSnapshotPath(scenario.PolymarketFile)));
        Assert.HasCount(35, scenario.Handler.Requests);
        Assert.IsTrue(scenario.Handler.Requests.All(uri => uri.Host == "external-api.kalshi.com"));
        StringAssert.Contains(scenario.Text, "Updated 35 races. Predictions have been saved.");
        StringAssert.Contains(scenario.Text, "Current Race Probabilities (closest first):");
    }

    private static PredictionSnapshot KalshiSnapshot(DateTimeOffset retrievedAt) => new(1, 2026, retrievedAt,
        KalshiRaceCatalog.All.Select(definition => new RacePrediction(definition.State, definition.EventSlug,
            definition.IsSpecial, null, definition.Markets.Select(market => new MarketPrediction(
                market.MarketId, market.MarketSlug, market.Caucus.ToString(), market.Caucus,
                market.Caucus == Caucus.Democratic ? 1 : 0,
                market.Caucus == Caucus.Democratic ? 1 : 0, null)).ToArray())).ToArray());

    private sealed class Scenario : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "senate-source-console-tests-" + Guid.NewGuid().ToString("N"));
        private readonly StringWriter _output = new(CultureInfo.InvariantCulture);
        private readonly HttpClient _http;
        public ForecastStore Store { get; }
        public string PolymarketFile { get; }
        public string? KalshiFile { get; }
        public RequestHandler Handler { get; }
        public string Text => _output.ToString();

        public Scenario(bool includeKalshi = true, bool successfulRefresh = false)
        {
            Handler = new RequestHandler(successfulRefresh);
            _http = new HttpClient(Handler);
            Store = new ForecastStore(_directory, Path.Combine(_directory, "bundled"));
            PolymarketFile = Store.SaveSnapshot(TestSnapshots.Certain());
            if (includeKalshi)
                KalshiFile = Store.SaveSnapshot(KalshiSnapshot(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero)));
        }

        public async Task RunAsync(string input)
        {
            using var reader = new StringReader(input);
            await new ConsoleApp(reader, _output, Store, new PolymarketClient(_http), new SimulationEngine(),
                new ConsolePresentation(_output), new ProbabilityCalculationEngine(), new KalshiClient(_http)).RunAsync();
        }

        public void Dispose()
        {
            _output.Dispose();
            _http.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class RequestHandler(bool successful) : HttpMessageHandler
    {
        private readonly List<Uri> _requests = [];
        public IReadOnlyList<Uri> Requests { get { lock (_requests) return _requests.ToArray(); } }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_requests) _requests.Add(request.RequestUri!);
            if (!successful)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));

            string ticker = Uri.UnescapeDataString(request.RequestUri!.Segments[^1]);
            RaceDefinition definition = KalshiRaceCatalog.All.Single(race => race.EventSlug == ticker);
            var source = new
            {
                @event = new
                {
                    event_ticker = ticker,
                    title = $"Who wins the {definition.State} Senate election?",
                    sub_title = "2026 election",
                    category = "Elections",
                    mutually_exclusive = true
                },
                markets = definition.Markets.Select(market => new
                {
                    ticker = market.MarketId,
                    event_ticker = ticker,
                    market_type = "binary",
                    status = "active",
                    rules_primary = $"Senator of {definition.State} " + (definition.IsSpecial
                        ? "pursuant to the Class III (special election) held on November 3, 2026"
                        : "for the term beginning in 2027") + ". Democratic party or Republican party.",
                    yes_sub_title = market.Caucus.ToString(),
                    yes_bid_dollars = market.Caucus switch { Caucus.Democratic => "0.69", Caucus.Republican => "0.29", _ => "0.00" },
                    yes_ask_dollars = market.Caucus switch { Caucus.Democratic => "0.71", Caucus.Republican => "0.31", _ => "0.002" }
                }).ToArray()
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(source))
            });
        }
    }
}
