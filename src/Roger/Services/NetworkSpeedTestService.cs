using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Roger.Models;

namespace Roger.Services;

/// <summary>
/// A bounded, sequential HTTPS throughput test against Cloudflare's public speed test endpoints.
/// This measures the route to Cloudflare, not guaranteed ISP line capacity. No telemetry is sent.
/// API reference: https://github.com/cloudflare/speedtest#api-reference
/// </summary>
public sealed class NetworkSpeedTestService : IDisposable
{
    public const long MaximumDownloadBytes = 32 * 1024 * 1024;
    public const long MaximumUploadBytes = 8 * 1024 * 1024;
    public const string TrafficNotice = "将连接 Cloudflare：最多下载 32 MiB、上传 8 MiB，另有少量 HTTP 开销。结果是到 Cloudflare 的单连接 HTTPS 速度。";
    private static readonly Uri Endpoint = new("https://speed.cloudflare.com/");
    private readonly HttpClient _http;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public NetworkSpeedTestService() : this(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(3)
        }) { }

    // An injected handler lets the smoke suite verify failures, cancellation and byte caps without network traffic.
    internal NetworkSpeedTestService(HttpMessageHandler handler)
    {
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Roger/0.1.0");
        _http.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
    }

    /// <summary>Call only after the user starts a test. Cancellation propagates without fabricating results.</summary>
    public async Task<SpeedTestResult> RunAsync(IProgress<SpeedTestProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!await _gate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException("已有网络测速正在进行。");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(55));
            var token = timeout.Token;
            try
            {
                progress?.Report(new SpeedTestProgress("latency", 0, 0, 0, "正在连接 Cloudflare，测量 HTTP 延迟…"));
                // Warm up DNS/TLS; subsequent probes measure HTTP response latency on the established connection.
                await ProbeAsync(token).ConfigureAwait(false);
                var latencies = new List<double>();
                for (var i = 0; i < 5; i++)
                {
                    latencies.Add(await ProbeAsync(token).ConfigureAwait(false));
                    progress?.Report(new SpeedTestProgress("latency", 2 + (i + 1) * 2, 0, 0,
                        $"HTTP 延迟 {latencies[^1]:F0} ms（{i + 1}/5）"));
                }

                var down = await DownloadAsync(progress, token).ConfigureAwait(false);
                var up = await UploadAsync(down.Bytes, progress, token).ConfigureAwait(false);
                var sortedLatency = latencies.OrderBy(x => x).ToArray();
                var jitter = latencies.Zip(latencies.Skip(1), (a, b) => Math.Abs(a - b)).Average();
                var result = new SpeedTestResult(down.Mbps, up.Mbps, sortedLatency[sortedLatency.Length / 2], jitter,
                    down.Bytes, up.Bytes, DateTimeOffset.Now, "Cloudflare · 单连接 HTTPS");
                progress?.Report(new SpeedTestProgress("complete", 100, up.Mbps, down.Bytes + up.Bytes, "测速完成"));
                return result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("测速超时，请检查网络连接后重试。测速服务在部分网络中可能不可达。");
            }
        }
        finally { _gate.Release(); }
    }

    private async Task<double> ProbeAsync(CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, NewDownloadUri(0));
        var watch = Stopwatch.StartNew();
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        watch.Stop();
        response.EnsureSuccessStatusCode();
        await response.Content.CopyToAsync(Stream.Null, token).ConfigureAwait(false);
        return watch.Elapsed.TotalMilliseconds;
    }

    private async Task<Transfer> DownloadAsync(IProgress<SpeedTestProgress>? progress, CancellationToken token)
    {
        const int blockSize = 4 * 1024 * 1024;
        var buffer = new byte[64 * 1024];
        long bytes = 0;
        var watch = Stopwatch.StartNew();
        var lastProgress = TimeSpan.Zero;
        using var phase = CancellationTokenSource.CreateLinkedTokenSource(token);
        phase.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (bytes < MaximumDownloadBytes && watch.Elapsed < TimeSpan.FromSeconds(12))
            {
                var target = Math.Min(blockSize, MaximumDownloadBytes - bytes);
                using var request = new HttpRequestMessage(HttpMethod.Get, NewDownloadUri(target));
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, phase.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentType?.MediaType?.Contains("text", StringComparison.OrdinalIgnoreCase) == true)
                    throw new HttpRequestException("测速服务返回了非测速数据，可能被网络代理或访问验证拦截。");
                using var stream = await response.Content.ReadAsStreamAsync(phase.Token).ConfigureAwait(false);
                long received = 0;
                while (received < target)
                {
                    var count = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, target - received)), phase.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    received += count;
                    bytes += count;
                    if (watch.Elapsed - lastProgress >= TimeSpan.FromMilliseconds(150))
                    {
                        lastProgress = watch.Elapsed;
                        progress?.Report(new SpeedTestProgress("download", 12 + 48 * Math.Max(bytes / (double)MaximumDownloadBytes, Math.Min(1, watch.Elapsed.TotalSeconds / 15)),
                            Mbps(bytes, watch.Elapsed), bytes, "正在测量下载速度…"));
                    }
                }
                if (received != target)
                    throw new IOException("下载测速数据提前结束，请重试。");
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && bytes > 0)
        {
            // Download bytes already received are measurable even when the phase time budget ends.
        }
        if (bytes == 0) throw new IOException("没有收到下载测速数据。");
        watch.Stop();
        progress?.Report(new SpeedTestProgress("download", 60, Mbps(bytes, watch.Elapsed), bytes, "下载测试完成"));
        return new Transfer(bytes, Mbps(bytes, watch.Elapsed));
    }

    private async Task<Transfer> UploadAsync(long downloadedBytes, IProgress<SpeedTestProgress>? progress, CancellationToken token)
    {
        var payload = new byte[256 * 1024];
        RandomNumberGenerator.Fill(payload);
        long bytes = 0;
        var watch = Stopwatch.StartNew();
        using var phase = CancellationTokenSource.CreateLinkedTokenSource(token);
        phase.CancelAfter(TimeSpan.FromSeconds(15));
        while (bytes < MaximumUploadBytes && watch.Elapsed < TimeSpan.FromSeconds(12))
        {
            // Small fixed blocks bound unacknowledged traffic and allow progress on slower uplinks.
            var count = (int)Math.Min(payload.Length, MaximumUploadBytes - bytes);
            using var content = new ByteArrayContent(payload, 0, count);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(Endpoint, "__up")) { Content = content };
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, phase.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await response.Content.CopyToAsync(Stream.Null, phase.Token).ConfigureAwait(false);
            bytes += count;
            progress?.Report(new SpeedTestProgress("upload", 60 + 39 * Math.Max(bytes / (double)MaximumUploadBytes, Math.Min(1, watch.Elapsed.TotalSeconds / 15)),
                Mbps(bytes, watch.Elapsed), downloadedBytes + bytes, "正在测量上传速度…"));
        }
        if (bytes == 0) throw new IOException("没有完成上传测速数据传输。");
        watch.Stop();
        return new Transfer(bytes, Mbps(bytes, watch.Elapsed));
    }

    private static Uri NewDownloadUri(long bytes) => new(Endpoint, $"__down?bytes={bytes}&pulse={Guid.NewGuid():N}");
    private static double Mbps(long bytes, TimeSpan elapsed) => bytes * 8d / Math.Max(elapsed.TotalSeconds, 0.001) / 1_000_000;
    private sealed record Transfer(long Bytes, double Mbps);
    public void Dispose() => _http.Dispose();
}
