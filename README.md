# US Senate Elections Forecast

A .NET 10 console application for the 2026 U.S. Senate elections. Forecast all 35 contests, including the Florida and Ohio special elections, using independent, correlated, or conditional Monte Carlo, or direct probability calculation. Results include control probabilities and final seat distributions.

## Run

Install the .NET 10 SDK, then run from this directory:

```powershell
dotnet run --project USSenateElectionsForecast
```

The application works offline immediately with snapshots for October 24 and 25, 2026. **Latest available** defaults to `polymarket_predictions_20261025.json`, captured on **October 25, 2026 at 14:56:34 UTC (10:56 a.m. Eastern)**. The October 24 snapshot remains selectable. The active filename appears in the menus alongside its retrieval time; results also identify the retrieval time. No predictions are downloaded automatically. Select **Configure > Update Predictions from Polymarket** to retrieve current prices. No account, wallet, or API key is required.

```text
1) Independent Monte Carlo Simulation
2) Correlated Monte Carlo Simulation
3) Conditional Monte Carlo
4) Direct Probability Calculation
5) Configure
0) Exit

Configure
1) Update Predictions from Polymarket
2) Configure Rho (Correlation)
3) Configure Simulation Count
4) List Current US Senate Polymarket Probabilities
5) Select Predictions File
0) Back
```

Enter a menu number, use `0` to go back or exit, and press Enter after an action to return. Blank configuration input keeps the current value. Defaults are **rho 0.12** and a **simulation count of 100,000**. Rho accepts finite numbers from 0 through 0.99 using a decimal point; counts accept integers from 1,000 through 10,000,000. The count means election trials for options 1 and 2 and national-factor samples for option 3. Option 4 calculates both models directly and ignores the simulation count. EOF exits cleanly; Ctrl+C cancels a forecast calculation or refresh.

**Configure > Select Predictions File** lists available `polymarket_predictions_YYYYMMDD.json` snapshots, newest date first, and marks the active file. The default **Latest available** uses the newest valid dated snapshot. Choosing a specific file remembers that choice across restarts; choose **Latest available** again to resume automatic selection. Selecting a file uses saved data without downloading predictions. Use `0` to return without changing the selection.

Options **2, 3, and the correlated result in 4** calculate the same probability model with different numerical methods. At around **1,000,000 samples**, the sampled estimates generally approach the direct correlated calculation, although small sampling differences can remain. Option 4 integrates directly without sampling. Increasing the sample count reduces calculation noise; it does not remove uncertainty about the election itself.

**Configure > List Current US Senate Polymarket Probabilities** shows all 79 outcome probabilities from the selected snapshot without fetching new data or changing saved settings. Races appear closest first: closeness is the gap in percentage points between the two highest normalized outcome probabilities in each race. Smaller gaps come first, with state names breaking ties; outcomes within a race appear from highest to lowest probability. The list includes the snapshot's retrieval time and identifies special elections. Use **Update Predictions from Polymarket** first to refresh the listed probabilities.

## Forecast model

Every trial starts with **34 Democratic-caucus seats and 31 Republican seats** not on the 2026 ballot, then awards one seat in each of the 35 races for 2026. The baseline counts Angus King and Bernie Sanders with Democrats. Sources are the Senate's [Class I](https://www.senate.gov/senators/Class_I.htm), [Class II](https://www.senate.gov/senators/Class_II.htm), and [Class III](https://www.senate.gov/senators/Class_III.htm) rosters, [Senate caucus facts](https://www.dailypress.senate.gov/on-the-floor/senate-facts/), and the [FEC's 2026 election calendar](https://www.fec.gov/resources/cms-content/documents/2026pdates.pdf).

Democratic and Republican winners are assumed to join their respective caucuses. Independent, Green, or other candidates without a known caucus remain in an **unknown / unaffiliated** bloc. Alaska's candidate contracts use explicit mappings supported by the [official Alaska candidate list](https://www.elections.alaska.gov/candidates/?election=26prim); candidate names do not determine party or caucus automatically. The catalog preserves the contract IDs, slugs, and source links.

The model assumes a Republican vice president, consistent with the Senate's [tie-vote record](https://www.senate.gov/legislative/TieVotes.htm). Republican control requires at least 50 Republican-caucus seats; Democratic control requires at least 51 Democratic-caucus seats. Other outcomes are labeled **uncertain control**, because unaffiliated senators could determine the controlling caucus. Seat ranges are the 5th–95th percentiles of the final seat distribution, rather than confidence intervals for the estimated mean.

All calculation methods show Democratic and Republican seat probability charts, followed by a footer with probabilities of at least 50 through 60 Democratic-caucus seats. For example, `>=50 Dem Seats` counts outcomes with 50 or more seats. Unknown / unaffiliated seats remain included in the summary and control calculations.

Independent mode samples a separate uniform draw for each race. Correlated mode uses a Gaussian copula:

```text
nationalFactor, raceFactor[i] ~ independent standard normal
Z[i] = sqrt(rho) * nationalFactor + sqrt(1 - rho) * raceFactor[i]
```

Cumulative normal thresholds select a race's outcome in the fixed order Democratic caucus, unknown caucus, Republican caucus. Higher national scores favor Republicans. This preserves each race's marginal probabilities. **Rho is correlation between the underlying normal scores, not between observed win/loss indicators.** Independent mode ignores the configured rho; correlated mode with rho zero has the same independent behavior. Normal sampling and quantiles use MathNet.Numerics 5.0.0.

**Why default to 0.12?** It is a modest, configurable modeling assumption: the shared national factor contributes **12% of each standardized race score's variance**, with the remaining **88%** coming from local effects. Historical shared polling errors motivate including correlation. [Nate Silver's FLIPR methodology](https://www.natesilver.net/p/flipr-midterms-model-methodology) describes national, local, and demographic sources of correlated forecast errors. That supports the modeling principle; **0.12 itself is this app's assumption, not a published Silver estimate or a coefficient calibrated from historical data by this app**. Change rho to explore how stronger or weaker shared movement affects Senate control.

### Conditional Monte Carlo

Given a national-factor value, races are independent under the same Gaussian model. Option 3 samples that factor and calculates each race's conditional Democratic, unknown, and Republican probabilities. For each bloc, a Poisson-binomial recurrence aggregates the 35 races into a complete conditional seat distribution:

```text
v[0] = 1
For each race with conditional win probability q:
    newV[k] = (1 - q) * v[k] + q * v[k - 1]
```

The method averages those full distributions across national-factor samples. It retains election uncertainty within each national environment; a histogram of conditional mean seats would give a different and incorrect distribution. Each sample does more work than an ordinary Monte Carlo trial. At rho zero, option 3 computes the independent distribution directly and reports that no national-factor samples were needed.

### Direct Probability Calculation

Option 4 displays the independent result, followed by the correlated result at the configured rho. Independent aggregation applies the same recurrence once to the current probabilities and is exact apart from floating-point arithmetic. Correlated calculation integrates the full conditional distributions against the standard-normal density over factor values `[-9,9]`; the excluded Gaussian tail mass is approximately `2.26e-19`.

Integration uses the existing MathNet package's [GaussLegendreRule](https://numerics.mathdotnet.com/api/MathNet.Numerics.Integration/GaussLegendreRule.htm), doubling the order from 32 up to 8192. It requires two consecutive refinements with maximum per-bloc probability-distribution L1 change at most `1e-10`, mass error at most `1e-10`, and analytic mean error at most `1e-8` seats. This is a numerical convergence criterion, rather than a certified error bound. The correlated result shows the final integration order and tolerance. At rho zero it uses exact independent aggregation instead.

If integration reaches its limit without convergence, the app explains the numerical failure and keeps the already displayed independent result. It does not display an unconverged correlated forecast. Direct calculations do not create artificial integer trial counts: charts, percentiles, and tail probabilities use the calculated probability masses directly.

Control probabilities follow from the marginal distributions: `P(D >= 51)`, `P(R >= 50)`, and `1 - P(D >= 51) - P(R >= 50)` for uncertain control. The two control events are disjoint under the existing rules, so these calculations do not require a joint seat distribution.

For the October 24 snapshot, direct calculation gives:

| Model | Democratic control | Republican control | Uncertain control |
|---|---:|---:|---:|
| Independent | 72.51% | 21.09% | 6.40% |
| Correlated, rho 0.12 | 67.06% | 28.89% | 4.05% |

Both direct models preserve expected Democratic seats of approximately `51.4329914373`. Conditional Monte Carlo estimates converge toward the correlated direct result as the number of factor samples increases.

## Prediction data and refreshes

[Polymarket's public Gamma API](https://docs.polymarket.com/market-data/discover-markets) supplies the input prices. The bundle contains 35 races and 79 priced winner contracts. Most races have Democratic and Republican party contracts; Nebraska, Montana, Idaho, and South Dakota have an additional independent contract, and Alaska uses seven candidate contracts. Alabama's event slug ends in `-154`. The catalog pins exact event slugs, market IDs, and caucus mappings to avoid confusing similarly named events or candidate labels with the wrong contracts.

For each contract the parser finds the `Yes` outcome by its label, including when the API encodes its arrays as JSON strings. Inactive, unpriced placeholders are ignored. All expected contracts must be present; newly active unmapped contracts stop the refresh with a request to review the catalog. Resolved, priced known contracts may be included as deterministic probabilities.

Prices are market-implied inputs, not necessarily a normalized categorical distribution. Each race's mutually exclusive Yes prices are divided by their total before simulation. Raw prices must be finite and within 0–1; their total must be within 0.90–1.10 to detect substantially incomplete or changed markets. The bundled data preserves raw prices, normalized probabilities, retrieval time, upstream update times, and source contract identifiers. A current refresh applies the same validation.

The refresh uses at most four concurrent requests, bounded request timeouts, and limited retries for rate limits and server errors. It saves `polymarket_predictions_YYYYMMDD.json` using the retrieval date in UTC only after **all 35 races** validate. A second refresh on the same UTC date replaces that date's saved file; earlier dates remain available. A successful refresh returns selection to **Latest available**. Failed, partial, or unrecognized updates leave the previous predictions selected. No missing race receives an invented probability.

Settings and refreshed predictions live in `%LOCALAPPDATA%\USSenateElectionsForecast` on Windows (`Environment.SpecialFolder.LocalApplicationData` on other platforms). Available dated snapshots are discovered in both this data directory and the application's bundled `Data` directory. Dates are sorted by filename; a valid data-directory copy takes precedence over a bundled copy for the same date. Invalid snapshots are skipped with a warning. If a remembered file is missing or invalid, the app warns and uses the latest valid snapshot without rewriting the saved setting; invalid settings fall back to defaults.

Existing `predictions.json` and `polymarket_predictions.json` caches are retained and copied byte for byte to a `polymarket_predictions_YYYYMMDD.json` file based on their UTC retrieval date, unless that dated file already exists. Older `predictions_YYYYMMDD.json` files and remembered selections remain supported; data-directory files are copied to the new prefix without overwriting existing files. Saved snapshots are validated, and saves use a temporary file and atomic replacement. For a separate data directory:

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

The published output includes `Data/polymarket_predictions_20261024.json` and `Data/polymarket_predictions_20261025.json` and can forecast without network access. The MSTest suite uses seeded simulations, captured Gamma fixtures, mocked HTTP, and temporary storage. Tests cover marginal probabilities, the analytic two-race Gaussian joint probability, independent aggregation against enumerated outcomes, bundled-data direct results, conditional sampling, convergence at rho 0.99, forced numerical failure, weighted percentiles, inclusive tail probabilities, seat/control totals, the tie-break and unknown-caucus cases, parsing and normalization, complete refreshes, retained caches after failures, dated-file discovery and selection, settings recovery, menu navigation, cancellation, EOF, and chart formatting. Tests do not call live services or change your saved app data.
