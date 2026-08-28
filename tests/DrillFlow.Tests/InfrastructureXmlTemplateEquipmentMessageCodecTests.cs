using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using DrillFlow.Application.Communication;
using DrillFlow.Infrastructure.Communication;
using Xunit;

namespace DrillFlow.Tests;

public sealed class InfrastructureXmlTemplateEquipmentMessageCodecTests
{
    private readonly XmlTemplateEquipmentMessageCodec _codec = new();

    [Fact]
    public void EmbeddedTemplates_RoundTripAllSixRequestsAndResponses()
    {
        foreach (var request in CreateRequests())
        {
            var requestBytes = _codec.SerializeRequest(request);
            Assert.False(HasUtf8Bom(requestBytes));
            Assert.True(_codec.TryDeserializeRequest(requestBytes, out var restoredRequest));
            Assert.NotNull(restoredRequest);
            Assert.Equal(request.CorrelationId, restoredRequest!.CorrelationId);
            Assert.Equal(request.Action, restoredRequest.Action);

            var response = CreateResponse(request.CorrelationId, request.Action);
            var responseBytes = _codec.SerializeResponse(response);
            Assert.False(HasUtf8Bom(responseBytes));
            Assert.True(_codec.TryDeserializeResponse(responseBytes, request, out var restoredResponse));
            Assert.NotNull(restoredResponse);
            Assert.Equal(response.CorrelationId, restoredResponse!.CorrelationId);
            Assert.Equal(response.Action, restoredResponse.Action);
            Assert.Equal(0, restoredResponse.Result);
        }
    }

    [Fact]
    public void StageRequest_UsesScientificNotationAndXmlEscapesStringFields()
    {
        var integration = new EquipmentRequestMessage(
            42,
            EquipmentActionNames.Integration,
            new Dictionary<string, object?>
            {
                ["hfw"] = 3.02E-6,
                ["frame_count"] = 8,
                ["image_path"] = @"C:\Images\A&B.png"
            });

        var xml = Encoding.UTF8.GetString(_codec.SerializeRequest(integration));

        Assert.Contains("<hfw>3.02E-6</hfw>", xml, StringComparison.Ordinal);
        Assert.Contains("<image_path>C:\\Images\\A&amp;B.png</image_path>", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("{{{", xml, StringComparison.Ordinal);
        Assert.True(_codec.TryDeserializeRequest(Encoding.UTF8.GetBytes(xml), out var restored));
        Assert.Equal(@"C:\Images\A&B.png", restored!.Parameters["image_path"]);
    }

    [Fact]
    public void VendorTextTemplate_ReplacesOnlyExactTokensAndAllowsRepeatedFields()
    {
        var codec = new XmlTemplateEquipmentMessageCodec(LoadVendorLikeTemplate);
        var request = new EquipmentRequestMessage(
            42,
            EquipmentActionNames.Stage,
            new Dictionary<string, object?>
            {
                ["move_mode"] = "relative",
                ["stage_x"] = 1E-6,
                ["stage_y"] = -2.56E-3
            });

        var xml = Encoding.UTF8.GetString(codec.SerializeRequest(request));

        Assert.Contains("<plain>correlation_id correlation_id</plain>", xml, StringComparison.Ordinal);
        Assert.Contains("<!-- correlation_id=42 -->", xml, StringComparison.Ordinal);
        Assert.Contains("correlation_id=\"42\"", xml, StringComparison.Ordinal);
        Assert.Contains("{{{{correlation_id}}}}", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("{{{stage_x}}}", xml, StringComparison.Ordinal);
        Assert.True(codec.TryDeserializeRequest(Encoding.UTF8.GetBytes(xml), out var restored));
        Assert.NotNull(restored);
        Assert.Equal(42, restored!.CorrelationId);
        Assert.Equal(EquipmentActionNames.Stage, restored.Action);
        Assert.Equal("relative", restored.Parameters["move_mode"]);
        Assert.Equal(1E-6, restored.Parameters["stage_x"]);
        Assert.Equal(-2.56E-3, restored.Parameters["stage_y"]);
    }

    [Fact]
    public void RepeatedPlaceholder_MustContainTheSameValueAtEveryParsedPosition()
    {
        var codec = new XmlTemplateEquipmentMessageCodec(LoadVendorLikeTemplate);
        var response = CreateResponse(42, EquipmentActionNames.Stage);
        var xml = Encoding.UTF8.GetString(codec.SerializeResponse(response));
        var expectedRequest = new EquipmentRequestMessage(
            42,
            EquipmentActionNames.Stage,
            new Dictionary<string, object?>
            {
                ["move_mode"] = "relative",
                ["stage_x"] = 0d,
                ["stage_y"] = 0d
            });

        Assert.Contains("<correlation-copy>42</correlation-copy>", xml, StringComparison.Ordinal);
        Assert.True(codec.TryDeserializeResponse(
            Encoding.UTF8.GetBytes(xml),
            expectedRequest,
            out var restored));
        Assert.Equal(42, restored!.CorrelationId);

        var inconsistent = xml.Replace(
            "<correlation-copy>42</correlation-copy>",
            "<correlation-copy>43</correlation-copy>");
        Assert.False(codec.TryDeserializeResponse(
            Encoding.UTF8.GetBytes(inconsistent),
            expectedRequest,
            out _));
    }

    [Fact]
    public void Render_DoesNotInterpretPlaceholderTextInsideAnInsertedValue()
    {
        var path = @"C:\Images\{{{hfw}}}&'frame'.png";
        var request = new EquipmentRequestMessage(
            52,
            EquipmentActionNames.Integration,
            new Dictionary<string, object?>
            {
                ["hfw"] = 1E-3,
                ["frame_count"] = 8,
                ["image_path"] = path
            });

        var xml = Encoding.UTF8.GetString(_codec.SerializeRequest(request));

        Assert.Contains("<hfw>1E-3</hfw>", xml, StringComparison.Ordinal);
        Assert.Contains("{{{hfw}}}", xml, StringComparison.Ordinal);
        Assert.Contains("&amp;&apos;frame&apos;", xml, StringComparison.Ordinal);
        Assert.True(_codec.TryDeserializeRequest(Encoding.UTF8.GetBytes(xml), out var restored));
        Assert.Equal(path, restored!.Parameters["image_path"]);
    }

    [Fact]
    public void GenericDeserializer_RejectsPayloadAcceptedByMultipleActionTemplates()
    {
        var codec = new XmlTemplateEquipmentMessageCodec((action, direction) =>
        {
            if (string.Equals(direction, "request", StringComparison.Ordinal)
                && (string.Equals(action, EquipmentActionNames.Stage, StringComparison.Ordinal)
                    || string.Equals(action, EquipmentActionNames.Camera, StringComparison.Ordinal)))
            {
                var x = string.Equals(action, EquipmentActionNames.Stage, StringComparison.Ordinal)
                    ? "stage_x"
                    : "camera_x";
                var y = string.Equals(action, EquipmentActionNames.Stage, StringComparison.Ordinal)
                    ? "stage_y"
                    : "camera_y";
                return "<move><id>{{{correlation_id}}}</id>"
                       + "<mode>{{{move_mode}}}</mode>"
                       + "<x>{{{" + x + "}}}</x>"
                       + "<y>{{{" + y + "}}}</y></move>";
            }

            return CreateContractTextTemplate(action, direction);
        });
        var request = new EquipmentRequestMessage(
            51,
            EquipmentActionNames.Stage,
            new Dictionary<string, object?>
            {
                ["move_mode"] = "relative",
                ["stage_x"] = 1E-6,
                ["stage_y"] = 2E-6
            });

        var payload = codec.SerializeRequest(request);

        Assert.False(codec.TryDeserializeRequest(payload, out _));
    }

    [Fact]
    public void ExpectedResponseDeserializer_AlsoRejectsMultipleActionMatches()
    {
        var codec = new XmlTemplateEquipmentMessageCodec((action, direction) =>
        {
            if (string.Equals(direction, "response", StringComparison.Ordinal)
                && (string.Equals(action, EquipmentActionNames.Stage, StringComparison.Ordinal)
                    || string.Equals(action, EquipmentActionNames.Camera, StringComparison.Ordinal)))
            {
                var x = string.Equals(action, EquipmentActionNames.Stage, StringComparison.Ordinal)
                    ? "current_stage_x"
                    : "current_camera_x";
                var y = string.Equals(action, EquipmentActionNames.Stage, StringComparison.Ordinal)
                    ? "current_stage_y"
                    : "current_camera_y";
                return "<move-response><id>{{{correlation_id}}}</id>"
                       + "<result>{{{result}}}</result>"
                       + "<x>{{{" + x + "}}}</x>"
                       + "<y>{{{" + y + "}}}</y></move-response>";
            }

            return CreateContractTextTemplate(action, direction);
        });
        var payload = codec.SerializeResponse(CreateResponse(53, EquipmentActionNames.Stage));
        var expectedRequest = new EquipmentRequestMessage(
            53,
            EquipmentActionNames.Stage,
            new Dictionary<string, object?>
            {
                ["move_mode"] = "relative",
                ["stage_x"] = 0d,
                ["stage_y"] = 0d
            });

        Assert.False(codec.TryDeserializeResponse(payload, out _));
        Assert.False(codec.TryDeserializeResponse(payload, expectedRequest, out _));
    }

    [Fact]
    public void Extraction_TriesLaterLiteralBoundariesUntilOneLogicalMessageMatches()
    {
        var codec = new XmlTemplateEquipmentMessageCodec((action, direction) =>
        {
            if (string.Equals(action, EquipmentActionNames.Integration, StringComparison.Ordinal)
                && string.Equals(direction, "request", StringComparison.Ordinal))
            {
                return "<integration-request>"
                       + "{{{image_path}}}-{{{correlation_id}}}"
                       + "<hfw>{{{hfw}}}</hfw>"
                       + "<frames>{{{frame_count}}}</frames>"
                       + "</integration-request>";
            }

            return CreateContractTextTemplate(action, direction);
        });
        var path = @"C:\frames\" + new string('-', 300) + "sample.png";
        var request = new EquipmentRequestMessage(
            54,
            EquipmentActionNames.Integration,
            new Dictionary<string, object?>
            {
                ["hfw"] = 1E-3,
                ["frame_count"] = 8,
                ["image_path"] = path
            });

        var payload = codec.SerializeRequest(request);

        Assert.True(codec.TryDeserializeRequest(payload, out var restored));
        Assert.NotNull(restored);
        Assert.Equal(54, restored!.CorrelationId);
        Assert.Equal(path, restored.Parameters["image_path"]);
    }

    [Fact]
    public void Extraction_RejectsDelimiterFloodWithoutMaterializingEveryFalseBoundary()
    {
        var codec = new XmlTemplateEquipmentMessageCodec((action, direction) =>
        {
            if (string.Equals(action, EquipmentActionNames.Integration, StringComparison.Ordinal)
                && string.Equals(direction, "request", StringComparison.Ordinal))
            {
                return "<integration-request>"
                       + "{{{image_path}}}-{{{correlation_id}}}"
                       + "<hfw>{{{hfw}}}</hfw>"
                       + "<frames>{{{frame_count}}}</frames>"
                       + "</integration-request>";
            }

            return CreateContractTextTemplate(action, direction);
        });
        var malformed = Encoding.UTF8.GetBytes(
            "<integration-request>" + @"C:\frames\" + new string('-', 20_000));
        var elapsed = Stopwatch.StartNew();

        var parsed = codec.TryDeserializeRequest(malformed, out _);

        elapsed.Stop();
        Assert.False(parsed);
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(10),
            $"Delimiter flood parsing took {elapsed.Elapsed}.");
    }

    [Fact]
    public void Extraction_RejectsRepeatedLargeFieldDelimiterFloodWithinResourceBudget()
    {
        var codec = new XmlTemplateEquipmentMessageCodec((action, direction) =>
        {
            if (string.Equals(action, EquipmentActionNames.Integration, StringComparison.Ordinal)
                && string.Equals(direction, "request", StringComparison.Ordinal))
            {
                return "<integration-request>"
                       + "{{{image_path}}}X{{{image_path}}}"
                       + "<id>{{{correlation_id}}}</id>"
                       + "<hfw>{{{hfw}}}</hfw>"
                       + "<frames>{{{frame_count}}}</frames>"
                       + "</integration-request>";
            }

            return CreateContractTextTemplate(action, direction);
        });
        var repeatedPaths = string.Join(
            "X",
            Enumerable.Repeat(@"C:\frames\sample.png", 2_000));
        var malformed = Encoding.UTF8.GetBytes(
            "<integration-request>"
            + repeatedPaths
            + "<id>54</id><hfw>1E-3</hfw><frames>8</frames></integration-request>");
        var elapsed = Stopwatch.StartNew();

        var parsed = codec.TryDeserializeRequest(malformed, out _);

        elapsed.Stop();
        Assert.False(parsed);
        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(10),
            $"Repeated-field delimiter flood parsing took {elapsed.Elapsed}.");
    }

    [Fact]
    public void Template_RejectsAnUnsafeNumberOfPlaceholderOccurrences()
    {
        var error = Assert.Throws<InvalidDataException>(() =>
            new XmlTemplateEquipmentMessageCodec((action, direction) =>
            {
                if (string.Equals(action, EquipmentActionNames.Stage, StringComparison.Ordinal)
                    && string.Equals(direction, "request", StringComparison.Ordinal))
                {
                    return string.Concat(
                        Enumerable.Repeat("{{{correlation_id}}}-", 257));
                }

                return CreateContractTextTemplate(action, direction);
            }));

        Assert.Contains("more than 256 placeholder occurrences", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Response_MustMatchBothCorrelationAndAction()
    {
        var responseBytes = _codec.SerializeResponse(CreateResponse(9, EquipmentActionNames.Camera));

        var camera = CreateRequests().Single(item => item.Action == EquipmentActionNames.Camera);
        Assert.False(_codec.TryDeserializeResponse(
            responseBytes,
            new EquipmentRequestMessage(10, camera.Action, camera.Parameters),
            out _));
        Assert.False(_codec.TryDeserializeResponse(
            responseBytes,
            new EquipmentRequestMessage(
                9,
                EquipmentActionNames.Stage,
                new Dictionary<string, object?>
                {
                    ["move_mode"] = "relative",
                    ["stage_x"] = 0d,
                    ["stage_y"] = 0d
                }),
            out _));
    }

    [Theory]
    [InlineData(0d)]
    [InlineData(2.4E-3d)]
    [InlineData(double.PositiveInfinity)]
    public void Hfw_MustBeStrictlyInsideContractRange(double hfw)
    {
        var request = new EquipmentRequestMessage(
            1,
            EquipmentActionNames.Live,
            new Dictionary<string, object?>
            {
                ["hfw"] = hfw,
                ["frame_count"] = 1,
                ["image_path"] = @"C:\Images\live.png"
            });

        Assert.Throws<InvalidDataException>(() => _codec.SerializeRequest(request));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(65)]
    [InlineData(128)]
    public void IntegrationFrameCount_MustBePowerOfTwoAtMost64(int frameCount)
    {
        var request = new EquipmentRequestMessage(
            1,
            EquipmentActionNames.Integration,
            new Dictionary<string, object?>
            {
                ["hfw"] = 1E-3,
                ["frame_count"] = frameCount,
                ["image_path"] = @"C:\Images\integration.png"
            });

        Assert.Throws<InvalidDataException>(() => _codec.SerializeRequest(request));
    }

    [Fact]
    public void FocusMatrix_AllowsNullOrEmptyAndRequiresPositiveFinitePairs()
    {
        var nullResponse = new EquipmentResponseMessage(
            7,
            EquipmentActionNames.Focus,
            0,
            new Dictionary<string, object?> { ["z_to_sharpness_2d"] = null });
        var emptyResponse = new EquipmentResponseMessage(
            8,
            EquipmentActionNames.Focus,
            0,
            new Dictionary<string, object?>
            {
                ["z_to_sharpness_2d"] = Array.Empty<IReadOnlyList<double>>()
            });
        var validResponse = new EquipmentResponseMessage(
            9,
            EquipmentActionNames.Focus,
            0,
            new Dictionary<string, object?>
            {
                ["z_to_sharpness_2d"] = new[]
                {
                    new[] { 0.1d, 500d },
                    new[] { 1.5d, 600d }
                }
            });

        Assert.Contains("<z_to_sharpness_2d>null</z_to_sharpness_2d>",
            Encoding.UTF8.GetString(_codec.SerializeResponse(nullResponse)), StringComparison.Ordinal);
        Assert.Contains("<z_to_sharpness_2d>[]</z_to_sharpness_2d>",
            Encoding.UTF8.GetString(_codec.SerializeResponse(emptyResponse)), StringComparison.Ordinal);
        var validBytes = _codec.SerializeResponse(validResponse);
        Assert.Contains("<z_to_sharpness_2d>[[",
            Encoding.UTF8.GetString(validBytes), StringComparison.Ordinal);
        Assert.True(_codec.TryDeserializeResponse(validBytes, out var restored));
        Assert.Equal(2, restored!.ZToSharpness2D!.Count);
        Assert.Equal(0.1d, restored.ZToSharpness2D[0][0]);
        Assert.Equal(500d, restored.ZToSharpness2D[0][1]);

        var invalid = new EquipmentResponseMessage(
            10,
            EquipmentActionNames.Focus,
            0,
            new Dictionary<string, object?>
            {
                ["z_to_sharpness_2d"] = new[] { new[] { 0d, 1d } }
            });
        Assert.Throws<InvalidDataException>(() => _codec.SerializeResponse(invalid));
    }

    [Fact]
    public void FocusMatrix_DeserializerAllowsJsonWhitespaceAroundPunctuation()
    {
        var compact = _codec.SerializeResponse(new EquipmentResponseMessage(
            10,
            EquipmentActionNames.Focus,
            0,
            new Dictionary<string, object?>
            {
                ["z_to_sharpness_2d"] = new[] { new[] { 1d, 1d } }
            }));
        var xml = Encoding.UTF8.GetString(compact).Replace(
            "[[1E0,1E0]]",
            " [ [ 0.1 , 500 ] ,\r\n\t[ 1.5 , 600 ] ] ");

        Assert.True(_codec.TryDeserializeResponse(Encoding.UTF8.GetBytes(xml), out var restored));
        Assert.NotNull(restored);
        Assert.Equal(2, restored!.ZToSharpness2D!.Count);
        Assert.Equal(0.1d, restored.ZToSharpness2D[0][0]);
        Assert.Equal(500d, restored.ZToSharpness2D[0][1]);
        Assert.Equal(1.5d, restored.ZToSharpness2D[1][0]);
        Assert.Equal(600d, restored.ZToSharpness2D[1][1]);

        var invalidXml = xml.Replace("0.1", "0");
        Assert.False(_codec.TryDeserializeResponse(
            Encoding.UTF8.GetBytes(invalidXml),
            out _));
    }

    [Fact]
    public void TemplateCatalog_FailsClosedWhenARequiredPlaceholderIsMissing()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            new XmlTemplateEquipmentMessageCodec(
                (_, _) => "<message>{{{type}}}</message>"));

        Assert.Contains("missing:", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("correlation_id", exception.Message, StringComparison.Ordinal);
        Assert.Contains("stage_x", exception.Message, StringComparison.Ordinal);
        Assert.Contains("exact", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TemplateCatalog_ReportsUnexpectedAndInvalidExactPlaceholders()
    {
        var unexpected = Assert.Throws<InvalidDataException>(() =>
            new XmlTemplateEquipmentMessageCodec((action, direction) =>
            {
                var template = CreateContractTextTemplate(action, direction);
                return string.Equals(action, EquipmentActionNames.Stage, StringComparison.Ordinal)
                       && string.Equals(direction, "request", StringComparison.Ordinal)
                    ? template + "<extra>{{{unknown_field}}}</extra>"
                    : template;
            }));
        Assert.Contains("unexpected: unknown_field", unexpected.Message, StringComparison.Ordinal);

        var invalid = Assert.Throws<InvalidDataException>(() =>
            new XmlTemplateEquipmentMessageCodec(
                (_, _) => "<message>{{{ correlation_id }}}</message>"));
        Assert.Contains("invalid placeholder", invalid.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("{{{correlation_id}}}", invalid.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TemplateCatalog_FailsFastWhenATemplateContainsUtf8BomMarker()
    {
        var exception = Assert.Throws<InvalidDataException>(() =>
            new XmlTemplateEquipmentMessageCodec((_, _) => "\uFEFF<message />"));

        Assert.Contains("BOM", exception.Message, StringComparison.Ordinal);
        Assert.Contains("U+FEFF", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CodecRejectsWirePayloadsOverTheExplicitSafetyLimit()
    {
        var oversizedPath = @"C:\" + new string(
            'a',
            EquipmentMessageLimits.MaximumWirePayloadBytes) + ".png";
        var oversizedRequest = new EquipmentRequestMessage(
            15,
            EquipmentActionNames.Integration,
            new Dictionary<string, object?>
            {
                ["hfw"] = 1E-3,
                ["frame_count"] = 1,
                ["image_path"] = oversizedPath
            });

        var exception = Assert.Throws<InvalidDataException>(() =>
            _codec.SerializeRequest(oversizedRequest));
        Assert.Contains(
            EquipmentMessageLimits.MaximumWirePayloadBytes.ToString(),
            exception.Message,
            StringComparison.Ordinal);

        Assert.False(_codec.TryDeserializeResponse(
            new byte[EquipmentMessageLimits.MaximumWirePayloadBytes + 1],
            out _));
    }

    [Fact]
    public void ResultOne_IsAValidLogicalResponse()
    {
        var response = new EquipmentResponseMessage(
            12,
            EquipmentActionNames.Abort,
            1);

        var bytes = _codec.SerializeResponse(response);

        Assert.True(_codec.TryDeserializeResponse(bytes, out var restored));
        Assert.Equal(1, restored!.Result);
        Assert.False(restored.IsSuccess);
    }

    private static IReadOnlyList<EquipmentRequestMessage> CreateRequests()
    {
        return new[]
        {
            new EquipmentRequestMessage(1, EquipmentActionNames.Stage, new Dictionary<string, object?>
            {
                ["move_mode"] = "relative", ["stage_x"] = 1E-6, ["stage_y"] = -2.56E-3
            }),
            new EquipmentRequestMessage(2, EquipmentActionNames.Camera, new Dictionary<string, object?>
            {
                ["move_mode"] = "absolute", ["camera_x"] = -1E-6, ["camera_y"] = 8.2E-3
            }),
            new EquipmentRequestMessage(3, EquipmentActionNames.Focus, new Dictionary<string, object?>
            {
                ["hfw"] = 3.02E-6, ["range"] = 50E-6, ["steps"] = 13
            }),
            new EquipmentRequestMessage(4, EquipmentActionNames.Integration, new Dictionary<string, object?>
            {
                ["hfw"] = 3.02E-6, ["frame_count"] = 8,
                ["image_path"] = @"C:\Images\integration.png"
            }),
            new EquipmentRequestMessage(5, EquipmentActionNames.Live, new Dictionary<string, object?>
            {
                ["hfw"] = 3.02E-6, ["frame_count"] = 1,
                ["image_path"] = @"C:\Images\live.png"
            }),
            new EquipmentRequestMessage(6, EquipmentActionNames.Abort)
        };
    }

    private static string LoadVendorLikeTemplate(string action, string direction)
    {
        if (string.Equals(action, EquipmentActionNames.Stage, StringComparison.Ordinal)
            && string.Equals(direction, "request", StringComparison.Ordinal))
        {
            // type/action are fixed vendor literals. Plain field names and four-brace text are
            // ordinary template bytes; only exact triple-brace tokens are replaceable slots.
            return "<stage-request type=\"request\" action=\"stage\">"
                   + "<plain>correlation_id correlation_id</plain>"
                   + "<near-miss>{{{{correlation_id}}}}</near-miss>"
                   + "<!-- correlation_id={{{correlation_id}}} -->"
                   + "<payload correlation_id=\"{{{correlation_id}}}\">"
                   + "<move_mode>{{{move_mode}}}</move_mode>"
                   + "<stage_x>{{{stage_x}}}</stage_x>"
                   + "<stage_y>{{{stage_y}}}</stage_y>"
                   + "</payload></stage-request>";
        }

        if (string.Equals(action, EquipmentActionNames.Stage, StringComparison.Ordinal)
            && string.Equals(direction, "response", StringComparison.Ordinal))
        {
            return "<stage-response type=\"response\" action=\"stage\">"
                   + "<correlation>{{{correlation_id}}}</correlation>"
                   + "<result>{{{result}}}</result>"
                   + "<x>{{{current_stage_x}}}</x>"
                   + "<y>{{{current_stage_y}}}</y>"
                   + "<correlation-copy>{{{correlation_id}}}</correlation-copy>"
                   + "</stage-response>";
        }

        return CreateContractTextTemplate(action, direction);
    }

    private static string CreateContractTextTemplate(string action, string direction)
    {
        var fields = new List<string> { "type", "correlation_id", "action" };
        var isRequest = string.Equals(direction, "request", StringComparison.Ordinal);
        if (!isRequest)
        {
            fields.Add("result");
        }

        switch (action)
        {
            case EquipmentActionNames.Stage:
                fields.AddRange(isRequest
                    ? new[] { "move_mode", "stage_x", "stage_y" }
                    : new[] { "current_stage_x", "current_stage_y" });
                break;
            case EquipmentActionNames.Camera:
                fields.AddRange(isRequest
                    ? new[] { "move_mode", "camera_x", "camera_y" }
                    : new[] { "current_camera_x", "current_camera_y" });
                break;
            case EquipmentActionNames.Focus:
                fields.AddRange(isRequest
                    ? new[] { "hfw", "range", "steps" }
                    : new[] { "z_to_sharpness_2d" });
                break;
            case EquipmentActionNames.Integration:
            case EquipmentActionNames.Live:
                fields.AddRange(new[] { "hfw", "frame_count", "image_path" });
                break;
        }

        var builder = new StringBuilder();
        builder.Append('<').Append(action).Append('-').Append(direction).Append('>');
        foreach (var field in fields)
        {
            builder.Append('<').Append(field).Append('>')
                .Append("{{{").Append(field).Append("}}}")
                .Append("</").Append(field).Append('>');
        }

        return builder.Append("</").Append(action).Append('-').Append(direction).Append('>')
            .ToString();
    }

    private static EquipmentResponseMessage CreateResponse(int correlationId, string action)
    {
        IReadOnlyDictionary<string, object?> properties;
        switch (action)
        {
            case EquipmentActionNames.Stage:
                properties = new Dictionary<string, object?>
                {
                    ["current_stage_x"] = -3.2E-6,
                    ["current_stage_y"] = 4.12E-4
                };
                break;
            case EquipmentActionNames.Camera:
                properties = new Dictionary<string, object?>
                {
                    ["current_camera_x"] = -3.2E-9,
                    ["current_camera_y"] = 7.62E-6
                };
                break;
            case EquipmentActionNames.Focus:
                properties = new Dictionary<string, object?>
                {
                    ["z_to_sharpness_2d"] = new[] { new[] { 0.1d, 500d } }
                };
                break;
            case EquipmentActionNames.Integration:
                properties = new Dictionary<string, object?>
                {
                    ["hfw"] = 3.02E-6,
                    ["frame_count"] = 8,
                    ["image_path"] = @"C:\Images\integration.png"
                };
                break;
            case EquipmentActionNames.Live:
                properties = new Dictionary<string, object?>
                {
                    ["hfw"] = 3.02E-6,
                    ["frame_count"] = 1,
                    ["image_path"] = @"C:\Images\live.png"
                };
                break;
            default:
                properties = new Dictionary<string, object?>();
                break;
        }

        return new EquipmentResponseMessage(correlationId, action, 0, properties);
    }

    private static bool HasUtf8Bom(byte[] bytes)
    {
        return bytes.Length >= 3
               && bytes[0] == 0xEF
               && bytes[1] == 0xBB
               && bytes[2] == 0xBF;
    }
}
