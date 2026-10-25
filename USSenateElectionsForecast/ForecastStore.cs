using System.Globalization;
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

/// <summary>Keeps dated, validated snapshots alongside the user's saved selection.</summary>
public sealed class ForecastStore(string dataDirectory, string bundledDataDirectory)
{
    public string DataDirectory { get; } = Path.GetFullPath(dataDirectory);
    public string BundledDataDirectory { get; } = Path.GetFullPath(bundledDataDirectory);
    public string SettingsPath => Path.Combine(DataDirectory, "settings.json");
    public string LegacySnapshotPath => Path.Combine(DataDirectory, "polymarket_predictions.json");
    public string PreviousLegacySnapshotPath => Path.Combine(DataDirectory, "predictions.json");

    public string GetSnapshotPath(string fileName)
    {
        if (!PredictionFileNames.TryParse(fileName, out _))
            throw new ArgumentException("Prediction files must be named polymarket_predictions_YYYYMMDD.json.", nameof(fileName));
        return Path.Combine(DataDirectory, fileName);
    }

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

    public IReadOnlyList<PredictionFileOption> ListSnapshots(Action<string>? warn = null) =>
        FindSnapshots(warn).Select(x => new PredictionFileOption(x.FileName, PredictionFileNames.Date(x.FileName))).ToArray();

    public PredictionSnapshot LoadSnapshot(Action<string>? warn = null) => LoadPrediction(warn: warn).Snapshot;

    public StoredPrediction LoadPrediction(string? selectedFileName = null, Action<string>? warn = null, bool fallbackToLatest = true)
    {
        if (selectedFileName is not null)
        {
            if (!PredictionFileNames.TryParse(selectedFileName, out _) && !PredictionFileNames.TryParseLegacy(selectedFileName, out _))
                throw new ArgumentException("Prediction files must be named polymarket_predictions_YYYYMMDD.json.", nameof(selectedFileName));
            selectedFileName = PredictionFileNames.Normalize(selectedFileName);
        }

        var snapshots = FindSnapshots(warn);
        if (selectedFileName is not null)
        {
            var selected = snapshots.FirstOrDefault(x => x.FileName == selectedFileName);
            if (selected is not null) return selected;
            string message = $"Selected predictions file {selectedFileName} is missing or invalid.";
            if (!fallbackToLatest) throw new InvalidDataException(message);
            warn?.Invoke($"{message} Using Latest available.");
        }

        return snapshots.FirstOrDefault() ?? throw new InvalidDataException(
            "No valid prediction snapshot is available. Use Configure > Update Predictions from Polymarket, " +
            "or restore a Data/polymarket_predictions_YYYYMMDD.json file from the application distribution.");
    }

    public void SaveSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        WriteAtomically(SettingsPath, settings);
    }

    public string SaveSnapshot(PredictionSnapshot snapshot)
    {
        SnapshotValidator.Validate(snapshot);
        string fileName = PredictionFileNames.ForSnapshot(snapshot);
        WriteAtomically(GetSnapshotPath(fileName), snapshot);
        return fileName;
    }

    private IReadOnlyList<StoredPrediction> FindSnapshots(Action<string>? warn)
    {
        var snapshots = new Dictionary<string, StoredPrediction>(StringComparer.Ordinal);
        var legacyFallbacks = new[] { LegacySnapshotPath, PreviousLegacySnapshotPath }
            .Select(path => MigrateLegacySnapshot(path, warn)).OfType<StoredPrediction>().ToArray();
        AddSnapshots(DataDirectory, snapshots, warn, migrateLegacyNames: true);
        foreach (var fallback in legacyFallbacks) snapshots.TryAdd(fallback.FileName, fallback);
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(DataDirectory, BundledDataDirectory, pathComparison))
            AddSnapshots(BundledDataDirectory, snapshots, warn, migrateLegacyNames: false);

        return snapshots.Values.OrderByDescending(x => PredictionFileNames.Date(x.FileName)).ToArray();
    }

    private void AddSnapshots(string directory, Dictionary<string, StoredPrediction> snapshots,
        Action<string>? warn, bool migrateLegacyNames)
    {
        string[] paths;
        try
        {
            if (!Directory.Exists(directory)) return;
            paths = Directory.GetFiles(directory, "polymarket_predictions_*.json")
                .Concat(Directory.GetFiles(directory, "predictions_*.json")).ToArray();
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            warn?.Invoke($"Cannot list predictions in {directory}: {ex.Message}");
            return;
        }

        foreach (string path in paths)
        {
            string originalFileName = Path.GetFileName(path);
            bool legacyName = PredictionFileNames.TryParseLegacy(originalFileName, out _);
            if (!PredictionFileNames.TryParse(originalFileName, out _) && !legacyName)
            {
                warn?.Invoke($"Cannot use predictions file {originalFileName}: expected polymarket_predictions_YYYYMMDD.json with a valid date.");
                continue;
            }
            string fileName = PredictionFileNames.Normalize(originalFileName);
            if (snapshots.ContainsKey(fileName)) continue;
            try
            {
                var snapshot = ReadSnapshot(path);
                if (legacyName && migrateLegacyNames)
                {
                    string canonicalPath = GetSnapshotPath(fileName);
                    if (!File.Exists(canonicalPath))
                    {
                        try
                        {
                            byte[] bytes = File.ReadAllBytes(path);
                            WriteAtomically(canonicalPath, stream => stream.Write(bytes), overwrite: false);
                        }
                        catch (Exception ex) when (IsStorageError(ex))
                        {
                            warn?.Invoke($"Cannot rename legacy predictions file {originalFileName}: {ex.Message} The original file remains usable.");
                        }
                    }
                }
                snapshots.Add(fileName, new StoredPrediction(fileName, snapshot));
            }
            catch (Exception ex) when (IsStorageError(ex))
            {
                warn?.Invoke($"Cannot use predictions file {path}: {ex.Message} Skipping this file.");
            }
        }
    }

    private StoredPrediction? MigrateLegacySnapshot(string legacyPath, Action<string>? warn)
    {
        if (!File.Exists(legacyPath)) return null;
        StoredPrediction? legacy = null;
        try
        {
            byte[] bytes = File.ReadAllBytes(legacyPath);
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var snapshot = JsonSerializer.Deserialize<PredictionSnapshot>(reader.ReadToEnd(), ForecastJson.Options)
                ?? throw new InvalidDataException($"{Path.GetFileName(legacyPath)} contains no data.");
            SnapshotValidator.Validate(snapshot);
            string fileName = PredictionFileNames.ForSnapshot(snapshot);
            string path = GetSnapshotPath(fileName);
            if (File.Exists(path)) return null;
            legacy = new StoredPrediction(fileName, snapshot);
            WriteAtomically(path, stream => stream.Write(bytes), overwrite: false);
            return null;
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            warn?.Invoke($"Cannot archive legacy {Path.GetFileName(legacyPath)}: {ex.Message} The original file has been retained.");
            return legacy;
        }
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

    private void WriteAtomically<T>(string path, T value) =>
        WriteAtomically(path, stream => JsonSerializer.Serialize(stream, value, ForecastJson.Options));

    private void WriteAtomically(string path, Action<Stream> write, bool overwrite = true)
    {
        Directory.CreateDirectory(DataDirectory);
        var temporaryPath = Path.Combine(DataDirectory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                write(stream);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite);
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

internal static class PredictionFileNames
{
    private const string Prefix = "polymarket_predictions_";
    private const string LegacyPrefix = "predictions_";
    private const string Suffix = ".json";

    public static bool TryParse(string? fileName, out DateOnly date) => TryParse(fileName, Prefix, out date);

    public static bool TryParseLegacy(string? fileName, out DateOnly date) => TryParse(fileName, LegacyPrefix, out date);

    private static bool TryParse(string? fileName, string prefix, out DateOnly date)
    {
        date = default;
        return fileName is not null && fileName.Length == prefix.Length + 8 + Suffix.Length
            && fileName.StartsWith(prefix, StringComparison.Ordinal)
            && fileName.EndsWith(Suffix, StringComparison.Ordinal)
            && DateOnly.TryParseExact(fileName.AsSpan(prefix.Length, 8), "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out date);
    }

    public static string Normalize(string fileName) => TryParseLegacy(fileName, out var date)
        ? $"{Prefix}{date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}{Suffix}"
        : fileName;

    public static DateOnly Date(string fileName)
    {
        TryParse(fileName, out var date);
        return date;
    }

    public static string ForSnapshot(PredictionSnapshot snapshot) =>
        $"{Prefix}{snapshot.RetrievedAt.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}{Suffix}";
}
