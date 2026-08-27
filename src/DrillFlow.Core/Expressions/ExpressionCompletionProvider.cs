using System;
using System.Collections.Generic;
using System.Linq;
using DrillFlow.Core.Workflows;

namespace DrillFlow.Core.Expressions
{
    /// <summary>
    /// Produces context-aware completions for the safe DrillFlow expression language.
    /// The availability walk intentionally mirrors <see cref="Validation.WorkflowValidator"/>:
    /// only enabled, earlier and guaranteed actions are visible at an authoring location.
    /// </summary>
    public sealed class ExpressionCompletionProvider
    {
        private static readonly string[] ActionMembers =
        {
            "parameters", "result", "results", "last"
        };

        public ExpressionCompletionResult GetCompletions(
            WorkflowDocument document,
            Guid ownerNodeId,
            string? rawText,
            int caretIndex,
            IReadOnlyDictionary<Guid, IReadOnlyCollection<string>>? runtimeResultMembers = null)
        {
            if (document == null)
            {
                throw new ArgumentNullException(nameof(document));
            }

            var text = rawText ?? string.Empty;
            caretIndex = Math.Max(0, Math.Min(caretIndex, text.Length));
            if (!IsExpressionAtCaret(text, caretIndex) || IsCaretInsideString(text, caretIndex))
            {
                return ExpressionCompletionResult.Empty(caretIndex);
            }

            var available = FindAvailableActions(document, ownerNodeId);
            if (available.Count == 0)
            {
                return ExpressionCompletionResult.Empty(caretIndex);
            }

            var chainStart = FindChainStart(text, caretIndex);
            var chain = text.Substring(chainStart, caretIndex - chainStart);

            // If the complete token is already an object (for example "stage_1" or
            // "stage_1.parameters"), Ctrl+Space means "show that object's members".
            if (chain.Length > 0
                && chain[chain.Length - 1] != '.'
                && (caretIndex == text.Length
                    || (!IsIdentifierPart(text[caretIndex])
                        && text[caretIndex] != '.'
                        && text[caretIndex] != '['))
                && TryResolvePath(chain, available, out var exactContext)
                && exactContext.Kind != CompletionContextKind.Leaf)
            {
                var exactItems = CreateMemberItems(
                    exactContext,
                    string.Empty,
                    runtimeResultMembers,
                    appendSeparator: true);
                return new ExpressionCompletionResult(exactItems, caretIndex, 0);
            }

            var fragmentStart = caretIndex;
            while (fragmentStart > chainStart && IsIdentifierPart(text[fragmentStart - 1]))
            {
                fragmentStart--;
            }

            var fragment = text.Substring(fragmentStart, caretIndex - fragmentStart);
            var replacementEnd = caretIndex;
            while (replacementEnd < text.Length && IsIdentifierPart(text[replacementEnd]))
            {
                replacementEnd++;
            }

            var contextText = text.Substring(chainStart, fragmentStart - chainStart);
            if (contextText.EndsWith(".", StringComparison.Ordinal))
            {
                contextText = contextText.Substring(0, contextText.Length - 1);
            }

            IReadOnlyList<ExpressionCompletionItem> items;
            if (contextText.Length == 0)
            {
                items = available
                    .Where(node => node.Key.StartsWith(fragment, StringComparison.OrdinalIgnoreCase))
                    .Select(node => new ExpressionCompletionItem(
                        node.Key,
                        node.Key,
                        node.Kind + " action"))
                    .ToArray();
            }
            else if (TryResolvePath(contextText, available, out var context))
            {
                items = CreateMemberItems(
                    context,
                    fragment,
                    runtimeResultMembers,
                    appendSeparator: false);
            }
            else
            {
                items = Array.Empty<ExpressionCompletionItem>();
            }

            return new ExpressionCompletionResult(
                items,
                fragmentStart,
                replacementEnd - fragmentStart);
        }

        private static bool IsExpressionAtCaret(string text, int caretIndex)
        {
            var first = 0;
            while (first < text.Length && char.IsWhiteSpace(text[first]))
            {
                first++;
            }

            return first < text.Length && text[first] == '=' && caretIndex > first;
        }

        private static bool IsCaretInsideString(string text, int caretIndex)
        {
            var quote = '\0';
            for (var index = 0; index < caretIndex; index++)
            {
                var current = text[index];
                if (quote == '\0')
                {
                    if (current == '\'' || current == '"')
                    {
                        quote = current;
                    }

                    continue;
                }

                if (current == '\\' && index + 1 < caretIndex)
                {
                    index++;
                }
                else if (current == quote)
                {
                    quote = '\0';
                }
            }

            return quote != '\0';
        }

        private static int FindChainStart(string text, int caretIndex)
        {
            var index = caretIndex;
            while (index > 0)
            {
                var candidate = text[index - 1];
                if (!IsIdentifierPart(candidate)
                    && candidate != '.'
                    && candidate != '['
                    && candidate != ']')
                {
                    break;
                }

                index--;
            }

            return index;
        }

        private static bool IsIdentifierPart(char value)
        {
            return char.IsLetterOrDigit(value) || value == '_';
        }

        private static IReadOnlyList<ExpressionCompletionItem> CreateMemberItems(
            CompletionContext context,
            string fragment,
            IReadOnlyDictionary<Guid, IReadOnlyCollection<string>>? runtimeResultMembers,
            bool appendSeparator)
        {
            IEnumerable<ExpressionCompletionItem> candidates;
            switch (context.Kind)
            {
                case CompletionContextKind.Action:
                    candidates = ActionMembers.Select(member => new ExpressionCompletionItem(
                        member,
                        PrefixMember(member, appendSeparator),
                        member == "parameters"
                            ? "Resolved input parameters"
                            : member == "results"
                                ? "All results in the current run"
                                : "Latest result in the current run"));
                    break;

                case CompletionContextKind.Parameters:
                    candidates = context.Node.GetParameterBindings().Keys.Select(member =>
                        new ExpressionCompletionItem(
                            member,
                            PrefixMember(member, appendSeparator),
                            "Input parameter"));
                    break;

                case CompletionContextKind.Result:
                    candidates = GetResultMembers(context.Node, runtimeResultMembers).Select(member =>
                        new ExpressionCompletionItem(
                            member,
                            PrefixMember(member, appendSeparator),
                            "Result field"));
                    break;

                case CompletionContextKind.ResultsArray:
                    candidates = new[]
                    {
                        new ExpressionCompletionItem("last", appendSeparator ? ".last" : "last", "Latest result"),
                        new ExpressionCompletionItem("count", appendSeparator ? ".count" : "count", "Result count"),
                        new ExpressionCompletionItem("length", appendSeparator ? ".length" : "length", "Result count"),
                        new ExpressionCompletionItem("[0]", "[0]", "Result by zero-based index")
                    };
                    break;

                default:
                    candidates = Array.Empty<ExpressionCompletionItem>();
                    break;
            }

            return candidates
                .Where(item => item.DisplayText.StartsWith(fragment, StringComparison.OrdinalIgnoreCase))
                .GroupBy(item => item.DisplayText, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
        }

        private static string PrefixMember(string member, bool appendSeparator)
        {
            return appendSeparator ? "." + member : member;
        }

        private static IEnumerable<string> GetResultMembers(
            WorkflowNode node,
            IReadOnlyDictionary<Guid, IReadOnlyCollection<string>>? runtimeResultMembers)
        {
            var members = new List<string> { "correlation_id", "iteration_path" };
            switch (node.Kind)
            {
                case WorkflowNodeKind.Stage:
                    AddEquipmentEnvelope(members);
                    members.Add("current_stage_x");
                    members.Add("current_stage_y");
                    break;
                case WorkflowNodeKind.Camera:
                    AddEquipmentEnvelope(members);
                    members.Add("current_camera_x");
                    members.Add("current_camera_y");
                    break;
                case WorkflowNodeKind.Focus:
                    AddEquipmentEnvelope(members);
                    members.Add("z_to_sharpness_2d");
                    break;
                case WorkflowNodeKind.Integration:
                case WorkflowNodeKind.Live:
                    AddEquipmentEnvelope(members);
                    members.Add("hfw");
                    members.Add("frame_count");
                    members.Add("image_path");
                    break;
                case WorkflowNodeKind.Abort:
                    AddEquipmentEnvelope(members);
                    break;
                case WorkflowNodeKind.Http:
                    members.Add("status_code");
                    members.Add("is_success");
                    members.Add("reason_phrase");
                    members.Add("headers");
                    members.Add("body_text");
                    members.Add("content_type");
                    members.Add("json");
                    break;
                case WorkflowNodeKind.Delay:
                    members.Add("elapsed_milliseconds");
                    break;
                case WorkflowNodeKind.Repeat:
                    members.Add("count");
                    break;
                case WorkflowNodeKind.Conditional:
                    members.Add("branch_index");
                    members.Add("branch_kind");
                    break;
            }

            if (runtimeResultMembers != null
                && runtimeResultMembers.TryGetValue(node.Id, out var observed))
            {
                members.AddRange(observed.Where(member => !string.IsNullOrWhiteSpace(member)));
            }

            return members.Distinct(StringComparer.OrdinalIgnoreCase);
        }

        private static void AddEquipmentEnvelope(ICollection<string> members)
        {
            members.Add("type");
            members.Add("correlation_id");
            members.Add("action");
            members.Add("result");
        }

        private static bool TryResolvePath(
            string path,
            IReadOnlyList<WorkflowNode> available,
            out CompletionContext context)
        {
            context = default!;
            if (!TryTokenizePath(path, out var root, out var segments))
            {
                return false;
            }

            var node = available.LastOrDefault(candidate =>
                string.Equals(candidate.Key, root, StringComparison.OrdinalIgnoreCase));
            if (node == null)
            {
                return false;
            }

            var kind = CompletionContextKind.Action;
            foreach (var segment in segments)
            {
                switch (kind)
                {
                    case CompletionContextKind.Action:
                        if (string.Equals(segment, "parameters", StringComparison.OrdinalIgnoreCase))
                        {
                            kind = CompletionContextKind.Parameters;
                        }
                        else if (string.Equals(segment, "result", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(segment, "last", StringComparison.OrdinalIgnoreCase))
                        {
                            kind = CompletionContextKind.Result;
                        }
                        else if (string.Equals(segment, "results", StringComparison.OrdinalIgnoreCase))
                        {
                            kind = CompletionContextKind.ResultsArray;
                        }
                        else
                        {
                            return false;
                        }

                        break;

                    case CompletionContextKind.Parameters:
                        kind = node.GetParameterBindings().Keys.Any(key =>
                            string.Equals(key, segment, StringComparison.OrdinalIgnoreCase))
                            ? CompletionContextKind.Leaf
                            : CompletionContextKind.Invalid;
                        break;

                    case CompletionContextKind.Result:
                        // Equipment response fields are intentionally open-ended. Any first
                        // result member is therefore a valid leaf even when not yet observed.
                        kind = CompletionContextKind.Leaf;
                        break;

                    case CompletionContextKind.ResultsArray:
                        if (segment == "[]"
                            || string.Equals(segment, "last", StringComparison.OrdinalIgnoreCase))
                        {
                            kind = CompletionContextKind.Result;
                        }
                        else if (string.Equals(segment, "count", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(segment, "length", StringComparison.OrdinalIgnoreCase))
                        {
                            kind = CompletionContextKind.Leaf;
                        }
                        else
                        {
                            kind = CompletionContextKind.Invalid;
                        }

                        break;

                    default:
                        return false;
                }

                if (kind == CompletionContextKind.Invalid)
                {
                    return false;
                }
            }

            context = new CompletionContext(node, kind);
            return true;
        }

        private static bool TryTokenizePath(
            string path,
            out string root,
            out IReadOnlyList<string> segments)
        {
            root = string.Empty;
            segments = Array.Empty<string>();
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var index = 0;
            if (!TryReadIdentifier(path, ref index, out root))
            {
                return false;
            }

            var result = new List<string>();
            while (index < path.Length)
            {
                if (path[index] == '.')
                {
                    index++;
                    if (!TryReadIdentifier(path, ref index, out var member))
                    {
                        return false;
                    }

                    result.Add(member);
                }
                else if (path[index] == '[')
                {
                    index++;
                    var digitStart = index;
                    while (index < path.Length && char.IsDigit(path[index]))
                    {
                        index++;
                    }

                    if (digitStart == index || index >= path.Length || path[index] != ']')
                    {
                        return false;
                    }

                    index++;
                    result.Add("[]");
                }
                else
                {
                    return false;
                }
            }

            segments = result;
            return true;
        }

        private static bool TryReadIdentifier(string text, ref int index, out string identifier)
        {
            identifier = string.Empty;
            if (index >= text.Length || (!char.IsLetter(text[index]) && text[index] != '_'))
            {
                return false;
            }

            var start = index++;
            while (index < text.Length && IsIdentifierPart(text[index]))
            {
                index++;
            }

            identifier = text.Substring(start, index - start);
            return true;
        }

        private static IReadOnlyList<WorkflowNode> FindAvailableActions(
            WorkflowDocument document,
            Guid ownerNodeId)
        {
            var available = new List<WorkflowNode>();
            return VisitBlock(document.Nodes, ownerNodeId, available, out var result)
                ? result
                : Array.Empty<WorkflowNode>();
        }

        private static bool VisitBlock(
            IList<WorkflowNode>? nodes,
            Guid ownerNodeId,
            List<WorkflowNode> available,
            out IReadOnlyList<WorkflowNode> result)
        {
            result = Array.Empty<WorkflowNode>();
            if (nodes == null)
            {
                return false;
            }

            foreach (var node in nodes)
            {
                if (node == null)
                {
                    continue;
                }

                if (node.Id == ownerNodeId)
                {
                    result = available.ToArray();
                    return true;
                }

                if (node is RepeatNode repeat)
                {
                    var bodyAvailable = new List<WorkflowNode>(available);
                    if (VisitBlock(repeat.Body, ownerNodeId, bodyAvailable, out result))
                    {
                        return true;
                    }

                    if (node.IsEnabled)
                    {
                        AddDistinct(available, bodyAvailable);
                    }
                }
                else if (node is ConditionalNode conditional && conditional.Branches != null)
                {
                    foreach (var branch in conditional.Branches)
                    {
                        var branchAvailable = new List<WorkflowNode>(available);
                        if (VisitBlock(branch?.Body, ownerNodeId, branchAvailable, out result))
                        {
                            return true;
                        }
                    }
                }

                if (node.IsEnabled && IsUsableKey(node.Key))
                {
                    AddDistinct(available, new[] { node });
                }
            }

            return false;
        }

        private static void AddDistinct(List<WorkflowNode> destination, IEnumerable<WorkflowNode> source)
        {
            foreach (var node in source)
            {
                if (node.IsEnabled
                    && IsUsableKey(node.Key)
                    && destination.All(existing => !string.Equals(
                        existing.Key,
                        node.Key,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    destination.Add(node);
                }
            }
        }

        private static bool IsUsableKey(string? key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return false;
            }

            var nonEmptyKey = key!;
            if (!IsAsciiIdentifierStart(nonEmptyKey[0]))
            {
                return false;
            }

            return nonEmptyKey.Skip(1).All(IsAsciiIdentifierPart)
                   && !string.Equals(nonEmptyKey, "true", StringComparison.OrdinalIgnoreCase)
                   && !string.Equals(nonEmptyKey, "false", StringComparison.OrdinalIgnoreCase)
                   && !string.Equals(nonEmptyKey, "null", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAsciiIdentifierStart(char value)
        {
            return value == '_'
                   || value >= 'A' && value <= 'Z'
                   || value >= 'a' && value <= 'z';
        }

        private static bool IsAsciiIdentifierPart(char value)
        {
            return IsAsciiIdentifierStart(value) || value >= '0' && value <= '9';
        }

        private sealed class CompletionContext
        {
            public CompletionContext(WorkflowNode node, CompletionContextKind kind)
            {
                Node = node;
                Kind = kind;
            }

            public WorkflowNode Node { get; }

            public CompletionContextKind Kind { get; }
        }

        private enum CompletionContextKind
        {
            Invalid,
            Action,
            Parameters,
            Result,
            ResultsArray,
            Leaf
        }
    }

    public sealed class ExpressionCompletionResult
    {
        public ExpressionCompletionResult(
            IReadOnlyList<ExpressionCompletionItem> items,
            int replacementStart,
            int replacementLength)
        {
            Items = items ?? throw new ArgumentNullException(nameof(items));
            ReplacementStart = replacementStart;
            ReplacementLength = replacementLength;
        }

        public IReadOnlyList<ExpressionCompletionItem> Items { get; }

        public int ReplacementStart { get; }

        public int ReplacementLength { get; }

        public static ExpressionCompletionResult Empty(int caretIndex)
        {
            return new ExpressionCompletionResult(
                Array.Empty<ExpressionCompletionItem>(),
                caretIndex,
                0);
        }
    }

    public sealed class ExpressionCompletionItem
    {
        public ExpressionCompletionItem(string displayText, string insertionText, string description)
        {
            DisplayText = displayText ?? throw new ArgumentNullException(nameof(displayText));
            InsertionText = insertionText ?? throw new ArgumentNullException(nameof(insertionText));
            Description = description ?? string.Empty;
        }

        public string DisplayText { get; }

        public string InsertionText { get; }

        public string Description { get; }

        public string DisplayLine => string.IsNullOrWhiteSpace(Description)
            ? DisplayText
            : DisplayText + "  —  " + Description;
    }
}
