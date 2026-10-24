using System.Globalization;
using System.Text.Json;

namespace USSenateElectionsForecast;

public sealed class ConsoleApp(
    TextReader input,
    TextWriter output,
    ForecastStore store,
    PolymarketClient client,
    SimulationEngine engine,
    ConsolePresentation presentation)
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
                ["Independent Monte Carlo Simulation", "Correlated Monte Carlo Simulation", "Configure"], "Exit");
            int? selection = ReadSelection(3);
            if (selection is null or 0) return;
            if (selection == 3)
            {
                if (!await ConfigureAsync(cancellationToken)) return;
                continue;
            }
            if (_snapshot is null)
            {
                presentation.Warning("Simulation is unavailable until predictions are loaded. Select Configure > Update Predictions from Polymarket.");
            }
            else
            {
                output.WriteLine("Running simulation...");
                var mode = selection == 1 ? SimulationMode.Independent : SimulationMode.Correlated;
                try
                {
                    var result = await Task.Run(() => engine.Run(_snapshot, mode, _settings.TrialCount,
                        _settings.Rho, cancellationToken: cancellationToken), cancellationToken);
                    presentation.Results(result);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
                catch (Exception error) when (error is InvalidDataException or ArgumentException)
                {
                    presentation.Error($"Simulation could not run: {error.Message} Update Predictions from Polymarket and try again.");
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
                ["Update Predictions from Polymarket", "Configure Rho (Correlation)", "Configure Simulation Count"], "Back");
            int? selection = ReadSelection(3);
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
            else if (!ConfigureValue(selection == 2)) return false;
        }
        return false;
    }

    private bool ConfigureValue(bool rho)
    {
        while (true)
        {
            output.Write(rho
                ? $"Enter rho from 0.00 to 0.99 (current {_settings.Rho.ToString("0.00############", CultureInfo.InvariantCulture)}; blank keeps current): "
                : $"Enter simulation count from 1,000 to 10,000,000 (current {_settings.TrialCount.ToString("N0", CultureInfo.InvariantCulture)}; blank keeps current): ");
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
