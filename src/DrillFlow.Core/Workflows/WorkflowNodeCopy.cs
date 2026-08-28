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

            return CloneManyForInsertion(new[] { source }, existingAliases)[0];
        }

        /// <summary>
        /// Creates independent copies of several workflow subtrees as one ordered batch.
        /// References between different selected roots are rewritten together so copying
        /// actions such as "focus" followed by "integration = focus.parameters..." preserves
        /// the relationship after both aliases are regenerated.
        /// </summary>
        public static IReadOnlyList<WorkflowNode> CloneManyForInsertion(
            IEnumerable<WorkflowNode> sources,
            IEnumerable<string> existingAliases)
        {
            if (sources == null)
            {
                throw new ArgumentNullException(nameof(sources));
            }

            var sourceList = sources.ToList();
            if (sourceList.Any(source => source == null))
            {
                throw new ArgumentException("A copied workflow batch cannot contain a null node.", nameof(sources));
            }

            ValidateUnambiguousBatch(sourceList, nameof(sources));

            var clones = sourceList.Select(CloneNode).ToList();
            var usedAliases = new HashSet<string>(
                existingAliases ?? Enumerable.Empty<string>(),
                StringComparer.OrdinalIgnoreCase);
            var aliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var node in clones.SelectMany(Enumerate))
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

            foreach (var clone in clones)
            {
                RewriteInternalReferences(clone, aliasMap);
            }

            return clones;
        }

        /// <summary>
        /// Alias rewriting requires one source node for each source alias. Duplicate
        /// identities (including selecting both a container and one of its descendants)
        /// and duplicate aliases would make a reference's intended target ambiguous, so
        /// fail before creating a partially meaningful batch.
        /// </summary>
        private static void ValidateUnambiguousBatch(
            IEnumerable<WorkflowNode> roots,
            string parameterName)
        {
            var identities = new HashSet<Guid>();
            var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots)
            {
                ValidateUnambiguousSubtree(root, identities, aliases, parameterName);
            }
        }

        private static void ValidateUnambiguousSubtree(
            WorkflowNode node,
            ISet<Guid> identities,
            ISet<string> aliases,
            string parameterName)
        {
            if (!identities.Add(node.Id))
            {
                throw new ArgumentException(
                    "A copied workflow batch cannot contain duplicate or overlapping nodes.",
                    parameterName);
            }

            var alias = node.Key ?? string.Empty;
            if (!aliases.Add(alias))
            {
                throw new ArgumentException(
                    $"A copied workflow batch contains the duplicate alias '{alias}'.",
                    parameterName);
            }

            foreach (var child in node.GetChildren())
            {
                if (child == null)
                {
                    throw new ArgumentException(
                        "A copied workflow subtree cannot contain a null node.",
                        parameterName);
                }

                ValidateUnambiguousSubtree(child, identities, aliases, parameterName);
            }
        }

        private static WorkflowNode CloneNode(WorkflowNode source)
        {
            WorkflowNode target;
            switch (source)
            {
                case StageNode stage:
                    target = new StageNode
                    {
                        MoveMode = CloneBinding(stage.MoveMode),
                        StageX = CloneBinding(stage.StageX),
                        StageY = CloneBinding(stage.StageY)
                    };
                    break;

                case CameraNode camera:
                    target = new CameraNode
                    {
                        MoveMode = CloneBinding(camera.MoveMode),
                        CameraX = CloneBinding(camera.CameraX),
                        CameraY = CloneBinding(camera.CameraY)
                    };
                    break;

                case FocusNode focus:
                    target = new FocusNode
                    {
                        HorizontalFieldWidth = CloneBinding(focus.HorizontalFieldWidth),
                        Range = CloneBinding(focus.Range),
                        Steps = CloneBinding(focus.Steps)
                    };
                    break;

                case IntegrationNode integration:
                    target = new IntegrationNode
                    {
                        HorizontalFieldWidth = CloneBinding(integration.HorizontalFieldWidth),
                        FrameCount = CloneBinding(integration.FrameCount),
                        ImagePath = CloneBinding(integration.ImagePath)
                    };
                    break;

                case LiveNode live:
                    target = new LiveNode
                    {
                        HorizontalFieldWidth = CloneBinding(live.HorizontalFieldWidth),
                        FrameCount = CloneBinding(live.FrameCount),
                        ImagePath = CloneBinding(live.ImagePath)
                    };
                    break;

                case OmNode om:
                    target = new OmNode
                    {
                        ImagePath = CloneBinding(om.ImagePath)
                    };
                    break;

                case LensNode lens:
                    target = new LensNode
                    {
                        LensMode = CloneBinding(lens.LensMode)
                    };
                    break;

                case AutoContrastBrightnessNode autoContrastBrightness:
                    target = new AutoContrastBrightnessNode
                    {
                        HorizontalFieldWidth = CloneBinding(autoContrastBrightness.HorizontalFieldWidth)
                    };
                    break;

                case AbortNode _:
                    target = new AbortNode();
                    break;

                case HttpActionNode http:
                    target = new HttpActionNode
                    {
                        Method = CloneBinding(http.Method),
                        Url = CloneBinding(http.Url),
                        Headers = CloneBinding(http.Headers),
                        Body = CloneBinding(http.Body),
                        TimeoutMilliseconds = CloneBinding(http.TimeoutMilliseconds)
                    };
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
