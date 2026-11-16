using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Serilog;
using TradingBot.Application;
using TradingBot.Domain;
using TradingBot.Infrastructure;
using TradingBot.Worker;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, EnvironmentName = Environments.Production });
var root = EnvironmentConfiguration.FindRoot();
var options = EnvironmentConfiguration.Load(root, builder.Environment.EnvironmentName, k => builder.Configuration[k]);
const int port = 5101;
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Host.UseSerilog((_, log) => log.MinimumLevel.Information().MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning).WriteTo.Console());
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<TopGainersService>();
builder.Services.AddSingleton<ObservationPriceFeed>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ObservationPriceFeed>());
builder.Services.AddSingleton<BrokerOrderFeed>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BrokerOrderFeed>());
builder.Services.AddSingleton<BrokerAccountMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BrokerAccountMonitor>());
builder.Services.AddSingleton<ObservationEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ObservationEngine>());
builder.Services.AddSingleton<ObservationJournal>();
builder.Services.AddSingleton<ExecutionGate>();
builder.Services.AddSingleton<TradingRiskService>();
Directory.CreateDirectory(Path.GetDirectoryName(options.DatabasePath)!);
// OS-level exclusive lock prevents two hosts writing the same environment's ledger.
using var ownership = new FileStream(options.DatabasePath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
builder.Services.AddDbContextFactory<TradingDbContext>(o => o.UseSqlite($"Data Source={options.DatabasePath};Default Timeout=10"));
builder.Services.AddSingleton<ITradingStore, PaperTradingBrokerAdapter>();
builder.Services.AddHttpClient("indstocks", c => c.Timeout = TimeSpan.FromSeconds(15)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<ITradingBroker>(sp => new IndstocksBrokerAdapter(sp.GetRequiredService<IHttpClientFactory>().CreateClient("indstocks"), options));
builder.Services.AddSingleton<IBrokerAccountReader>(sp => (IBrokerAccountReader)sp.GetRequiredService<ITradingBroker>());
builder.Services.AddHostedService<PaperFillWorker>();
builder.Services.AddHostedService<DashboardBroadcaster>();
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
  o.Cookie.Name = $"TradingBot.{options.Environment}"; o.Cookie.HttpOnly = true; o.Cookie.SameSite = SameSiteMode.Strict; o.ExpireTimeSpan = TimeSpan.FromHours(8); o.SlidingExpiration = false;
  o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; }; o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization();
builder.Services.AddDataProtection().SetApplicationName($"AlgoTrade.{options.Environment}");
builder.Services.AddAntiforgery(o => { o.HeaderName = "X-CSRF-TOKEN"; o.Cookie.Name = $"TradingBot.Csrf.{options.Environment}"; o.Cookie.SameSite = SameSiteMode.Strict; });
builder.Services.AddRateLimiter(o =>
{
  o.RejectionStatusCode = 429;
  o.AddFixedWindowLimiter("login", c => { c.PermitLimit = 5; c.Window = TimeSpan.FromMinutes(1); c.QueueLimit = 0; });
  o.AddFixedWindowLimiter("broker", c => { c.PermitLimit = 15; c.Window = TimeSpan.FromMinutes(1); c.QueueLimit = 0; });
});
builder.Services.AddSignalR(); builder.Services.AddOpenApi();
var app = builder.Build();
await app.Services.GetRequiredService<ITradingStore>().InitializeAsync();
if (args.Contains("--migrate-only")) { Console.WriteLine($"Migrations applied: {options.Environment}."); return; }
app.Use(async (ctx, next) =>
{
  if (ctx.Request.Host.Host is not ("127.0.0.1" or "localhost")) { ctx.Response.StatusCode = 400; return; }
  ctx.Response.Headers["X-Content-Type-Options"] = "nosniff"; ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
  ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self' ws://127.0.0.1:* ws://localhost:*; img-src 'self' data:; frame-ancestors 'none'; base-uri 'self'";
  if (ctx.Request.Path.StartsWithSegments("/api")) ctx.Response.Headers.CacheControl = "no-store";
  try { await next(); }
  catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
  catch (Exception ex)
  {
    if (ctx.Response.HasStarted) throw;
    ctx.Response.StatusCode = ex is InvalidOperationException or ArgumentException or NotSupportedException ? 400 : 503;
    await ctx.Response.WriteAsJsonAsync(new { error = ex is InvalidOperationException or ArgumentException or NotSupportedException ? ex.Message : "Operation failed. Check local logs and database availability; do not resubmit an uncertain order with a new ID." });
  }
});
app.UseRateLimiter(); app.UseAuthentication(); app.UseAuthorization();
app.Use(async (ctx, next) =>
{
  if (ctx.Request.Path.StartsWithSegments("/api") && ctx.Request.Method is not ("GET" or "HEAD" or "OPTIONS"))
  {
    try { await ctx.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(ctx); }
    catch (AntiforgeryValidationException) { ctx.Response.StatusCode = 403; await ctx.Response.WriteAsJsonAsync(new { error = "CSRF validation failed. Reload the page and sign in again." }); return; }
  }
  await next();
});
app.MapGet("/healthz", () => Results.Ok(new { status = "ok", environment = options.Environment, mode = "PAPER" }));
app.MapGet("/api/auth/csrf", (HttpContext c, IAntiforgery a) => new { token = a.GetAndStoreTokens(c).RequestToken });
app.MapPost("/api/auth/login", async (LoginRequest request, HttpContext ctx) =>
{
  var expected = SHA256.HashData(Encoding.UTF8.GetBytes(options.LocalPassword)); var actual = SHA256.HashData(Encoding.UTF8.GetBytes(request.Password ?? ""));
  if (!CryptographicOperations.FixedTimeEquals(expected, actual)) return Results.Unauthorized();
  var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "Local operator")], CookieAuthenticationDefaults.AuthenticationScheme);
  await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity)); return Results.Ok(new { authenticated = true });
}).RequireRateLimiting("login");
app.MapPost("/api/auth/logout", async (HttpContext ctx) => { await ctx.SignOutAsync(); return Results.Ok(); }).RequireAuthorization();
var api = app.MapGroup("/api").RequireAuthorization();
api.MapGet("/dashboard", (ITradingStore s, CancellationToken c) => s.GetPortfolioAsync(c));
api.MapGet("/dashboard/pnl", (ITradingStore s, CancellationToken c) => s.GetTradesAsync(c));
api.MapGet("/positions", (ITradingStore s, CancellationToken c) => s.GetPositionsAsync(c));
api.MapGet("/orders", (ITradingStore s, CancellationToken c) => s.GetOrdersAsync(c));
api.MapGet("/trades", (ITradingStore s, CancellationToken c) => s.GetTradesAsync(c));
api.MapGet("/logs", (ITradingStore s, CancellationToken c) => s.GetEventsAsync(c));
api.MapGet("/system/status", (ITradingStore s, CancellationToken c) => s.GetStateAsync(c));
api.MapGet("/trading/status", (HttpContext ctx, TimeProvider clock, ObservationPriceFeed feed) => new {
  requestedMode = ctx.User.HasClaim(TradingSessionPolicy.LiveIntentClaim, "true") ? "LIVE" : "PAPER",
  liveReady = false, blocker = TradingSessionPolicy.LiveBlocker,
  startWindowOpen = TradingSessionPolicy.IsStartWindow(clock.GetUtcNow()),
  serverUtc = clock.GetUtcNow(), window = "07:00–18:00 IST", deployment = options.Environment,
  observation = "Three-symbol real-data what-if research; no order execution", feedStatus = feed.Status
});
api.MapPost("/trading/mode", async (ModeRequest r, HttpContext ctx, ITradingStore store, ExecutionGate gate, CancellationToken ct) => {
  if (r.Mode is not ("LIVE" or "PAPER")) throw new ArgumentException("Select PAPER or LIVE.");
  TradingSessionPolicy.Confirm(r.Confirmation, r.Mode == "LIVE" ? "GO LIVE" : "PAPER");
  if (r.Mode == "LIVE" && options.Environment != "Production") throw new InvalidOperationException("Open the PROD workspace at http://127.0.0.1:5101 to request Live mode.");
  await gate.Mutex.WaitAsync(ct);
  try {
    if ((await store.GetStateAsync(ct)).State is EngineState.PAPER_RUNNING or EngineState.LIVE_RUNNING or EngineState.OBSERVING)
      throw new InvalidOperationException("Stop the current session before changing mode. Holdings are retained.");
    var ticket = await ctx.AuthenticateAsync();
    var identity = new ClaimsIdentity(ctx.User.Identity as ClaimsIdentity);
    foreach (var claim in identity.FindAll(TradingSessionPolicy.LiveIntentClaim).ToList()) identity.RemoveClaim(claim);
    if (r.Mode == "LIVE") identity.AddClaim(new Claim(TradingSessionPolicy.LiveIntentClaim, "true"));
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity), ticket.Properties);
    return Results.Ok(new { requestedMode = r.Mode, liveReady = false, message = r.Mode == "LIVE" ? TradingSessionPolicy.LiveBlocker : "Paper selected. No session has started." });
  } finally { gate.Mutex.Release(); }
});
api.MapPost("/trading/start", async (ConfirmRequest r, HttpContext ctx, ObservationEngine observer, TimeProvider clock, CancellationToken ct) => {
  TradingSessionPolicy.CheckStart(clock.GetUtcNow(), r.Confirmation);
  if (ctx.User.HasClaim(TradingSessionPolicy.LiveIntentClaim, "true")) throw new NotSupportedException(TradingSessionPolicy.LiveBlocker);
  return await observer.StartAsync(r.Confirmation, ct);
});
api.MapGet("/research/journal", (ObservationJournal journal, CancellationToken ct) => journal.ReadAsync(ct));
api.MapGet("/research/suggestions/{id:long}", async (long id, ObservationJournal journal, CancellationToken ct) => {
  var detail = await journal.DetailAsync(id, ct); return detail == null ? Results.NotFound() : Results.Ok(detail);
});
api.MapGet("/system/health", async (ITradingStore s, ObservationPriceFeed feed, CancellationToken c) => new { database = "ok", control = await s.GetStateAsync(c), environment = options.Environment, mode = "PAPER", broker = options.AccessToken.Length == 0 ? "Not configured" : "Configured; use read-only connection test", marketFeed = feed.Status, dataFreshness = "Research blocks stale prices; the paper ledger is not live-valued", lastSuccessfulBrokerSync = (DateTime?)null, automaticStrategies = "Observation-only Breakout-v1 on three symbols; no order execution", liveReady = false, blockers = new[] { "Live execution and broker reconciliation not implemented", "Static IP and broker compliance not verified", "Full exchange calendar, broad screening, news and index context unavailable" } });
api.MapGet("/settings", () => new { environment = options.Environment, tradingMode = "PAPER", capitalLimit = options.CapitalLimit, maxOpenPositions = options.MaxOpenPositions, dailyLossLimit = options.DailyLossLimit, totalLossLimit = options.TotalLossLimit, autoTradingEnabled = false, brokerTokenConfigured = options.AccessToken.Length > 0 });
api.MapPost("/system/emergency-stop", async (ITradingStore s, IHubContext<DashboardHub> h, CancellationToken c) => { var result = await s.StopAsync(c); await h.Clients.All.SendAsync("refresh", c); return result; });
api.MapPost("/system/recover", (ConfirmRequest r, HttpContext ctx, ITradingStore s, TimeProvider clock, CancellationToken c) => {
  if (!TradingSessionPolicy.IsStartWindow(clock.GetUtcNow())) throw new InvalidOperationException("Starting is allowed only from 7:00 AM until 6:00 PM IST.");
  if (ctx.User.HasClaim(TradingSessionPolicy.LiveIntentClaim, "true")) throw new InvalidOperationException("Select PAPER before starting a simulated session.");
  return s.RecoverAsync(r.Confirmation, c);
});
api.MapGet("/market/top-gainers", (TopGainersService gainers, CancellationToken ct) => gainers.GetAsync(ct)).RequireRateLimiting("broker");
api.MapPost("/paper/orders", (PaperOrderRequest r, ITradingStore s, CancellationToken c) => s.SubmitPaperAsync(r, c));
api.MapPost("/backtesting", (BacktestRequest r) => Backtest.Run(r.Candles, options.CapitalLimit, r.Lookback, options.SlippageBps));
api.MapGet("/strategy", () => new { status = "Research only", executionEnabled = false, entry = "SMA momentum backtest available; automated screening not implemented", costs = "Indicative NSE delivery estimates; validate current tariff before use" });
api.MapGet("/tradelist/today", () => Results.Ok(new { items = Array.Empty<object>(), status = "Unavailable", reason = "Verified instrument universe, market-cap classification, session calendar and fresh market data are required. No candidates have been invented." }));
api.MapGet("/tradelist/history", () => Array.Empty<object>());
api.MapGet("/news", () => new { items = Array.Empty<object>(), status = "No licensed news source configured" });
api.MapGet("/earnings", () => Array.Empty<object>());
var broker = api.MapGroup("/broker").RequireRateLimiting("broker");
broker.MapGet("/profile", (ITradingBroker b, CancellationToken c) => b.GetProfileAsync(c));
broker.MapGet("/funds", (ITradingBroker b, CancellationToken c) => b.GetFundsAsync(c));
// Cached local snapshots do not consume the limiter used for outbound broker reads.
api.MapGet("/broker/account", (BrokerAccountMonitor monitor) => monitor.Snapshot());
api.MapPost("/broker/account/monitor", (MonitorRequest request, BrokerAccountMonitor monitor) => { monitor.SetEnabled(request.Enabled); return Results.Ok(monitor.Snapshot()); });
broker.MapGet("/order-book", (IBrokerAccountReader b, CancellationToken c) => b.GetOrderBookAsync(c));
broker.MapGet("/trade-book", (IBrokerAccountReader b, CancellationToken c) => b.GetTradeBookAsync(c));
broker.MapGet("/holdings", (ITradingBroker b, CancellationToken c) => b.GetHoldingsAsync(c));
broker.MapGet("/positions", (ITradingBroker b, CancellationToken c) => b.GetPositionsAsync(c));
broker.MapGet("/quotes", (string scripCodes, ITradingBroker b, CancellationToken c) => b.GetQuotesAsync(scripCodes, c));
broker.MapGet("/candles", (string scripCode, long start, long end, ITradingBroker b, CancellationToken c) => b.GetCandlesAsync(scripCode, start, end, c));
broker.MapGet("/instruments", async (ITradingBroker b, CancellationToken c) => Results.Text(await b.GetInstrumentsAsync(c), "text/csv"));
app.MapHub<DashboardHub>("/hubs/dashboard").RequireAuthorization();
app.MapOpenApi().RequireAuthorization();
var staticRoot = Path.Combine(root, "ui", "dist");
if (Directory.Exists(staticRoot))
{
  var provider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(staticRoot);
  app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = provider }); app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
  app.MapFallback(async c =>
  {
    if (c.Request.Path.StartsWithSegments("/api") || c.Request.Path.StartsWithSegments("/hubs")) { c.Response.StatusCode = 404; return; }
    c.Response.ContentType = "text/html"; await c.Response.SendFileAsync(Path.Combine(staticRoot, "index.html"));
  });
}
await app.RunAsync();
public sealed record LoginRequest(string Password);
public sealed record ConfirmRequest(string Confirmation);
public sealed record ModeRequest(string? Mode, string? Confirmation);
public sealed record MonitorRequest(bool Enabled);
public sealed record BacktestRequest(IReadOnlyList<Candle> Candles, int Lookback = 5);
public sealed class DashboardHub : Hub;
public sealed class DashboardBroadcaster(IHubContext<DashboardHub> hub, ObservationPriceFeed prices, BrokerAccountMonitor account) : BackgroundService
{
  protected override async Task ExecuteAsync(CancellationToken ct)
  {
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
    var counter = 0;
    try { while (await timer.WaitForNextTickAsync(ct)) {
      if (++counter % 5 == 0) await hub.Clients.All.SendAsync("refresh", ct);
      if (prices.HasSubscriptions || account.Enabled) await hub.Clients.All.SendAsync("marketRefresh", ct);
    } } catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
  }
}
public partial class Program;
