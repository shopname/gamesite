using System.Threading.RateLimiting;
using GameSite.Server.Api;
using GameSite.Server.Catalog;
using GameSite.Server.Core;
using GameSite.Server.Data;
using GameSite.Server.Games;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// ---------------- サービス ----------------
builder.Services.Configure<SiteOptions>(builder.Configuration.GetSection("Site"));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<GameCatalog>();
builder.Services.AddSingleton<PlayerStore>();
builder.Services.AddSingleton<GameRegistry>();
builder.Services.AddSingleton<SessionStore>();

builder.Services.AddProblemDetails();
builder.Services.AddResponseCompression(o => o.EnableForHttps = true);

// 公開サーバーでの連打・大量アクセス対策（クライアントごとに 1 分あたりの回数を制限）
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        ctx.Request.Path.StartsWithSegments("/api")
            ? RateLimitPartition.GetFixedWindowLimiter(ClientKey(ctx), _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 300,
                Window = TimeSpan.FromMinutes(1),
            })
            : RateLimitPartition.GetNoLimiter("static"));
    o.AddPolicy(ApiEndpoints.SessionPolicy, ctx =>
        RateLimitPartition.GetFixedWindowLimiter(ClientKey(ctx), _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 240,
            Window = TimeSpan.FromMinutes(1),
        }));
});

// Render / Azure などのリバースプロキシ配下で、本当のクライアント IP を使う
var behindProxy = builder.Configuration.GetValue<bool>("Hosting:BehindProxy");
if (behindProxy)
{
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownNetworks.Clear();
        o.KnownProxies.Clear();
    });
}

var app = builder.Build();

// ---------------- パイプライン ----------------
if (behindProxy) app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseResponseCompression();

app.Use(async (ctx, next) =>
{
    var h = ctx.Response.Headers;
    h["X-Content-Type-Options"] = "nosniff";
    h["Referrer-Policy"] = "strict-origin-when-cross-origin";
    h["X-Frame-Options"] = "SAMEORIGIN";
    await next();
});

app.UseRateLimiter();
app.UseSiteFiles();
app.MapGameSiteApi();
app.MapGet("/healthz", () => Results.Ok("ok"));

await app.Services.GetRequiredService<PlayerStore>().InitializeAsync();
_ = app.Services.GetRequiredService<GameRegistry>(); // 起動時にゲームロジックを登録してログに出す

app.Run();

static string ClientKey(HttpContext ctx) =>
    ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
