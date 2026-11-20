using System.Text.RegularExpressions;
using Microsoft.AspNetCore.RateLimiting;
using YoutubeDownloader.Core.Downloading;
using YoutubeDownloader.Core.Resolving;
using YoutubeExplode.Videos;
using YoutubeExplode.Videos.Streams;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("downloads", limiter =>
    {
        limiter.PermitLimit = 3;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
});
var app = builder.Build();
app.UseExceptionHandler();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new { ok = true, service = "YoutubeDownloader.Web" }));

app.MapPost("/api/resolve", async (ResolveRequest request, CancellationToken ct) =>
{
    if (!TryGetVideoId(request.Url, out var videoId))
        return Results.BadRequest(new { error = "Enter a valid YouTube video URL." });

    try
    {
        using var resolver = new QueryResolver();
        var result = await resolver.ResolveAsync(videoId.Value, ct);
        var video = result.Videos.FirstOrDefault();
        if (video is null) return Results.NotFound(new { error = "Video not found." });
        return Results.Ok(new
        {
            id = video.Id.Value,
            title = video.Title,
            author = video.Author.ChannelTitle,
            duration = video.Duration?.ToString(@"h\:mm\:ss") ?? "Unknown"
        });
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return Results.Problem(title: "Could not access this video", detail: SafeMessage(ex));
    }
});

app.MapPost("/api/download", async (DownloadRequest request, HttpContext context, CancellationToken ct) =>
{
    if (!request.Authorized)
        return Results.BadRequest(new { error = "Please confirm you have permission to download this content." });
    if (!TryGetVideoId(request.Url, out var videoId))
        return Results.BadRequest(new { error = "Enter a valid YouTube video URL." });

    var audioOnly = string.Equals(request.Format, "mp3", StringComparison.OrdinalIgnoreCase);
    var container = audioOnly ? Container.Mp3 : Container.Mp4;
    var quality = request.Quality?.ToLowerInvariant() switch
    {
        "360p" => VideoQualityPreference.UpTo360p,
        "480p" => VideoQualityPreference.UpTo480p,
        "720p" => VideoQualityPreference.UpTo720p,
        "1080p" => VideoQualityPreference.UpTo1080p,
        _ => VideoQualityPreference.Highest
    };

    var tempPath = Path.Combine(Path.GetTempPath(), $"yd-{Guid.NewGuid():N}.{(audioOnly ? "mp3" : "mp4")}");
    try
    {
        using var resolver = new QueryResolver();
        var resolved = await resolver.ResolveAsync(videoId.Value, ct);
        var video = resolved.Videos.FirstOrDefault();
        if (video is null) return Results.NotFound(new { error = "Video not found." });

        using var downloader = new VideoDownloader();
        var option = await downloader.GetBestDownloadOptionAsync(
            video.Id,
            new VideoDownloadPreference(container, audioOnly ? VideoQualityPreference.Highest : quality),
            cancellationToken: ct);
        await downloader.DownloadVideoAsync(tempPath, video, option, includeSubtitles: !audioOnly, cancellationToken: ct);

        var stream = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 64,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        context.Response.OnCompleted(async () =>
        {
            await stream.DisposeAsync();
            try { File.Delete(tempPath); } catch { }
        });
        return Results.File(stream, audioOnly ? "audio/mpeg" : "video/mp4",
            fileDownloadName: MakeFileName(video.Title, audioOnly ? "mp3" : "mp4"),
            enableRangeProcessing: true);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        return Results.Problem(title: "Download failed", detail: SafeMessage(ex));
    }
})
.RequireRateLimiting("downloads");

app.Run();

static bool TryGetVideoId(string? input, out VideoId videoId)
{
    videoId = default;
    if (string.IsNullOrWhiteSpace(input) || input.Length > 2048) return false;
    if (!Uri.TryCreate(input, UriKind.Absolute, out var uri)) return false;
    var host = uri.Host.ToLowerInvariant();
    if (host != "youtu.be" && host != "youtube.com" && !host.EndsWith(".youtube.com", StringComparison.Ordinal))
        return false;
    var candidate = host == "youtu.be"
        ? uri.AbsolutePath.Trim('/')
        : uri.AbsolutePath == "/watch"
            ? System.Web.HttpUtility.ParseQueryString(uri.Query)["v"]
            : uri.AbsolutePath.StartsWith("/shorts/", StringComparison.Ordinal)
                ? uri.AbsolutePath["/shorts/".Length..].Split('/')[0]
                : uri.AbsolutePath.StartsWith("/live/", StringComparison.Ordinal)
                    ? uri.AbsolutePath["/live/".Length..].Split('/')[0]
                    : "";
    if (VideoId.TryParse(candidate) is not { } parsed) return false;
    videoId = parsed;
    return true;
}

static string MakeFileName(string title, string extension)
{
    var safe = Regex.Replace(title, @"[\\/:*?""<>|\p{C}]", "_").Trim().Trim('.');
    if (safe.Length > 100) safe = safe[..100];
    return $"{(string.IsNullOrWhiteSpace(safe) ? "video" : safe)}.{extension}";
}

static string SafeMessage(Exception ex)
{
    var message = ex.Message;
    if (message.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase)
        || message.Contains("bot", StringComparison.OrdinalIgnoreCase))
        return "YouTube is requiring verification from this server. This app does not bypass YouTube access checks. Try again later or use content available without additional verification.";
    return message.Length > 300 ? message[..300] : message;
}

record ResolveRequest(string? Url);
record DownloadRequest(string? Url, string? Format, string? Quality, bool Authorized);
