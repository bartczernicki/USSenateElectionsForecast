using System.Globalization;
using System.Net;
using System.Text.Json;

namespace USSenateElectionsForecast.Markets.Polymarket;

public sealed class PolymarketClient(HttpClient httpClient)
{
    public async Task<PredictionSnapshot> FetchAsync(CancellationToken cancellationToken = default)
    {
        using var concurrency = new SemaphoreSlim(4);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var requests = PolymarketCatalog.All.Select(async definition =>
        {
            try
            {
                await concurrency.WaitAsync(linkedCancellation.Token);
                try { return await FetchRaceAsync(definition, linkedCancellation.Token); }
                finally { concurrency.Release(); }
            }
            catch
            {
                await linkedCancellation.CancelAsync();
                throw;
            }
        });
        var races = await Task.WhenAll(requests);
        var snapshot = new PredictionSnapshot(PolymarketCatalog.SchemaVersion, PolymarketCatalog.ElectionYear, DateTimeOffset.UtcNow, races);
        SnapshotValidator.Validate(snapshot, PredictionSource.Polymarket);
        return snapshot;
    }

    private async Task<RacePrediction> FetchRaceAsync(PolymarketRaceDefinition definition, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var response = await httpClient.GetAsync(definition.SourceUrl, cancellationToken);
            if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt < 3)
            {
                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(300 * attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 0, 5000)), cancellationToken);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Polymarket could not update {definition.State}: HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).", null, response.StatusCode);
            var json = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseRace(json, definition);
        }
    }

    public static RacePrediction ParseRace(string json, PolymarketRaceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (RequiredString(root, "slug") != definition.EventSlug)
                throw new InvalidDataException($"Polymarket returned the wrong event for {definition.State}.");
            var description = RequiredString(root, "description");
            if (!description.Contains("2026", StringComparison.Ordinal) || !description.Contains(definition.State, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"The {definition.State} event does not identify the expected 2026 Senate election.");
            if (!root.TryGetProperty("markets", out var markets) || markets.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"The {definition.State} event has no market array.");

            var predictions = new List<MarketPrediction>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var market in markets.EnumerateArray())
            {
                if (!Flag(market, "active") && !Flag(market, "closed"))
                    continue; // Gamma reserves inactive, unpriced candidate placeholders.
                var id = RequiredString(market, "id");
                var expected = definition.Markets.SingleOrDefault(x => x.MarketId == id)
                    ?? throw new InvalidDataException($"Unexpected active contract {id} in {definition.State}. Review the race catalog before updating.");
                if (!seen.Add(id) || RequiredString(market, "slug") != expected.MarketSlug)
                    throw new InvalidDataException($"Duplicate or changed contract {id} in {definition.State}.");

                using var outcomesDocument = ArrayDocument(market, "outcomes");
                using var pricesDocument = ArrayDocument(market, "outcomePrices");
                var outcomes = outcomesDocument.RootElement.EnumerateArray().ToArray();
                var prices = pricesDocument.RootElement.EnumerateArray().ToArray();
                if (outcomes.Length != 2 || prices.Length != 2)
                    throw new InvalidDataException($"Market {id} must contain exactly two Yes/No outcomes and prices.");
                var labels = outcomes.Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null).ToArray();
                var yesIndex = Array.FindIndex(labels, x => string.Equals(x, "Yes", StringComparison.OrdinalIgnoreCase));
                var noIndex = Array.FindIndex(labels, x => string.Equals(x, "No", StringComparison.OrdinalIgnoreCase));
                if (yesIndex < 0 || noIndex < 0 || yesIndex == noIndex)
                    throw new InvalidDataException($"Market {id} does not have distinct Yes/No outcomes.");
                var numbers = prices.Select(ReadPrice).ToArray();
                if (Math.Abs(numbers.Sum() - 1) > 1e-6)
                    throw new InvalidDataException($"Market {id} has inconsistent Yes/No prices.");
                var label = OptionalString(market, "groupItemTitle") ?? OptionalString(market, "question");
                if (string.IsNullOrWhiteSpace(label))
                    throw new InvalidDataException($"Market {id} has no outcome label.");
                predictions.Add(new(id, expected.MarketSlug, label.Trim(), expected.Caucus, numbers[yesIndex], 0, OptionalDate(market, "updatedAt")));
            }

            var rawSum = predictions.Sum(x => x.RawYesPrice);
            SnapshotValidator.ValidateRawSum(rawSum, definition.State);
            var normalized = predictions.Select(x => x with { Probability = x.RawYesPrice / rawSum }).ToArray();
            var race = new RacePrediction(definition.State, definition.EventSlug, definition.IsSpecial, OptionalDate(root, "updatedAt"), normalized);
            SnapshotValidator.ValidateRace(race, definition);
            return race;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException($"Polymarket returned invalid JSON for {definition.State}.", exception);
        }
    }

    private static JsonDocument ArrayDocument(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
            throw new InvalidDataException($"Polymarket market is missing {name}.");
        var document = property.ValueKind switch
        {
            JsonValueKind.String => JsonDocument.Parse(property.GetString()!),
            JsonValueKind.Array => JsonDocument.Parse(property.GetRawText()),
            _ => throw new InvalidDataException($"Polymarket {name} must be a JSON array or an encoded JSON array.")
        };
        if (document.RootElement.ValueKind == JsonValueKind.Array)
            return document;
        document.Dispose();
        throw new InvalidDataException($"Polymarket {name} does not contain an array.");
    }

    private static double ReadPrice(JsonElement element)
    {
        double value;
        if (element.ValueKind == JsonValueKind.Number)
        {
            if (!element.TryGetDouble(out value))
                throw new InvalidDataException("Polymarket returned an invalid numeric price.");
        }
        else if (element.ValueKind != JsonValueKind.String || !double.TryParse(element.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            throw new InvalidDataException("Polymarket returned an invalid price.");
        if (!double.IsFinite(value) || value is < 0 or > 1)
            throw new InvalidDataException("Polymarket prices must be finite values between 0 and 1.");
        return value;
    }

    private static bool Flag(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static string RequiredString(JsonElement element, string name) => OptionalString(element, name)
        ?? throw new InvalidDataException($"Polymarket data is missing {name}.");
    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;

    private static DateTimeOffset? OptionalDate(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date))
            return date;
        throw new InvalidDataException($"Polymarket returned an invalid {name} timestamp.");
    }
}
