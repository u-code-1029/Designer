using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DrillFlow.Application.Http;

/// <summary>
/// Executes designer-owned HTTP requests. Keeping this behind an interface
/// isolates workflow orchestration from System.Net.Http and makes execution
/// deterministic in tests.
/// </summary>
public interface IHttpActionExecutor
{
    Task<HttpActionResponse> ExecuteAsync(
        HttpActionRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class HttpActionRequest
{
    public HttpActionRequest(
        string method,
        string url,
        object? headers,
        object? body,
        TimeSpan timeout)
    {
        Method = method ?? throw new ArgumentNullException(nameof(method));
        Url = url ?? throw new ArgumentNullException(nameof(url));
        Headers = headers;
        Body = body;
        Timeout = timeout;
    }

    public string Method { get; }

    public string Url { get; }

    public object? Headers { get; }

    public object? Body { get; }

    public TimeSpan Timeout { get; }
}

public sealed class HttpActionResponse
{
    public HttpActionResponse(
        int statusCode,
        string reasonPhrase,
        IReadOnlyDictionary<string, string[]> headers,
        string body,
        string contentType,
        object? json)
    {
        StatusCode = statusCode;
        ReasonPhrase = reasonPhrase ?? string.Empty;
        Headers = headers ?? throw new ArgumentNullException(nameof(headers));
        Body = body ?? string.Empty;
        ContentType = contentType ?? string.Empty;
        Json = json;
    }

    public int StatusCode { get; }

    public bool IsSuccessStatusCode => StatusCode >= 200 && StatusCode <= 299;

    public string ReasonPhrase { get; }

    public IReadOnlyDictionary<string, string[]> Headers { get; }

    public string Body { get; }

    public string ContentType { get; }

    /// <summary>
    /// Parsed JSON composed only of dictionaries, arrays and primitive CLR
    /// values. Null means that the response body was empty or was not JSON.
    /// </summary>
    public object? Json { get; }
}
