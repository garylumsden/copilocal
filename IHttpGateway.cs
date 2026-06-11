namespace Copilocal;

internal interface IHttpGateway
{
    (bool Ok, int Status, string Body) PostJson(string url, string json, int timeoutMs);

    string GetString(string url, int timeoutMs);

    void DownloadToFile(string url, string path, int timeoutMs);
}
