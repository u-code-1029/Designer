using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using DrillFlow.Application.Communication;
using DrillFlow.Infrastructure.Communication;
using Xunit;

namespace DrillFlow.Tests;

public sealed class InfrastructureXmlTemplateLoadingTests
{
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(false, true);

    [Fact]
    public void ExternalVendorTemplates_ControlPublishedRequestSuccessAndFailureShapes()
    {
        using var directory = new InfrastructureTestDirectory();
        CopyEmbeddedTemplates(directory.Path);
        WriteVendorStageTemplates(directory.Path);
        var codec = new XmlTemplateEquipmentMessageCodec(directory.Path);
        var request = StageRequest();

        var requestXml = Utf8WithoutBom.GetString(codec.SerializeRequest(request));
        Assert.Equal(VendorRequest
            .Replace("{{{correlation_id}}}", "501")
            .Replace("{{{move_mode}}}", "relative")
            .Replace("{{{stage_x}}}", "1E-06")
            .Replace("{{{stage_y}}}", "-2E-06"), requestXml);
        Assert.True(codec.TryDeserializeRequest(Utf8WithoutBom.GetBytes(requestXml), out var restoredRequest));
        Assert.Equal(501, restoredRequest!.CorrelationId);
        Assert.Equal(EquipmentActionNames.Stage, restoredRequest.Action);

        var success = StageResponse();
        var successPayload = codec.SerializeResponse(success);
        Assert.Equal(VendorResponse
            .Replace("{{{correlation_id}}}", "501")
            .Replace("{{{result}}}", "0")
            .Replace("{{{current_stage_x}}}", "3E-06")
            .Replace("{{{current_stage_y}}}", "-4E-06"), Utf8WithoutBom.GetString(successPayload));
        Assert.True(codec.TryDeserializeResponse(successPayload, request, out var restoredResponse));
        Assert.Equal(3E-6, restoredResponse!.CurrentStageX);
        Assert.Equal(-4E-6, restoredResponse.CurrentStageY);

        var failedPayload = codec.SerializeResponse(new EquipmentResponseMessage(501, EquipmentActionNames.Stage, 1));
        Assert.Equal(VendorFailureResponse.Replace("{{{correlation_id}}}", "501").Replace("{{{result}}}", "1"),
            Utf8WithoutBom.GetString(failedPayload));
        Assert.True(codec.TryDeserializeResponse(failedPayload, request, out var failure));
        Assert.Equal(1, failure!.Result);
    }

    [Fact]
    public void LoadedTemplates_AreAnImmutableSnapshotUntilCodecIsRecreated()
    {
        using var directory = new InfrastructureTestDirectory();
        CopyEmbeddedTemplates(directory.Path);
        WriteVendorStageTemplates(directory.Path);
        var existingCodec = new XmlTemplateEquipmentMessageCodec(directory.Path);
        var original = existingCodec.SerializeRequest(StageRequest());

        File.WriteAllText(TemplatePath(directory.Path, "Stage", "request"),
            VendorRequest.Replace("ControllerA", "ControllerB"), Utf8WithoutBom);

        Assert.Equal(original, existingCodec.SerializeRequest(StageRequest()));
        var restartedCodec = new XmlTemplateEquipmentMessageCodec(directory.Path);
        Assert.Contains("ControllerB", Utf8WithoutBom.GetString(restartedCodec.SerializeRequest(StageRequest())),
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("response")]
    [InlineData("failure-response")]
    public void PresentDirectory_WithMissingRequiredTemplateDoesNotUseEmbeddedFallback(string direction)
    {
        using var directory = new InfrastructureTestDirectory();
        CopyEmbeddedTemplates(directory.Path);
        var path = TemplatePath(directory.Path, "Stage", direction);
        File.Delete(path);

        var exception = Assert.Throws<InvalidDataException>(() => new XmlTemplateEquipmentMessageCodec(directory.Path));

        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ExplicitMissingDirectory_IsAnError()
    {
        using var directory = new InfrastructureTestDirectory();
        var missing = Path.Combine(directory.Path, "missing-templates");

        var exception = Assert.Throws<InvalidDataException>(() => new XmlTemplateEquipmentMessageCodec(missing));

        Assert.Contains(missing, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFileAtTheTemplateDirectoryPath_IsAnError()
    {
        using var directory = new InfrastructureTestDirectory();
        var path = Path.Combine(directory.Path, "Templates");
        File.WriteAllText(path, "not a directory", Utf8WithoutBom);

        var exception = Assert.Throws<InvalidDataException>(() => new XmlTemplateEquipmentMessageCodec(path));

        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingDefaultDirectory_UsesEmbeddedLoaderOnlyWhenAllowed()
    {
        using var directory = new InfrastructureTestDirectory();
        var missing = Path.Combine(directory.Path, "Templates");
        var loader = EquipmentXmlTemplateLoader.Create(missing, true,
            (action, direction) => action + "/" + direction,
            (_, _) => throw new InvalidOperationException("No external file should be decoded."));

        Assert.Equal("stage/request", loader("stage", "request"));
    }

    [Fact]
    public void SingleUtf8Bom_IsAcceptedWithoutChangingTemplateRendering()
    {
        using var directory = new InfrastructureTestDirectory();
        CopyEmbeddedTemplates(directory.Path);
        WriteVendorStageTemplates(directory.Path);
        var path = TemplatePath(directory.Path, "Stage", "request");
        File.WriteAllText(path, VendorRequest, new UTF8Encoding(true, true));

        var payload = new XmlTemplateEquipmentMessageCodec(directory.Path).SerializeRequest(StageRequest());

        Assert.Equal((byte)'<', payload[0]);
        Assert.Contains("ControllerA", Utf8WithoutBom.GetString(payload), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("invalid-utf8")]
    [InlineData("utf16")]
    [InlineData("double-bom")]
    [InlineData("embedded-bom")]
    [InlineData("nul")]
    public void InvalidTemplateEncoding_IsRejectedWithItsFilePath(string encoding)
    {
        using var directory = new InfrastructureTestDirectory();
        CopyEmbeddedTemplates(directory.Path);
        var path = TemplatePath(directory.Path, "Stage", "request");
        var bytes = encoding switch
        {
            "empty" => Array.Empty<byte>(),
            "invalid-utf8" => new byte[] { 0xC3, 0x28 },
            "utf16" => Encoding.Unicode.GetBytes(VendorRequest),
            "double-bom" => Utf8WithoutBom.GetBytes("\uFEFF\uFEFF" + VendorRequest),
            "embedded-bom" => Utf8WithoutBom.GetBytes(VendorRequest.Replace("ControllerA", "Controller\uFEFFA")),
            "nul" => Utf8WithoutBom.GetBytes(VendorRequest.Replace("ControllerA", "Controller\0A")),
            _ => throw new InvalidOperationException()
        };
        File.WriteAllBytes(path, bytes);

        var exception = Assert.Throws<InvalidDataException>(() => new XmlTemplateEquipmentMessageCodec(directory.Path));

        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OversizedTemplate_IsRejectedBeforePayloadAllocation()
    {
        using var directory = new InfrastructureTestDirectory();
        CopyEmbeddedTemplates(directory.Path);
        var path = TemplatePath(directory.Path, "Stage", "request");
        using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            stream.SetLength((long)EquipmentMessageLimits.MaximumWirePayloadBytes + 1);
        }

        var exception = Assert.Throws<InvalidDataException>(() => new XmlTemplateEquipmentMessageCodec(directory.Path));

        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
        Assert.Contains("byte limit", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("missing-field")]
    [InlineData("malformed-xml")]
    [InlineData("dtd")]
    public void InvalidTemplateContractOrXml_IsRejectedAtStartup(string defect)
    {
        using var directory = new InfrastructureTestDirectory();
        CopyEmbeddedTemplates(directory.Path);
        var path = TemplatePath(directory.Path, "Stage", "request");
        var template = defect switch
        {
            "missing-field" => VendorRequest.Replace("{{{stage_x}}}", "fixed-x"),
            "malformed-xml" => VendorRequest.Replace("</VendorPacket>", "</DifferentPacket>"),
            "dtd" => VendorRequest.Replace("<VendorPacket", "<!DOCTYPE VendorPacket [<!ENTITY equipment 'controller'>]><VendorPacket"),
            _ => throw new InvalidOperationException()
        };
        File.WriteAllText(path, template, Utf8WithoutBom);

        var exception = Assert.Throws<InvalidDataException>(() => new XmlTemplateEquipmentMessageCodec(directory.Path));

        Assert.Contains(path, exception.Message, StringComparison.Ordinal);
    }

    private const string VendorRequest = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
        + "<VendorPacket Version=\"release-2026\">\n"
        + "  <NamedTypedObject Type=\"StageCommand\" Name=\"ControllerA\">\n"
        + "    <Header Correlation=\"{{{correlation_id}}}\" />\n"
        + "    <Coordinates Move=\"{{{move_mode}}}\" X=\"{{{stage_x}}}\" Y=\"{{{stage_y}}}\" />\n"
        + "    <Description>correlation_id stays literal</Description>\n"
        + "  </NamedTypedObject>\n</VendorPacket>";

    private const string VendorResponse = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
        + "<VendorPacket Version=\"release-2026\">\n"
        + "  <NamedTypedObject Type=\"StageReply\" Name=\"ControllerA\">\n"
        + "    <Header Correlation=\"{{{correlation_id}}}\" Status=\"{{{result}}}\" />\n"
        + "    <Coordinates X=\"{{{current_stage_x}}}\" Y=\"{{{current_stage_y}}}\" />\n"
        + "  </NamedTypedObject>\n</VendorPacket>";

    private const string VendorFailureResponse = "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
        + "<VendorPacket Version=\"release-2026\">\n"
        + "  <NamedTypedObject Type=\"StageFailure\" Name=\"ControllerA\">\n"
        + "    <Header Correlation=\"{{{correlation_id}}}\" Status=\"{{{result}}}\" />\n"
        + "  </NamedTypedObject>\n</VendorPacket>";

    private static EquipmentRequestMessage StageRequest() => new(501, EquipmentActionNames.Stage,
        new Dictionary<string, object?>
        {
            ["move_mode"] = "relative",
            ["stage_x"] = 1E-6,
            ["stage_y"] = -2E-6
        });

    private static EquipmentResponseMessage StageResponse() => new(501, EquipmentActionNames.Stage, 0,
        new Dictionary<string, object?>
        {
            ["current_stage_x"] = 3E-6,
            ["current_stage_y"] = -4E-6
        });

    private static string TemplatePath(string directory, string folder, string direction) =>
        Path.Combine(directory, folder, direction + ".xml");

    private static void WriteVendorStageTemplates(string directory)
    {
        File.WriteAllText(TemplatePath(directory, "Stage", "request"), VendorRequest, Utf8WithoutBom);
        File.WriteAllText(TemplatePath(directory, "Stage", "response"), VendorResponse, Utf8WithoutBom);
        File.WriteAllText(TemplatePath(directory, "Stage", "failure-response"), VendorFailureResponse, Utf8WithoutBom);
    }

    private static void CopyEmbeddedTemplates(string directory)
    {
        var assembly = typeof(XmlTemplateEquipmentMessageCodec).GetTypeInfo().Assembly;
        var prefix = typeof(XmlTemplateEquipmentMessageCodec).Namespace + ".Templates.";
        foreach (var name in assembly.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var relative = name.Substring(prefix.Length);
            var separator = relative.IndexOf('.');
            var folder = Path.Combine(directory, relative.Substring(0, separator));
            Directory.CreateDirectory(folder);
            using (var input = assembly.GetManifestResourceStream(name)!)
            using (var output = File.Create(Path.Combine(folder, relative.Substring(separator + 1))))
            {
                input.CopyTo(output);
            }
        }
    }
}
