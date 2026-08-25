using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Application.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DrillFlow.Infrastructure.Http;

/// <summary>
/// Executes HTTP designer actions without involving the equipment file
/// transport. One long-lived HttpClient is supplied by DI to avoid socket
/// exhaustion on .NET Framework.
/// </summary>
public sealed class HttpActionExecutor : IHttpActionExecutor
{
    private readonly HttpClient _client;
    private readonly ILogger<HttpActionExecutor> _logger;

    public HttpActionExecutor(HttpClient client, ILogger<HttpActionExecutor> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<HttpActionResponse> ExecuteAsync(
        HttpActionRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request == null)
        {
            throw new ArgumentNullException(nameof(request));
        }

        if (request.Timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "HTTP timeout must be positive.");
        }

        var headers = ParseHeaders(request.Headers);
        var safeLogUrl = GetSafeLogUrl(request.Url);
        using (var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Url))
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            message.Content = CreateContent(request.Body);
            ApplyHeaders(message, headers);
            timeout.CancelAfter(request.Timeout);

            _logger.LogInformation(
                "Executing designer HTTP {Method} request to {Url} with timeout {TimeoutMs} ms",
                request.Method,
                safeLogUrl,
                request.Timeout.TotalMilliseconds);

            HttpResponseMessage response;
            try
            {
                response = await _client.SendAsync(message, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"HTTP {request.Method} request to '{safeLogUrl}' exceeded "
                    + $"{request.Timeout.TotalMilliseconds:0} ms.",
                    exception);
            }

            using (response)
            {
                var body = response.Content == null
                    ? string.Empty
                    : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                var responseHeaders = ReadResponseHeaders(response);
                var contentType = response.Content?.Headers.ContentType?.ToString() ?? string.Empty;
                var json = TryParseJson(body);

                _logger.LogInformation(
                    "Designer HTTP request to {Url} completed with status {StatusCode}",
                    safeLogUrl,
                    (int)response.StatusCode);

                return new HttpActionResponse(
                    (int)response.StatusCode,
                    response.ReasonPhrase ?? string.Empty,
                    responseHeaders,
                    body,
                    contentType,
                    json);
            }
        }
    }

    private static IReadOnlyDictionary<string, string[]> ParseHeaders(object? value)
    {
        if (value == null)
        {
            return new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        }

        JToken token;
        try
        {
            if (value is string text)
            {
                if (string.IsNullOrWhiteSpace(text))
                {
                    return new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                }

                token = JToken.Parse(text);
            }
            else
            {
                token = JToken.FromObject(value);
            }
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("HTTP headers must be a valid JSON object.", nameof(value), exception);
        }

        if (!(token is JObject objectToken))
        {
            throw new ArgumentException("HTTP headers must be a JSON object.", nameof(value));
        }

        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in objectToken.Properties())
        {
            if (string.IsNullOrWhiteSpace(property.Name))
            {
                throw new ArgumentException("HTTP header names cannot be empty.", nameof(value));
            }

            var values = property.Value is JArray array
                ? array.Select(HeaderValue).ToArray()
                : new[] { HeaderValue(property.Value) };
            headers[property.Name] = values;
        }

        return headers;
    }

    private static string GetSafeLogUrl(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri)
            ? uri.GetLeftPart(UriPartial.Path)
            : "<invalid-url>";
    }

    private static string HeaderValue(JToken token)
    {
        if (token.Type == JTokenType.Null || token.Type == JTokenType.Undefined)
        {
            return string.Empty;
        }

        if (token is JValue value)
        {
            return Convert.ToString(value.Value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        throw new ArgumentException("HTTP header values must be scalar values or arrays of scalar values.");
    }

    private static HttpContent? CreateContent(object? body)
    {
        if (body == null)
        {
            return null;
        }

        if (body is string text)
        {
            if (text.Length == 0)
            {
                return null;
            }

            return new StringContent(
                text,
                Encoding.UTF8,
                IsJson(text) ? "application/json" : "text/plain");
        }

        return new StringContent(
            JsonConvert.SerializeObject(body),
            Encoding.UTF8,
            "application/json");
    }

    private static void ApplyHeaders(
        HttpRequestMessage request,
        IReadOnlyDictionary<string, string[]> headers)
    {
        foreach (var header in headers)
        {
            if (string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                if (request.Content == null)
                {
                    request.Content = new ByteArrayContent(Array.Empty<byte>());
                }

                request.Content.Headers.Remove(header.Key);
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                if (request.Content == null)
                {
                    request.Content = new ByteArrayContent(Array.Empty<byte>());
                }

                if (!request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value))
                {
                    throw new ArgumentException($"HTTP header '{header.Key}' is not valid.");
                }
            }
        }
    }

    private static IReadOnlyDictionary<string, string[]> ReadResponseHeaders(HttpResponseMessage response)
    {
        var headers = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers)
        {
            headers[header.Key] = header.Value.ToArray();
        }

        if (response.Content != null)
        {
            foreach (var header in response.Content.Headers)
            {
                headers[header.Key] = header.Value.ToArray();
            }
        }

        return headers;
    }

    private static bool IsJson(string text)
    {
        try
        {
            JToken.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static object? TryParseJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            using (var reader = new JsonTextReader(new StringReader(text))
            {
                DateParseHandling = DateParseHandling.None,
                FloatParseHandling = FloatParseHandling.Double
            })
            {
                return ConvertToken(JToken.ReadFrom(reader));
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static object? ConvertToken(JToken token)
    {
        switch (token.Type)
        {
            case JTokenType.Object:
                return ConvertObject((JObject)token);
            case JTokenType.Array:
                return ((JArray)token).Select(ConvertToken).ToArray();
            case JTokenType.Integer:
            case JTokenType.Float:
                return Convert.ToDouble(((JValue)token).Value, CultureInfo.InvariantCulture);
            case JTokenType.Boolean:
                return token.Value<bool>();
            case JTokenType.Null:
            case JTokenType.Undefined:
                return null;
            case JTokenType.String:
            case JTokenType.Date:
            case JTokenType.Guid:
            case JTokenType.Uri:
            case JTokenType.TimeSpan:
                return Convert.ToString(((JValue)token).Value, CultureInfo.InvariantCulture);
            default:
                return token.ToString(Formatting.None);
        }
    }

    private static IReadOnlyDictionary<string, object?> ConvertObject(JObject token)
    {
        var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in token.Properties())
        {
            // Expressions are case-insensitive, so JSON names that differ only
            // by case cannot be represented separately. Keep the last value,
            // matching the rest of the expression object model.
            result[property.Name] = ConvertToken(property.Value);
        }

        return result;
    }
}
