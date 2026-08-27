using System;
using System.Collections.Generic;
using System.Linq;
using DrillFlow.Core.Workflows;

namespace DrillFlow.Desktop.Services;

public static class WorkflowNodeFactory
{
    public static WorkflowNode Create(WorkflowNodeKind kind, IEnumerable<string> existingAliases)
    {
        WorkflowNode node = kind switch
        {
            WorkflowNodeKind.Stage => new StageNode(),
            WorkflowNodeKind.Camera => new CameraNode(),
            WorkflowNodeKind.Focus => new FocusNode(),
            WorkflowNodeKind.Integration => new IntegrationNode
            {
                ImagePath = ParameterBinding.Literal(@"C:\DrillFlow\Images\integration.bmp")
            },
            WorkflowNodeKind.Live => new LiveNode
            {
                ImagePath = ParameterBinding.Literal(@"C:\DrillFlow\Images\live.bmp")
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
