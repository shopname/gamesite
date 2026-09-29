using GameSite.Server.Catalog;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.FileProviders.Physical;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace GameSite.Server.Core;

/// <summary>
/// フロント側ファイル（HTML/CSS/JS）の配信。
/// 安全のため、ホームフォルダと game.json のあるゲームフォルダ以外は配信しない
/// （server/ のソースコードやデータベースが外から見えないようにする）。
/// </summary>
public static class SiteFiles
{
    public static void UseSiteFiles(this WebApplication app)
    {
        var catalog = app.Services.GetRequiredService<GameCatalog>();
        var site = app.Services.GetRequiredService<IOptions<SiteOptions>>().Value;
        var provider = new PhysicalFileProvider(catalog.Root, ExclusionFilters.Sensitive);

        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path;
            if (path.StartsWithSegments("/api") || path.StartsWithSegments("/healthz"))
            {
                await next();
                return;
            }

            var first = path.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (first is null)
            {
                ctx.Response.Redirect(site.HomeUrl);
                return;
            }
            if (!catalog.IsServedFolder(first))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            await next();
        });

        var defaults = new DefaultFilesOptions { FileProvider = provider };
        defaults.DefaultFileNames.Clear();
        defaults.DefaultFileNames.Add("index.html");
        defaults.DefaultFileNames.Add(site.HomePage);
        app.UseDefaultFiles(defaults);

        var types = new FileExtensionContentTypeProvider();
        types.Mappings[".webmanifest"] = "application/manifest+json";

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = provider,
            ContentTypeProvider = types,
            OnPrepareResponse = ctx =>
            {
                // HTML/CSS/JS/JSON は毎回更新確認（ゲームを更新したらすぐ反映される）。画像等は 1 日キャッシュ。
                var ext = Path.GetExtension(ctx.File.Name).ToLowerInvariant();
                ctx.Context.Response.Headers[HeaderNames.CacheControl] =
                    ext is ".html" or ".css" or ".js" or ".json" or ".webmanifest" ? "no-cache" : "public, max-age=86400";
            },
        });
    }
}
