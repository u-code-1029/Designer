using System;

namespace DrillFlow.Application.Http;

/// <summary>Produces a diagnostic URL without request credentials or token-bearing suffixes.</summary>
public static class HttpRequestDiagnostics
{
    public static string GetSafeLogUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return "<invalid-url>";
        }

        // GetLeftPart alone retains authority user-info. Rebuild the URI before removing the
        // query and fragment so every HTTP diagnostic uses the same credential-safe policy.
        var safe = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty,
            Query = string.Empty,
            Fragment = string.Empty
        };
        return safe.Uri.GetLeftPart(UriPartial.Path);
    }
}
