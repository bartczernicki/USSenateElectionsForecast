using System.Net;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using USSenateElectionsForecast.Markets.Polymarket;

namespace USSenateElectionsForecast.Tests;

[TestClass]
public sealed class ConsoleTests
{
    [TestMethod]
    public async Task MenusValidateInputAndNavigateBackWithoutRecursion()
    {
        using var scenario = new Scenario("bad\n6\n5\n9\n0\n0\n");
        await scenario.RunAsync();
        StringAssert.Contains(scenario.Text, "US Senate Elections Forecast");
        StringAssert.Contains(scenario.Text, "1) Independent Monte Carlo Simulation");
        StringAssert.Contains(scenario.Text, "2) Correlated Monte Carlo Simulation");
        StringAssert.Contains(scenario.Text, "3) Conditional Monte Carlo");
        StringAssert.Contains(scenario.Text, "4) Direct Probability Calculation");
        StringAssert.Contains(scenario.Text, "5) Configure");
        Assert.DoesNotContain("Options 2/3 and option 4's correlated result use the same model.", scenario.Text);
        Assert.DoesNotContain("At about 1,000,000 samples, 2/3 generally approach 4's direct calculation.", scenario.Text);
        Assert.DoesNotContain("Small sampling differences can remain; option 4 integrates without sampling.", scenario.Text);
        Assert.DoesNotContain("Select an option:", scenario.Text);
        StringAssert.Contains(scenario.Text, "0) Exit");
        StringAssert.Contains(scenario.Text, "0) Back");
        Assert.AreEqual(2, Occurrences(scenario.Text, "Please enter a number from 0 to 5."));
        Assert.AreEqual(1, Occurrences(scenario.Text, "Please enter a number from 0 to 6."));
        StringAssert.Contains(scenario.Text, "4) List Current US Senate Probabilities (Polymarket)");
        StringAssert.Contains(scenario.Text, $"5) Select Predictions File ({scenario.Store.LoadPrediction().FileName})");
        StringAssert.Contains(scenario.Text, "6) Select Prediction Source (Polymarket)");
        Assert.DoesNotContain("\u001b", scenario.Text);
    }

    [TestMethod]
    public async Task RhoAndCountRejectInvalidValuesAndPersistValidValues()
    {
        using var scenario = new Scenario("5\n2\nNaN\nInfinity\n-0.01\n1\n0.99\n3\n999\n10000001\n1,2,3,4\n1,000\n0\n0\n");
        await scenario.RunAsync();
        AppSettings settings = scenario.Store.LoadSettings();
        Assert.AreEqual(0.99, settings.Rho);
        StringAssert.Contains(scenario.Text, "- Rho links race scores: 0 = independent; higher = more national swing.");
        StringAssert.Contains(scenario.Text, "- Default 0.12: 12% national / 88% local variance; our assumption, not Silver's.");
        StringAssert.Contains(scenario.Text, "- Shared polling errors motivate correlation (Silver/FiveThirtyEight).");
        Assert.AreEqual(1_000, settings.TrialCount);
        Assert.AreEqual(4, Occurrences(scenario.Text, "Enter a finite number between"));
        Assert.AreEqual(3, Occurrences(scenario.Text, "Enter a whole number between"));
    }

    [TestMethod]
    public async Task BlankValuesRetainSettingsAndZeroIsValidRho()
    {
        using var scenario = new Scenario("5\n2\n0.123456\n2\n\n2\n0\n3\n\n0\n0\n");
        await scenario.RunAsync();
        AppSettings settings = scenario.Store.LoadSettings();
        Assert.AreEqual(0, settings.Rho);
        Assert.AreEqual(100_000, settings.TrialCount);
        Assert.AreEqual(2, Occurrences(scenario.Text, "Configuration saved."));
        StringAssert.Contains(scenario.Text, "Rho: 0.123456");
    }

    [TestMethod]
    public async Task FailedUpdateRetainsSavedPredictionsAndReturnsToConfigure()
    {
        using var scenario = new Scenario("5\n1\n\n0\n0\n");
        var before = scenario.Store.LoadSnapshot();
        await scenario.RunAsync();
        var after = scenario.Store.LoadSnapshot();
        Assert.AreEqual(before.RetrievedAt, after.RetrievedAt);
        StringAssert.Contains(scenario.Text, "Could not update predictions:");
        StringAssert.Contains(scenario.Text, "The previous predictions are still selected.");
        Assert.IsGreaterThanOrEqualTo(2, Occurrences(scenario.Text, "1) Update Predictions (Polymarket)"));
    }

    [TestMethod]
    public async Task SuccessfulUpdatePersistsAllRacesAndUsesNewSnapshotForSimulation()
    {
        using var handler = new SuccessfulRequestHandler();
        using var scenario = new Scenario("5\n1\n\n4\n\n0\n1\n\n0\n", handler: handler);
        string previousFile = scenario.Store.SaveSnapshot(TestSnapshots.Certain() with
        {
            RetrievedAt = TestSnapshots.Load().RetrievedAt.AddDays(-1)
        });
        string previousContents = File.ReadAllText(scenario.Store.GetSnapshotPath(previousFile));
        scenario.Store.SaveSettings(new AppSettings(0.12, 1_000, previousFile));
        DateTimeOffset began = DateTimeOffset.UtcNow;
        await scenario.RunAsync();
        PredictionSnapshot saved = scenario.Store.LoadSnapshot();
        Assert.AreEqual(35, handler.RequestCount);
        Assert.IsNull(scenario.Store.LoadSettings().SelectedPredictionFile);
        Assert.AreEqual(previousContents, File.ReadAllText(scenario.Store.GetSnapshotPath(previousFile)));
        Assert.IsGreaterThanOrEqualTo(began, saved.RetrievedAt);
        Assert.AreEqual(JsonSerializer.Serialize(TestSnapshots.Load().Races, ForecastJson.Options),
            JsonSerializer.Serialize(saved.Races, ForecastJson.Options));
        StringAssert.Contains(scenario.Text, "Updated 35 races. Predictions have been saved.");
        string timestamp = saved.RetrievedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        StringAssert.Contains(scenario.Text, $"Trials: 1,000 | Predictions retrieved: {timestamp}");
        StringAssert.Contains(scenario.Text, "Senate Control Probability:");
        using var expectedListing = new StringWriter(CultureInfo.InvariantCulture);
        new ConsolePresentation(expectedListing).Predictions(saved);
        StringAssert.Contains(scenario.Text, expectedListing.ToString());
    }

    [TestMethod]
    public async Task ListingShowsCurrentSnapshotWithoutFetchingOrChangingSavedData()
    {
        using var handler = new FailedRequestHandler();
        using var scenario = new Scenario("5\n4\n\n0\n0\n", handler: handler);
        scenario.Store.SaveSettings(new AppSettings(0.42, 1_000));
        string predictionsPath = scenario.Store.GetSnapshotPath(scenario.Store.LoadPrediction().FileName);
        string predictionsBefore = File.ReadAllText(predictionsPath);
        string settingsBefore = File.ReadAllText(scenario.Store.SettingsPath);
        using var expectedListing = new StringWriter(CultureInfo.InvariantCulture);
        new ConsolePresentation(expectedListing).Predictions(scenario.Store.LoadSnapshot());

        await scenario.RunAsync();

        Assert.AreEqual(0, handler.RequestCount);
        Assert.AreEqual(predictionsBefore, File.ReadAllText(predictionsPath));
        Assert.AreEqual(settingsBefore, File.ReadAllText(scenario.Store.SettingsPath));
        StringAssert.Contains(scenario.Text, expectedListing.ToString());
        Assert.IsGreaterThanOrEqualTo(2, Occurrences(scenario.Text, "4) List Current US Senate Probabilities (Polymarket)"));
        Assert.DoesNotContain("Running simulation...", scenario.Text);
    }

    [TestMethod]
    public async Task MissingSnapshotLeavesMenusUsableAndExplainsDisabledSimulation()
    {
        using var scenario = new Scenario("1\n\n2\n\n3\n\n4\n\n5\n4\n\n0\n0\n", noSnapshot: true);
        await scenario.RunAsync();
        StringAssert.Contains(scenario.Text, "Predictions unavailable.");
        Assert.AreEqual(4, Occurrences(scenario.Text, "Forecast calculation is unavailable until predictions are loaded."));
        StringAssert.Contains(scenario.Text, "1) Update Predictions (Polymarket)");
        StringAssert.Contains(scenario.Text.Replace(Environment.NewLine, " "), "load them.");
        Assert.IsGreaterThanOrEqualTo(2, Occurrences(scenario.Text, "4) List Current US Senate Probabilities (Polymarket)"));
        StringAssert.Contains(scenario.Text, "5) Select Predictions File (Unavailable)");
        StringAssert.Contains(scenario.Text, "6) Select Prediction Source (Polymarket)");
    }

    [TestMethod]
    public async Task HistoricalSelectionPersistsAndIsUsedForForecastsAfterRestart()
    {
        using var handler = new FailedRequestHandler();
        using var scenario = new Scenario("5\n5\n3\n\n0\n1\n\n0\n", handler: handler);
        string older = scenario.Store.SaveSnapshot(TestSnapshots.Certain(democraticWins: 35) with
        {
            RetrievedAt = TestSnapshots.Load().RetrievedAt.AddDays(-1)
        });
        scenario.Store.SaveSettings(new AppSettings(0, 1_000));

        await scenario.RunAsync();

        Assert.AreEqual(older, scenario.Store.LoadSettings().SelectedPredictionFile);
        StringAssert.Contains(scenario.Text, "1) Latest available (selected)");
        StringAssert.Contains(scenario.Text, "2) polymarket_predictions_20261024.json (active)");
        StringAssert.Contains(scenario.Text, $"3) {older}");
        StringAssert.Contains(scenario.Text, $"Selected predictions file: {older}");
        StringAssert.Contains(scenario.Text, $"Predictions file: {older}");
        StringAssert.Contains(scenario.Text, "Probability of >=60 Dem Seats: 100.00 %");

        int restartOffset = scenario.Text.Length;
        await scenario.RunAgainAsync("1\n\n0\n");
        string restartOutput = scenario.Text[restartOffset..];
        StringAssert.Contains(restartOutput, $"Predictions file: {older}");
        StringAssert.Contains(restartOutput, "Probability of >=60 Dem Seats: 100.00 %");
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task ReturningToLatestSelectsNewestAvailableFileAndKeepsHistoricalData()
    {
        using var handler = new FailedRequestHandler();
        using var scenario = new Scenario("5\n5\n1\n\n0\n0\n", handler: handler);
        string older = scenario.Store.SaveSnapshot(TestSnapshots.Certain(democraticWins: 35) with
        {
            RetrievedAt = TestSnapshots.Load().RetrievedAt.AddDays(-1)
        });
        scenario.Store.SaveSettings(new AppSettings(0, 1_000, older));
        string newest = scenario.Store.SaveSnapshot(TestSnapshots.Certain() with
        {
            RetrievedAt = TestSnapshots.Load().RetrievedAt.AddDays(2)
        });
        string olderContents = File.ReadAllText(scenario.Store.GetSnapshotPath(older));

        await scenario.RunAsync();

        Assert.IsNull(scenario.Store.LoadSettings().SelectedPredictionFile);
        StringAssert.Contains(scenario.Text, $"Selected predictions file: {newest}");
        StringAssert.Contains(scenario.Text, $"Predictions file: {newest}");
        Assert.AreEqual(olderContents, File.ReadAllText(scenario.Store.GetSnapshotPath(older)));
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task MissingRememberedFileFallsBackToLatestWithoutRewritingSettings()
    {
        using var handler = new FailedRequestHandler();
        using var scenario = new Scenario("0\n", handler: handler);
        scenario.Store.SaveSettings(new AppSettings(0.2, 1_000, "polymarket_predictions_20261020.json"));
        string settingsBefore = File.ReadAllText(scenario.Store.SettingsPath);

        await scenario.RunAsync();

        StringAssert.Contains(scenario.Text.ReplaceLineEndings(" "), "is missing or invalid.");
        StringAssert.Contains(scenario.Text, "polymarket_predictions_20261020.json");
        StringAssert.Contains(scenario.Text, "Predictions file: polymarket_predictions_20261024.json");
        Assert.AreEqual(settingsBefore, File.ReadAllText(scenario.Store.SettingsPath));
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task PickerBackAndInvalidInputKeepSelectedFileAndSettings()
    {
        using var handler = new FailedRequestHandler();
        using var scenario = new Scenario("5\n5\n9\n0\n0\n0\n", handler: handler);
        string older = scenario.Store.SaveSnapshot(TestSnapshots.Load() with
        {
            RetrievedAt = TestSnapshots.Load().RetrievedAt.AddDays(-1)
        });
        scenario.Store.SaveSettings(new AppSettings(0.2, 1_000, older));
        string settingsBefore = File.ReadAllText(scenario.Store.SettingsPath);

        await scenario.RunAsync();

        StringAssert.Contains(scenario.Text, "Please enter a number from 0 to 3.");
        StringAssert.Contains(scenario.Text, $"Predictions file: {older}");
        Assert.DoesNotContain("Selected predictions file:", scenario.Text);
        Assert.AreEqual(settingsBefore, File.ReadAllText(scenario.Store.SettingsPath));
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task FailedSelectionSettingsSaveRetainsPreviouslyActivePredictions()
    {
        using var handler = new FailedRequestHandler();
        using var scenario = new Scenario("5\n5\n3\n\n4\n\n0\n0\n", handler: handler);
        string older = scenario.Store.SaveSnapshot(TestSnapshots.Certain(democraticWins: 35) with
        {
            RetrievedAt = TestSnapshots.Load().RetrievedAt.AddDays(-1)
        });
        Directory.CreateDirectory(scenario.Store.SettingsPath);
        using var expectedListing = new StringWriter(CultureInfo.InvariantCulture);
        new ConsolePresentation(expectedListing).Predictions(TestSnapshots.Load());

        await scenario.RunAsync();

        StringAssert.Contains(scenario.Text, "Error:");
        Assert.DoesNotContain("Selected predictions file:", scenario.Text);
        Assert.DoesNotContain($"Predictions file: {older}", scenario.Text);
        StringAssert.Contains(scenario.Text, expectedListing.ToString());
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task FileRemovedAfterPickerOpensDoesNotChangeSelectedPredictions()
    {
        using var handler = new FailedRequestHandler();
        ForecastStore? store = null;
        using var scenario = new Scenario("5\n5\n3\n\n4\n\n0\n0\n", handler: handler,
            beforeRead: read =>
            {
                if (read == 3) File.Delete(store!.GetSnapshotPath("polymarket_predictions_20261023.json"));
            });
        store = scenario.Store;
        string older = store.SaveSnapshot(TestSnapshots.Certain(democraticWins: 35) with
        {
            RetrievedAt = TestSnapshots.Load().RetrievedAt.AddDays(-1)
        });
        store.SaveSettings(new AppSettings(0, 1_000));
        string settingsBefore = File.ReadAllText(store.SettingsPath);

        await scenario.RunAsync();

        StringAssert.Contains(scenario.Text, "Error:");
        Assert.DoesNotContain("Selected predictions file:", scenario.Text);
        Assert.DoesNotContain($"Predictions file: {older}", scenario.Text);
        Assert.AreEqual(settingsBefore, File.ReadAllText(store.SettingsPath));
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task CancellationAtPickerPromptKeepsSelectionAndExitsWithoutPausing()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new FailedRequestHandler();
        using var scenario = new Scenario("5\n5\n3\n", handler: handler,
            beforeRead: read =>
            {
                if (read == 3) cancellation.Cancel();
            });
        scenario.Store.SaveSnapshot(TestSnapshots.Load() with
        {
            RetrievedAt = TestSnapshots.Load().RetrievedAt.AddDays(-1)
        });
        scenario.Store.SaveSettings(new AppSettings(0, 1_000));
        string settingsBefore = File.ReadAllText(scenario.Store.SettingsPath);

        await scenario.RunAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.DoesNotContain("Selected predictions file:", scenario.Text);
        Assert.DoesNotContain("Press Enter to return", scenario.Text);
        Assert.AreEqual(settingsBefore, File.ReadAllText(scenario.Store.SettingsPath));
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    public async Task BothSimulationMenusRenderPartyDistributionsAndDemocraticThresholds()
    {
        using var scenario = new Scenario("1\n\n2\n\n0\n");
        scenario.Store.SaveSettings(new AppSettings(0.12, 1_000));
        await scenario.RunAsync();
        StringAssert.Contains(scenario.Text, "Independent Monte Carlo Simulation");
        StringAssert.Contains(scenario.Text, "Correlated Monte Carlo Simulation");
        Assert.AreEqual(2, Occurrences(scenario.Text, "Senate Control Probability:"));
        Assert.AreEqual(2, Occurrences(scenario.Text, "Democratic Caucus Seat Distribution:"));
        Assert.AreEqual(2, Occurrences(scenario.Text, "Republican Caucus Seat Distribution:"));
        Assert.DoesNotContain("Unknown / Unaffiliated Seat Distribution:", scenario.Text);
        Assert.AreEqual(2, Occurrences(scenario.Text, "Democratic seat probabilities (at least):"));
        Assert.AreEqual(2, Occurrences(scenario.Text, "Probability of >=50 Dem Seats:"));
        Assert.AreEqual(2, Occurrences(scenario.Text, "Probability of >=60 Dem Seats:"));
        StringAssert.Contains(scenario.Text, "90% interval:");
        StringAssert.Contains(scenario.Text, "shared latent Gaussian correlation");
    }

    [TestMethod]
    public async Task ConditionalAndDirectMenusRenderWeightedResultsWithAccurateMethodLabels()
    {
        using var scenario = new Scenario("3\n\n4\n\n0\n");
        scenario.Store.SaveSettings(new AppSettings(0.12, 1_000));
        await scenario.RunAsync();
        StringAssert.Contains(scenario.Text, "National-factor samples: 1,000");
        StringAssert.Contains(scenario.Text, "Direct Probability Calculation (Independent)");
        StringAssert.Contains(scenario.Text, "Exact independent aggregation, subject to floating-point arithmetic.");
        StringAssert.Contains(scenario.Text, "Direct Probability Calculation (Correlated)");
        StringAssert.Contains(scenario.Text, "Numerical integration converged at order");
        StringAssert.Contains(scenario.Text, "tolerance 1E-10");
        Assert.AreEqual(3, Occurrences(scenario.Text, "Senate Control Probability:"));
        Assert.AreEqual(3, Occurrences(scenario.Text, "Democratic Caucus Seat Distribution:"));
        Assert.AreEqual(3, Occurrences(scenario.Text, "Republican Caucus Seat Distribution:"));
        Assert.AreEqual(3, Occurrences(scenario.Text, "Probability of >=50 Dem Seats:"));
        Assert.AreEqual(3, Occurrences(scenario.Text, "Probability of >=60 Dem Seats:"));
        Assert.DoesNotContain("Unknown / Unaffiliated Seat Distribution:", scenario.Text);
        Assert.DoesNotContain("Trials:", scenario.Text);
        Assert.IsGreaterThan(scenario.Text.IndexOf("Direct Probability Calculation (Independent)", StringComparison.Ordinal),
            scenario.Text.IndexOf("Direct Probability Calculation (Correlated)", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ConditionalRhoZeroReportsExactCalculationWithoutSampling()
    {
        using var scenario = new Scenario("3\n\n0\n");
        scenario.Store.SaveSettings(new AppSettings(0, 1_000));
        await scenario.RunAsync();
        StringAssert.Contains(scenario.Text, "National-factor samples: 0");
        StringAssert.Contains(scenario.Text, "Rho is zero: exact independent aggregation; no national-factor sampling.");
        Assert.DoesNotContain("Trials:", scenario.Text);
    }

    [TestMethod]
    public async Task DirectNonconvergenceRetainsIndependentResultsAndReturnsToMainMenu()
    {
        using var scenario = new Scenario("4\n\n0\n", probabilityEngine: new ProbabilityCalculationEngine(32));
        await scenario.RunAsync();
        StringAssert.Contains(scenario.Text, "Direct Probability Calculation (Independent)");
        StringAssert.Contains(scenario.Text, "Correlated direct calculation did not converge:");
        StringAssert.Contains(scenario.Text, "The independent results above remain available.");
        Assert.DoesNotContain("Direct Probability Calculation (Correlated)", scenario.Text);
        Assert.AreEqual(1, Occurrences(scenario.Text, "Senate Control Probability:"));
        Assert.IsGreaterThanOrEqualTo(2, Occurrences(scenario.Text, "5) Configure"));
        string error = scenario.Text[scenario.Text.IndexOf("Error: Correlated direct", StringComparison.Ordinal)..]
            .Split("Press Enter", StringSplitOptions.None)[0];
        Assert.DoesNotContain("Update Predictions", error);
    }

    [TestMethod]
    public async Task CancellationExitsConditionalAndDirectCalculationsWithoutPausing()
    {
        foreach (string message in new[] { "Running conditional Monte Carlo...", "Calculating correlated direct probabilities..." })
        {
            using var cancellation = new CancellationTokenSource();
            using var writer = new CancellationWriter(message, cancellation);
            using var scenario = new Scenario(message.StartsWith("Running", StringComparison.Ordinal) ? "3\n" : "4\n", writer: writer);
            scenario.Store.SaveSettings(new AppSettings(0.12, 1_000));
            await scenario.RunAsync(cancellation.Token).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.IsTrue(cancellation.IsCancellationRequested);
            Assert.DoesNotContain("Press Enter to return", scenario.Text);
            Assert.DoesNotContain("Error:", scenario.Text);
            if (message.StartsWith("Calculating", StringComparison.Ordinal))
                StringAssert.Contains(scenario.Text, "Direct Probability Calculation (Independent)");
        }
    }

    [TestMethod]
    public void WeightedResultsRenderProbabilitiesWithoutTrialCounts()
    {
        double[] democratic = new double[101], republican = new double[101], unknown = new double[101];
        democratic[49] = 0.123456;
        democratic[51] = 0.876544;
        republican[49] = 0.876544;
        republican[51] = 0.123456;
        unknown[0] = 1;
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        var result = new ProbabilityResult(CalculationMethod.DirectCorrelated, null, 0.12, DateTimeOffset.UtcNow,
            new ProbabilitySeatDistribution(democratic), new ProbabilitySeatDistribution(republican),
            new ProbabilitySeatDistribution(unknown), new IntegrationDiagnostics(128, 1e-10, 1e-14, 1e-15, 1e-14));

        new ConsolePresentation(output).Results(result);

        string text = output.ToString();
        Assert.DoesNotContain("Trials:", text);
        Assert.DoesNotContain("samples:", text);
        StringAssert.Contains(text, "Probability of >=50 Dem Seats: 87.65 %");
        StringAssert.Contains(text, "Numerical integration converged at order 128; tolerance 1E-10.");
        Assert.DoesNotContain("Unknown / Unaffiliated Seat Distribution:", text);
        StringAssert.Contains(text, "Unknown / unaffiliated");
        foreach (string line in text.ReplaceLineEndings("\n").Split('\n')) Assert.IsLessThanOrEqualTo(80, line.Length);
    }

    [TestMethod]
    public void DemocraticThresholdFooterIncludesExactThresholdAndHigherSeats()
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        int[] democratic = new int[101], republican = new int[101], unknown = new int[101];
        democratic[51] = 2;
        democratic[52] = 3;
        democratic[60] = 4;
        democratic[61] = 1;
        republican[49] = 2;
        republican[48] = 3;
        republican[40] = 4;
        republican[39] = 1;
        unknown[0] = 10;
        var result = new SimulationResult(SimulationMode.Independent, 10, 0, DateTimeOffset.UtcNow,
            new SeatDistribution(democratic), new SeatDistribution(republican), new SeatDistribution(unknown), 10, 0, 0);

        new ConsolePresentation(output).Results(result);

        string text = output.ToString();
        StringAssert.Contains(text, "Probability of >=50 Dem Seats: 100.00 %");
        StringAssert.Contains(text, "Probability of >=51 Dem Seats: 100.00 %");
        StringAssert.Contains(text, "Probability of >=52 Dem Seats: 80.00 %");
        StringAssert.Contains(text, "Probability of >=59 Dem Seats: 50.00 %");
        StringAssert.Contains(text, "Probability of >=60 Dem Seats: 50.00 %");
        Assert.IsGreaterThan(text.IndexOf("Republican Caucus Seat Distribution:", StringComparison.Ordinal),
            text.IndexOf("Democratic seat probabilities (at least):", StringComparison.Ordinal));
        Assert.DoesNotContain("Unknown / Unaffiliated Seat Distribution:", text);
    }

    [TestMethod]
    public async Task EndOfInputExitsFromEveryPrompt()
    {
        foreach (string input in new[] { "", "5\n", "5\n2\n", "5\n3\n", "5\n4\n", "5\n5\n", "5\n5\n2\n", "1\n", "3\n", "4\n" })
        {
            using var scenario = new Scenario(input);
            scenario.Store.SaveSettings(new AppSettings(0, 1_000));
            await scenario.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [TestMethod]
    public void RenderingUsesFortyCellFractionalBarsAndNoAnsiForInjectedWriter()
    {
        using var output = new StringWriter();
        var presentation = new ConsolePresentation(output, enableColor: true);
        int[] democratic = new int[101], republican = new int[101], unknown = new int[101];
        democratic[49] = 1; democratic[51] = 79;
        republican[49] = 79; republican[51] = 1;
        unknown[0] = 80;
        var result = new SimulationResult(SimulationMode.Correlated, 80, 0.12, DateTimeOffset.UtcNow,
            new SeatDistribution(democratic), new SeatDistribution(republican), new SeatDistribution(unknown), 79, 1, 0);
        presentation.Context(new AppSettings(), TestSnapshots.Load());
        presentation.Results(result);
        string text = output.ToString();
        Assert.DoesNotContain("\u001b", text);
        string fractionalRow = text.Split('\n').First(line => line.Contains("49 seats") && line.Contains('▌'));
        Assert.AreEqual(40, fractionalRow.Split('|')[1].Length);
        StringAssert.Contains(fractionalRow, "1.25");
        StringAssert.Contains(text, "Mean:");
        StringAssert.Contains(text, "tie-breaking vote favors the Republican caucus");
        foreach (string line in text.ReplaceLineEndings("\n").Split('\n')) Assert.IsLessThanOrEqualTo(80, line.Length);
    }

    private static int Occurrences(string text, string term) => text.Split(term, StringSplitOptions.None).Length - 1;

    private sealed class Scenario : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "senate-console-tests-" + Guid.NewGuid().ToString("N"));
        private readonly TextReader _input;
        private readonly StringWriter _output;
        private readonly HttpClient _http;
        private readonly ProbabilityCalculationEngine _probabilityEngine;
        public ForecastStore Store { get; }
        public string Text => _output.ToString();

        public Scenario(string input, bool noSnapshot = false, HttpMessageHandler? handler = null,
            ProbabilityCalculationEngine? probabilityEngine = null, StringWriter? writer = null,
            Action<int>? beforeRead = null)
        {
            _input = beforeRead is null ? new StringReader(input) : new CallbackReader(input, beforeRead);
            _http = new HttpClient(handler ?? new FailedRequestHandler());
            _probabilityEngine = probabilityEngine ?? new ProbabilityCalculationEngine();
            _output = writer ?? new StringWriter(CultureInfo.InvariantCulture);
            string bundled = Path.Combine(_directory, "bundled");
            if (!noSnapshot)
            {
                Directory.CreateDirectory(bundled);
                const string fixtureFile = "polymarket_predictions_20261024.json";
                File.Copy(Path.Combine(AppContext.BaseDirectory, "Data", fixtureFile), Path.Combine(bundled, fixtureFile));
            }
            Store = new ForecastStore(_directory, bundled);
            if (!noSnapshot) Store.SaveSnapshot(TestSnapshots.Load());
        }

        public Task RunAsync(CancellationToken cancellationToken = default) => new ConsoleApp(_input, _output, Store,
            new PolymarketClient(_http), new SimulationEngine(), new ConsolePresentation(_output),
            _probabilityEngine).RunAsync(cancellationToken);

        public async Task RunAgainAsync(string input)
        {
            using var reader = new StringReader(input);
            await new ConsoleApp(reader, _output, Store, new PolymarketClient(_http),
                new SimulationEngine(), new ConsolePresentation(_output), _probabilityEngine).RunAsync();
        }

        public void Dispose()
        {
            _input.Dispose();
            _output.Dispose();
            _http.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class CallbackReader(string text, Action<int> beforeRead) : StringReader(text)
    {
        private int _reads;

        public override string? ReadLine()
        {
            beforeRead(++_reads);
            return base.ReadLine();
        }
    }

    private sealed class CancellationWriter(string message, CancellationTokenSource cancellation) : StringWriter(CultureInfo.InvariantCulture)
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value == message) cancellation.Cancel();
        }
    }

    private sealed class FailedRequestHandler : HttpMessageHandler
    {
        private int _requestCount;
        public int RequestCount => _requestCount;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("Invalid request.") });
        }
    }

    private sealed class SuccessfulRequestHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _events;
        private int _requestCount;
        public int RequestCount => _requestCount;

        public SuccessfulRequestHandler()
        {
            using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "polymarket-events.json")));
            _events = document.RootElement.GetProperty("events").EnumerateArray()
                .ToDictionary(item => item.GetProperty("slug").GetString()!, item => item.GetRawText());
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requestCount);
            string slug = Uri.UnescapeDataString(request.RequestUri!.Segments[^1]);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_events[slug], Encoding.UTF8, "application/json")
            });
        }
    }
}
