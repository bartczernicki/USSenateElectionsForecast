using System.Globalization;
using System.Net;
using System.Text.Json;
using USSenateElectionsForecast.Markets;
using USSenateElectionsForecast.Markets.Polymarket;

namespace USSenateElectionsForecast.Markets.Kalshi;

/// <summary>Downloads public Kalshi Senate quotes without credentials.</summary>
public sealed class KalshiClient(HttpClient httpClient)
{
    public const string EventsBaseUrl = "https://external-api.kalshi.com/trade-api/v2/events/";

    public async Task<PredictionSnapshot> FetchAsync(CancellationToken cancellationToken = default)
    {
        using var concurrency = new SemaphoreSlim(4);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var requests = KalshiRaceCatalog.All.Select(async definition =>
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
        SnapshotValidator.Validate(snapshot, PredictionSource.Kalshi);
        return snapshot;
    }

    private async Task<RacePrediction> FetchRaceAsync(RaceDefinition definition, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var response = await httpClient.GetAsync(EventsBaseUrl + Uri.EscapeDataString(definition.EventSlug), cancellationToken);
            if ((response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500) && attempt < 3)
            {
                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromMilliseconds(300 * attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 0, 5000)), cancellationToken);
                continue;
            }
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Kalshi could not update {definition.State}: HTTP {(int)response.StatusCode} ({response.ReasonPhrase}).", null, response.StatusCode);
            string json = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseRace(json, definition);
        }
    }

    public static RacePrediction ParseRace(string json, RaceDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("event", out var sourceEvent) || sourceEvent.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Kalshi returned no event for {definition.State}.");
            if (RequiredString(sourceEvent, "event_ticker") != definition.EventSlug)
                throw new InvalidDataException($"Kalshi returned the wrong event for {definition.State}.");
            string title = RequiredString(sourceEvent, "title");
            string subtitle = RequiredString(sourceEvent, "sub_title");
            if (!title.Contains(definition.State, StringComparison.OrdinalIgnoreCase)
                || !title.Contains("Senate", StringComparison.OrdinalIgnoreCase)
                || !subtitle.Contains("2026", StringComparison.Ordinal)
                || RequiredString(sourceEvent, "category") != "Elections")
                throw new InvalidDataException($"The {definition.State} event does not identify the expected 2026 Senate election.");
            if (!sourceEvent.TryGetProperty("mutually_exclusive", out var exclusive) || exclusive.ValueKind != JsonValueKind.True)
                throw new InvalidDataException($"The {definition.State} Kalshi outcomes must be mutually exclusive.");
            if (!root.TryGetProperty("markets", out var markets) || markets.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException($"The {definition.State} Kalshi event has no market array.");

            var predictions = new List<MarketPrediction>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var market in markets.EnumerateArray())
            {
                string ticker = RequiredString(market, "ticker");
                var expected = definition.Markets.SingleOrDefault(x => x.MarketId == ticker)
                    ?? throw new InvalidDataException($"Unexpected Kalshi contract {ticker} in {definition.State}. Review the race catalog before updating.");
                if (!seen.Add(ticker) || RequiredString(market, "event_ticker") != definition.EventSlug)
                    throw new InvalidDataException($"Duplicate or changed Kalshi contract {ticker} in {definition.State}.");
                if (RequiredString(market, "market_type") != "binary" || RequiredString(market, "status") != "active")
                    throw new InvalidDataException($"Kalshi contract {ticker} is not an active binary market.");
                string rules = RequiredString(market, "rules_primary");
                string electionRule = definition.IsSpecial
                    ? "Class III (special election) held on November 3, 2026"
                    : "term beginning in 2027";
                bool partyMatches = expected.Caucus switch
                {
                    Caucus.Democratic => rules.Contains("Democratic party", StringComparison.OrdinalIgnoreCase)
                        || (definition.State == "Minnesota" && rules.Contains("Democratic (DFL) party", StringComparison.OrdinalIgnoreCase)),
                    Caucus.Republican => rules.Contains("Republican party", StringComparison.OrdinalIgnoreCase),
                    _ => true
                };
                if (!rules.Contains($"Senator of {definition.State}", StringComparison.OrdinalIgnoreCase)
                    || !rules.Contains(electionRule, StringComparison.OrdinalIgnoreCase)
                    || !partyMatches)
                    throw new InvalidDataException($"Kalshi contract {ticker} does not identify the expected Senate term and outcome.");

                double bid = ReadPrice(market, "yes_bid_dollars");
                double ask = ReadPrice(market, "yes_ask_dollars");
                if (bid > ask || ask == 0)
                    throw new InvalidDataException($"Kalshi contract {ticker} has crossed or empty Yes quotes. Current bid and ask prices are required.");
                string? label = OptionalString(market, "yes_sub_title") ?? OptionalString(market, "title");
                if (string.IsNullOrWhiteSpace(label))
                    throw new InvalidDataException($"Kalshi contract {ticker} has no outcome label.");
                predictions.Add(new(ticker, expected.MarketSlug, label.Trim(), expected.Caucus,
                    (bid + ask) / 2, 0, OptionalDate(market, "updated_time")));
            }

            double rawSum = predictions.Sum(x => x.RawYesPrice);
            SnapshotValidator.ValidateRawSum(rawSum, definition.State);
            var normalized = predictions.Select(x => x with { Probability = x.RawYesPrice / rawSum }).ToArray();
            var race = new RacePrediction(definition.State, definition.EventSlug, definition.IsSpecial,
                OptionalDate(sourceEvent, "last_updated_ts"), normalized);
            SnapshotValidator.ValidateRace(race, definition);
            return race;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            throw new InvalidDataException($"Kalshi returned invalid JSON for {definition.State}.", exception);
        }
    }

    private static double ReadPrice(JsonElement element, string name)
    {
        string text = RequiredString(element, name);
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            || !double.IsFinite(value) || value is < 0 or > 1)
            throw new InvalidDataException($"Kalshi {name} must be a finite dollar price between 0 and 1.");
        return value;
    }

    private static string RequiredString(JsonElement element, string name) =>
        OptionalString(element, name) is { } value && !string.IsNullOrWhiteSpace(value) ? value
            : throw new InvalidDataException($"Kalshi data is missing {name}.");

    private static string? OptionalString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString() : null;

    private static DateTimeOffset? OptionalDate(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date))
            return date == default ? null : date;
        throw new InvalidDataException($"Kalshi returned an invalid {name} timestamp.");
    }
}
