using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DrillFlow.Core.Expressions
{
    public enum ExpressionValueKind
    {
        Null,
        Number,
        String,
        Boolean,
        Object,
        Array
    }

    /// <summary>A deliberately small value system used by the safe evaluator.</summary>
    public sealed class ExpressionValue
    {
        private readonly object? _value;

        private ExpressionValue(ExpressionValueKind kind, object? value)
        {
            Kind = kind;
            _value = value;
        }

        public static ExpressionValue Null { get; } = new ExpressionValue(ExpressionValueKind.Null, null);

        public ExpressionValueKind Kind { get; }

        public static ExpressionValue Number(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "Expression numbers must be finite.");
            }

            return new ExpressionValue(ExpressionValueKind.Number, value);
        }

        public static ExpressionValue String(string value)
        {
            return new ExpressionValue(ExpressionValueKind.String, value ?? string.Empty);
        }

        public static ExpressionValue Boolean(bool value)
        {
            return new ExpressionValue(ExpressionValueKind.Boolean, value);
        }

        public static ExpressionValue Object(IEnumerable<KeyValuePair<string, ExpressionValue>> members)
        {
            if (members == null)
            {
                throw new ArgumentNullException(nameof(members));
            }

            var dictionary = new Dictionary<string, ExpressionValue>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in members)
            {
                dictionary[member.Key] = member.Value ?? Null;
            }

            return new ExpressionValue(ExpressionValueKind.Object, dictionary);
        }

        public static ExpressionValue Array(IEnumerable<ExpressionValue> items)
        {
            if (items == null)
            {
                throw new ArgumentNullException(nameof(items));
            }

            return new ExpressionValue(ExpressionValueKind.Array, items.Select(x => x ?? Null).ToArray());
        }

        public double AsNumber()
        {
            if (Kind != ExpressionValueKind.Number)
            {
                throw new InvalidOperationException($"Expected a number, but found {Kind}.");
            }

            return (double)_value!;
        }

        public string AsString()
        {
            if (Kind != ExpressionValueKind.String)
            {
                throw new InvalidOperationException($"Expected a string, but found {Kind}.");
            }

            return (string)_value!;
        }

        public bool AsBoolean()
        {
            if (Kind != ExpressionValueKind.Boolean)
            {
                throw new InvalidOperationException($"Expected a Boolean, but found {Kind}.");
            }

            return (bool)_value!;
        }

        public IReadOnlyDictionary<string, ExpressionValue> AsObject()
        {
            if (Kind != ExpressionValueKind.Object)
            {
                throw new InvalidOperationException($"Expected an object, but found {Kind}.");
            }

            return (IReadOnlyDictionary<string, ExpressionValue>)_value!;
        }

        public IReadOnlyList<ExpressionValue> AsArray()
        {
            if (Kind != ExpressionValueKind.Array)
            {
                throw new InvalidOperationException($"Expected an array, but found {Kind}.");
            }

            return (IReadOnlyList<ExpressionValue>)_value!;
        }

        public object? ToObject()
        {
            switch (Kind)
            {
                case ExpressionValueKind.Null:
                    return null;
                case ExpressionValueKind.Number:
                case ExpressionValueKind.String:
                case ExpressionValueKind.Boolean:
                    return _value;
                case ExpressionValueKind.Object:
                    return AsObject().ToDictionary(x => x.Key, x => x.Value.ToObject(), StringComparer.OrdinalIgnoreCase);
                case ExpressionValueKind.Array:
                    return AsArray().Select(x => x.ToObject()).ToArray();
                default:
                    throw new InvalidOperationException("Unknown expression value kind.");
            }
        }

        public static ExpressionValue FromObject(object? value)
        {
            if (value == null)
            {
                return Null;
            }

            if (value is ExpressionValue expressionValue)
            {
                return expressionValue;
            }

            if (value is string text)
            {
                return String(text);
            }

            if (value is bool boolean)
            {
                return Boolean(boolean);
            }

            if (IsNumeric(value))
            {
                return Number(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            }

            if (value is IReadOnlyDictionary<string, object?> readOnlyDictionary)
            {
                return Object(readOnlyDictionary.Select(x =>
                    new KeyValuePair<string, ExpressionValue>(x.Key, FromObject(x.Value))));
            }

            if (value is IDictionary<string, object?> dictionary)
            {
                return Object(dictionary.Select(x =>
                    new KeyValuePair<string, ExpressionValue>(x.Key, FromObject(x.Value))));
            }

            if (value is IDictionary nonGenericDictionary)
            {
                var members = new List<KeyValuePair<string, ExpressionValue>>();
                foreach (DictionaryEntry entry in nonGenericDictionary)
                {
                    if (entry.Key is string key)
                    {
                        members.Add(new KeyValuePair<string, ExpressionValue>(key, FromObject(entry.Value)));
                    }
                }

                return Object(members);
            }

            if (value is IEnumerable enumerable)
            {
                var items = new List<ExpressionValue>();
                foreach (var item in enumerable)
                {
                    items.Add(FromObject(item));
                }

                return Array(items);
            }

            throw new ArgumentException(
                $"Values of type '{value.GetType().FullName}' cannot be exposed to expressions.",
                nameof(value));
        }

        public override string ToString()
        {
            switch (Kind)
            {
                case ExpressionValueKind.Null:
                    return "null";
                case ExpressionValueKind.Number:
                    return AsNumber().ToString("R", CultureInfo.InvariantCulture);
                case ExpressionValueKind.String:
                    return AsString();
                case ExpressionValueKind.Boolean:
                    return AsBoolean() ? "true" : "false";
                case ExpressionValueKind.Array:
                    return "[" + string.Join(", ", AsArray().Select(x => x.ToString())) + "]";
                case ExpressionValueKind.Object:
                    return "{object}";
                default:
                    return string.Empty;
            }
        }

        private static bool IsNumeric(object value)
        {
            switch (Type.GetTypeCode(value.GetType()))
            {
                case TypeCode.Byte:
                case TypeCode.SByte:
                case TypeCode.UInt16:
                case TypeCode.UInt32:
                case TypeCode.UInt64:
                case TypeCode.Int16:
                case TypeCode.Int32:
                case TypeCode.Int64:
                case TypeCode.Decimal:
                case TypeCode.Double:
                case TypeCode.Single:
                    return true;
                default:
                    return false;
            }
        }
    }
}
