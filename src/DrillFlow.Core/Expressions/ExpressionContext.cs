using System;
using System.Collections.Generic;
using System.Linq;
using DrillFlow.Core.Runtime;
using DrillFlow.Core.Workflows;

namespace DrillFlow.Core.Expressions
{
    public sealed class ExpressionContext
    {
        private readonly Dictionary<string, ExpressionValue> _variables =
            new Dictionary<string, ExpressionValue>(StringComparer.OrdinalIgnoreCase);

        public ExpressionContext SetVariable(string name, object? value)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A variable name is required.", nameof(name));
            }

            _variables[name] = ExpressionValue.FromObject(value);
            return this;
        }

        public ExpressionContext SetVariable(string name, ExpressionValue value)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("A variable name is required.", nameof(name));
            }

            _variables[name] = value ?? ExpressionValue.Null;
            return this;
        }

        public bool TryGetVariable(string name, out ExpressionValue value)
        {
            return _variables.TryGetValue(name, out value!);
        }

        /// <summary>
        /// Exposes an action as key.parameters, key.result, key.results and
        /// key.last. result/last are null until the action has run.
        /// </summary>
        public ExpressionContext SetAction(
            WorkflowNode node,
            IReadOnlyDictionary<string, object?> evaluatedParameters,
            IEnumerable<ActionExecutionResult>? results = null)
        {
            if (node == null)
            {
                throw new ArgumentNullException(nameof(node));
            }

            if (evaluatedParameters == null)
            {
                throw new ArgumentNullException(nameof(evaluatedParameters));
            }

            var resultValues = (results ?? Enumerable.Empty<ActionExecutionResult>())
                .Select(CreateResultValue)
                .ToArray();
            var latest = resultValues.Length == 0 ? ExpressionValue.Null : resultValues[resultValues.Length - 1];

            var action = ExpressionValue.Object(new[]
            {
                Pair("parameters", ExpressionValue.FromObject(evaluatedParameters)),
                Pair("result", latest),
                Pair("results", ExpressionValue.Array(resultValues)),
                Pair("last", latest)
            });

            return SetVariable(node.Key, action);
        }

        public ExpressionContext SetAction(
            WorkflowNode node,
            IReadOnlyDictionary<string, object?> evaluatedParameters,
            RunResultStore resultStore)
        {
            if (resultStore == null)
            {
                throw new ArgumentNullException(nameof(resultStore));
            }

            return SetAction(node, evaluatedParameters, resultStore.GetAll(node.Id));
        }

        private static ExpressionValue CreateResultValue(ActionExecutionResult result)
        {
            var fields = new Dictionary<string, object?>(result.Values, StringComparer.OrdinalIgnoreCase);
            if (!fields.ContainsKey("correlation_id"))
            {
                fields["correlation_id"] = result.CorrelationId;
            }

            if (!fields.ContainsKey("iteration_path"))
            {
                fields["iteration_path"] = result.IterationPath.ToArray();
            }

            return ExpressionValue.FromObject(fields);
        }

        private static KeyValuePair<string, ExpressionValue> Pair(string key, ExpressionValue value)
        {
            return new KeyValuePair<string, ExpressionValue>(key, value);
        }
    }

    public sealed class ExpressionAnalysis
    {
        internal ExpressionAnalysis(
            IEnumerable<string> rootIdentifiers,
            IEnumerable<ExpressionMemberReference> firstLevelMemberReferences)
        {
            RootIdentifiers = rootIdentifiers
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            FirstLevelMemberReferences = firstLevelMemberReferences
                .Distinct(ExpressionMemberReferenceComparer.Instance)
                .ToArray();
        }

        public IReadOnlyCollection<string> RootIdentifiers { get; }

        /// <summary>
        /// Direct members accessed from root identifiers, such as the
        /// "parameters" member in action.parameters.stage_x. Deeper result
        /// fields are intentionally not analyzed because equipment responses
        /// may add arbitrary properties at runtime.
        /// </summary>
        public IReadOnlyCollection<ExpressionMemberReference> FirstLevelMemberReferences { get; }

        private sealed class ExpressionMemberReferenceComparer : IEqualityComparer<ExpressionMemberReference>
        {
            public static ExpressionMemberReferenceComparer Instance { get; } =
                new ExpressionMemberReferenceComparer();

            public bool Equals(ExpressionMemberReference? x, ExpressionMemberReference? y)
            {
                if (ReferenceEquals(x, y))
                {
                    return true;
                }

                return x != null
                       && y != null
                       && string.Equals(x.RootIdentifier, y.RootIdentifier, StringComparison.OrdinalIgnoreCase)
                       && string.Equals(x.MemberName, y.MemberName, StringComparison.OrdinalIgnoreCase);
            }

            public int GetHashCode(ExpressionMemberReference obj)
            {
                unchecked
                {
                    return (StringComparer.OrdinalIgnoreCase.GetHashCode(obj.RootIdentifier) * 397)
                           ^ StringComparer.OrdinalIgnoreCase.GetHashCode(obj.MemberName);
                }
            }
        }
    }

    public sealed class ExpressionMemberReference
    {
        internal ExpressionMemberReference(string rootIdentifier, string memberName)
        {
            RootIdentifier = rootIdentifier;
            MemberName = memberName;
        }

        public string RootIdentifier { get; }

        public string MemberName { get; }
    }
}
