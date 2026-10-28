using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using USSenateElectionsForecast.Markets;
using USSenateElectionsForecast.Markets.Kalshi;
using USSenateElectionsForecast.Markets.Polymarket;

namespace USSenateElectionsForecast.Tests;

[TestClass]
public sealed class KalshiClientTests
{
    public TestContext TestContext { get; set; } = null!;
    private static RaceDefinition Texas => KalshiRaceCatalog.All.Single(x => x.State == "Texas");

    [TestMethod]
    public void CatalogCoversEquivalentRacesSpecialElectionsAndVerifiedIndependentContracts()
    {
        Assert.HasCount(35, KalshiRaceCatalog.All);
        CollectionAssert.AreEquivalent(PolymarketCatalog.All.Select(x => x.State).ToArray(), KalshiRaceCatalog.All.Select(x => x.State).ToArray());
        CollectionAssert.AreEquivalent(new[] { "Florida", "Ohio" }, KalshiRaceCatalog.All.Where(x => x.IsSpecial).Select(x => x.State).ToArray());
        Assert.HasCount(74, KalshiRaceCatalog.All.SelectMany(x => x.Markets).ToArray());
        Assert.HasCount(74, KalshiRaceCatalog.All.SelectMany(x => x.Markets).Select(x => x.MarketId).Distinct().ToArray());
        Assert.IsTrue(KalshiRaceCatalog.All.SelectMany(x => x.Markets).All(x => x.MarketId == x.MarketSlug));
        CollectionAssert.AreEquivalent(new[] { "Idaho", "Montana", "Nebraska", "South Dakota" },
            KalshiRaceCatalog.All.Where(x => x.Markets.Any(m => m.Caucus == Caucus.Unknown)).Select(x => x.State).ToArray());
        Assert.AreEqual("SENATELA-26", KalshiRaceCatalog.All.Single(x => x.State == "Kentucky").EventSlug);
        Assert.AreEqual("KXSENATELA-26NOV", KalshiRaceCatalog.All.Single(x => x.State == "Louisiana").EventSlug);
        Assert.AreEqual("SENATEFLS-26", KalshiRaceCatalog.All.Single(x => x.State == "Florida").EventSlug);
        Assert.AreEqual("SENATEOHS-26", KalshiRaceCatalog.All.Single(x => x.State == "Ohio").EventSlug);
    }

    [TestMethod]
    public void CapturedResponsesPreserveEverySourceMidquoteAndExplicitCaucusMapping()
    {
        foreach (var definition in KalshiRaceCatalog.All)
        {
            var source = Event(definition);
            var parsed = KalshiClient.ParseRace(source.ToJsonString(), definition);
            Assert.HasCount(definition.Markets.Count, parsed.Outcomes);
            Assert.AreEqual(1, parsed.Outcomes.Sum(x => x.Probability), 1e-12);
            Assert.IsNull(parsed.SourceUpdatedAt); // Kalshi currently returns a year-1 sentinel for event updates.
            foreach (var outcome in parsed.Outcomes)
            {
                var market = source["markets"]!.AsArray().Single(x => x!["ticker"]!.GetValue<string>() == outcome.MarketId)!;
                double bid = double.Parse(market["yes_bid_dollars"]!.GetValue<string>(), CultureInfo.InvariantCulture);
                double ask = double.Parse(market["yes_ask_dollars"]!.GetValue<string>(), CultureInfo.InvariantCulture);
                Assert.AreEqual((bid + ask) / 2, outcome.RawYesPrice, 1e-12);
                Assert.AreEqual(outcome.RawYesPrice / parsed.Outcomes.Sum(x => x.RawYesPrice), outcome.Probability, 1e-12);
                Assert.AreEqual(definition.Markets.Single(x => x.MarketId == outcome.MarketId).Caucus, outcome.Caucus);
                Assert.AreEqual(market["yes_sub_title"]!.GetValue<string>(), outcome.Label);
                Assert.IsNotNull(outcome.UpdatedAt);
            }
        }
    }

    [TestMethod]
    public void BundledKalshiSnapshotRetainsTheExistingJsonSchemaAndNativeIdentifiers()
    {
        string json = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "kalshi_predictions_20261028.json"));
        var snapshot = JsonSerializer.Deserialize<PredictionSnapshot>(json, ForecastJson.Options)!;
        SnapshotValidator.Validate(snapshot, PredictionSource.Kalshi);
        Assert.AreEqual(1, snapshot.SchemaVersion);
        Assert.AreEqual(2026, snapshot.ElectionYear);
        Assert.AreEqual(new DateOnly(2026, 10, 28), DateOnly.FromDateTime(snapshot.RetrievedAt.UtcDateTime));
        Assert.HasCount(35, snapshot.Races);
        Assert.HasCount(74, snapshot.Races.SelectMany(x => x.Outcomes).ToArray());

        using var document = JsonDocument.Parse(json);
        CollectionAssert.AreEquivalent(new[] { "schemaVersion", "electionYear", "retrievedAt", "races" },
            document.RootElement.EnumerateObject().Select(x => x.Name).ToArray());
        foreach (var race in document.RootElement.GetProperty("races").EnumerateArray())
        {
            CollectionAssert.AreEquivalent(new[] { "state", "eventSlug", "isSpecial", "sourceUpdatedAt", "outcomes" },
                race.EnumerateObject().Select(x => x.Name).ToArray());
            var definition = KalshiRaceCatalog.All.Single(x => x.State == race.GetProperty("state").GetString());
            Assert.AreEqual(definition.EventSlug, race.GetProperty("eventSlug").GetString());
            foreach (var outcome in race.GetProperty("outcomes").EnumerateArray())
            {
                CollectionAssert.AreEquivalent(new[] { "marketId", "marketSlug", "label", "caucus", "rawYesPrice", "probability", "updatedAt" },
                    outcome.EnumerateObject().Select(x => x.Name).ToArray());
                string ticker = outcome.GetProperty("marketId").GetString()!;
                Assert.IsTrue(definition.Markets.Any(x => x.MarketId == ticker));
                Assert.AreEqual(ticker, outcome.GetProperty("marketSlug").GetString());
                Assert.AreEqual(JsonValueKind.String, outcome.GetProperty("caucus").ValueKind);
            }
        }
    }

    [TestMethod]
    public void MidpointUsesInvariantDollarQuotesRatherThanLastTradeAndSupportsZeroBids()
    {
        var source = Event(Texas);
        SetQuotes(source, Texas.Markets[0].MarketId, "0.00", "0.02");
        SetQuotes(source, Texas.Markets[1].MarketId, "0.98", "1.00");
        source["markets"]!.AsArray().First()!["last_price_dollars"] = "0.50";
        CultureInfo previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            var parsed = KalshiClient.ParseRace(source.ToJsonString(), Texas);
            Assert.AreEqual(0.01, parsed.ProbabilityFor(Caucus.Democratic), 1e-12);
            Assert.AreEqual(0.99, parsed.ProbabilityFor(Caucus.Republican), 1e-12);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [TestMethod]
    [DataRow("-0.01", "0.5")]
    [DataRow("0.5", "1.01")]
    [DataRow("0.9", "0.8")]
    [DataRow("0", "0")]
    [DataRow("NaN", "0.5")]
    [DataRow("0.2", "Infinity")]
    [DataRow("", "0.5")]
    [DataRow("0,2", "0.5")]
    public void InvalidMissingOrCrossedQuotesAreRejected(string bid, string ask)
    {
        var source = Event(Texas);
        SetQuotes(source, Texas.Markets[0].MarketId, bid, ask);
        Assert.ThrowsExactly<InvalidDataException>(() => KalshiClient.ParseRace(source.ToJsonString(), Texas));
    }

    [TestMethod]
    public void MissingQuoteNeverFallsBackToStaleLastTrade()
    {
        var source = Event(Texas);
        source["markets"]!.AsArray().First()!.AsObject().Remove("yes_ask_dollars");
        Assert.ThrowsExactly<InvalidDataException>(() => KalshiClient.ParseRace(source.ToJsonString(), Texas));
    }

    [TestMethod]
    [DataRow(0.90, true)]
    [DataRow(1.10, true)]
    [DataRow(0.899, false)]
    [DataRow(1.101, false)]
    public void MidquoteTotalsUseTheSameRawPriceGuard(double sum, bool accepted)
    {
        var source = Event(Texas);
        foreach (var definition in Texas.Markets)
        {
            string price = (sum / 2).ToString("R", CultureInfo.InvariantCulture);
            SetQuotes(source, definition.MarketId, price, price);
        }
        if (accepted)
            Assert.AreEqual(0.5, KalshiClient.ParseRace(source.ToJsonString(), Texas).ProbabilityFor(Caucus.Democratic), 1e-12);
        else
            Assert.ThrowsExactly<InvalidDataException>(() => KalshiClient.ParseRace(source.ToJsonString(), Texas));
    }

    [TestMethod]
    public void ChangedEventYearStatePartyAndNonExclusiveOutcomesAreRejected()
    {
        foreach (var property in new[] { "event_ticker", "title", "sub_title", "category", "mutually_exclusive" })
        {
            var source = Event(Texas);
            source["event"]![property] = property switch
            {
                "event_ticker" => JsonValue.Create("SENATETX-28"),
                "title" => JsonValue.Create("Oklahoma Senate winner?"),
                "sub_title" => JsonValue.Create("In 2028"),
                "category" => JsonValue.Create("Sports"),
                _ => JsonValue.Create(false)
            };
            Assert.ThrowsExactly<InvalidDataException>(() => KalshiClient.ParseRace(source.ToJsonString(), Texas));
        }
        foreach (string rules in new[]
        {
            "If a representative of the Democratic party is sworn in as a Senator of Oklahoma for the term beginning in 2027, then the market resolves to Yes.",
            "If a representative of the Democratic party is sworn in as a Senator of Texas for the term beginning in 2029, then the market resolves to Yes.",
            "If a representative of the Republican party is sworn in as a Senator of Texas for the term beginning in 2027, then the market resolves to Yes."
        })
        {
            var source = Event(Texas);
            source["markets"]!.AsArray().Single(x => x!["ticker"]!.GetValue<string>() == Texas.Markets[0].MarketId)!["rules_primary"] = rules;
            Assert.ThrowsExactly<InvalidDataException>(() => KalshiClient.ParseRace(source.ToJsonString(), Texas));
        }
    }

    [TestMethod]
    public void MissingDuplicateUnexpectedOrInactiveContractsAreRejected()
    {
        var missing = Event(Texas);
        missing["markets"]!.AsArray().RemoveAt(0);
        Assert.ThrowsExactly<InvalidDataException>(() => KalshiClient.ParseRace(missing.ToJsonString(), Texas));
        var duplicate = Event(Texas);
        duplicate["markets"]!.AsArray().Add(duplicate["markets"]!.AsArray().First()!.DeepClone());
        Assert.ThrowsExactly<InvalidDataException>(() => KalshiClient.ParseRace(duplicate.ToJsonString(), Texas));
        var unexpected = Event(Texas);
        unexpected["markets"]!.AsArray().First()!["ticker"] = "SENATETX-26-OTHER";
        Assert.ThrowsExactly<InvalidDataException>(() => KalshiClient.ParseRace(unexpected.ToJsonString(), Texas));
        foreach (var property in new[] { "status", "market_type", "event_ticker" })
        {
            var changed = Event(Texas);
            changed["markets"]!.AsArray().First()![property] = "unexpected";
            Assert.ThrowsExactly<InvalidDataException>(() => KalshiClient.ParseRace(changed.ToJsonString(), Texas));
        }
    }

    [TestMethod]
    [DataRow("Florida")]
    [DataRow("Ohio")]
    public void SpecialElectionsRequireTheVerifiedClassAndElectionDate(string state)
    {
        var definition = KalshiRaceCatalog.All.Single(x => x.State == state);
        var source = Event(definition);
        Assert.IsTrue(KalshiClient.ParseRace(source.ToJsonString(), definition).IsSpecial);
        var market = source["markets"]!.AsArray().First()!;
        string rules = market["rules_primary"]!.GetValue<string>();
        foreach (string changed in new[]
        {
            rules.Replace("November 3, 2026", "November 3, 2028", StringComparison.Ordinal),
            rules.Replace("Class III", "Class II", StringComparison.Ordinal),
            rules.Replace("Class III (special election) held on November 3, 2026", "term beginning in 2027", StringComparison.Ordinal)
        })
        {
            market["rules_primary"] = changed;
            Assert.ThrowsExactly<InvalidDataException>(() => KalshiClient.ParseRace(source.ToJsonString(), definition));
        }
    }

    [TestMethod]
    public void LabelsFallbackToTitleAndOptionalTimestampsRemainNullable()
    {
        var source = Event(Texas);
        var market = source["markets"]!.AsArray().First()!.AsObject();
        string ticker = market["ticker"]!.GetValue<string>();
        market["yes_sub_title"] = " ";
        market["updated_time"] = null;
        source["event"]!["last_updated_ts"] = "2026-10-28T12:00:00Z";
        var parsed = KalshiClient.ParseRace(source.ToJsonString(), Texas);
        Assert.AreEqual(market["title"]!.GetValue<string>(), parsed.Outcomes.Single(x => x.MarketId == ticker).Label);
        Assert.IsNull(parsed.Outcomes.Single(x => x.MarketId == ticker).UpdatedAt);
        Assert.AreEqual(DateTimeOffset.Parse("2026-10-28T12:00:00Z", CultureInfo.InvariantCulture), parsed.SourceUpdatedAt);
        market["updated_time"] = "invalid";
        Assert.ThrowsExactly<InvalidDataException>(() => KalshiClient.ParseRace(source.ToJsonString(), Texas));
    }

    [TestMethod]
    [DataRow("[]")]
    [DataRow("null")]
    [DataRow("42")]
    [DataRow("{")]
    [DataRow("{\"event\":null}")]
    public void MalformedPayloadsProduceDataErrors(string json) =>
        Assert.ThrowsExactly<InvalidDataException>(() => KalshiClient.ParseRace(json, Texas));

    [TestMethod]
    public async Task FetchBuildsCompleteSnapshotWithoutAuthenticationAndLimitsConcurrency()
    {
        var events = FixtureDictionary();
        using var handler = new FixtureHandler(async (request, _, token) =>
        {
            Assert.AreEqual("external-api.kalshi.com", request.RequestUri!.Host);
            Assert.IsFalse(request.Headers.Any(x => x.Key.StartsWith("KALSHI-ACCESS", StringComparison.OrdinalIgnoreCase)));
            await Task.Delay(5, token);
            return JsonResponse(events[request.RequestUri.Segments.Last()]);
        });
        using var http = new HttpClient(handler);
        var snapshot = await new KalshiClient(http).FetchAsync();
        SnapshotValidator.Validate(snapshot, PredictionSource.Kalshi);
        Assert.HasCount(35, snapshot.Races);
        Assert.AreEqual(35, handler.RequestCount);
        Assert.IsLessThanOrEqualTo(4, handler.MaximumConcurrentRequests);
        Assert.IsTrue(snapshot.RetrievedAt >= ReadFixtures()["capturedAtUtc"]!.GetValue<DateTimeOffset>());
    }

    [TestMethod]
    [DataRow(HttpStatusCode.TooManyRequests)]
    [DataRow(HttpStatusCode.ServiceUnavailable)]
    public async Task TransientFailuresAreRetriedWithoutDroppingRaces(HttpStatusCode status)
    {
        var events = FixtureDictionary();
        using var handler = new FixtureHandler((request, attempt, _) =>
        {
            if (request.RequestUri!.Segments.Last() == KalshiRaceCatalog.All[0].EventSlug && attempt == 1)
            {
                var limited = new HttpResponseMessage(status);
                limited.Headers.RetryAfter = new(TimeSpan.Zero);
                return Task.FromResult(limited);
            }
            return Task.FromResult(JsonResponse(events[request.RequestUri!.Segments.Last()]));
        });
        using var http = new HttpClient(handler);
        await new KalshiClient(http).FetchAsync();
        Assert.AreEqual(36, handler.RequestCount);
    }

    [TestMethod]
    public async Task PermanentHttpFailureAndInvalidPayloadPreventPartialSnapshot()
    {
        using var failed = new FixtureHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)));
        using var failedHttp = new HttpClient(failed);
        var error = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => new KalshiClient(failedHttp).FetchAsync());
        Assert.AreEqual(HttpStatusCode.NotFound, error.StatusCode);
        Assert.IsLessThan(35, failed.RequestCount);
        using var invalid = new FixtureHandler((_, _, _) => Task.FromResult(JsonResponse("{}")));
        using var invalidHttp = new HttpClient(invalid);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new KalshiClient(invalidHttp).FetchAsync());
        Assert.IsLessThan(35, invalid.RequestCount);
    }

    [TestMethod]
    public async Task CallerCancellationPreventsRequests()
    {
        using var handler = new FixtureHandler((_, _, _) => Task.FromResult(JsonResponse("{}")));
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new KalshiClient(http).FetchAsync(cancellation.Token));
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task HttpTimeoutCoversReadingTheResponseBody()
    {
        using var handler = new FixtureHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new SlowContent()
        }));
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(50) };
        await Assert.ThrowsAsync<OperationCanceledException>(() => new KalshiClient(http).FetchAsync(TestContext.CancellationToken));
    }

    private static JsonObject ReadFixtures() => JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "kalshi-events.json")))!.AsObject();

    private static JsonObject Event(RaceDefinition definition) => ReadFixtures()["events"]!.AsArray()
        .Single(x => x!["event"]!["event_ticker"]!.GetValue<string>() == definition.EventSlug)!.DeepClone().AsObject();

    private static Dictionary<string, string> FixtureDictionary() => ReadFixtures()["events"]!.AsArray()
        .ToDictionary(x => x!["event"]!["event_ticker"]!.GetValue<string>(), x => x!.ToJsonString());

    private static void SetQuotes(JsonObject source, string ticker, string bid, string ask)
    {
        var market = source["markets"]!.AsArray().Single(x => x!["ticker"]!.GetValue<string>() == ticker)!;
        market["yes_bid_dollars"] = bid;
        market["yes_ask_dollars"] = ask;
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class SlowContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.Delay(Timeout.Infinite);
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
            Task.Delay(Timeout.Infinite, cancellationToken);
    }

    private sealed class FixtureHandler(Func<HttpRequestMessage, int, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, int> attempts = new();
        private int requestCount;
        private int concurrentRequests;
        private int maximumConcurrentRequests;
        public int RequestCount => requestCount;
        public int MaximumConcurrentRequests => maximumConcurrentRequests;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestCount);
            int concurrent = Interlocked.Increment(ref concurrentRequests);
            int previous;
            do { previous = maximumConcurrentRequests; }
            while (concurrent > previous && Interlocked.CompareExchange(ref maximumConcurrentRequests, concurrent, previous) != previous);
            try
            {
                int attempt = attempts.AddOrUpdate(request.RequestUri!.AbsolutePath, 1, (_, old) => old + 1);
                return await respond(request, attempt, cancellationToken);
            }
            finally { Interlocked.Decrement(ref concurrentRequests); }
        }
    }
}
