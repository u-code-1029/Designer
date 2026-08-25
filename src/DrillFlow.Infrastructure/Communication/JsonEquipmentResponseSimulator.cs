using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DrillFlow.Application.Communication;
using DrillFlow.Core.Workflows;
using DrillFlow.Infrastructure.IO;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DrillFlow.Infrastructure.Communication;

/// <summary>
/// JSON commissioning implementation. It deliberately does not take the equipment exchange lock:
/// the real transport owns that lock while waiting for this response. Publication still uses a
/// fully flushed temporary file and atomic pathname replacement.
/// </summary>
public sealed class JsonEquipmentResponseSimulator : IEquipmentResponseSimulator
{
    private readonly EquipmentCommunicationOptions _options;

    public JsonEquipmentResponseSimulator(IOptions<EquipmentCommunicationOptions> options)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
    }

    public string PayloadFormat => "JSON";

    public async Task<EquipmentResponseSimulationDraft> CreateDraftAsync(
        WorkflowNode node,
        int? fallbackCorrelationId,
        CancellationToken cancellationToken)
    {
        if (node == null)
        {
            throw new ArgumentNullException(nameof(node));
        }

        var requestPath = Path.Combine(_options.ExchangeDirectory, _options.RequestFileName);
        var responsePath = Path.Combine(_options.ExchangeDirectory, _options.ResponseFileName);
        var activeRequest = await TryReadRequestAsync(requestPath, cancellationToken).ConfigureAwait(false);
        var index = activeRequest?.Index ?? fallbackCorrelationId ?? 1;
        var response = CreateTemplate(node, index);
        return new EquipmentResponseSimulationDraft(
            response.ToString(Formatting.Indented),
            responsePath,
            activeRequest);
    }

    public ResponsePayloadValidationResult ValidatePayload(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return ResponsePayloadValidationResult.Failure("Response payload is empty.");
        }

        JObject root;
        try
        {
            root = JObject.Parse(payload);
        }
        catch (JsonException exception)
        {
            return ResponsePayloadValidationResult.Failure("Invalid JSON: " + exception.Message);
        }

        if (!TryReadPositiveIndex(root["index"], out _))
        {
            return ResponsePayloadValidationResult.Failure("'index' must be a positive 32-bit integer.");
        }

        if (root["command"]?.Type != JTokenType.String
            || !string.Equals(root["command"]!.Value<string>(), "return", StringComparison.Ordinal))
        {
            return ResponsePayloadValidationResult.Failure("'command' must be exactly 'return'.");
        }

        return ResponsePayloadValidationResult.Success;
    }

    public async Task PublishAsync(string payload, CancellationToken cancellationToken)
    {
        var validation = ValidatePayload(payload);
        if (!validation.IsValid)
        {
            throw new InvalidDataException(string.Join(" ", validation.Errors));
        }

        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_options.ExchangeDirectory);
        var responseRoot = JObject.Parse(payload);
        TryReadPositiveIndex(responseRoot["index"], out var responseIndex);
        if (_options.EquipmentRequestLifecycle
            == EquipmentRequestFileLifecycle.EquipmentDeletesAfterRead)
        {
            await DeleteMatchingRequestAsync(responseIndex, cancellationToken).ConfigureAwait(false);
        }

        var responsePath = Path.Combine(_options.ExchangeDirectory, _options.ResponseFileName);
        var tempPath = responsePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(payload);
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(true);
            }

            cancellationToken.ThrowIfCancellationRequested();
            AtomicFilePublisher.PublishCompletedTempFile(tempPath, responsePath);
        }
        finally
        {
            TryDelete(tempPath);
        }
    }

    private async Task DeleteMatchingRequestAsync(int responseIndex, CancellationToken cancellationToken)
    {
        var requestPath = Path.Combine(_options.ExchangeDirectory, _options.RequestFileName);
        var activeRequest = await TryReadRequestAsync(requestPath, cancellationToken).ConfigureAwait(false);
        if (activeRequest == null)
        {
            // The real controller (or a previous test step) may already have consumed it.
            return;
        }

        if (activeRequest.Index != responseIndex)
        {
            throw new InvalidOperationException(
                $"The active request index is {activeRequest.Index}, but the simulated response index is "
                + $"{responseIndex}. The request was not deleted.");
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                File.Delete(requestPath);
                return;
            }
            catch (FileNotFoundException)
            {
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(_options.PollingInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(_options.PollingInterval, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static JObject CreateTemplate(WorkflowNode node, int index)
    {
        var response = new JObject
        {
            ["index"] = index,
            ["command"] = "return"
        };

        switch (node)
        {
            case MoveNode _:
                response["position_x"] = 0d;
                response["position_y"] = 0d;
                break;
            case MeasureNode _:
                response["measured_distance"] = 1E-3d;
                break;
            case DrillNode drill:
                var authoredPath = drill.DrillResultPath?.RawText;
                response["drill_result_path"] = drill.DrillResultPath != null
                                                 && !drill.DrillResultPath.IsExpression
                                                 && !string.IsNullOrWhiteSpace(authoredPath)
                    ? authoredPath
                    : @"C:\DrillFlow\Results\drill-result.csv";
                break;
            case AbortNode _:
                break;
            default:
                throw new ArgumentException(
                    "Only equipment actions have a response payload.",
                    nameof(node));
        }

        return response;
    }

    private static async Task<EquipmentRequestSnapshot?> TryReadRequestAsync(
        string requestPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string json;
        try
        {
            using (var stream = new FileStream(
                       requestPath,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete,
                       4096,
                       FileOptions.Asynchronous))
            using (var reader = new StreamReader(stream, Encoding.UTF8, true))
            {
                json = await reader.ReadToEndAsync().ConfigureAwait(false);
            }
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var root = JObject.Parse(json);
            var index = root["index"];
            var command = root["command"];
            if (!TryReadPositiveIndex(index, out var requestIndex)
                || command?.Type != JTokenType.String)
            {
                return null;
            }

            return new EquipmentRequestSnapshot(requestIndex, command.Value<string>()!);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryReadPositiveIndex(JToken? token, out int index)
    {
        index = 0;
        return token?.Type == JTokenType.Integer
               && int.TryParse(
                   token.ToString(Formatting.None),
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out index)
               && index > 0;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
