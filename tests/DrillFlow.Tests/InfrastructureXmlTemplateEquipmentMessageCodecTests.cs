using System;
using System.Collections.Generic;
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
        Assert.Throws<InvalidDataException>(() => new XmlTemplateEquipmentMessageCodec(
            (_, _) => "<message>{{{type}}}</message>"));
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
