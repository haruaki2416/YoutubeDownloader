using System.Text.RegularExpressions;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using YoutubeDownloader.Core.Downloading;
using YoutubeDownloader.Core.Resolving;
using YoutubeExplode.Videos;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("queue", limiter =>
    {
        limiter.PermitLimit = 10;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
});
builder.Services.AddSingleton<DownloadQueue>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<DownloadQueue>());

var app = builder.Build();
app.UseExceptionHandler();
app.UseRateLimiter();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/health", () => Results.Ok(new { ok = true, service = "Sigma Brewer", engine = "YoutubeDownloader.Core", mode = "queued-stream-to-disk" }));

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
            durationSeconds = video.Duration?.TotalSeconds,
            duration = video.Duration?.ToString(@"h\\:mm\\:ss") ?? "Unknown"
        });
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return Results.Problem(title: "Could not access this video", detail: SafeMessage(ex));
    }
});

app.MapPost("/api/downloads", (DownloadRequest request, DownloadQueue queue) =>
{
    if (!request.Authorized)
        return Results.BadRequest(new { error = "Confirm that you own this content or have permission to download it." });
    if (!TryGetVideoId(request.Url, out _))
        return Results.BadRequest(new { error = "Enter a valid YouTube video URL." });
    if (!string.Equals(request.Format, "mp3", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(request.Format, "mp4", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest(new { error = "Choose MP4 or MP3." });

    if (!queue.TryEnqueue(request, out var job))
        return Results.Json(new { error = "The download queue is full. Try again shortly." }, statusCode: 429);

    return Results.Accepted($"/api/downloads/{job.Id}", new { id = job.Id, status = job.Status });
}).RequireRateLimiting("queue");

app.MapGet("/api/downloads/{id}", (string id, DownloadQueue queue) =>
{
    var job = queue.Find(id);
    return job is null ? Results.NotFound() : Results.Ok(new
    {
        id = job.Id,
        status = job.Status,
        progress = job.Progress,
        title = job.Title,
        fileName = job.FileName,
        error = job.Error,
        downloadUrl = job.Status == "completed" ? $"/api/downloads/{job.Id}/file" : null
    });
});

app.MapGet("/api/downloads/{id}/file", async (string id, HttpContext context, DownloadQueue queue) =>
{
    var job = queue.Find(id);
    if (job is null) return Results.NotFound();
    if (job.Status != "completed" || job.FilePath is null || !File.Exists(job.FilePath))
        return Results.Conflict(new { error = "This download is not ready yet." });

    var stream = new FileStream(job.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read,
        1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
    context.Response.OnCompleted(async () =>
    {
        await stream.DisposeAsync();
        queue.DeleteFile(job);
    });
    return Results.File(stream, job.Format == "mp3" ? "audio/mpeg" : "video/mp4",
        fileDownloadName: job.FileName, enableRangeProcessing: true);
});

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
            ? QueryHelpers.ParseQuery(uri.Query).TryGetValue("v", out var value) ? value.ToString() : ""
            : uri.AbsolutePath.StartsWith("/shorts/", StringComparison.Ordinal)
                ? uri.AbsolutePath["/shorts/".Length..].Split('/')[0]
                : uri.AbsolutePath.StartsWith("/live/", StringComparison.Ordinal)
                    ? uri.AbsolutePath["/live/".Length..].Split('/')[0]
                    : "";
    if (VideoId.TryParse(candidate) is not { } parsed) return false;
    videoId = parsed;
    return true;
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
