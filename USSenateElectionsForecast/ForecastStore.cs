using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace USSenateElectionsForecast;

public static class ForecastJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };
}

/// <summary>Keeps a complete usable snapshot until a replacement is validated and saved.</summary>
public sealed class ForecastStore(string dataDirectory, string bundledSnapshotPath)
{
    public string DataDirectory { get; } = Path.GetFullPath(dataDirectory);
    public string BundledSnapshotPath { get; } = Path.GetFullPath(bundledSnapshotPath);
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public string SnapshotPath => Path.Combine(DataDirectory, "predictions.json");

    public AppSettings LoadSettings(Action<string>? warn = null)
    {
        if (!File.Exists(SettingsPath)) return new();
        try
        {
            var settings = Read<AppSettings>(SettingsPath);
            settings.Validate();
            return settings;
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            warn?.Invoke($"Cannot load saved settings: {ex.Message} Using rho 0.12 and 100,000 trials.");
            return new();
        }
    }

    public PredictionSnapshot LoadSnapshot(Action<string>? warn = null)
    {
        if (File.Exists(SnapshotPath))
        {
            try { return ReadSnapshot(SnapshotPath); }
            catch (Exception ex) when (IsStorageError(ex))
            {
                warn?.Invoke($"Cannot use saved predictions: {ex.Message} Loading the bundled snapshot.");
            }
        }

        try { return ReadSnapshot(BundledSnapshotPath); }
        catch (Exception ex) when (IsStorageError(ex))
        {
            throw new InvalidDataException(
                "No valid prediction snapshot is available. Use Configure > Update Predictions from Polymarket, " +
                "or restore Data/bundled-predictions.json from the application distribution.", ex);
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        WriteAtomically(SettingsPath, settings);
    }

    public void SaveSnapshot(PredictionSnapshot snapshot)
    {
        SnapshotValidator.Validate(snapshot);
        WriteAtomically(SnapshotPath, snapshot);
    }

    private static PredictionSnapshot ReadSnapshot(string path)
    {
        var snapshot = Read<PredictionSnapshot>(path);
        SnapshotValidator.Validate(snapshot);
        return snapshot;
    }

    private static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path, Encoding.UTF8), ForecastJson.Options)
        ?? throw new InvalidDataException($"{Path.GetFileName(path)} contains no data.");

    private void WriteAtomically<T>(string path, T value)
    {
        Directory.CreateDirectory(DataDirectory);
        var temporaryPath = Path.Combine(DataDirectory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, value, ForecastJson.Options);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool IsStorageError(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or ArgumentException;
}
