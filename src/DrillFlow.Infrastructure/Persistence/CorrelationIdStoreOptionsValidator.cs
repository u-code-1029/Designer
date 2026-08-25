using System;
using System.Collections.Generic;
using System.IO;
using DrillFlow.Application.Persistence;
using Microsoft.Extensions.Options;

namespace DrillFlow.Infrastructure.Persistence;

public sealed class CorrelationIdStoreOptionsValidator : IValidateOptions<CorrelationIdStoreOptions>
{
    public ValidateOptionsResult Validate(string? name, CorrelationIdStoreOptions options)
    {
        if (options is null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        var failures = new List<string>();
        if (string.IsNullOrWhiteSpace(options.StateFilePath))
        {
            failures.Add("A correlation ID state file path is required.");
        }
        else
        {
            if (!Path.IsPathRooted(options.StateFilePath))
            {
                failures.Add("The correlation ID state file path must be absolute.");
            }

            if (string.IsNullOrWhiteSpace(Path.GetFileName(options.StateFilePath)))
            {
                failures.Add("The correlation ID state file path must identify a file.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}

