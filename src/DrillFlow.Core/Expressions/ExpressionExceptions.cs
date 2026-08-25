using System;

namespace DrillFlow.Core.Expressions
{
    public class ExpressionException : Exception
    {
        public ExpressionException(string message, int position)
            : base(message + $" (position {position})")
        {
            Position = position;
        }

        public ExpressionException(string message, int position, Exception innerException)
            : base(message + $" (position {position})", innerException)
        {
            Position = position;
        }

        public int Position { get; }
    }

    public sealed class ExpressionSyntaxException : ExpressionException
    {
        public ExpressionSyntaxException(string message, int position)
            : base(message, position)
        {
        }
    }

    public sealed class ExpressionEvaluationException : ExpressionException
    {
        public ExpressionEvaluationException(string message, int position)
            : base(message, position)
        {
        }
    }
}
