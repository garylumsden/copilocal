using Copilocal;

namespace Copilocal.Tests.Fakes;

internal sealed class FakeHttpGateway : IHttpGateway
{
    private readonly Dictionary<string, Queue<object>> _postResponses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Queue<object>> _getResponses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Exception> _downloadExceptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _downloadContents = new(StringComparer.Ordinal);

    internal List<PostCall> PostCalls { get; } = [];
    internal List<GetCall> GetCalls { get; } = [];
    internal List<DownloadCall> DownloadCalls { get; } = [];

    internal void AddPost(string url, bool ok, int status, string body) =>
        Enqueue(_postResponses, url, new PostResponse(ok, status, body));

    internal void AddPostException(string url, Exception exception) =>
        Enqueue(_postResponses, url, exception);

    internal void AddGet(string url, string body) =>
        Enqueue(_getResponses, url, body);

    internal void AddGetException(string url, Exception exception) =>
        Enqueue(_getResponses, url, exception);

    internal void AddDownloadException(string url, Exception exception) =>
        _downloadExceptions[url] = exception;

    internal void AddDownloadContent(string url, string content) =>
        _downloadContents[url] = content;

    public (bool Ok, int Status, string Body) PostJson(string url, string json, int timeoutMs)
    {
        PostCalls.Add(new PostCall(url, json, timeoutMs));
        var response = Dequeue(_postResponses, url);

        return response switch
        {
            PostResponse r => (r.Ok, r.Status, r.Body),
            Exception ex => throw ex,
            null => throw new InvalidOperationException($"No fake POST response configured for '{url}'."),
            _ => throw new InvalidOperationException($"Unsupported fake POST response for '{url}'."),
        };
    }

    public string GetString(string url, int timeoutMs)
    {
        GetCalls.Add(new GetCall(url, timeoutMs));
        var response = Dequeue(_getResponses, url);

        return response switch
        {
            string body => body,
            Exception ex => throw ex,
            null => throw new InvalidOperationException($"No fake GET response configured for '{url}'."),
            _ => throw new InvalidOperationException($"Unsupported fake GET response for '{url}'."),
        };
    }

    public void DownloadToFile(string url, string path, int timeoutMs)
    {
        DownloadCalls.Add(new DownloadCall(url, path, timeoutMs));

        if (_downloadExceptions.TryGetValue(url, out var exception))
            throw exception;

        if (_downloadContents.TryGetValue(url, out var content))
            File.WriteAllText(path, content);
    }

    private static void Enqueue(Dictionary<string, Queue<object>> responses, string url, object response)
    {
        if (!responses.TryGetValue(url, out var queue))
        {
            queue = new Queue<object>();
            responses[url] = queue;
        }

        queue.Enqueue(response);
    }

    private static object? Dequeue(Dictionary<string, Queue<object>> responses, string url) =>
        responses.TryGetValue(url, out var queue) && queue.Count > 0 ? queue.Dequeue() : null;

    private sealed record PostResponse(bool Ok, int Status, string Body);

    internal sealed record PostCall(string Url, string Json, int TimeoutMs);

    internal sealed record GetCall(string Url, int TimeoutMs);

    internal sealed record DownloadCall(string Url, string Path, int TimeoutMs);
}
