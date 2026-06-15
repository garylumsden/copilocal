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

    public (bool Ok, int Status, string Body) PostJson(string url, string json, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = Http.PostAsync(url, content, cts.Token).GetAwaiter().GetResult();
        string body = resp.Content.ReadAsStringAsync(cts.Token).GetAwaiter().GetResult();
        return (resp.IsSuccessStatusCode, (int)resp.StatusCode, body);
    }

    public string GetString(string url, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        return Http.GetStringAsync(url, cts.Token).GetAwaiter().GetResult();
    }

    public void DownloadToFile(string url, string path, int timeoutMs)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        using var resp = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).GetAwaiter().GetResult();
        resp.EnsureSuccessStatusCode();
        using var fs = File.Create(path);
        resp.Content.CopyToAsync(fs, cts.Token).GetAwaiter().GetResult();
    }
}
