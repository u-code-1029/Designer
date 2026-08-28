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
        public const double MaximumHorizontalFieldWidthMetres = 2.4E-3d;
        public const int MinimumFocusSteps = 4;
        public const int MaximumIntegrationFrameCount = 64;
        public const int MaximumDelayMilliseconds = 29999;
        public const int MaximumHttpTimeoutMilliseconds = 300000;

        public static MoveCoordinateMode GetMoveMode(ExpressionValue value)
        {
            var text = GetString(value, "Move mode").Trim();
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

        public static LensMode GetLensMode(ExpressionValue value)
        {
            var text = GetString(value, "Lens mode").Trim();
            if (string.Equals(text, "lens1", StringComparison.OrdinalIgnoreCase))
            {
                return LensMode.Lens1;
            }

            if (string.Equals(text, "lens2", StringComparison.OrdinalIgnoreCase))
            {
                return LensMode.Lens2;
            }

            if (string.Equals(text, "no_change", StringComparison.OrdinalIgnoreCase))
            {
                return LensMode.NoChange;
            }

            throw new ParameterValidationException(
                "Lens mode must be 'lens1', 'lens2', or 'no_change'.");
        }

        public static double GetMoveCoordinate(ExpressionValue value, string parameterName)
        {
            return GetFiniteCoordinate(value, parameterName);
        }

        public static double GetCoordinate(ExpressionValue value, string parameterName)
        {
            return GetFiniteCoordinate(value, parameterName);
        }

        public static double GetFiniteCoordinate(ExpressionValue value, string parameterName)
        {
            return GetFiniteNumber(value, parameterName);
        }

        /// <summary>
        /// Validates a coordinate that already has a numeric value. Live equipment interaction
        /// uses this overload without manufacturing an expression value.
        /// </summary>
        public static double GetMoveCoordinate(double number, string parameterName)
        {
            return GetFiniteCoordinate(number, parameterName);
        }

        public static double GetCoordinate(double number, string parameterName)
        {
            return GetFiniteCoordinate(number, parameterName);
        }

        public static double GetFiniteCoordinate(double number, string parameterName)
        {
            if (double.IsNaN(number) || double.IsInfinity(number))
            {
                throw new ParameterValidationException($"{parameterName} must be finite.");
            }

            return number;
        }

        public static double GetHorizontalFieldWidth(ExpressionValue value)
        {
            var number = GetFiniteNumber(value, "Horizontal field width");
            if (number <= 0d || number >= MaximumHorizontalFieldWidthMetres)
            {
                throw new ParameterValidationException(
                    "Horizontal field width must be greater than 0 m and less than 2.4E-3 m.");
            }

            return number;
        }

        public static double GetFocusRange(ExpressionValue value)
        {
            var number = GetFiniteNumber(value, "Focus range");
            if (number <= 0d)
            {
                throw new ParameterValidationException("Focus range must be greater than 0 m.");
            }

            return number;
        }

        public static int GetFocusSteps(ExpressionValue value)
        {
            return GetInteger(value, "Focus steps", MinimumFocusSteps, int.MaxValue);
        }

        public static int GetIntegrationFrameCount(ExpressionValue value)
        {
            var count = GetInteger(value, "Integration frame count", 1, MaximumIntegrationFrameCount);
            if ((count & (count - 1)) != 0)
            {
                throw new ParameterValidationException(
                    "Integration frame count must be a power of two from 1 through 64.");
            }

            return count;
        }

        public static int GetLiveFrameCount(ExpressionValue value)
        {
            return GetInteger(value, "Live frame count", 1, 1);
        }

        public static string GetImagePath(ExpressionValue value)
        {
            return GetAbsoluteImagePath(value);
        }

        public static string GetAbsoluteImagePath(ExpressionValue value)
        {
            var path = GetNonEmptyString(value, "Image path").Trim();
            if (!IsSupportedAbsoluteWindowsFilePath(path))
            {
                throw new ParameterValidationException(
                    "Image path must be an absolute local or UNC Windows filename.");
            }

            return path;
        }

        public static bool IsSupportedAbsoluteWindowsFilePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || path![path.Length - 1] == '\\')
            {
                return false;
            }

            var driveRooted = path.Length > 3
                              && IsAsciiLetter(path[0])
                              && path[1] == ':'
                              && path[2] == '\\';
            var uncRooted = false;
            if (path.Length > 5 && path[0] == '\\' && path[1] == '\\' && path[2] != '\\')
            {
                var serverEnd = path.IndexOf('\\', 2);
                if (serverEnd > 2)
                {
                    var shareEnd = path.IndexOf('\\', serverEnd + 1);
                    uncRooted = shareEnd > serverEnd + 1 && shareEnd < path.Length - 1;
                }
            }

            if (!driveRooted && !uncRooted)
            {
                return false;
            }

            for (var index = 0; index < path.Length; index++)
            {
                var character = path[index];
                if (character < ' '
                    || character == '"'
                    || character == '<'
                    || character == '>'
                    || character == '|'
                    || character == '?'
                    || character == '*'
                    || character == '/')
                {
                    return false;
                }

                if (character == ':' && (!driveRooted || index != 1))
                {
                    return false;
                }
            }

            var lastSeparator = path.LastIndexOf('\\');
            var fileName = lastSeparator >= 0
                ? path.Substring(lastSeparator + 1)
                : string.Empty;
            if (fileName.Length == 0
                || string.Equals(fileName, ".", StringComparison.Ordinal)
                || string.Equals(fileName, "..", StringComparison.Ordinal)
                || fileName[fileName.Length - 1] == '.'
                || fileName[fileName.Length - 1] == ' ')
            {
                return false;
            }

            return true;
        }

        public static int GetRepeatCount(ExpressionValue value)
        {
            return GetInteger(value, "Repeat count", 1, int.MaxValue);
        }

        public static int GetDelayMilliseconds(ExpressionValue value)
        {
            return GetInteger(value, "Delay", 0, MaximumDelayMilliseconds);
        }

        public static string GetHttpMethod(ExpressionValue value)
        {
            var method = GetString(value, "HTTP method").Trim();
            if (string.Equals(method, "GET", StringComparison.OrdinalIgnoreCase)
                || string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase))
            {
                return method.ToUpperInvariant();
            }

            throw new ParameterValidationException("HTTP method must be GET or POST.");
        }

        public static string GetHttpUrl(ExpressionValue value)
        {
            var text = GetNonEmptyString(value, "HTTP URL").Trim();
            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
                || !(string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                     || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ParameterValidationException("HTTP URL must be an absolute http or https URL.");
            }

            return uri.AbsoluteUri;
        }

        public static object? GetHttpHeaders(ExpressionValue value)
        {
            if (value.Kind != ExpressionValueKind.Null
                && value.Kind != ExpressionValueKind.String
                && value.Kind != ExpressionValueKind.Object)
            {
                throw new ParameterValidationException(
                    "HTTP headers must be a JSON object string, an object expression, or null.");
            }

            return value.ToObject();
        }

        public static object? GetHttpBody(ExpressionValue value)
        {
            return value.ToObject();
        }

        public static int GetHttpTimeoutMilliseconds(ExpressionValue value)
        {
            return GetInteger(value, "HTTP timeout", 1, MaximumHttpTimeoutMilliseconds);
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

        private static bool IsAsciiLetter(char value)
        {
            return (value >= 'A' && value <= 'Z') || (value >= 'a' && value <= 'z');
        }
    }
}
