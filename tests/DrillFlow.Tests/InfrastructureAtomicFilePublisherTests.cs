using System;
using System.IO;
using DrillFlow.Infrastructure.IO;
using Xunit;

namespace DrillFlow.Tests;

public sealed class InfrastructureAtomicFilePublisherTests
{
    [Theory]
    [InlineData(1, true)]   // ERROR_INVALID_FUNCTION
    [InlineData(50, true)]  // ERROR_NOT_SUPPORTED
    [InlineData(120, true)] // ERROR_CALL_NOT_IMPLEMENTED
    [InlineData(5, false)]  // ERROR_ACCESS_DENIED
    [InlineData(32, false)] // ERROR_SHARING_VIOLATION
    [InlineData(33, false)] // ERROR_LOCK_VIOLATION
    [InlineData(53, false)] // ERROR_BAD_NETPATH
    [InlineData(64, false)] // ERROR_NETNAME_DELETED
    public void ReplaceFallback_IsLimitedToKnownUnsupportedErrors(int nativeError, bool expected)
    {
        var exception = new IOException(
            "simulated File.Replace failure",
            unchecked((int)(0x80070000u | (uint)nativeError)));

        Assert.Equal(expected, AtomicFilePublisher.IsKnownUnsupportedReplaceError(exception));
    }

    [Fact]
    public void Publisher_ReplacesDestinationWithACompletelyWrittenTempFile()
    {
        using var directory = new InfrastructureTestDirectory();
        var destination = Path.Combine(directory.Path, "request.json");
        var temp = Path.Combine(directory.Path, "request.completed.tmp");
        File.WriteAllText(destination, "old complete content");
        File.WriteAllText(temp, "new complete content");

        AtomicFilePublisher.PublishCompletedTempFile(temp, destination);

        Assert.Equal("new complete content", File.ReadAllText(destination));
        Assert.False(File.Exists(temp));
    }
}
