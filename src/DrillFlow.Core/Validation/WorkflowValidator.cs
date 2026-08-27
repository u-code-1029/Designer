using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DrillFlow.Core.Expressions;
using DrillFlow.Core.Workflows;

namespace DrillFlow.Core.Validation
{
    public sealed class WorkflowValidator
    {
        private static readonly Regex KeyPattern = new Regex(
            "^[A-Za-z_][A-Za-z0-9_]*$",
            RegexOptions.CultureInvariant);

        private readonly ExpressionEngine _expressions;

        public WorkflowValidator()
            : this(new ExpressionEngine())
        {
        }

        public WorkflowValidator(ExpressionEngine expressions)
        {
            _expressions = expressions ?? throw new ArgumentNullException(nameof(expressions));
        }

        public WorkflowValidationResult Validate(WorkflowDocument document)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            var state = new State(_expressions);
            state.ValidateDocument(document);
            return new WorkflowValidationResult(state.Issues);
        }

        private sealed class State
        {
            private readonly ExpressionEngine _expressions;
            private readonly Dictionary<string, WorkflowNode> _nodesByKey =
                new Dictionary<string, WorkflowNode>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<Guid, WorkflowNode> _nodesById =
                new Dictionary<Guid, WorkflowNode>();
            private readonly HashSet<WorkflowNode> _registered =
                new HashSet<WorkflowNode>(ReferenceComparer<WorkflowNode>.Instance);
            private readonly HashSet<WorkflowNode> _registrationStack =
                new HashSet<WorkflowNode>(ReferenceComparer<WorkflowNode>.Instance);
            private readonly HashSet<Guid> _branchIds = new HashSet<Guid>();
            private static readonly HashSet<string> SupportedActionMembers =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "parameters",
                    "result",
                    "results",
                    "last"
                };
            private bool _hasStructuralGraphError;

            public State(ExpressionEngine expressions)
            {
                _expressions = expressions;
            }

            public List<ValidationIssue> Issues { get; } = new List<ValidationIssue>();

            public void ValidateDocument(WorkflowDocument document)
            {
                if (document.SchemaVersion != WorkflowDocument.CurrentSchemaVersion)
                {
                    Add("document.schema_version", "The workflow schema version is not supported.", null, "schemaVersion");
                }

                if (document.Id == Guid.Empty)
                {
                    Add("document.id", "The workflow must have a non-empty stable ID.", null, "id");
                }

                if (string.IsNullOrWhiteSpace(document.Name))
                {
                    Add("document.name", "The workflow name is required.", null, "name");
                }

                if (document.Nodes == null)
                {
                    Add("document.nodes", "The workflow node collection is missing.", null, "nodes");
                    return;
                }

                RegisterBlock(document.Nodes, "nodes");
                if (_hasStructuralGraphError)
                {
                    return;
                }

                ValidateBlock(
                    document.Nodes,
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    "nodes");
            }

            private void RegisterBlock(IList<WorkflowNode>? nodes, string path)
            {
                if (nodes == null)
                {
                    Add("structure.nodes_missing", "A node collection is missing.", null, path);
                    return;
                }

                for (var index = 0; index < nodes.Count; index++)
                {
                    var node = nodes[index];
                    var nodePath = $"{path}[{index}]";
                    if (node == null)
                    {
                        Add("structure.null_node", "A workflow cannot contain a null node.", null, nodePath);
                        continue;
                    }

                    RegisterNode(node, nodePath);
                }
            }

            private void RegisterNode(WorkflowNode node, string path)
            {
                if (_registrationStack.Contains(node))
                {
                    _hasStructuralGraphError = true;
                    Add("structure.cycle", "A control-flow container cannot contain itself.", node.Id, path);
                    return;
                }

                if (!_registered.Add(node))
                {
                    _hasStructuralGraphError = true;
                    Add("structure.shared_node", "A workflow node instance may appear in only one location.", node.Id, path);
                    return;
                }

                _registrationStack.Add(node);

                if (node.Id == Guid.Empty)
                {
                    Add("node.id", "Every node must have a non-empty stable ID.", node.Id, path + ".id");
                }
                else if (_nodesById.ContainsKey(node.Id))
                {
                    Add("node.duplicate_id", "Node IDs must be unique.", node.Id, path + ".id");
                }
                else
                {
                    _nodesById.Add(node.Id, node);
                }

                if (string.IsNullOrWhiteSpace(node.Key))
                {
                    Add("node.key_required", "Every node requires an expression key/alias.", node.Id, path + ".key");
                }
                else if (!KeyPattern.IsMatch(node.Key) || IsReservedKeyword(node.Key))
                {
                    Add(
                        "node.key_format",
                        "A node key must be an identifier that starts with a letter or underscore and is not a reserved literal.",
                        node.Id,
                        path + ".key");
                }
                else if (_nodesByKey.ContainsKey(node.Key))
                {
                    Add("node.duplicate_key", $"The node key '{node.Key}' is already in use.", node.Id, path + ".key");
                }
                else
                {
                    _nodesByKey.Add(node.Key, node);
                }

                if (string.IsNullOrWhiteSpace(node.DisplayName))
                {
                    Add("node.display_name", "Every node requires a display name.", node.Id, path + ".displayName");
                }

                if (node is RepeatNode repeat)
                {
                    RegisterBlock(repeat.Body, path + ".body");
                }
                else if (node is ConditionalNode conditional)
                {
                    if (conditional.Branches == null)
                    {
                        Add("conditional.branches_missing", "Conditional branches are missing.", node.Id, path + ".branches");
                    }
                    else
                    {
                        for (var branchIndex = 0; branchIndex < conditional.Branches.Count; branchIndex++)
                        {
                            var branch = conditional.Branches[branchIndex];
                            var branchPath = $"{path}.branches[{branchIndex}]";
                            if (branch == null)
                            {
                                Add("conditional.null_branch", "A conditional cannot contain a null branch.", node.Id, branchPath);
                                continue;
                            }

                            if (branch.Id == Guid.Empty)
                            {
                                Add("conditional.branch_id", "Every branch requires a non-empty stable ID.", node.Id, branchPath + ".id");
                            }
                            else if (!_branchIds.Add(branch.Id))
                            {
                                Add("conditional.duplicate_branch_id", "Branch IDs must be unique.", node.Id, branchPath + ".id");
                            }

                            RegisterBlock(branch.Body, branchPath + ".body");
                        }
                    }
                }

                _registrationStack.Remove(node);
            }

            private HashSet<string> ValidateBlock(
                IList<WorkflowNode>? nodes,
                HashSet<string> available,
                string path)
            {
                if (nodes == null)
                {
                    return available;
                }

                for (var index = 0; index < nodes.Count; index++)
                {
                    var node = nodes[index];
                    if (node == null || !_registered.Contains(node))
                    {
                        continue;
                    }

                    var nodePath = $"{path}[{index}]";
                    ValidateNodeParameters(node, available, nodePath);

                    if (node is RepeatNode repeat)
                    {
                        var bodyAvailable = ValidateBlock(
                            repeat.Body,
                            new HashSet<string>(available, StringComparer.OrdinalIgnoreCase),
                            nodePath + ".body");

                        // count >= 1, so enabled body action results can be
                        // referenced after the repeat in execution order.
                        if (node.IsEnabled)
                        {
                            foreach (var key in bodyAvailable)
                            {
                                available.Add(key);
                            }
                        }
                    }
                    else if (node is ConditionalNode conditional)
                    {
                        ValidateConditional(conditional, available, nodePath);
                        // Branch-local aliases are not guaranteed to have a
                        // value outside the branch and are intentionally not
                        // made available here.
                    }

                    if (node.IsEnabled
                        && !string.IsNullOrWhiteSpace(node.Key)
                        && KeyPattern.IsMatch(node.Key))
                    {
                        available.Add(node.Key);
                    }
                }

                return available;
            }

            private void ValidateNodeParameters(WorkflowNode node, HashSet<string> available, string path)
            {
                switch (node)
                {
                    case StageNode stage:
                        ValidateValue(stage.MoveMode, node, "move_mode", available, path + ".moveMode", ParameterValueValidator.GetMoveMode);
                        ValidateValue(stage.StageX, node, "stage_x", available, path + ".stageX", value => ParameterValueValidator.GetFiniteCoordinate(value, "Stage X"));
                        ValidateValue(stage.StageY, node, "stage_y", available, path + ".stageY", value => ParameterValueValidator.GetFiniteCoordinate(value, "Stage Y"));
                        break;
                    case CameraNode camera:
                        ValidateValue(camera.MoveMode, node, "move_mode", available, path + ".moveMode", ParameterValueValidator.GetMoveMode);
                        ValidateValue(camera.CameraX, node, "camera_x", available, path + ".cameraX", value => ParameterValueValidator.GetFiniteCoordinate(value, "Camera X"));
                        ValidateValue(camera.CameraY, node, "camera_y", available, path + ".cameraY", value => ParameterValueValidator.GetFiniteCoordinate(value, "Camera Y"));
                        break;
                    case FocusNode focus:
                        ValidateValue(focus.HorizontalFieldWidth, node, "hfw", available, path + ".horizontalFieldWidth", ParameterValueValidator.GetHorizontalFieldWidth);
                        ValidateValue(focus.Range, node, "range", available, path + ".range", ParameterValueValidator.GetFocusRange);
                        ValidateValue(focus.Steps, node, "steps", available, path + ".steps", ParameterValueValidator.GetFocusSteps);
                        break;
                    case IntegrationNode integration:
                        ValidateValue(integration.HorizontalFieldWidth, node, "hfw", available, path + ".horizontalFieldWidth", ParameterValueValidator.GetHorizontalFieldWidth);
                        ValidateValue(integration.FrameCount, node, "frame_count", available, path + ".frameCount", ParameterValueValidator.GetIntegrationFrameCount);
                        ValidateValue(integration.ImagePath, node, "image_path", available, path + ".imagePath", ParameterValueValidator.GetAbsoluteImagePath);
                        break;
                    case LiveNode live:
                        ValidateValue(live.HorizontalFieldWidth, node, "hfw", available, path + ".horizontalFieldWidth", ParameterValueValidator.GetHorizontalFieldWidth);
                        ValidateValue(live.FrameCount, node, "frame_count", available, path + ".frameCount", ParameterValueValidator.GetLiveFrameCount);
                        ValidateValue(live.ImagePath, node, "image_path", available, path + ".imagePath", ParameterValueValidator.GetAbsoluteImagePath);
                        break;
                    case HttpActionNode http:
                        ValidateValue(http.Method, node, "method", available, path + ".method", ParameterValueValidator.GetHttpMethod);
                        ValidateValue(http.Url, node, "url", available, path + ".url", ParameterValueValidator.GetHttpUrl);
                        ValidateValue(http.Headers, node, "headers", available, path + ".headers", ParameterValueValidator.GetHttpHeaders);
                        ValidateValue(http.Body, node, "body", available, path + ".body", ParameterValueValidator.GetHttpBody);
                        ValidateValue(http.TimeoutMilliseconds, node, "timeout_ms", available, path + ".timeoutMilliseconds", ParameterValueValidator.GetHttpTimeoutMilliseconds);
                        break;
                    case DelayNode delay:
                        ValidateValue(delay.DurationMilliseconds, node, "milliseconds", available, path + ".durationMilliseconds", ParameterValueValidator.GetDelayMilliseconds);
                        break;
                    case RepeatNode repeat:
                        ValidateValue(repeat.Count, node, "count", available, path + ".count", ParameterValueValidator.GetRepeatCount);
                        break;
                }
            }

            private void ValidateConditional(ConditionalNode node, HashSet<string> available, string path)
            {
                if (node.Branches == null || node.Branches.Count == 0)
                {
                    Add("conditional.branches_required", "A conditional requires at least an If branch.", node.Id, path + ".branches");
                    return;
                }

                var sawElse = false;
                for (var index = 0; index < node.Branches.Count; index++)
                {
                    var branch = node.Branches[index];
                    if (branch == null)
                    {
                        continue;
                    }

                    var branchPath = $"{path}.branches[{index}]";
                    if (index == 0 && branch.Kind != ConditionalBranchKind.If)
                    {
                        Add("conditional.first_branch", "The first conditional branch must be If.", node.Id, branchPath + ".kind");
                    }
                    else if (index > 0 && branch.Kind == ConditionalBranchKind.If)
                    {
                        Add("conditional.additional_if", "Only the first branch may be If; use ElseIf.", node.Id, branchPath + ".kind");
                    }

                    if (branch.Kind == ConditionalBranchKind.Else)
                    {
                        if (sawElse || index != node.Branches.Count - 1)
                        {
                            Add("conditional.else_position", "Else may occur once and must be the final branch.", node.Id, branchPath + ".kind");
                        }

                        sawElse = true;
                        if (branch.Condition != null && !string.IsNullOrWhiteSpace(branch.Condition.RawText))
                        {
                            Add("conditional.else_condition", "An Else branch cannot have a condition.", node.Id, branchPath + ".condition");
                        }
                    }
                    else
                    {
                        ValidateValue(
                            branch.Condition,
                            node,
                            "condition",
                            available,
                            branchPath + ".condition",
                            value => ParameterValueValidator.GetBoolean(value, "Condition"));
                    }

                    ValidateBlock(
                        branch.Body,
                        new HashSet<string>(available, StringComparer.OrdinalIgnoreCase),
                        branchPath + ".body");
                }
            }

            private void ValidateValue<T>(
                ParameterBinding? binding,
                WorkflowNode node,
                string parameterName,
                HashSet<string> available,
                string path,
                Func<ExpressionValue, T> valueValidator)
            {
                if (binding == null)
                {
                    Add("parameter.missing", $"Parameter '{parameterName}' is missing.", node.Id, path);
                    return;
                }

                if (binding.IsExpression)
                {
                    ExpressionAnalysis analysis;
                    try
                    {
                        analysis = _expressions.Analyze(binding.ExpressionText);
                    }
                    catch (ExpressionException exception)
                    {
                        Add("expression.syntax", exception.Message, node.Id, path);
                        return;
                    }

                    foreach (var reference in analysis.RootIdentifiers)
                    {
                        if (available.Contains(reference))
                        {
                            continue;
                        }

                        var code = _nodesByKey.ContainsKey(reference)
                            ? "expression.reference_not_previous"
                            : "expression.unknown_reference";
                        var message = _nodesByKey.ContainsKey(reference)
                            ? $"Action '{reference}' is not an earlier, guaranteed action at this location."
                            : $"Expression references unknown action '{reference}'.";
                        Add(code, message, node.Id, path);
                    }

                    foreach (var reference in analysis.FirstLevelMemberReferences)
                    {
                        if (!_nodesByKey.ContainsKey(reference.RootIdentifier)
                            || SupportedActionMembers.Contains(reference.MemberName))
                        {
                            continue;
                        }

                        Add(
                            "expression.unknown_action_member",
                            $"Action '{reference.RootIdentifier}' has no top-level member '{reference.MemberName}'. "
                            + "Use parameters, result, results, or last.",
                            node.Id,
                            path);
                    }

                    // Constant expressions can be fully range/type checked now.
                    if (analysis.RootIdentifiers.Count == 0)
                    {
                        ValidateEvaluatedValue(
                            () => _expressions.Evaluate(binding.ExpressionText),
                            valueValidator,
                            node,
                            path);
                    }

                    return;
                }

                ValidateEvaluatedValue(
                    () => _expressions.EvaluateLiteral(binding.RawText),
                    valueValidator,
                    node,
                    path);
            }

            private void ValidateEvaluatedValue<T>(
                Func<ExpressionValue> evaluate,
                Func<ExpressionValue, T> validate,
                WorkflowNode node,
                string path)
            {
                try
                {
                    validate(evaluate());
                }
                catch (ExpressionException exception)
                {
                    Add("parameter.range_or_type", exception.Message, node.Id, path);
                }
                catch (ParameterValidationException exception)
                {
                    Add("parameter.range_or_type", exception.Message, node.Id, path);
                }
            }

            private void Add(string code, string message, Guid? nodeId, string path)
            {
                Issues.Add(new ValidationIssue(code, message, ValidationSeverity.Error, nodeId, path));
            }

            private static bool IsReservedKeyword(string key)
            {
                return string.Equals(key, "true", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(key, "false", StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(key, "null", StringComparison.OrdinalIgnoreCase);
            }
        }

        private sealed class ReferenceComparer<T> : IEqualityComparer<T>
            where T : class
        {
            public static ReferenceComparer<T> Instance { get; } = new ReferenceComparer<T>();

            public bool Equals(T? x, T? y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(T obj)
            {
                return RuntimeHelpers.GetHashCode(obj);
            }
        }
    }
}
