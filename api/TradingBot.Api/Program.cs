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

using Serilog;
using TradingBot.Application;
using TradingBot.Domain;
using TradingBot.Infrastructure;


var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, EnvironmentName = Environments.Production });
var root = EnvironmentConfiguration.FindRoot();
var options = EnvironmentConfiguration.Load(root, builder.Environment.EnvironmentName, k => builder.Configuration[k]);
const int port = 5101;
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Host.UseSerilog((_, log) => log.MinimumLevel.Information().MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning).WriteTo.Console());
builder.Services.AddSingleton(options);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(sp => new RealtimeJournalStore(options.JournalPath, sp.GetRequiredService<TimeProvider>()));
builder.Services.AddHttpClient("indstocks", c => c.Timeout = TimeSpan.FromSeconds(15)).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddSingleton<ITradingBroker>(sp => new IndstocksBrokerAdapter(sp.GetRequiredService<IHttpClientFactory>().CreateClient("indstocks"), options));
builder.Services.AddSingleton<IBrokerAccountReader>(sp => (IBrokerAccountReader)sp.GetRequiredService<ITradingBroker>());
builder.Services.AddSingleton<IClosingPriceReader>(sp => (IClosingPriceReader)sp.GetRequiredService<ITradingBroker>());
builder.Services.AddSingleton<TopGainersService>();
builder.Services.AddSingleton<ObservationPriceFeed>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ObservationPriceFeed>());
builder.Services.AddSingleton<BrokerOrderFeed>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BrokerOrderFeed>());
builder.Services.AddSingleton<BrokerAccountMonitor>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<BrokerAccountMonitor>());
builder.Services.AddSingleton<RealtimeResearchEngine>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<RealtimeResearchEngine>());
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
await app.Services.GetRequiredService<RealtimeJournalStore>().InitializeAsync();
if ((args.Contains("--initialize-only") || args.Contains("--migrate-only"))) { Console.WriteLine($"JSON journal initialized: {options.Environment}."); return; }
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
    await ctx.Response.WriteAsJsonAsync(new { error = ex is InvalidOperationException or ArgumentException or NotSupportedException ? ex.Message : "Operation failed. Check local logs, storage and service availability." });
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
app.MapGet("/healthz", () => Results.Ok(new { status = "ok", environment = options.Environment, mode = "REALTIME_READ_ONLY" }));
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
async Task<object> Workspace(RealtimeJournalStore journal, CancellationToken ct) {
  var state = await journal.ReadAsync(ct);
  return new { serverUtc = DateTimeOffset.UtcNow, state.State, state.Reason, state.UpdatedUtc, state.Sessions, state.Assessments, state.Entries, state.Coverage,
    executionEnabled = false, journalFile = journal.FileName, storageError = journal.PersistenceError,
    summary = new { suggestions = state.Entries.Count, daysObserved = state.Coverage.Select(x => TradingSessionPolicy.InIndia(x.MinuteUtc).Date).Distinct().Count(), observedMinutes = state.Coverage.Count, coverageGaps = state.Entries.Count(x => x.CoverageGap), referenceMovement = state.Entries.Sum(x => x.ObservedMove ?? 0), brokerEvents = state.BrokerEvents.Count, actualFills = state.BrokerFills.Count },
    note = "Reference movement is a watched-price comparison, not a trade, fill or realized profit. Only broker-reported fills are actual trades." };
}
api.MapGet("/workspace", (RealtimeJournalStore j, CancellationToken ct) => Workspace(j, ct));
api.MapGet("/research/journal", (RealtimeJournalStore j, CancellationToken ct) => Workspace(j, ct));
api.MapGet("/system/status", async (RealtimeJournalStore j, CancellationToken ct) => { var s = await j.ReadAsync(ct); return new { s.State, s.Reason, updatedUtc = s.UpdatedUtc }; });
api.MapGet("/trading/status", (TimeProvider clock, ObservationPriceFeed feed) => new { workspace = "REALTIME", executionEnabled = false, startWindowOpen = TradingSessionPolicy.IsStartWindow(clock.GetUtcNow()), serverUtc = clock.GetUtcNow(), feedStatus = feed.Status });
api.MapPost("/trading/start", async (ConfirmRequest request, RealtimeResearchEngine engine, CancellationToken ct) => { await engine.StartWatchingAsync(request.Confirmation, ct); return Results.Ok(new { watching = true, executionEnabled = false }); });
api.MapPost("/trading/stop", async (RealtimeResearchEngine engine, CancellationToken ct) => { await engine.StopWatchingAsync(ct); return Results.Ok(new { watching = false }); });
api.MapPost("/system/emergency-stop", async (RealtimeResearchEngine engine, CancellationToken ct) => { await engine.StopWatchingAsync(ct); return Results.Ok(new { watching = false, note = "Watching stopped; broker orders placed elsewhere are not cancelled." }); });
api.MapGet("/system/health", (RealtimeJournalStore journal, ObservationPriceFeed prices, BrokerOrderFeed orders, BrokerAccountMonitor monitor) => new { environment = "Production", workspace = "REALTIME", executionEnabled = false, storage = "Atomic JSON journal", journalFile = journal.FileName, storageError = journal.PersistenceError, priceFeed = prices.Status, orderFeed = orders.Status, accountMonitoring = monitor.Enabled });
api.MapGet("/settings", () => new { environment = "Production", workspace = "REALTIME", executionEnabled = false, brokerTokenConfigured = options.AccessToken.Length > 0 });
api.MapGet("/market/top-gainers", (TopGainersService gainers, CancellationToken ct) => gainers.GetAsync(ct)).RequireRateLimiting("broker");
api.MapGet("/broker/account", (BrokerAccountMonitor monitor) => monitor.Snapshot());
api.MapPost("/broker/account/monitor", (MonitorRequest request, BrokerAccountMonitor monitor) => { monitor.SetEnabled(request.Enabled); return Results.Ok(monitor.Snapshot()); });
api.MapGet("/journal/broker-evidence", async (RealtimeJournalStore j, CancellationToken ct) => { var state = await j.ReadAsync(ct); return new { events = state.BrokerEvents.TakeLast(500), fills = state.BrokerFills.TakeLast(500) }; });
var broker = api.MapGroup("/broker").RequireRateLimiting("broker");
broker.MapGet("/profile", (ITradingBroker b, CancellationToken ct) => b.GetProfileAsync(ct));
broker.MapGet("/funds", (ITradingBroker b, CancellationToken ct) => b.GetFundsAsync(ct));
broker.MapGet("/holdings", (ITradingBroker b, CancellationToken ct) => b.GetHoldingsAsync(ct));
broker.MapGet("/positions", (ITradingBroker b, CancellationToken ct) => b.GetPositionsAsync(ct));
broker.MapGet("/order-book", (IBrokerAccountReader b, CancellationToken ct) => b.GetOrderBookAsync(ct));
broker.MapGet("/trade-book", (IBrokerAccountReader b, CancellationToken ct) => b.GetTradeBookAsync(ct));
broker.MapGet("/quotes", (string scripCodes, ITradingBroker b, CancellationToken ct) => b.GetQuotesAsync(scripCodes, ct));
broker.MapGet("/candles", (string scripCode, long start, long end, ITradingBroker b, CancellationToken ct) => b.GetCandlesAsync(scripCode, start, end, ct));
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
public sealed record ConfirmRequest(string? Confirmation);
public sealed record MonitorRequest(bool Enabled);
public sealed class DashboardHub : Hub;
public sealed class DashboardBroadcaster(IHubContext<DashboardHub> hub, ObservationPriceFeed prices, BrokerAccountMonitor account) : BackgroundService
{
  protected override async Task ExecuteAsync(CancellationToken ct) {
    using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1)); var counter = 0;
    try { while (await timer.WaitForNextTickAsync(ct)) {
      if (++counter % 5 == 0) await hub.Clients.All.SendAsync("refresh", ct);
      if (prices.HasSubscriptions || account.Enabled) await hub.Clients.All.SendAsync("marketRefresh", ct);
    } } catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
  }
}
public partial class Program;
