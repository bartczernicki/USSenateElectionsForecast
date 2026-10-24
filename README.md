# US Senate Elections Forecast

A .NET 10 console application for the 2026 U.S. Senate elections. Run independent or correlated Monte Carlo simulations of all 35 contests, including the Florida and Ohio special elections, and see control probabilities and final seat distributions.

## Run

Install the .NET 10 SDK, then run from this directory:

```powershell
dotnet run --project USSenateElectionsForecast
```

The application works offline immediately using a real Polymarket snapshot captured on **October 24, 2026 at 21:15:35 UTC (5:15 p.m. Eastern)**. The selected snapshot's retrieval date appears in the menus and results. Select **Configure > Update Predictions from Polymarket** to retrieve current prices. No account, wallet, or API key is required.

```text
1) Independent Monte Carlo Simulation
2) Correlated Monte Carlo Simulation
3) Configure
0) Exit

Configure
1) Update Predictions from Polymarket
2) Configure Rho (Correlation)
3) Configure Simulation Count
0) Back
```

Enter a menu number, use `0` to go back or exit, and press Enter after an action to return. Blank configuration input keeps the current value. Defaults are **rho 0.12** and **100,000 trials**. Rho accepts finite numbers from 0 through 0.99 using a decimal point; trial counts accept integers from 1,000 through 10,000,000. EOF exits cleanly; Ctrl+C cancels a simulation or refresh.

## Forecast model

Every trial starts with **34 Democratic-caucus seats and 31 Republican seats** not on the 2026 ballot, then awards one seat in each of the 35 races for 2026. The baseline counts Angus King and Bernie Sanders with Democrats. Sources are the Senate's [Class I](https://www.senate.gov/senators/Class_I.htm), [Class II](https://www.senate.gov/senators/Class_II.htm), and [Class III](https://www.senate.gov/senators/Class_III.htm) rosters, [Senate caucus facts](https://www.dailypress.senate.gov/on-the-floor/senate-facts/), and the [FEC's 2026 election calendar](https://www.fec.gov/resources/cms-content/documents/2026pdates.pdf).

Democratic and Republican winners are assumed to join their respective caucuses. Independent, Green, or other candidates without a known caucus remain in an **unknown / unaffiliated** bloc. Alaska's candidate contracts use explicit mappings supported by the [official Alaska candidate list](https://www.elections.alaska.gov/candidates/?election=26prim); candidate names do not determine party or caucus automatically. The catalog preserves the contract IDs, slugs, and source links.

The model assumes a Republican vice president, consistent with the Senate's [tie-vote record](https://www.senate.gov/legislative/TieVotes.htm). Republican control requires at least 50 Republican-caucus seats; Democratic control requires at least 51 Democratic-caucus seats. Other outcomes are labeled **uncertain control**, because unaffiliated senators could determine the controlling caucus. Seat ranges are the empirical 5th–95th percentiles of simulated outcomes, rather than confidence intervals for the estimated mean.

Both simulation results show Democratic and Republican seat histograms, followed by a footer with probabilities of at least 50 through 60 Democratic-caucus seats. For example, `>=50 Dem Seats` counts outcomes with 50 or more seats. Unknown / unaffiliated seats remain included in the summary and control calculations.

Independent mode samples a separate uniform draw for each race. Correlated mode uses a Gaussian copula:

```text
nationalFactor, raceFactor[i] ~ independent standard normal
Z[i] = sqrt(rho) * nationalFactor + sqrt(1 - rho) * raceFactor[i]
```

Cumulative normal thresholds select a race's outcome in the fixed order Democratic caucus, unknown caucus, Republican caucus. Higher national scores favor Republicans. This preserves each race's marginal probabilities. **Rho is correlation between the underlying normal scores, not between observed win/loss indicators.** Independent mode ignores the configured rho; correlated mode with rho zero has the same independent behavior. Normal sampling and quantiles use MathNet.Numerics 5.0.0.

## Prediction data and refreshes

[Polymarket's public Gamma API](https://docs.polymarket.com/market-data/discover-markets) supplies the input prices. The bundle contains 35 races and 79 priced winner contracts. Most races have Democratic and Republican party contracts; Nebraska, Montana, Idaho, and South Dakota have an additional independent contract, and Alaska uses seven candidate contracts. Alabama's event slug ends in `-154`. The catalog pins exact event slugs, market IDs, and caucus mappings to avoid confusing similarly named events or candidate labels with the wrong contracts.

For each contract the parser finds the `Yes` outcome by its label, including when the API encodes its arrays as JSON strings. Inactive, unpriced placeholders are ignored. All expected contracts must be present; newly active unmapped contracts stop the refresh with a request to review the catalog. Resolved, priced known contracts may be included as deterministic probabilities.

Prices are market-implied inputs, not necessarily a normalized categorical distribution. Each race's mutually exclusive Yes prices are divided by their total before simulation. Raw prices must be finite and within 0–1; their total must be within 0.90–1.10 to detect substantially incomplete or changed markets. The bundled data preserves raw prices, normalized probabilities, retrieval time, upstream update times, and source contract identifiers. A current refresh applies the same validation.

The refresh uses at most four concurrent requests, bounded request timeouts, and limited retries for rate limits and server errors. It replaces the saved snapshot only after **all 35 races** validate. Failed, partial, or unrecognized updates leave the previous predictions selected. No missing race receives an invented probability.

Settings and refreshed predictions live in `%LOCALAPPDATA%\USSenateElectionsForecast` on Windows (`Environment.SpecialFolder.LocalApplicationData` on other platforms). A valid saved snapshot takes precedence over the bundle. Invalid cached data falls back to the bundle with a message; invalid settings fall back to defaults. Saves use a temporary file and atomic replacement. For a separate data directory:

```powershell
dotnet run --project USSenateElectionsForecast -- --data-dir .\my-forecast-data
```

This version uses the fixed 2026 catalog, caucus assumptions, and vice-president assumption above. Contract or affiliation changes require reviewing that catalog before accepting new outcomes.

## Build and verify

```powershell
dotnet build USSenateElectionsForecast.sln -c Release
dotnet test USSenateElectionsForecast.sln -c Release
dotnet publish USSenateElectionsForecast/USSenateElectionsForecast.csproj -c Release -o artifacts/publish
```

The published output includes `Data/bundled-predictions.json` and can forecast without network access. The MSTest suite uses seeded simulations, captured Gamma fixtures, mocked HTTP, and temporary storage. Tests cover marginal probabilities, the analytic two-race Gaussian joint probability, seat/control totals, the tie-break and unknown-caucus cases, parsing and normalization, complete refreshes, retained caches after failures, settings recovery, menu navigation, EOF, and chart formatting. Tests do not call live services or change your saved app data.
