using System.Net.Http.Headers;
using System.Reflection;
using System.Text;

namespace Copilocal.Infrastructure;

internal sealed class HttpGateway : IHttpGateway
{
    static readonly HttpClient Http = CreateHttp();

    static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        // GitHub's API requires a User-Agent; harmless for local providers. Derive the
        // version from the assembly so it never drifts from the published build.
        string version = typeof(HttpGateway).Assembly.GetName().Version?.ToString(3) ?? "0";
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("copilocal", version));
        return c;
    }

    public (bool Ok, int Status, string Body) PostJson(string url, string json, int timeoutMs, string? bearerToken = null)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrWhiteSpace(bearerToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken.Trim());
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = Http.Send(req, cts.Token);
        string body = resp.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
        return (resp.IsSuccessStatusCode, (int)resp.StatusCode, body);
    }

    public string GetString(string url, int timeoutMs, string? bearerToken = null)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(bearerToken))
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken.Trim());
        using var resp = Http.Send(req, cts.Token);
        resp.EnsureSuccessStatusCode();
        return resp.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
    }

    public void DownloadToFile(string url, string path, int timeoutMs)
    {
        string partialPath = path + ".part";
        try
        {
            using var cts = new CancellationTokenSource(timeoutMs);
            using var resp = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).GetAwaiter().GetResult();
            resp.EnsureSuccessStatusCode();
            using (var fs = File.Create(partialPath))
            {
                resp.Content.CopyToAsync(fs, cts.Token).GetAwaiter().GetResult();
            }
            File.Move(partialPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(partialPath);
            throw;
        }
    }

    static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
