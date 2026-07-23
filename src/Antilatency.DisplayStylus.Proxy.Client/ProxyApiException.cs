namespace Antilatency.DisplayStylus.Proxy.Client;

public sealed class ProxyApiException : Exception {
    public ProxyApiException(int statusCode, string code, string message) : base(message) {
        StatusCode = statusCode;
        Code = code;
    }

    public int StatusCode { get; }
    public string Code { get; }
}
