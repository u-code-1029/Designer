using System;

namespace DrillFlow.Core.Workflows
{
    /// <summary>
    /// Preserves the text entered by the workflow author. Values whose first
    /// non-whitespace character is '=' are expressions; all other values are
    /// literals.
    /// </summary>
    public sealed class ParameterBinding : IEquatable<ParameterBinding>
    {
        public ParameterBinding()
            : this(string.Empty)
        {
        }

        public ParameterBinding(string rawText)
        {
            RawText = rawText ?? string.Empty;
        }

        public string RawText { get; set; }

        public bool IsExpression
        {
            get
            {
                var text = RawText ?? string.Empty;
                var index = 0;
                while (index < text.Length && char.IsWhiteSpace(text[index]))
                {
                    index++;
                }

                return index < text.Length && text[index] == '=';
            }
        }

        public string ExpressionText
        {
            get
            {
                if (!IsExpression)
                {
                    return string.Empty;
                }

                var text = (RawText ?? string.Empty).TrimStart();
                return text.Substring(1).Trim();
            }
        }

        public static ParameterBinding Literal(string value)
        {
            return new ParameterBinding(value);
        }

        public static ParameterBinding Expression(string expression)
        {
            if (expression == null)
            {
                throw new ArgumentNullException(nameof(expression));
            }

            var text = expression.Trim();
            if (text.StartsWith("=", StringComparison.Ordinal))
            {
                text = text.Substring(1).TrimStart();
            }

            return new ParameterBinding("=" + text);
        }

        public bool Equals(ParameterBinding? other)
        {
            return other != null && string.Equals(RawText, other.RawText, StringComparison.Ordinal);
        }

        public override bool Equals(object? obj)
        {
            return Equals(obj as ParameterBinding);
        }

        public override int GetHashCode()
        {
            return (RawText ?? string.Empty).GetHashCode();
        }

        public override string ToString()
        {
            return RawText ?? string.Empty;
        }
    }
}
