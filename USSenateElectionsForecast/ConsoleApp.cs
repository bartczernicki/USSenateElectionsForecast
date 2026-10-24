using System.Globalization;
using System.Text.Json;

namespace USSenateElectionsForecast;

public sealed class ConsoleApp(
    TextReader input,
    TextWriter output,
    ForecastStore store,
    PolymarketClient client,
    SimulationEngine engine,
    ConsolePresentation presentation,
    ProbabilityCalculationEngine probabilityEngine)
{
    private AppSettings _settings = new();
    private PredictionSnapshot? _snapshot;

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        presentation.Banner();
        _settings = store.LoadSettings(presentation.Warning);
        try { _snapshot = store.LoadSnapshot(presentation.Warning); }
        catch (Exception error) when (IsStorageError(error))
        {
            presentation.Error($"Could not load predictions: {error.Message}");
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            presentation.Context(_settings, _snapshot);
            presentation.Menu(null,
                ["Independent Monte Carlo Simulation", "Correlated Monte Carlo Simulation", "Conditional Monte Carlo", "Direct Probability Calculation", "Configure"], "Exit");
            presentation.MethodAgreementNote();
            int? selection = ReadSelection(5);
            if (selection is null or 0) return;
            if (selection == 5)
            {
                if (!await ConfigureAsync(cancellationToken)) return;
                continue;
            }
            if (_snapshot is null)
            {
                presentation.Warning("Forecast calculation is unavailable until predictions are loaded. Select Configure > Update Predictions from Polymarket.");
            }
            else
            {
                try
                {
                    if (selection is 1 or 2)
                    {
                        output.WriteLine("Running simulation...");
                        var mode = selection == 1 ? SimulationMode.Independent : SimulationMode.Correlated;
                        var result = await Task.Run(() => engine.Run(_snapshot, mode, _settings.TrialCount,
                            _settings.Rho, cancellationToken: cancellationToken), cancellationToken);
                        presentation.Results(result);
                    }
                    else if (selection == 3)
                    {
                        output.WriteLine("Running conditional Monte Carlo...");
                        var result = await Task.Run(() => probabilityEngine.RunConditional(_snapshot,
                            _settings.TrialCount, _settings.Rho, cancellationToken: cancellationToken), cancellationToken);
                        presentation.Results(result);
                    }
                    else
                    {
                        output.WriteLine("Calculating independent direct probabilities...");
                        var independent = await Task.Run(() => probabilityEngine.CalculateIndependent(_snapshot,
                            cancellationToken), cancellationToken);
                        presentation.Results(independent);
                        output.WriteLine();
                        output.WriteLine("Calculating correlated direct probabilities...");
                        var correlated = await Task.Run(() => probabilityEngine.CalculateCorrelated(_snapshot,
                            _settings.Rho, cancellationToken), cancellationToken);
                        presentation.Results(correlated);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (NumericalConvergenceException error)
                {
                    presentation.Error($"Correlated direct calculation did not converge: {error.Message}");
                    presentation.Warning("The independent results above remain available. Try Correlated Monte Carlo or Conditional Monte Carlo for this rho.");
                }
                catch (ArithmeticException error)
                {
                    presentation.Error($"Forecast calculation failed numerically: {error.Message}");
                    presentation.Warning("Try another calculation method. Your predictions and settings are unchanged.");
                }
                catch (Exception error) when (error is InvalidDataException or ArgumentException)
                {
                    presentation.Error($"Forecast calculation could not run: {error.Message} Update Predictions from Polymarket and try again.");
                }
            }
            if (!Pause()) return;
        }
    }

    private async Task<bool> ConfigureAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            presentation.Context(_settings, _snapshot);
            presentation.Menu("Configure",
                ["Update Predictions from Polymarket", "Configure Rho (Correlation)", "Configure Simulation Count", "List Current US Senate Polymarket Probabilities"], "Back");
            int? selection = ReadSelection(4);
            if (selection is null) return false;
            if (selection == 0) return true;
            if (selection == 1)
            {
                output.WriteLine("Updating all 35 Senate races from Polymarket...");
                try
                {
                    PredictionSnapshot updated = await client.FetchAsync(cancellationToken);
                    store.SaveSnapshot(updated);
                    _snapshot = updated;
                    presentation.Success($"Updated {updated.Races.Count} races. Predictions have been saved.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return false; }
                catch (Exception error) when (IsStorageError(error) || error is HttpRequestException or OperationCanceledException or ArgumentException)
                {
                    presentation.Error($"Could not update predictions: {error.Message}");
                    presentation.Warning("The previous predictions are still selected. Check your connection and data-folder permissions, then try again.");
                }
                if (!Pause()) return false;
            }
            else if (selection == 4)
            {
                if (_snapshot is not null) presentation.Predictions(_snapshot);
                else presentation.Warning("Predictions unavailable. Use Configure > Update Predictions from Polymarket to load them.");
                if (!Pause()) return false;
            }
            else if (!ConfigureValue(selection == 2)) return false;
        }
        return false;
    }

    private bool ConfigureValue(bool rho)
    {
        while (true)
        {
            output.WriteLine(rho
                ? "Rho range: 0.00 to 0.99. Blank keeps the current value."
                : "Simulation count range: 1,000 to 10,000,000. Blank keeps the current value.");
            if (!rho) output.WriteLine("This count sets election trials or conditional national-factor samples.");
            output.Write(rho
                ? $"Enter rho (current {_settings.Rho.ToString("0.00############", CultureInfo.InvariantCulture)}): "
                : $"Enter simulation count (current {_settings.TrialCount.ToString("N0", CultureInfo.InvariantCulture)}): ");
            string? text = input.ReadLine()?.Trim();
            if (text is null) return false;
            if (text.Length == 0) return true;

            AppSettings updated;
            if (rho)
            {
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
                    !double.IsFinite(value) || value is < 0 or > 0.99)
                {
                    presentation.Warning("Enter a finite number between 0.00 and 0.99, using a decimal point.");
                    continue;
                }
                updated = _settings with { Rho = value };
            }
            else
            {
                if (!int.TryParse(text, NumberStyles.Integer | NumberStyles.AllowThousands,
                    CultureInfo.InvariantCulture, out int value) || value is < 1_000 or > 10_000_000
                    || text.Contains(',') && text != value.ToString("N0", CultureInfo.InvariantCulture))
                {
                    presentation.Warning("Enter a whole number between 1,000 and 10,000,000.");
                    continue;
                }
                updated = _settings with { TrialCount = value };
            }

            try
            {
                store.SaveSettings(updated);
                _settings = updated;
                presentation.Success("Configuration saved.");
            }
            catch (Exception error) when (IsStorageError(error))
            {
                presentation.Error($"Could not save configuration: {error.Message}");
                presentation.Warning("Check your data-folder permissions and try again. The current configuration is unchanged.");
            }
            return true;
        }
    }

    private int? ReadSelection(int maximum)
    {
        while (true)
        {
            output.Write("Select an option: ");
            string? text = input.ReadLine()?.Trim();
            if (text is null) return null;
            if (int.TryParse(text, out int selection) && selection >= 0 && selection <= maximum) return selection;
            presentation.Warning($"Please enter a number from 0 to {maximum}.");
        }
    }

    private bool Pause()
    {
        output.Write("Press Enter to return to the menu: ");
        return input.ReadLine() is not null;
    }

    private static bool IsStorageError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException;
}
