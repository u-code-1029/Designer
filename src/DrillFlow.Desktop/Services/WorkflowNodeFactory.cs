using System;
using System.Collections.Generic;
using System.Linq;
using DrillFlow.Application.Communication;
using DrillFlow.Core.Workflows;

namespace DrillFlow.Desktop.Services;

public static class WorkflowNodeFactory
{
    public static WorkflowNode Create(
        WorkflowNodeKind kind,
        IEnumerable<string> existingAliases,
        EquipmentCommunicationOptions communicationOptions)
    {
        if (communicationOptions is null)
        {
            throw new ArgumentNullException(nameof(communicationOptions));
        }

        WorkflowNode node = kind switch
        {
            WorkflowNodeKind.Stage => new StageNode(),
            WorkflowNodeKind.Camera => new CameraNode(),
            WorkflowNodeKind.Focus => new FocusNode(),
            WorkflowNodeKind.Integration => new IntegrationNode
            {
                ImagePath = ParameterBinding.Literal(CreateImagePath(communicationOptions, "integration.bmp"))
            },
            WorkflowNodeKind.Live => new LiveNode
            {
                ImagePath = ParameterBinding.Literal(CreateImagePath(communicationOptions, "live.bmp"))
            },
            WorkflowNodeKind.Om => new OmNode
            {
                ImagePath = ParameterBinding.Literal(CreateImagePath(communicationOptions, "om.bmp"))
            },
            WorkflowNodeKind.Lens => new LensNode
            {
                LensMode = ParameterBinding.Literal("no_change")
            },
            WorkflowNodeKind.AutoContrastBrightness => new AutoContrastBrightnessNode
            {
                HorizontalFieldWidth = ParameterBinding.Literal("2.04E-6")
            },
            WorkflowNodeKind.Abort => new AbortNode(),
            WorkflowNodeKind.Http => new HttpActionNode(),
            WorkflowNodeKind.Delay => new DelayNode(),
            WorkflowNodeKind.Repeat => new RepeatNode { Count = ParameterBinding.Literal("2") },
            WorkflowNodeKind.Conditional => CreateConditional(),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        node.Key = CreateUniqueAlias(kind, existingAliases);
        node.DisplayName = node.Key;
        return node;
    }

    private static string CreateImagePath(
        EquipmentCommunicationOptions communicationOptions,
        string fileName)
    {
        // Settings mutates the active options instance. Resolve the folder for every
        // new action, including the exchange-folder fallback when no folder is set.
        var directory = communicationOptions.LiveImageDirectory;
        if (directory.Length == 0)
        {
            throw new InvalidOperationException(
                "An image directory must be configured before creating an image action.");
        }

        return EquipmentImagePath.Create(directory, fileName);
    }

    private static ConditionalNode CreateConditional()
    {
        return new ConditionalNode
        {
            Branches = new List<ConditionalBranch>
            {
                new()
                {
                    Kind = ConditionalBranchKind.If,
                    Condition = ParameterBinding.Expression("true")
                },
                new()
                {
                    Kind = ConditionalBranchKind.Else,
                    Condition = null
                }
            }
        };
    }

    private static string CreateUniqueAlias(WorkflowNodeKind kind, IEnumerable<string> existingAliases)
    {
        var prefix = kind switch
        {
            WorkflowNodeKind.Conditional => "if",
            WorkflowNodeKind.AutoContrastBrightness => "acb",
            _ => kind.ToString().ToLowerInvariant()
        };
        var existing = new HashSet<string>(existingAliases, StringComparer.OrdinalIgnoreCase);

        for (var index = 1; index < int.MaxValue; index++)
        {
            var candidate = prefix + "_" + index;
            if (!existing.Contains(candidate))
            {
                return candidate;
            }
        }

        return prefix + "_" + Guid.NewGuid().ToString("N");
    }
}
