using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace DrillFlow.Core.Workflows
{
    /// <summary>
    /// Creates an independent workflow subtree for designer copy/paste operations.
    /// Runtime identity and expression aliases are regenerated while references to
    /// actions inside the copied subtree follow the regenerated aliases.
    /// </summary>
    public static class WorkflowNodeCopy
    {
        public static WorkflowNode CloneForInsertion(
            WorkflowNode source,
            IEnumerable<string> existingAliases)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            var clone = CloneNode(source);
            var usedAliases = new HashSet<string>(
                existingAliases ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            var aliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in Enumerate(clone))
            {
                var originalAlias = node.Key ?? string.Empty;
                var newAlias = CreateUniqueAlias(originalAlias, usedAliases);
                if (!aliasMap.ContainsKey(originalAlias))
                {
                    aliasMap.Add(originalAlias, newAlias);
                }

                node.Key = newAlias;
                usedAliases.Add(newAlias);
            }

            RewriteInternalReferences(clone, aliasMap);
            return clone;
        }

        private static WorkflowNode CloneNode(WorkflowNode source)
        {
            WorkflowNode target;
            switch (source)
            {
                case MoveNode move:
                    target = new MoveNode
                    {
                        MoveMode = CloneBinding(move.MoveMode),
                        MoveX = CloneBinding(move.MoveX),
                        MoveY = CloneBinding(move.MoveY)
                    };
                    break;

                case MeasureNode measure:
                    target = new MeasureNode
                    {
                        Thickness = CloneBinding(measure.Thickness)
                    };
                    break;

                case DrillNode drill:
                    target = new DrillNode
                    {
                        Thickness = CloneBinding(drill.Thickness),
                        DrillResultPath = CloneBinding(drill.DrillResultPath)
                    };
                    break;

                case AbortNode _:
                    target = new AbortNode();
                    break;

                case DelayNode delay:
                    target = new DelayNode
                    {
                        DurationMilliseconds = CloneBinding(delay.DurationMilliseconds)
                    };
                    break;

                case RepeatNode repeat:
                    target = new RepeatNode
                    {
                        Count = CloneBinding(repeat.Count),
                        Body = (repeat.Body ?? new List<WorkflowNode>())
                            .Select(CloneNode)
                            .ToList()
                    };
                    break;

                case ConditionalNode conditional:
                    target = new ConditionalNode
                    {
                        Branches = (conditional.Branches ?? new List<ConditionalBranch>())
                            .Select(CloneBranch)
                            .ToList()
                    };
                    break;

                default:
                    throw new NotSupportedException(
                        $"Workflow node type '{source.GetType().FullName}' cannot be copied.");
            }

            // Constructors intentionally supply a new node Id. Copy only authored state.
            target.Key = source.Key ?? string.Empty;
            target.DisplayName = source.DisplayName ?? string.Empty;
            target.IsEnabled = source.IsEnabled;
            target.HasBreakpoint = source.HasBreakpoint;
            return target;
        }

        private static ConditionalBranch CloneBranch(ConditionalBranch source)
        {
            if (source == null)
            {
                throw new InvalidOperationException("A conditional branch cannot be null.");
            }

            // The branch constructor intentionally supplies a new branch Id.
            return new ConditionalBranch
            {
                Kind = source.Kind,
                Condition = source.Condition == null ? null : CloneBinding(source.Condition),
                Body = (source.Body ?? new List<WorkflowNode>()).Select(CloneNode).ToList()
            };
        }

        private static ParameterBinding CloneBinding(ParameterBinding binding)
        {
            return new ParameterBinding(binding?.RawText ?? string.Empty);
        }

        private static IEnumerable<WorkflowNode> Enumerate(WorkflowNode root)
        {
            yield return root;
            foreach (var child in root.GetChildren())
            {
                foreach (var descendant in Enumerate(child))
                {
                    yield return descendant;
                }
            }
        }

        private static string CreateUniqueAlias(string originalAlias, ISet<string> usedAliases)
        {
            var root = string.IsNullOrWhiteSpace(originalAlias) ? "action" : originalAlias.Trim();
            var candidate = root + "_copy";
            var suffix = 2;
            while (usedAliases.Contains(candidate))
            {
                candidate = root + "_copy" + suffix;
                suffix++;
            }

            return candidate;
        }

        private static void RewriteInternalReferences(
            WorkflowNode root,
            IReadOnlyDictionary<string, string> aliasMap)
        {
            foreach (var node in Enumerate(root))
            {
                foreach (var binding in node.GetParameterBindings().Values)
                {
                    RewriteBinding(binding, aliasMap);
                }

                if (node is ConditionalNode conditional)
                {
                    foreach (var branch in conditional.Branches ?? new List<ConditionalBranch>())
                    {
                        if (branch?.Condition != null)
                        {
                            RewriteBinding(branch.Condition, aliasMap);
                        }
                    }
                }
            }
        }

        private static void RewriteBinding(
            ParameterBinding binding,
            IReadOnlyDictionary<string, string> aliasMap)
        {
            if (binding == null || !binding.IsExpression)
            {
                return;
            }

            var source = binding.RawText ?? string.Empty;
            var rewritten = new StringBuilder(source.Length);
            var quote = '\0';
            for (var index = 0; index < source.Length; index++)
            {
                var current = source[index];
                if (quote != '\0')
                {
                    rewritten.Append(current);
                    if (current == '\\' && index + 1 < source.Length)
                    {
                        rewritten.Append(source[++index]);
                    }
                    else if (current == quote)
                    {
                        quote = '\0';
                    }

                    continue;
                }

                if (current == '\'' || current == '"')
                {
                    quote = current;
                    rewritten.Append(current);
                    continue;
                }

                if (!IsIdentifierStart(current))
                {
                    rewritten.Append(current);
                    continue;
                }

                var end = index + 1;
                while (end < source.Length && IsIdentifierPart(source[end]))
                {
                    end++;
                }

                var identifier = source.Substring(index, end - index);
                var previous = index - 1;
                while (previous >= 0 && char.IsWhiteSpace(source[previous]))
                {
                    previous--;
                }

                rewritten.Append(
                    (previous < 0 || source[previous] != '.')
                    && aliasMap.TryGetValue(identifier, out var mappedAlias)
                        ? mappedAlias
                        : identifier);
                index = end - 1;
            }

            binding.RawText = rewritten.ToString();
        }

        private static bool IsIdentifierStart(char value)
        {
            return value == '_' || value >= 'A' && value <= 'Z' || value >= 'a' && value <= 'z';
        }

        private static bool IsIdentifierPart(char value)
        {
            return IsIdentifierStart(value) || value >= '0' && value <= '9';
        }
    }
}
