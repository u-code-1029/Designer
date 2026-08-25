using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DrillFlow.Core.Workflows;

namespace DrillFlow.Core.Expressions
{
    /// <summary>
    /// Tokenizes, parses and evaluates the DrillFlow expression language. It
    /// does not compile, reflect over, or execute CLR/C# code.
    /// </summary>
    public sealed class ExpressionEngine
    {
        public ExpressionValue Evaluate(string expression, ExpressionContext? context = null)
        {
            var parser = new Parser(PrepareExpression(expression));
            var root = parser.Parse();
            return root.Evaluate(context ?? new ExpressionContext());
        }

        public ExpressionValue Evaluate(ParameterBinding binding, ExpressionContext? context = null)
        {
            if (binding == null)
            {
                throw new ArgumentNullException(nameof(binding));
            }

            return binding.IsExpression
                ? Evaluate(binding.ExpressionText, context)
                : EvaluateLiteral(binding.RawText);
        }

        public ExpressionValue EvaluateLiteral(string? literal)
        {
            var text = (literal ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return ExpressionValue.String(string.Empty);
            }

            if (text[0] == '\'' || text[0] == '"')
            {
                var parser = new Parser(text);
                var node = parser.Parse();
                if (!(node is LiteralNode literalNode) || literalNode.Value.Kind != ExpressionValueKind.String)
                {
                    throw new ExpressionSyntaxException("A quoted literal must contain only one string.", 0);
                }

                return literalNode.Value;
            }

            if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
            {
                return ExpressionValue.Boolean(true);
            }

            if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
            {
                return ExpressionValue.Boolean(false);
            }

            if (string.Equals(text, "null", StringComparison.OrdinalIgnoreCase))
            {
                return ExpressionValue.Null;
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
            {
                if (double.IsNaN(number) || double.IsInfinity(number))
                {
                    throw new ExpressionEvaluationException("Numeric literals must be finite.", 0);
                }

                return ExpressionValue.Number(number);
            }

            return ExpressionValue.String(literal ?? string.Empty);
        }

        public ExpressionAnalysis Analyze(string expression)
        {
            var parser = new Parser(PrepareExpression(expression));
            parser.Parse();
            return new ExpressionAnalysis(parser.RootIdentifiers, parser.FirstLevelMemberReferences);
        }

        private static string PrepareExpression(string expression)
        {
            if (expression == null)
            {
                throw new ArgumentNullException(nameof(expression));
            }

            var text = expression.Trim();
            if (text.StartsWith("=", StringComparison.Ordinal))
            {
                text = text.Substring(1).Trim();
            }

            if (text.Length == 0)
            {
                throw new ExpressionSyntaxException("An expression is required.", 0);
            }

            return text;
        }

        private enum TokenKind
        {
            End,
            Number,
            String,
            Identifier,
            True,
            False,
            Null,
            Plus,
            Minus,
            Star,
            Slash,
            Bang,
            EqualEqual,
            BangEqual,
            Less,
            LessEqual,
            Greater,
            GreaterEqual,
            AndAnd,
            OrOr,
            Dot,
            LeftParenthesis,
            RightParenthesis,
            LeftBracket,
            RightBracket
        }

        private sealed class Token
        {
            public Token(TokenKind kind, string text, int position, object? value = null)
            {
                Kind = kind;
                Text = text;
                Position = position;
                Value = value;
            }

            public TokenKind Kind { get; }
            public string Text { get; }
            public int Position { get; }
            public object? Value { get; }
        }

        private sealed class Lexer
        {
            private readonly string _source;
            private int _position;

            public Lexer(string source)
            {
                _source = source;
            }

            public Token Next()
            {
                SkipWhitespace();
                if (_position >= _source.Length)
                {
                    return new Token(TokenKind.End, string.Empty, _position);
                }

                var start = _position;
                var current = _source[_position];
                if (char.IsDigit(current) || (current == '.' && PeekIsDigit(1)))
                {
                    return ReadNumber();
                }

                if (current == '\'' || current == '"')
                {
                    return ReadString();
                }

                if (IsIdentifierStart(current))
                {
                    return ReadIdentifier();
                }

                _position++;
                switch (current)
                {
                    case '+': return new Token(TokenKind.Plus, "+", start);
                    case '-': return new Token(TokenKind.Minus, "-", start);
                    case '*': return new Token(TokenKind.Star, "*", start);
                    case '/': return new Token(TokenKind.Slash, "/", start);
                    case '.': return new Token(TokenKind.Dot, ".", start);
                    case '(': return new Token(TokenKind.LeftParenthesis, "(", start);
                    case ')': return new Token(TokenKind.RightParenthesis, ")", start);
                    case '[': return new Token(TokenKind.LeftBracket, "[", start);
                    case ']': return new Token(TokenKind.RightBracket, "]", start);
                    case '!':
                        if (Match('=')) return new Token(TokenKind.BangEqual, "!=", start);
                        return new Token(TokenKind.Bang, "!", start);
                    case '=':
                        if (Match('=')) return new Token(TokenKind.EqualEqual, "==", start);
                        throw new ExpressionSyntaxException("Use '==' for equality.", start);
                    case '<':
                        if (Match('=')) return new Token(TokenKind.LessEqual, "<=", start);
                        return new Token(TokenKind.Less, "<", start);
                    case '>':
                        if (Match('=')) return new Token(TokenKind.GreaterEqual, ">=", start);
                        return new Token(TokenKind.Greater, ">", start);
                    case '&':
                        if (Match('&')) return new Token(TokenKind.AndAnd, "&&", start);
                        throw new ExpressionSyntaxException("Use '&&' for logical AND.", start);
                    case '|':
                        if (Match('|')) return new Token(TokenKind.OrOr, "||", start);
                        throw new ExpressionSyntaxException("Use '||' for logical OR.", start);
                    default:
                        throw new ExpressionSyntaxException($"Unexpected character '{current}'.", start);
                }
            }

            private Token ReadNumber()
            {
                var start = _position;
                var hasDigits = false;
                while (_position < _source.Length && char.IsDigit(_source[_position]))
                {
                    hasDigits = true;
                    _position++;
                }

                if (_position < _source.Length && _source[_position] == '.')
                {
                    _position++;
                    while (_position < _source.Length && char.IsDigit(_source[_position]))
                    {
                        hasDigits = true;
                        _position++;
                    }
                }

                if (!hasDigits)
                {
                    throw new ExpressionSyntaxException("A number requires at least one digit.", start);
                }

                if (_position < _source.Length && (_source[_position] == 'e' || _source[_position] == 'E'))
                {
                    var exponentPosition = _position;
                    _position++;
                    if (_position < _source.Length && (_source[_position] == '+' || _source[_position] == '-'))
                    {
                        _position++;
                    }

                    var exponentStart = _position;
                    while (_position < _source.Length && char.IsDigit(_source[_position]))
                    {
                        _position++;
                    }

                    if (_position == exponentStart)
                    {
                        throw new ExpressionSyntaxException("The exponent requires digits.", exponentPosition);
                    }
                }

                var text = _source.Substring(start, _position - start);
                if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ||
                    double.IsNaN(number) || double.IsInfinity(number))
                {
                    throw new ExpressionSyntaxException("The numeric literal is not finite or valid.", start);
                }

                return new Token(TokenKind.Number, text, start, number);
            }

            private Token ReadString()
            {
                var start = _position;
                var quote = _source[_position++];
                var builder = new StringBuilder();
                while (_position < _source.Length)
                {
                    var current = _source[_position++];
                    if (current == quote)
                    {
                        return new Token(TokenKind.String, _source.Substring(start, _position - start), start, builder.ToString());
                    }

                    if (current != '\\')
                    {
                        builder.Append(current);
                        continue;
                    }

                    if (_position >= _source.Length)
                    {
                        throw new ExpressionSyntaxException("An escape sequence is incomplete.", _position - 1);
                    }

                    var escaped = _source[_position++];
                    switch (escaped)
                    {
                        case '\\': builder.Append('\\'); break;
                        case '\'': builder.Append('\''); break;
                        case '"': builder.Append('"'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'u': builder.Append(ReadUnicodeEscape()); break;
                        default:
                            throw new ExpressionSyntaxException($"Unknown escape sequence '\\{escaped}'.", _position - 2);
                    }
                }

                throw new ExpressionSyntaxException("The string literal is not terminated.", start);
            }

            private char ReadUnicodeEscape()
            {
                var start = _position;
                if (_position + 4 > _source.Length)
                {
                    throw new ExpressionSyntaxException("A Unicode escape requires four hexadecimal digits.", start);
                }

                var text = _source.Substring(_position, 4);
                if (!ushort.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                {
                    throw new ExpressionSyntaxException("A Unicode escape contains invalid hexadecimal digits.", start);
                }

                _position += 4;
                return (char)code;
            }

            private Token ReadIdentifier()
            {
                var start = _position++;
                while (_position < _source.Length && IsIdentifierPart(_source[_position]))
                {
                    _position++;
                }

                var text = _source.Substring(start, _position - start);
                if (string.Equals(text, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return new Token(TokenKind.True, text, start, true);
                }

                if (string.Equals(text, "false", StringComparison.OrdinalIgnoreCase))
                {
                    return new Token(TokenKind.False, text, start, false);
                }

                if (string.Equals(text, "null", StringComparison.OrdinalIgnoreCase))
                {
                    return new Token(TokenKind.Null, text, start);
                }

                return new Token(TokenKind.Identifier, text, start, text);
            }

            private bool Match(char expected)
            {
                if (_position < _source.Length && _source[_position] == expected)
                {
                    _position++;
                    return true;
                }

                return false;
            }

            private bool PeekIsDigit(int offset)
            {
                return _position + offset < _source.Length && char.IsDigit(_source[_position + offset]);
            }

            private void SkipWhitespace()
            {
                while (_position < _source.Length && char.IsWhiteSpace(_source[_position]))
                {
                    _position++;
                }
            }

            private static bool IsIdentifierStart(char value)
            {
                return value == '_' || char.IsLetter(value);
            }

            private static bool IsIdentifierPart(char value)
            {
                return value == '_' || char.IsLetterOrDigit(value);
            }
        }

        private sealed class Parser
        {
            private readonly Lexer _lexer;
            private Token _current;
            private readonly HashSet<string> _rootIdentifiers =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly List<ExpressionMemberReference> _firstLevelMemberReferences =
                new List<ExpressionMemberReference>();

            public Parser(string source)
            {
                _lexer = new Lexer(source);
                _current = _lexer.Next();
            }

            public IEnumerable<string> RootIdentifiers => _rootIdentifiers;

            public IEnumerable<ExpressionMemberReference> FirstLevelMemberReferences =>
                _firstLevelMemberReferences;

            public Node Parse()
            {
                var result = ParseOr();
                if (_current.Kind != TokenKind.End)
                {
                    throw new ExpressionSyntaxException($"Unexpected token '{_current.Text}'.", _current.Position);
                }

                return result;
            }

            private Node ParseOr()
            {
                var left = ParseAnd();
                while (_current.Kind == TokenKind.OrOr)
                {
                    var operation = Take();
                    left = new BinaryNode(left, operation, ParseAnd());
                }

                return left;
            }

            private Node ParseAnd()
            {
                var left = ParseEquality();
                while (_current.Kind == TokenKind.AndAnd)
                {
                    var operation = Take();
                    left = new BinaryNode(left, operation, ParseEquality());
                }

                return left;
            }

            private Node ParseEquality()
            {
                var left = ParseComparison();
                while (_current.Kind == TokenKind.EqualEqual || _current.Kind == TokenKind.BangEqual)
                {
                    var operation = Take();
                    left = new BinaryNode(left, operation, ParseComparison());
                }

                return left;
            }

            private Node ParseComparison()
            {
                var left = ParseTerm();
                while (_current.Kind == TokenKind.Less ||
                       _current.Kind == TokenKind.LessEqual ||
                       _current.Kind == TokenKind.Greater ||
                       _current.Kind == TokenKind.GreaterEqual)
                {
                    var operation = Take();
                    left = new BinaryNode(left, operation, ParseTerm());
                }

                return left;
            }

            private Node ParseTerm()
            {
                var left = ParseFactor();
                while (_current.Kind == TokenKind.Plus || _current.Kind == TokenKind.Minus)
                {
                    var operation = Take();
                    left = new BinaryNode(left, operation, ParseFactor());
                }

                return left;
            }

            private Node ParseFactor()
            {
                var left = ParseUnary();
                while (_current.Kind == TokenKind.Star || _current.Kind == TokenKind.Slash)
                {
                    var operation = Take();
                    left = new BinaryNode(left, operation, ParseUnary());
                }

                return left;
            }

            private Node ParseUnary()
            {
                if (_current.Kind == TokenKind.Bang ||
                    _current.Kind == TokenKind.Plus ||
                    _current.Kind == TokenKind.Minus)
                {
                    var operation = Take();
                    return new UnaryNode(operation, ParseUnary());
                }

                return ParsePostfix();
            }

            private Node ParsePostfix()
            {
                var value = ParsePrimary();
                while (true)
                {
                    if (_current.Kind == TokenKind.Dot)
                    {
                        Take();
                        var member = Expect(TokenKind.Identifier, "A member name is required after '.'.");
                        if (value is VariableNode variable)
                        {
                            _firstLevelMemberReferences.Add(
                                new ExpressionMemberReference(variable.Name, (string)member.Value!));
                        }

                        value = new MemberNode(value, (string)member.Value!, member.Position);
                        continue;
                    }

                    if (_current.Kind == TokenKind.LeftBracket)
                    {
                        var bracket = Take();
                        var index = ParseOr();
                        Expect(TokenKind.RightBracket, "A closing ']' is required.");
                        value = new IndexNode(value, index, bracket.Position);
                        continue;
                    }

                    return value;
                }
            }

            private Node ParsePrimary()
            {
                switch (_current.Kind)
                {
                    case TokenKind.Number:
                    {
                        var token = Take();
                        return new LiteralNode(ExpressionValue.Number((double)token.Value!), token.Position);
                    }
                    case TokenKind.String:
                    {
                        var token = Take();
                        return new LiteralNode(ExpressionValue.String((string)token.Value!), token.Position);
                    }
                    case TokenKind.True:
                    case TokenKind.False:
                    {
                        var token = Take();
                        return new LiteralNode(ExpressionValue.Boolean((bool)token.Value!), token.Position);
                    }
                    case TokenKind.Null:
                    {
                        var token = Take();
                        return new LiteralNode(ExpressionValue.Null, token.Position);
                    }
                    case TokenKind.Identifier:
                    {
                        var token = Take();
                        var name = (string)token.Value!;
                        _rootIdentifiers.Add(name);
                        return new VariableNode(name, token.Position);
                    }
                    case TokenKind.LeftParenthesis:
                    {
                        Take();
                        var inner = ParseOr();
                        Expect(TokenKind.RightParenthesis, "A closing ')' is required.");
                        return inner;
                    }
                    default:
                        throw new ExpressionSyntaxException("A value or sub-expression is required.", _current.Position);
                }
            }

            private Token Take()
            {
                var token = _current;
                _current = _lexer.Next();
                return token;
            }

            private Token Expect(TokenKind kind, string message)
            {
                if (_current.Kind != kind)
                {
                    throw new ExpressionSyntaxException(message, _current.Position);
                }

                return Take();
            }
        }

        private abstract class Node
        {
            protected Node(int position)
            {
                Position = position;
            }

            protected int Position { get; }
            public abstract ExpressionValue Evaluate(ExpressionContext context);
        }

        private sealed class LiteralNode : Node
        {
            public LiteralNode(ExpressionValue value, int position)
                : base(position)
            {
                Value = value;
            }

            public ExpressionValue Value { get; }

            public override ExpressionValue Evaluate(ExpressionContext context)
            {
                return Value;
            }
        }

        private sealed class VariableNode : Node
        {
            private readonly string _name;

            public VariableNode(string name, int position)
                : base(position)
            {
                _name = name;
            }

            public string Name => _name;

            public override ExpressionValue Evaluate(ExpressionContext context)
            {
                if (!context.TryGetVariable(_name, out var value))
                {
                    throw new ExpressionEvaluationException($"Unknown variable '{_name}'.", Position);
                }

                return value;
            }
        }

        private sealed class MemberNode : Node
        {
            private readonly Node _target;
            private readonly string _member;

            public MemberNode(Node target, string member, int position)
                : base(position)
            {
                _target = target;
                _member = member;
            }

            public override ExpressionValue Evaluate(ExpressionContext context)
            {
                var target = _target.Evaluate(context);
                if (target.Kind == ExpressionValueKind.Object)
                {
                    if (target.AsObject().TryGetValue(_member, out var value))
                    {
                        return value;
                    }

                    throw new ExpressionEvaluationException($"Object has no member '{_member}'.", Position);
                }

                if (target.Kind == ExpressionValueKind.Array)
                {
                    var values = target.AsArray();
                    if (string.Equals(_member, "last", StringComparison.OrdinalIgnoreCase))
                    {
                        return values.Count == 0 ? ExpressionValue.Null : values[values.Count - 1];
                    }

                    if (string.Equals(_member, "count", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(_member, "length", StringComparison.OrdinalIgnoreCase))
                    {
                        return ExpressionValue.Number(values.Count);
                    }
                }

                throw new ExpressionEvaluationException(
                    $"Values of kind {target.Kind} do not expose member '{_member}'.",
                    Position);
            }
        }

        private sealed class IndexNode : Node
        {
            private readonly Node _target;
            private readonly Node _index;

            public IndexNode(Node target, Node index, int position)
                : base(position)
            {
                _target = target;
                _index = index;
            }

            public override ExpressionValue Evaluate(ExpressionContext context)
            {
                var target = _target.Evaluate(context);
                var indexValue = _index.Evaluate(context);
                if (target.Kind == ExpressionValueKind.Object)
                {
                    if (indexValue.Kind != ExpressionValueKind.String)
                    {
                        throw new ExpressionEvaluationException(
                            "An object member index must be a string.",
                            Position);
                    }

                    var member = indexValue.AsString();
                    if (!target.AsObject().TryGetValue(member, out var memberValue))
                    {
                        throw new ExpressionEvaluationException(
                            $"Object has no member '{member}'.",
                            Position);
                    }

                    return memberValue;
                }

                if (target.Kind != ExpressionValueKind.Array)
                {
                    throw new ExpressionEvaluationException(
                        "Only arrays and objects can be indexed.",
                        Position);
                }

                if (indexValue.Kind != ExpressionValueKind.Number)
                {
                    throw new ExpressionEvaluationException("An array index must be a number.", Position);
                }

                var number = indexValue.AsNumber();
                if (number < 0 || number > int.MaxValue || number != Math.Truncate(number))
                {
                    throw new ExpressionEvaluationException("An array index must be a non-negative integer.", Position);
                }

                var index = (int)number;
                var values = target.AsArray();
                if (index >= values.Count)
                {
                    throw new ExpressionEvaluationException(
                        $"Array index {index} is outside the array of length {values.Count}.",
                        Position);
                }

                return values[index];
            }
        }

        private sealed class UnaryNode : Node
        {
            private readonly Token _operation;
            private readonly Node _operand;

            public UnaryNode(Token operation, Node operand)
                : base(operation.Position)
            {
                _operation = operation;
                _operand = operand;
            }

            public override ExpressionValue Evaluate(ExpressionContext context)
            {
                var value = _operand.Evaluate(context);
                switch (_operation.Kind)
                {
                    case TokenKind.Bang:
                        return ExpressionValue.Boolean(!RequireBoolean(value));
                    case TokenKind.Plus:
                        return ExpressionValue.Number(RequireNumber(value));
                    case TokenKind.Minus:
                        return FiniteNumber(-RequireNumber(value));
                    default:
                        throw new ExpressionEvaluationException("Unknown unary operation.", Position);
                }
            }

            private bool RequireBoolean(ExpressionValue value)
            {
                if (value.Kind != ExpressionValueKind.Boolean)
                {
                    throw new ExpressionEvaluationException("Logical negation requires a Boolean.", Position);
                }

                return value.AsBoolean();
            }

            private double RequireNumber(ExpressionValue value)
            {
                if (value.Kind != ExpressionValueKind.Number)
                {
                    throw new ExpressionEvaluationException("A unary numeric operation requires a number.", Position);
                }

                return value.AsNumber();
            }
        }

        private sealed class BinaryNode : Node
        {
            private readonly Node _left;
            private readonly Token _operation;
            private readonly Node _right;

            public BinaryNode(Node left, Token operation, Node right)
                : base(operation.Position)
            {
                _left = left;
                _operation = operation;
                _right = right;
            }

            public override ExpressionValue Evaluate(ExpressionContext context)
            {
                var left = _left.Evaluate(context);
                if (_operation.Kind == TokenKind.AndAnd)
                {
                    var leftBoolean = RequireBoolean(left, "Logical AND");
                    return leftBoolean
                        ? ExpressionValue.Boolean(RequireBoolean(_right.Evaluate(context), "Logical AND"))
                        : ExpressionValue.Boolean(false);
                }

                if (_operation.Kind == TokenKind.OrOr)
                {
                    var leftBoolean = RequireBoolean(left, "Logical OR");
                    return leftBoolean
                        ? ExpressionValue.Boolean(true)
                        : ExpressionValue.Boolean(RequireBoolean(_right.Evaluate(context), "Logical OR"));
                }

                var right = _right.Evaluate(context);
                switch (_operation.Kind)
                {
                    case TokenKind.Plus:
                        if (left.Kind == ExpressionValueKind.Number && right.Kind == ExpressionValueKind.Number)
                        {
                            return FiniteNumber(left.AsNumber() + right.AsNumber());
                        }

                        if (left.Kind == ExpressionValueKind.String && right.Kind == ExpressionValueKind.String)
                        {
                            return ExpressionValue.String(left.AsString() + right.AsString());
                        }

                        throw TypeError("Addition requires two numbers or two strings.");
                    case TokenKind.Minus:
                        return FiniteNumber(RequireNumber(left, "Subtraction") - RequireNumber(right, "Subtraction"));
                    case TokenKind.Star:
                        return FiniteNumber(RequireNumber(left, "Multiplication") * RequireNumber(right, "Multiplication"));
                    case TokenKind.Slash:
                    {
                        var divisor = RequireNumber(right, "Division");
                        if (divisor == 0d)
                        {
                            throw new ExpressionEvaluationException("Division by zero is not allowed.", Position);
                        }

                        return FiniteNumber(RequireNumber(left, "Division") / divisor);
                    }
                    case TokenKind.EqualEqual:
                        return ExpressionValue.Boolean(AreEqual(left, right));
                    case TokenKind.BangEqual:
                        return ExpressionValue.Boolean(!AreEqual(left, right));
                    case TokenKind.Less:
                        return ExpressionValue.Boolean(Compare(left, right) < 0);
                    case TokenKind.LessEqual:
                        return ExpressionValue.Boolean(Compare(left, right) <= 0);
                    case TokenKind.Greater:
                        return ExpressionValue.Boolean(Compare(left, right) > 0);
                    case TokenKind.GreaterEqual:
                        return ExpressionValue.Boolean(Compare(left, right) >= 0);
                    default:
                        throw new ExpressionEvaluationException("Unknown binary operation.", Position);
                }
            }

            private bool RequireBoolean(ExpressionValue value, string operation)
            {
                if (value.Kind != ExpressionValueKind.Boolean)
                {
                    throw new ExpressionEvaluationException($"{operation} requires Boolean operands.", Position);
                }

                return value.AsBoolean();
            }

            private double RequireNumber(ExpressionValue value, string operation)
            {
                if (value.Kind != ExpressionValueKind.Number)
                {
                    throw new ExpressionEvaluationException($"{operation} requires numeric operands.", Position);
                }

                return value.AsNumber();
            }

            private int Compare(ExpressionValue left, ExpressionValue right)
            {
                if (left.Kind == ExpressionValueKind.Number && right.Kind == ExpressionValueKind.Number)
                {
                    return left.AsNumber().CompareTo(right.AsNumber());
                }

                if (left.Kind == ExpressionValueKind.String && right.Kind == ExpressionValueKind.String)
                {
                    return string.Compare(left.AsString(), right.AsString(), StringComparison.Ordinal);
                }

                throw TypeError("Ordering comparisons require two numbers or two strings.");
            }

            private ExpressionEvaluationException TypeError(string message)
            {
                return new ExpressionEvaluationException(message, Position);
            }

            private static bool AreEqual(ExpressionValue left, ExpressionValue right)
            {
                if (left.Kind != right.Kind)
                {
                    return false;
                }

                switch (left.Kind)
                {
                    case ExpressionValueKind.Null:
                        return true;
                    case ExpressionValueKind.Number:
                        return left.AsNumber().Equals(right.AsNumber());
                    case ExpressionValueKind.String:
                        return string.Equals(left.AsString(), right.AsString(), StringComparison.Ordinal);
                    case ExpressionValueKind.Boolean:
                        return left.AsBoolean() == right.AsBoolean();
                    default:
                        return ReferenceEquals(left, right);
                }
            }
        }

        private static ExpressionValue FiniteNumber(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                throw new ExpressionEvaluationException("The numeric result is not finite.", 0);
            }

            return ExpressionValue.Number(value);
        }
    }
}
