namespace USSenateElectionsForecast;

public sealed record MarketDefinition(string MarketId, string MarketSlug, Caucus Caucus)
{
    public string SourceUrl => $"https://gamma-api.polymarket.com/markets/{MarketId}";
}

public sealed record RaceDefinition(
    string State,
    string EventSlug,
    bool IsSpecial,
    IReadOnlyList<MarketDefinition> Markets)
{
    public string EventUrl => $"https://polymarket.com/event/{EventSlug}";
    public string SourceUrl => $"https://gamma-api.polymarket.com/events/slug/{EventSlug}";
}

public static class RaceCatalog
{
    public const int ElectionYear = 2026;
    public const int SchemaVersion = 1;
    public const int DemocraticBaseline = 34;
    public const int RepublicanBaseline = 31;

    public const string SenateSourceUrl = "https://www.dailypress.senate.gov/on-the-floor/senate-facts/";
    public const string AlaskaAffiliationsSourceUrl = "https://www.elections.alaska.gov/candidates/?election=26prim";
    public const string ClassISourceUrl = "https://www.senate.gov/senators/Class_I.htm";
    public const string ClassIISourceUrl = "https://www.senate.gov/senators/Class_II.htm";
    public const string ClassIIISourceUrl = "https://www.senate.gov/senators/Class_III.htm";
    public const string ElectionCalendarSourceUrl = "https://www.fec.gov/resources/cms-content/documents/2026pdates.pdf";
    public const string VicePresidentTieSourceUrl = "https://www.senate.gov/legislative/TieVotes.htm";

    // Market IDs and slugs were verified against Gamma on October 24, 2026.
    // D/R party or registration implies the corresponding caucus in this model.
    // Unknown independent affiliations, Greens, and unmapped Alaska candidates stay Unknown.
    public static IReadOnlyList<RaceDefinition> All { get; } = Array.AsReadOnly(new[]
    {
        Pair("Alabama", "alabama", "630627", "630628", eventSuffix: "-154"),
        new RaceDefinition("Alaska", "alaska-senate-election-winner", false, Array.AsReadOnly(new[]
        {
            Candidate("634974", "dan-sullivan", Caucus.Republican),
            Candidate("634975", "ann-diener", Caucus.Unknown),
            Candidate("634976", "mary-peltola", Caucus.Democratic),
            Candidate("634977", "dustin-darden", Caucus.Republican),
            Candidate("634978", "richard-grayson", Caucus.Unknown),
            Candidate("634979", "candidate-a", Caucus.Unknown),
            Candidate("634980", "candidate-b", Caucus.Democratic)
        })),
        Pair("Arkansas", "arkansas", "630653", "630654"),
        Pair("Colorado", "colorado", "630666", "630667"),
        Pair("Delaware", "delaware", "630679", "630680"),
        Pair("Florida", "florida", "631044", "631045", isSpecial: true),
        Pair("Georgia", "georgia", "630692", "630693"),
        Pair("Idaho", "idaho", "630705", "630706", extra: new("630707", "will-person-a-win-the-idaho-senate-race-in-2026", Caucus.Unknown)),
        Pair("Illinois", "illinois", "630720", "630721"),
        Pair("Iowa", "iowa", "630733", "630734"),
        Pair("Kansas", "kansas", "630746", "630747"),
        Pair("Kentucky", "kentucky", "630759", "630760"),
        Pair("Louisiana", "louisiana", "634878", "634879"),
        Pair("Maine", "maine", "630772", "630773"),
        Pair("Massachusetts", "massachusetts", "630790", "630791"),
        Pair("Michigan", "michigan", "630805", "630806"),
        Pair("Minnesota", "minnesota", "630818", "630819"),
        Pair("Mississippi", "mississippi", "631017", "631018"),
        Pair("Montana", "montana", "630831", "630832", extra: new("630833", "will-an-independent-win-the-montana-senate-race-in-2026", Caucus.Unknown)),
        Pair("Nebraska", "nebraska", "634891", "634892", extra: new("634893", "will-an-independent-win-the-nebraska-senate-race-in-2026", Caucus.Unknown)),
        Pair("New Hampshire", "new-hampshire", "630844", "630845"),
        Pair("New Jersey", "new-jersey", "630857", "630858"),
        Pair("New Mexico", "new-mexico", "630870", "630871"),
        Pair("North Carolina", "north-carolina", "630883", "630884"),
        Pair("Ohio", "ohio", "631057", "631058", isSpecial: true),
        Pair("Oklahoma", "oklahoma", "631030", "631031"),
        Pair("Oregon", "oregon", "630898", "630899"),
        Pair("Rhode Island", "rhode-island", "630911", "630912"),
        Pair("South Carolina", "south-carolina", "630924", "630925"),
        Pair("South Dakota", "south-dakota", "630937", "630938", extra: new("630939", "will-person-a-win-the-south-dakota-senate-race-in-2026", Caucus.Unknown)),
        Pair("Tennessee", "tennessee", "630950", "630951"),
        Pair("Texas", "texas", "630963", "630964"),
        Pair("Virginia", "virginia", "630976", "630977"),
        Pair("West Virginia", "west-virginia", "630989", "630990"),
        Pair("Wyoming", "wyoming", "631002", "631003")
    });

    private static MarketDefinition Candidate(string id, string candidate, Caucus caucus) =>
        new(id, $"will-{candidate}-win-the-alaska-senate-race-in-2026", caucus);

    private static RaceDefinition Pair(string state, string slug, string democraticId, string republicanId,
        bool isSpecial = false, string eventSuffix = "", MarketDefinition? extra = null)
    {
        var markets = new List<MarketDefinition>
        {
            new(democraticId, $"will-the-democrats-win-the-{slug}-senate-race-in-2026", Caucus.Democratic),
            new(republicanId, $"will-the-republicans-win-the-{slug}-senate-race-in-2026", Caucus.Republican)
        };
        if (extra is not null)
            markets.Add(extra);
        return new(state, $"{slug}-senate-election-winner{eventSuffix}", isSpecial, markets.AsReadOnly());
    }
}
