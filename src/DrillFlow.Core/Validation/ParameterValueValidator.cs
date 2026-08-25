using System;
using DrillFlow.Core.Expressions;
using DrillFlow.Core.Workflows;

namespace DrillFlow.Core.Validation
{
    public sealed class ParameterValidationException : Exception
    {
        public ParameterValidationException(string message)
            : base(message)
        {
        }
    }

    /// <summary>
    /// Runtime checks shared by the workflow validator and runner. Expression
    /// values must pass these checks after evaluation and before a request is
    /// written.
    /// </summary>
    public static class ParameterValueValidator
    {
        public const double MoveCoordinateLimitMetres = 0.5d;
        public const double MaximumThicknessMetres = 2.4E-3d;
        public const int MaximumDelayMilliseconds = 29999;

        public static MoveCoordinateMode GetMoveMode(ExpressionValue value)
        {
            var text = GetString(value, "Move mode");
            if (string.Equals(text, "relative", StringComparison.OrdinalIgnoreCase))
            {
                return MoveCoordinateMode.Relative;
            }

            if (string.Equals(text, "absolute", StringComparison.OrdinalIgnoreCase))
            {
                return MoveCoordinateMode.Absolute;
            }

            throw new ParameterValidationException("Move mode must be 'relative' or 'absolute'.");
        }

        public static double GetMoveCoordinate(ExpressionValue value, string parameterName)
        {
            var number = GetFiniteNumber(value, parameterName);
            if (number <= -MoveCoordinateLimitMetres || number >= MoveCoordinateLimitMetres)
            {
                throw new ParameterValidationException(
                    $"{parameterName} must be greater than -0.5 m and less than 0.5 m.");
            }

            return number;
        }

        public static double GetThickness(ExpressionValue value)
        {
            var number = GetFiniteNumber(value, "Thickness");
            if (number <= 0d || number > MaximumThicknessMetres)
            {
                throw new ParameterValidationException("Thickness must be greater than 0 m and at most 2.4E-3 m.");
            }

            return number;
        }

        public static int GetRepeatCount(ExpressionValue value)
        {
            return GetInteger(value, "Repeat count", 1, int.MaxValue);
        }

        public static int GetDelayMilliseconds(ExpressionValue value)
        {
            return GetInteger(value, "Delay", 0, MaximumDelayMilliseconds);
        }

        public static string GetNonEmptyString(ExpressionValue value, string parameterName)
        {
            var text = GetString(value, parameterName);
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ParameterValidationException($"{parameterName} is required.");
            }

            return text;
        }

        public static bool GetBoolean(ExpressionValue value, string parameterName)
        {
            if (value.Kind != ExpressionValueKind.Boolean)
            {
                throw new ParameterValidationException($"{parameterName} must evaluate to a Boolean.");
            }

            return value.AsBoolean();
        }

        public static double GetFiniteNumber(ExpressionValue value, string parameterName)
        {
            if (value.Kind != ExpressionValueKind.Number)
            {
                throw new ParameterValidationException($"{parameterName} must evaluate to a number.");
            }

            var number = value.AsNumber();
            if (double.IsNaN(number) || double.IsInfinity(number))
            {
                throw new ParameterValidationException($"{parameterName} must be finite.");
            }

            return number;
        }

        private static string GetString(ExpressionValue value, string parameterName)
        {
            if (value.Kind != ExpressionValueKind.String)
            {
                throw new ParameterValidationException($"{parameterName} must evaluate to a string.");
            }

            return value.AsString();
        }

        private static int GetInteger(ExpressionValue value, string parameterName, int minimum, int maximum)
        {
            var number = GetFiniteNumber(value, parameterName);
            if (number != Math.Truncate(number) || number < minimum || number > maximum)
            {
                throw new ParameterValidationException(
                    $"{parameterName} must be an integer from {minimum} through {maximum}.");
            }

            return (int)number;
        }
    }
}
