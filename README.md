# AlgoTrade source

React/TypeScript UI and .NET 10 API for Indian cash-equity paper research and read-only broker account monitoring. Real-money execution is disabled.

This repository contains application source, tests, build/dependency manifests and automation scripts. Private application settings, all environment files (including examples), databases, logs, dependency directories, build outputs, source archives and local operator documentation are excluded.

## Build and test

Requires the .NET 10 SDK and Node.js. From the repository root:

```powershell
dotnet restore api/TradingBot.sln --locked-mode
dotnet build api/TradingBot.sln --configuration Release
dotnet test api/TradingBot.sln --configuration Release
cd ui
npm ci
npm run build
npm test
```

Compilation and mocked/disposable-database tests do not require broker credentials. Browser tests that alter state must use an isolated test host and database.

Running the application requires private local configuration supplied separately. Obtain or create that configuration locally before running the startup/setup scripts. No application configuration values, passwords or broker tokens are supplied in this repository. Do not upload local configuration or trading records.

PAPER research scenarios are hypothetical and are not broker fills or proof of profitability. Actual account monitoring is read-only. The application never authorizes real orders by selecting a UI mode.
