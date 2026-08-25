using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Application.Http;
using DrillFlow.Infrastructure.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace DrillFlow.Tests;

public sealed class InfrastructureHttpActionExecutorTests
{
    [Fact]
    public async Task ExecuteAsync_SendsPostHeadersAndJsonBodyAndParsesDynamicJson()
    {
        string? observedMethod = null;
        string? observedUrl = null;
        string? observedAuthorization = null;
        string? observedContentType = null;
        string? observedBody = null;
        var handler = new DelegateHandler(async (request, _) =>
        {
            observedMethod = request.Method.Method;
            observedUrl = request.RequestUri!.AbsoluteUri;
            observedAuthorization = string.Join(",", request.Headers.GetValues("Authorization"));
            observedContentType = request.Content!.Headers.ContentType!.MediaType;
            observedBody = await request.Content.ReadAsStringAsync();
            var response = new HttpResponseMessage(HttpStatusCode.Created)
            {
                ReasonPhrase = "Created",
                Content = new StringContent(
                    "{\"job\":{\"id\":17,\"ready\":true},\"items\":[{\"name\":\"A\"},2,null]}",
                    Encoding.UTF8,
                    "application/json")
            };
            response.Headers.TryAddWithoutValidation("X-Trace", "trace-1");
            return response;
        });
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var executor = new HttpActionExecutor(client, NullLogger<HttpActionExecutor>.Instance);

        var response = await executor.ExecuteAsync(new HttpActionRequest(
            "POST",
            "https://example.test/jobs",
            "{\"Authorization\":\"Bearer abc\",\"Content-Type\":\"application/json\"}",
            new Dictionary<string, object?> { ["name"] = "sample", ["count"] = 2 },
            TimeSpan.FromSeconds(5)));

        Assert.Equal("POST", observedMethod);
        Assert.Equal("https://example.test/jobs", observedUrl);
        Assert.Equal("Bearer abc", observedAuthorization);
        Assert.Equal("application/json", observedContentType);
        Assert.Contains("\"name\":\"sample\"", observedBody, StringComparison.Ordinal);
        Assert.Equal(201, response.StatusCode);
        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal("trace-1", Assert.Single(response.Headers["X-Trace"]));
        var root = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(response.Json);
        var job = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(root["job"]);
        Assert.Equal(17d, job["id"]);
        Assert.Equal(true, job["ready"]);
        var items = Assert.IsType<object?[]>(root["items"]);
        Assert.Equal(3, items.Length);
        Assert.Null(items[2]);
    }

    [Fact]
    public async Task ExecuteAsync_KeepsNonJsonAndNonSuccessResponsesAvailable()
    {
        var handler = new DelegateHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            ReasonPhrase = "Bad Request",
            Content = new StringContent("not-json", Encoding.UTF8, "text/plain")
        }));
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var executor = new HttpActionExecutor(client, NullLogger<HttpActionExecutor>.Instance);

        var response = await executor.ExecuteAsync(new HttpActionRequest(
            "GET",
            "https://example.test/invalid",
            null,
            null,
            TimeSpan.FromSeconds(5)));

        Assert.Equal(400, response.StatusCode);
        Assert.False(response.IsSuccessStatusCode);
        Assert.Equal("not-json", response.Body);
        Assert.Null(response.Json);
    }

    [Fact]
    public async Task ExecuteAsync_RejectsMalformedHeadersBeforeNetworkTraffic()
    {
        var calls = 0;
        var handler = new DelegateHandler((_, _) =>
        {
            Interlocked.Increment(ref calls);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var executor = new HttpActionExecutor(client, NullLogger<HttpActionExecutor>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => executor.ExecuteAsync(new HttpActionRequest(
            "GET",
            "https://example.test/",
            "[\"not\",\"an\",\"object\"]",
            null,
            TimeSpan.FromSeconds(5))));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task ExecuteAsync_DistinguishesOwnTimeoutFromCallerCancellation()
    {
        var handler = new DelegateHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        var executor = new HttpActionExecutor(client, NullLogger<HttpActionExecutor>.Instance);
        var request = new HttpActionRequest(
            "GET",
            "https://example.test/slow?token=must-not-be-logged",
            null,
            null,
            TimeSpan.FromMilliseconds(50));

        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => executor.ExecuteAsync(request));
        Assert.DoesNotContain("must-not-be-logged", timeout.Message, StringComparison.Ordinal);

        using var callerCancellation = new CancellationTokenSource();
        callerCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => executor.ExecuteAsync(request, callerCancellation.Token));
    }

    private sealed class DelegateHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;

        public DelegateHandler(
            Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        {
            _send = send;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return _send(request, cancellationToken);
        }
    }
}
