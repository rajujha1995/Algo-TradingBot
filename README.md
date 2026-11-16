# AlgoTrade source

React/TypeScript UI and .NET 10 API for real-time Indian cash-equity research and read-only INDstocks account monitoring. There is one Production workspace. Broker order submission and simulated execution are disabled.

Start Watching requires an exact confirmation and the permitted IST start window. The engine evaluates completed five-minute candles with fresh streamed prices, records qualifying setups for Reliance, ICICI Bank and SBI, and tracks subsequent observed reference-price movements. These observations are not trades, fills or realized profits. Missing data is flagged; outcomes during gaps are not invented. News/index confirmation and special-session calendars are not connected.

The private JSON journal stores watch sessions, suggestions with candle evidence, coverage, broker-reported order events and actual equity fills. Writes are serialized and atomic with a previous-version backup and single-process lease. Corrupt history is never silently reset; interrupted sessions pause after restart. Existing SQLite history is not opened or deleted. Monitoring and watching require manual activation after startup.

The richer dashboard includes a watch journal, weekly review, read-only account view and top gainers across available NSE/BSE cash equities. Gainers are fetched ranking snapshots. Latest quote, previous close and today's dated broker daily close remain separate; unavailable closes are not replaced with quotes. Broker daily closes are not exchange-certified and may be revised. A week of observations does not enable real orders automatically.

## Build and test

Requires .NET 10 SDK and a supported Node.js release. From the repository root:

```powershell
dotnet restore api/TradingBot.sln --locked-mode
dotnet build api/TradingBot.sln --configuration Release
dotnet test api/TradingBot.sln --configuration Release
cd ui
npm ci
npm run build
npm test
```

Compilation and unit/integration tests do not require broker credentials. Browser API tests require a disposable journal and isolated host. Never run tests against private account history.

Running requires private local configuration supplied separately. This source-only repository intentionally excludes all environment files and examples, application settings/configuration values, credentials, trading/account records, dependency directories, generated build outputs and local operational documentation. Do not upload them.
