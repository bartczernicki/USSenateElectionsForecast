# US Senate Elections Forecast

A .NET 10 console application that forecasts the 2026 U.S. Senate elections, including the Florida and Ohio special elections, using Polymarket prices.

## Assumptions & Configurations

- Simulation count: 100,000 default | Rho: 0.12
- Rho links underlying race scores: 0 = independent; higher = more shared national swing.
- Default 0.12: 12% national / 88% local score variance; this app's assumption, not Silver's.
- Shared polling errors motivate correlation ([Nate Silver's FLIPR methodology](https://www.natesilver.net/p/flipr-midterms-model-methodology)).
- Predictions file: `polymarket_predictions_20261025.json` (latest bundled snapshot)
- Election: 2026 | Races: 35 | Retrieved: 2026-10-25 14:56 UTC
- Seats off ballot: Democratic caucus 34 | Republican caucus 31
- Control: Democratic caucus 51+; Republican caucus 50+ (Republican VP breaks ties).

## Run and configure

Install the .NET 10 SDK, then run from this directory:

```powershell
dotnet run --project USSenateElectionsForecast
```

Choose a numbered calculation method or **5) Configure**. Use `0` to go back or exit, and press Enter after an action to return. Ctrl+C cancels a calculation or refresh.

Under **Configure**:

- **Configure Rho (Correlation)** / **Configure Simulation Count**: set rho from 0 through 0.99 using a decimal point, or a whole-number count from 1,000 through 10,000,000. Blank input keeps the current value; saved settings persist across restarts.
- **Select Predictions File**: choose a dated snapshot or **Latest available**, which selects the newest valid snapshot. A specific selection persists across restarts until changed or a successful refresh returns selection to **Latest available**. Selecting a file uses saved data without downloading anything.
- **List Current US Senate Polymarket Probabilities**: view the selected snapshot, with the closest races first. Closeness is the gap between a race's two highest normalized outcome probabilities.
- **Update Predictions from Polymarket**: manually fetch current prices. No account, wallet, or API key is required. Downloads never happen automatically.

## Calculation methods

- **1) Independent Monte Carlo Simulation**: sample each race separately for the configured number of election trials; rho is ignored.
- **2) Correlated Monte Carlo Simulation**: sample election trials with a shared national factor that moves races together while preserving each race's win probabilities.
- **3) Conditional Monte Carlo**: use the configured count to sample national environments, calculate a complete seat distribution for each, and average those distributions.
- **4) Direct Probability Calculation**: calculate independent and correlated results without sampling; the simulation count is ignored.

The correlated results in options 2, 3, and 4 use the same model. More samples reduce calculation noise; election uncertainty remains.

## Interpreting results

- Results show control probabilities, seat distributions, and the probability of reaching Democratic seat thresholds.
- Angus King and Bernie Sanders count with Democrats. Democratic and Republican winners are assumed to join their respective caucuses.
- Candidates without a known caucus remain **unknown / unaffiliated**. Control is **uncertain** when neither caucus reaches its threshold, because unaffiliated senators could determine the majority.
- Seat ranges span the **5th-95th percentiles** of possible final seats, describing election outcomes rather than confidence intervals for an estimated average.

## Prediction data and refreshes

- Bundled snapshots for October 24 and 25, 2026 work offline. The active filename and retrieval time appear in the menus.
- [Polymarket's public Gamma API](https://docs.polymarket.com/market-data/discover-markets) supplies market-implied prices. Each race's mutually exclusive Yes prices are divided by their total to produce probabilities that sum to one.
- Refreshes save a new snapshot only after all 35 races validate. Failed, partial, or unrecognized updates leave the previous predictions selected.
- Files use `polymarket_predictions_YYYYMMDD.json`, dated in UTC. Another refresh on the same UTC date replaces that day's file; earlier dates remain available.

## Build and verify

```powershell
dotnet build USSenateElectionsForecast.sln -c Release
dotnet test USSenateElectionsForecast.sln -c Release
dotnet publish USSenateElectionsForecast/USSenateElectionsForecast.csproj -c Release -o artifacts/publish
```
