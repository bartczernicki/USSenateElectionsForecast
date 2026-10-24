using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace USSenateElectionsForecast.Tests;

[TestClass]
public sealed class PredictionTests
{
    public TestContext TestContext { get; set; } = null!;
    private static RaceDefinition Texas => RaceCatalog.All.Single(x => x.State == "Texas");

    [TestMethod]
    public void VerifiedCatalogHasThirtyFiveRacesAndSeventyNineUniqueContracts()
    {
        Assert.HasCount(35, RaceCatalog.All);
        Assert.HasCount(35, RaceCatalog.All.Select(x => x.State).Distinct().ToArray());
        Assert.HasCount(79, RaceCatalog.All.SelectMany(x => x.Markets).Select(x => x.MarketId).Distinct().ToArray());
        Assert.HasCount(79, RaceCatalog.All.SelectMany(x => x.Markets).Select(x => x.MarketSlug).Distinct().ToArray());
        CollectionAssert.AreEquivalent(new[] { "Florida", "Ohio" }, RaceCatalog.All.Where(x => x.IsSpecial).Select(x => x.State).ToArray());
        Assert.AreEqual(100, RaceCatalog.DemocraticBaseline + RaceCatalog.RepublicanBaseline + RaceCatalog.All.Count);
        Assert.AreEqual("alabama-senate-election-winner-154", RaceCatalog.All[0].EventSlug);
    }

    [TestMethod]
    public void BundledSnapshotMatchesAllCapturedSourcePricesAndMappings()
    {
        var snapshot = LoadBundle();
        SnapshotValidator.Validate(snapshot);
        var fixtures = ReadFixtures();
        Assert.AreEqual(fixtures["capturedAtUtc"]!.GetValue<DateTimeOffset>(), snapshot.RetrievedAt);
        foreach (var definition in RaceCatalog.All)
        {
            var parsed = PolymarketClient.ParseRace(Event(definition).ToJsonString(), definition);
            var bundled = snapshot.Races.Single(x => x.State == definition.State);
            Assert.AreEqual(JsonSerializer.Serialize(parsed, ForecastJson.Options), JsonSerializer.Serialize(bundled, ForecastJson.Options));
        }
    }

    [TestMethod]
    public void CandidateAndIndependentMarketsUseExplicitMappings()
    {
        var alaskaDefinition = RaceCatalog.All.Single(x => x.State == "Alaska");
        var alaska = PolymarketClient.ParseRace(Event(alaskaDefinition).ToJsonString(), alaskaDefinition);
        Assert.HasCount(7, alaska.Outcomes);
        Assert.AreEqual(Caucus.Democratic, alaska.Outcomes.Single(x => x.MarketId == "634980").Caucus);
        Assert.AreEqual(Caucus.Republican, alaska.Outcomes.Single(x => x.MarketId == "634977").Caucus);
        Assert.AreEqual(Caucus.Unknown, alaska.Outcomes.Single(x => x.MarketId == "634979").Caucus);
        foreach (var state in new[] { "Nebraska", "Montana", "Idaho", "South Dakota" })
        {
            var definition = RaceCatalog.All.Single(x => x.State == state);
            var race = PolymarketClient.ParseRace(Event(definition).ToJsonString(), definition);
            Assert.HasCount(3, race.Outcomes);
            Assert.IsGreaterThan(0.0, race.ProbabilityFor(Caucus.Unknown));
        }
    }

    [TestMethod]
    public void InactiveUnpricedPlaceholdersAreIgnored()
    {
        var definition = RaceCatalog.All.Single(x => x.State == "Alaska");
        var source = Event(definition);
        Assert.IsGreaterThan(definition.Markets.Count, source["markets"]!.AsArray().Count);
        var race = PolymarketClient.ParseRace(source.ToJsonString(), definition);
        Assert.HasCount(definition.Markets.Count, race.Outcomes);
    }

    [TestMethod]
    public void YesPriceIsLocatedByOutcomeLabelAndNativeArraysAreAccepted()
    {
        var source = Event(Texas);
        var markets = source["markets"]!.AsArray();
        var first = markets.Single(x => x!["id"]!.GetValue<string>() == Texas.Markets[0].MarketId)!;
        first["outcomes"] = new JsonArray("No", "Yes");
        first["outcomePrices"] = new JsonArray(0.75, 0.25);
        SetPrice(markets, Texas.Markets[1].MarketId, 0.75);
        var race = PolymarketClient.ParseRace(source.ToJsonString(), Texas);
        Assert.AreEqual(0.25, race.ProbabilityFor(Caucus.Democratic), 1e-12);
    }

    [TestMethod]
    [DataRow(0.90, true)]
    [DataRow(1.10, true)]
    [DataRow(0.899, false)]
    [DataRow(1.101, false)]
    [DataRow(0.0, false)]
    public void RawPriceSumGuardAcceptsOnlyTheDocumentedRange(double sum, bool accepted)
    {
        var source = Event(Texas);
        foreach (var market in Texas.Markets)
            SetPrice(source["markets"]!.AsArray(), market.MarketId, sum / 2);
        if (accepted)
        {
            var race = PolymarketClient.ParseRace(source.ToJsonString(), Texas);
            Assert.AreEqual(0.5, race.ProbabilityFor(Caucus.Democratic), 1e-12);
        }
        else
        {
            var error = Assert.ThrowsExactly<InvalidDataException>(() => PolymarketClient.ParseRace(source.ToJsonString(), Texas));
            StringAssert.Contains(error.Message, "0.90–1.10");
        }
    }

    [TestMethod]
    public void UnexpectedActiveContractIsRejected()
    {
        var source = Event(Texas);
        var unexpected = source["markets"]!.AsArray().First()!.DeepClone();
        unexpected["id"] = "unexpected";
        source["markets"]!.AsArray().Add(unexpected);
        var error = Assert.ThrowsExactly<InvalidDataException>(() => PolymarketClient.ParseRace(source.ToJsonString(), Texas));
        StringAssert.Contains(error.Message, "Review the race catalog");
    }

    [TestMethod]
    public void ChangedSlugDuplicateMarketAndMissingExpectedMarketAreRejected()
    {
        var changed = Event(Texas);
        changed["markets"]!.AsArray().First()!["slug"] = "different-contract";
        Assert.ThrowsExactly<InvalidDataException>(() => PolymarketClient.ParseRace(changed.ToJsonString(), Texas));

        var duplicate = Event(Texas);
        duplicate["markets"]!.AsArray().Add(duplicate["markets"]!.AsArray().First()!.DeepClone());
        Assert.ThrowsExactly<InvalidDataException>(() => PolymarketClient.ParseRace(duplicate.ToJsonString(), Texas));

        var missing = Event(Texas);
        missing["markets"]!.AsArray().RemoveAt(0);
        Assert.ThrowsExactly<InvalidDataException>(() => PolymarketClient.ParseRace(missing.ToJsonString(), Texas));
    }

    [TestMethod]
    public void WrongEventAndWrongElectionYearAreRejected()
    {
        var wrongEvent = Event(Texas);
        wrongEvent["slug"] = "texas-us-senate-election-winner";
        Assert.ThrowsExactly<InvalidDataException>(() => PolymarketClient.ParseRace(wrongEvent.ToJsonString(), Texas));
        var wrongYear = Event(Texas);
        wrongYear["description"] = "Winner of the 2024 Texas Senate election.";
        Assert.ThrowsExactly<InvalidDataException>(() => PolymarketClient.ParseRace(wrongYear.ToJsonString(), Texas));
    }

    [TestMethod]
    [DataRow("[]")]
    [DataRow("null")]
    [DataRow("{")]
    [DataRow("42")]
    public void MalformedRootProducesAnActionableDataError(string json) =>
        Assert.ThrowsExactly<InvalidDataException>(() => PolymarketClient.ParseRace(json, Texas));

    [TestMethod]
    public void MalformedMarketStructureProducesADataError()
    {
        var source = Event(Texas);
        source["markets"] = new JsonArray((JsonNode?)null);
        Assert.ThrowsExactly<InvalidDataException>(() => PolymarketClient.ParseRace(source.ToJsonString(), Texas));
        source["markets"] = new JsonObject();
        Assert.ThrowsExactly<InvalidDataException>(() => PolymarketClient.ParseRace(source.ToJsonString(), Texas));
    }

    [TestMethod]
    [DataRow("[\"NaN\",\"NaN\"]")]
    [DataRow("[\"Infinity\",\"0\"]")]
    [DataRow("[\"-0.1\",\"1.1\"]")]
    [DataRow("[\"0.7\",\"0.7\"]")]
    [DataRow("[\"0.5\"]")]
    [DataRow("{}")]
    public void InvalidPriceArraysAreRejected(string prices)
    {
        var source = Event(Texas);
        source["markets"]!.AsArray().First()!["outcomePrices"] = prices;
        Assert.ThrowsExactly<InvalidDataException>(() => PolymarketClient.ParseRace(source.ToJsonString(), Texas));
    }

    [TestMethod]
    public void MissingPricesInvalidTimestampAndAmbiguousOutcomeLabelsAreRejected()
    {
        foreach (var property in new[] { "outcomePrices", "updatedAt", "outcomes" })
        {
            var source = Event(Texas);
            var market = source["markets"]!.AsArray().First()!;
            market[property] = property switch
            {
                "outcomePrices" => null,
                "updatedAt" => "invalid-date",
                _ => "[\"Yes\",\"Yes\"]"
            };
            Assert.ThrowsExactly<InvalidDataException>(() => PolymarketClient.ParseRace(source.ToJsonString(), Texas));
        }
    }

    [TestMethod]
    public void ClosedKnownMarketsWithResolvedPricesRemainUsable()
    {
        var source = Event(Texas);
        foreach (var market in source["markets"]!.AsArray().Where(x => x!["active"]!.GetValue<bool>()))
        {
            market!["active"] = false;
            market["closed"] = true;
            SetPrice(source["markets"]!.AsArray(), market["id"]!.GetValue<string>(), market["id"]!.GetValue<string>() == Texas.Markets[0].MarketId ? 1 : 0);
        }
        var race = PolymarketClient.ParseRace(source.ToJsonString(), Texas);
        Assert.AreEqual(1, race.ProbabilityFor(Caucus.Democratic));
    }

    [TestMethod]
    public void ValidatorRejectsIncompleteSnapshotsDuplicateRacesAndWrongSchema()
    {
        var snapshot = LoadBundle();
        Assert.ThrowsExactly<InvalidDataException>(() => SnapshotValidator.Validate(snapshot with { SchemaVersion = 2 }));
        Assert.ThrowsExactly<InvalidDataException>(() => SnapshotValidator.Validate(snapshot with { ElectionYear = 2024 }));
        Assert.ThrowsExactly<InvalidDataException>(() => SnapshotValidator.Validate(snapshot with { RetrievedAt = default }));
        Assert.ThrowsExactly<InvalidDataException>(() => SnapshotValidator.Validate(snapshot with { Races = snapshot.Races.Skip(1).ToArray() }));
        Assert.ThrowsExactly<InvalidDataException>(() => SnapshotValidator.Validate(snapshot with { Races = snapshot.Races.Select(_ => snapshot.Races[0]).ToArray() }));
    }

    [TestMethod]
    public void ValidatorRejectsAlteredCaucusNormalizationAndNonFiniteProbabilities()
    {
        var snapshot = LoadBundle();
        var sourceRace = snapshot.Races[0];
        foreach (var invalid in new[]
        {
            sourceRace.Outcomes[0] with { Caucus = Caucus.Unknown },
            sourceRace.Outcomes[0] with { Probability = 0.5 },
            sourceRace.Outcomes[0] with { RawYesPrice = double.NaN },
            sourceRace.Outcomes[0] with { Probability = double.PositiveInfinity },
            sourceRace.Outcomes[0] with { MarketSlug = "changed" }
        })
        {
            var race = sourceRace with { Outcomes = new[] { invalid }.Concat(sourceRace.Outcomes.Skip(1)).ToArray() };
            var changed = snapshot with { Races = new[] { race }.Concat(snapshot.Races.Skip(1)).ToArray() };
            Assert.ThrowsExactly<InvalidDataException>(() => SnapshotValidator.Validate(changed));
        }
    }

    [TestMethod]
    public async Task FetchBuildsOneCompleteSnapshotFromCapturedResponses()
    {
        var events = FixtureDictionary();
        using var handler = new FixtureHandler(async (request, _, token) =>
        {
            await Task.Delay(5, token);
            return JsonResponse(events[request.RequestUri!.Segments.Last()]);
        });
        using var http = new HttpClient(handler);
        var snapshot = await new PolymarketClient(http).FetchAsync();
        SnapshotValidator.Validate(snapshot);
        Assert.AreEqual(35, handler.RequestCount);
        Assert.IsLessThanOrEqualTo(4, handler.MaximumConcurrentRequests);
        Assert.IsTrue(snapshot.RetrievedAt >= LoadBundle().RetrievedAt);
    }

    [TestMethod]
    public async Task RateLimitedResponseIsRetriedWithoutDroppingARace()
    {
        var events = FixtureDictionary();
        using var handler = new FixtureHandler((request, attempt, _) =>
        {
            if (request.RequestUri!.Segments.Last() == RaceCatalog.All[0].EventSlug && attempt == 1)
            {
                var limited = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                limited.Headers.RetryAfter = new(TimeSpan.Zero);
                return Task.FromResult(limited);
            }
            return Task.FromResult(JsonResponse(events[request.RequestUri!.Segments.Last()]));
        });
        using var http = new HttpClient(handler);
        SnapshotValidator.Validate(await new PolymarketClient(http).FetchAsync());
        Assert.AreEqual(36, handler.RequestCount);
    }

    [TestMethod]
    public async Task FailedRaceCancelsRemainingFetchesAndPreservesTheFailure()
    {
        using var handler = new FixtureHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)));
        using var http = new HttpClient(handler);
        var error = await Assert.ThrowsExactlyAsync<HttpRequestException>(() => new PolymarketClient(http).FetchAsync());
        Assert.AreEqual(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.IsLessThan(35, handler.RequestCount);
    }

    [TestMethod]
    public async Task InvalidPayloadFailsTheWholeUpdate()
    {
        using var handler = new FixtureHandler((_, _, _) => Task.FromResult(JsonResponse("[]")));
        using var http = new HttpClient(handler);
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => new PolymarketClient(http).FetchAsync());
        Assert.IsLessThan(35, handler.RequestCount);
    }

    [TestMethod]
    public async Task CallerCancellationPreventsRequests()
    {
        using var handler = new FixtureHandler((_, _, _) => Task.FromResult(JsonResponse("{}")));
        using var http = new HttpClient(handler);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new PolymarketClient(http).FetchAsync(cancellation.Token));
        Assert.AreEqual(0, handler.RequestCount);
    }

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task HttpClientTimeoutAlsoCoversReadingTheResponseBody()
    {
        using var handler = new FixtureHandler((_, _, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new SlowContent()
        }));
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(50) };
        await Assert.ThrowsAsync<OperationCanceledException>(() => new PolymarketClient(http).FetchAsync(TestContext.CancellationToken));
    }

    private static PredictionSnapshot LoadBundle() => JsonSerializer.Deserialize<PredictionSnapshot>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Data", "bundled-predictions.json")), ForecastJson.Options)!;

    private static JsonObject ReadFixtures() => JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "polymarket-events.json")))!.AsObject();

    private static JsonObject Event(RaceDefinition definition) => ReadFixtures()["events"]!.AsArray()
        .Single(x => x!["slug"]!.GetValue<string>() == definition.EventSlug)!.DeepClone().AsObject();

    private static Dictionary<string, string> FixtureDictionary() => ReadFixtures()["events"]!.AsArray()
        .ToDictionary(x => x!["slug"]!.GetValue<string>(), x => x!.ToJsonString());

    private static void SetPrice(JsonArray markets, string id, double price)
    {
        var market = markets.Single(x => x!["id"]!.GetValue<string>() == id)!;
        market["outcomes"] = "[\"Yes\",\"No\"]";
        market["outcomePrices"] = JsonSerializer.Serialize(new[] { price, 1 - price });
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
        public int RequestCount => requestCount;
        public int MaximumConcurrentRequests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestCount);
            var concurrency = Interlocked.Increment(ref concurrentRequests);
            MaximumConcurrentRequests = Math.Max(MaximumConcurrentRequests, concurrency);
            try
            {
                var attempt = attempts.AddOrUpdate(request.RequestUri!.AbsolutePath, 1, (_, old) => old + 1);
                return await respond(request, attempt, cancellationToken);
            }
            finally { Interlocked.Decrement(ref concurrentRequests); }
        }
    }
}
