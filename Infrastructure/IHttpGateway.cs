namespace Copilocal.Infrastructure;

internal interface IHttpGateway
{
    (bool Ok, int Status, string Body) PostJson(string url, string json, int timeoutMs, string? bearerToken = null);

    string GetString(string url, int timeoutMs, string? bearerToken = null);

    void DownloadToFile(string url, string path, int timeoutMs);
}
