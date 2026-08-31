using System;
using System.Collections.Generic;
using System.IO;
using DrillFlow.Application.Communication;
using Microsoft.Extensions.Options;

namespace DrillFlow.Infrastructure.Communication;

public sealed class EquipmentCommunicationOptionsValidator
    : IValidateOptions<EquipmentCommunicationOptions>
{
    public ValidateOptionsResult Validate(string? name, EquipmentCommunicationOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        return Validate(name, EquipmentCommunicationSnapshot.Capture(options));
    }

    internal ValidateOptionsResult Validate(
        string? name,
        EquipmentCommunicationSnapshot options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var failures = new List<string>();

        if (string.IsNullOrWhiteSpace(options.ExchangeDirectory))
        {
            failures.Add("An equipment exchange directory is required.");
        }
        else if (!IsAbsoluteWindowsDirectory(options.ExchangeDirectory))
        {
            failures.Add("The equipment exchange directory must be an absolute local or UNC path.");
        }

        if (string.IsNullOrWhiteSpace(options.LiveImageDirectory))
        {
            failures.Add("A Live image directory is required.");
        }
        else if (!IsAbsoluteWindowsDirectory(options.LiveImageDirectory))
        {
            failures.Add("The Live image directory must be an absolute local or UNC path.");
        }

        ValidateLeafFileName(options.RequestFileName, "request", failures);
        ValidateLeafFileName(options.ResponseFileName, "response", failures);

        if (!string.IsNullOrWhiteSpace(options.RequestFileName)
            && !string.IsNullOrWhiteSpace(options.ResponseFileName)
            && string.Equals(
                options.RequestFileName,
                options.ResponseFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("Request and response file names must be different.");
        }

        if (string.Equals(
                options.RequestFileName,
                EquipmentCommunicationOptions.ExchangeLockFileName,
                StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                options.ResponseFileName,
                EquipmentCommunicationOptions.ExchangeLockFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            failures.Add(
                $"Request and response file names must be different from the reserved exchange "
                + $"lock file '{EquipmentCommunicationOptions.ExchangeLockFileName}'.");
        }

        ValidatePositiveDelay(options.ResponseTimeout, nameof(options.ResponseTimeout), failures);
        ValidatePositiveDelay(options.PollingInterval, nameof(options.PollingInterval), failures);
        ValidatePositiveDelay(options.StableReadDelay, nameof(options.StableReadDelay), failures);
        ValidateNonNegativeDelay(options.RetryDelay, nameof(options.RetryDelay), failures);
        ValidateNonNegativeDelay(
            options.RequestPublishDelay,
            nameof(options.RequestPublishDelay),
            failures);

        if (options.MaximumRetryCount < 0)
        {
            failures.Add("MaximumRetryCount cannot be negative.");
        }
        else if (options.RetryEnabled && options.MaximumRetryCount == 0)
        {
            failures.Add("MaximumRetryCount must be at least one when retry is enabled.");
        }

        if (!Enum.IsDefined(typeof(EquipmentRequestFileLifecycle), options.EquipmentRequestLifecycle))
        {
            failures.Add("EquipmentRequestLifecycle has an unsupported value.");
        }

        if (!Enum.IsDefined(typeof(ApplicationRequestFileLifecycle), options.ApplicationRequestLifecycle))
        {
            failures.Add("ApplicationRequestLifecycle has an unsupported value.");
        }

        if (!Enum.IsDefined(typeof(ApplicationResponseFileLifecycle), options.ApplicationResponseLifecycle))
        {
            failures.Add("ApplicationResponseLifecycle has an unsupported value.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void ValidateLeafFileName(
        string? value,
        string role,
        ICollection<string> failures)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            failures.Add($"A {role} file name is required.");
            return;
        }

        var fileName = value!;

        if (!string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
            || Path.IsPathRooted(fileName)
            || fileName == "."
            || fileName == "..")
        {
            failures.Add($"The {role} file name must be a leaf name without a directory.");
        }

        if (fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            failures.Add($"The {role} file name contains an invalid character.");
        }

        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension) || extension == ".")
        {
            failures.Add($"The {role} file name must include an extension.");
        }
    }

    private static bool IsAbsoluteWindowsDirectory(string value)
    {
        var path = value.Trim();
        for (var index = 0; index < path.Length; index++)
        {
            var character = path[index];
            if (character < ' '
                || character == '"'
                || character == '<'
                || character == '>'
                || character == '|'
                || character == '?'
                || character == '*'
                || character == ':' && index != 1)
            {
                return false;
            }
        }

        var isDriveAbsolute = path.Length >= 3
                              && IsAsciiLetter(path[0])
                              && path[1] == ':'
                              && IsDirectorySeparator(path[2]);
        if (isDriveAbsolute)
        {
            return true;
        }

        if (path.Length < 5
            || !IsDirectorySeparator(path[0])
            || !IsDirectorySeparator(path[1])
            || IsDirectorySeparator(path[2]))
        {
            return false;
        }

        var serverEnd = IndexOfDirectorySeparator(path, 2);
        if (serverEnd <= 2 || serverEnd == path.Length - 1)
        {
            return false;
        }

        var shareStart = serverEnd + 1;
        var shareEnd = IndexOfDirectorySeparator(path, shareStart);
        return (shareEnd < 0 ? path.Length : shareEnd) > shareStart;
    }

    private static int IndexOfDirectorySeparator(string value, int startIndex)
    {
        for (var index = startIndex; index < value.Length; index++)
        {
            if (IsDirectorySeparator(value[index]))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsDirectorySeparator(char value) => value == '\\' || value == '/';

    private static bool IsAsciiLetter(char value) =>
        value >= 'A' && value <= 'Z' || value >= 'a' && value <= 'z';

    private static void ValidatePositiveDelay(
        TimeSpan value,
        string propertyName,
        ICollection<string> failures)
    {
        if (value <= TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue)
        {
            failures.Add($"{propertyName} must be greater than zero and no longer than {int.MaxValue} ms.");
        }
    }

    private static void ValidateNonNegativeDelay(
        TimeSpan value,
        string propertyName,
        ICollection<string> failures)
    {
        if (value < TimeSpan.Zero || value.TotalMilliseconds > int.MaxValue)
        {
            failures.Add($"{propertyName} must be non-negative and no longer than {int.MaxValue} ms.");
        }
    }
}
