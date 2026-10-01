using DrillFlow.Application.Http;
using Xunit;

namespace DrillFlow.Tests;

public sealed class ApplicationHttpRequestDiagnosticsTests
{
    [Theory]
    [InlineData("https://operator:secret@example.test/camera/frame?token=secret#preview", "https://example.test/camera/frame")]
    [InlineData("http://operator:secret@example.test:8080/path?token=secret#preview", "http://example.test:8080/path")]
    [InlineData("https://example.test/camera/frame", "https://example.test/camera/frame")]
    [InlineData("https://[::1]:8443/camera?token=secret", "https://[::1]:8443/camera")]
    public void SafeLogUrl_PreservesEndpointWithoutCredentialsQueryOrFragment(
        string url,
        string expected)
    {
        Assert.Equal(expected, HttpRequestDiagnostics.GetSafeLogUrl(url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a URL with secret")]
    [InlineData("/relative?token=secret")]
    [InlineData("mailto:secret@example.test")]
    [InlineData("file:///secret.txt")]
    public void SafeLogUrl_RejectsNonHttpOrInvalidValues(string? url)
    {
        Assert.Equal("<invalid-url>", HttpRequestDiagnostics.GetSafeLogUrl(url));
    }
}
