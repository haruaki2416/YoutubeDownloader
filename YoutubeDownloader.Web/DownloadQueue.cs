using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Gress;
using Microsoft.AspNetCore.WebUtilities;
using YoutubeExplode.Videos;
using YoutubeDownloader.Core.Downloading;
using YoutubeDownloader.Core.Resolving;
using YoutubeExplode.Videos.Streams;

public sealed class DownloadJob
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string Status { get; set; } = "queued";
    public double Progress { get; set; }
    public string? Title { get; set; }
    public string? FileName { get; set; }
    public string? FilePath { get; set; }
    public string? Error { get; set; }
    public string Format { get; init; } = "mp4";
    public string? Url { get; init; }
    public string? Quality { get; init; }
}

public sealed class DownloadQueue : BackgroundService
{
    private readonly Channel<DownloadJob> _queue = Channel.CreateBounded<DownloadJob>(
        new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true, SingleWriter = false });
    private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new();
    private readonly string _dataDirectory = Environment.GetEnvironmentVariable("DATA_DIR") ?? "/app/data";

    public bool TryEnqueue(DownloadRequest request, out DownloadJob job)
    {
        job = new DownloadJob { Url = request.Url, Format = request.Format!.ToLowerInvariant(), Quality = request.Quality };
        _jobs[job.Id] = job;
        if (_queue.Writer.TryWrite(job)) return true;
        _jobs.TryRemove(job.Id, out _);
        return false;
    }

    public DownloadJob? Find(string id) => _jobs.TryGetValue(id, out var job) ? job : null;

    public void DeleteFile(DownloadJob job)
    {
        if (job.FilePath is not null)
        {
            try { File.Delete(job.FilePath); } catch { }
            job.FilePath = null;
        }
        _jobs.TryRemove(job.Id, out _);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                job.Status = "resolving";
                using var resolver = new QueryResolver();
                if (!TryGetVideoId(job.Url, out var videoId)) throw new InvalidOperationException("Invalid YouTube video URL.");
                var resolved = await resolver.ResolveAsync(videoId.Value, stoppingToken);
                var video = resolved.Videos.FirstOrDefault()
                    ?? throw new InvalidOperationException("Video not found or unavailable.");
                job.Title = video.Title;
                var audioOnly = job.Format == "mp3";
                var container = audioOnly ? Container.Mp3 : Container.Mp4;
                var quality = job.Quality?.ToLowerInvariant() switch
                {
                    "360p" => VideoQualityPreference.UpTo360p,
                    "480p" => VideoQualityPreference.UpTo480p,
                    "720p" => VideoQualityPreference.UpTo720p,
                    "1080p" => VideoQualityPreference.UpTo1080p,
                    _ => VideoQualityPreference.Highest
                };
                using var downloader = new VideoDownloader();
                var option = await downloader.GetBestDownloadOptionAsync(
                    video.Id,
                    new VideoDownloadPreference(container, audioOnly ? VideoQualityPreference.Highest : quality),
                    cancellationToken: stoppingToken);

                Directory.CreateDirectory(_dataDirectory);
                var extension = audioOnly ? "mp3" : "mp4";
                var safeTitle = Regex.Replace(video.Title, @"[\\/:*?""<>|\p{C}]", "_").Trim().Trim('.');
                if (safeTitle.Length > 100) safeTitle = safeTitle[..100];
                job.FileName = $"{(string.IsNullOrWhiteSpace(safeTitle) ? "video" : safeTitle)}.{extension}";
                job.FilePath = Path.Combine(_dataDirectory, $"{job.Id}.{extension}");
                job.Status = "downloading";
                var progress = new Progress<Percentage>(p => job.Progress = Math.Clamp(p.Fraction, 0, 1));
                await downloader.DownloadVideoAsync(job.FilePath, video, option,
                    includeSubtitles: !audioOnly, ffmpegPath: "ffmpeg", progress: progress,
                    cancellationToken: stoppingToken);
                job.Progress = 1;
                job.Status = "completed";
            }
            catch (OperationCanceledException)
            {
                job.Status = "cancelled";
                if (job.FilePath is not null) try { File.Delete(job.FilePath); } catch { }
            }
            catch (Exception ex)
            {
                job.Status = "failed";
                var message = ex.Message;
                job.Error = message.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("bot", StringComparison.OrdinalIgnoreCase)
                    ? "YouTube is requiring verification from this server. This app does not bypass YouTube access checks."
                    : message.Length > 300 ? message[..300] : message;
                if (job.FilePath is not null) try { File.Delete(job.FilePath); } catch { }
            }
        }
    }

    private static bool TryGetVideoId(string? input, out VideoId videoId)
    {
        videoId = default;
        if (string.IsNullOrWhiteSpace(input) || input.Length > 2048
            || !Uri.TryCreate(input, UriKind.Absolute, out var uri)) return false;
        var host = uri.Host.ToLowerInvariant();
        if (host != "youtu.be" && host != "youtube.com" && !host.EndsWith(".youtube.com", StringComparison.Ordinal)) return false;
        var candidate = host == "youtu.be" ? uri.AbsolutePath.Trim('/')
            : uri.AbsolutePath == "/watch"
                ? QueryHelpers.ParseQuery(uri.Query).TryGetValue("v", out var value) ? value.ToString() : ""
                : uri.AbsolutePath.StartsWith("/shorts/", StringComparison.Ordinal) ? uri.AbsolutePath["/shorts/".Length..].Split('/')[0]
                : uri.AbsolutePath.StartsWith("/live/", StringComparison.Ordinal) ? uri.AbsolutePath["/live/".Length..].Split('/')[0] : "";
        if (VideoId.TryParse(candidate) is not { } parsed) return false;
        videoId = parsed;
        return true;
    }
}
