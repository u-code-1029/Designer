using System;
using System.Collections.Generic;
using System.Text;
using DrillFlow.Core.Expressions;

namespace DrillFlow.Infrastructure.Persistence;

/// <summary>
/// Renames legacy equipment members only on direct references to known equipment Actions.
/// The source spelling and unrelated JSON members/string literals remain untouched.
/// </summary>
internal static class Version1ExpressionMigration
{
    public static string Rewrite(string source, ISet<string> stageKeys, ISet<string> equipmentKeys)
    {
        var tokens = Tokenize(source);
        var replacements = new SortedDictionary<int, Replacement>();
        for (var index = 0; index < tokens.Count; index++)
        {
            var root = tokens[index];
            if (root.Kind != TokenKind.Identifier || index > 0 && tokens[index - 1].Kind == TokenKind.Dot)
            {
                continue;
            }

            var key = source.Substring(root.Start, root.Length);
            if (!equipmentKeys.Contains(key))
            {
                continue;
            }

            var wrappers = 0;
            for (var previous = index - 1; previous >= 0 && tokens[previous].Kind == TokenKind.LeftParenthesis; previous--)
            {
                wrappers++;
            }

            var cursor = index + 1;
            SkipGroupingClosers(tokens, ref cursor, ref wrappers);
            if (!TryReadMember(source, tokens, ref cursor, out var container))
            {
                continue;
            }

            SkipGroupingClosers(tokens, ref cursor, ref wrappers);
            if (EqualsMember(container.Name, "results"))
            {
                var selector = cursor;
                if (TryReadMember(source, tokens, ref cursor, out var latest)
                    && EqualsMember(latest.Name, "last"))
                {
                    SkipGroupingClosers(tokens, ref cursor, ref wrappers);
                }
                else
                {
                    // Result indices are expressions, not just digit literals. Matching brackets
                    // also preserves indices containing nested access and quoted JSON keys.
                    cursor = selector;
                    if (cursor >= tokens.Count || tokens[cursor].Kind != TokenKind.LeftBracket
                        || tokens[cursor].ClosingIndex < 0)
                    {
                        continue;
                    }

                    cursor = tokens[cursor].ClosingIndex + 1;
                    SkipGroupingClosers(tokens, ref cursor, ref wrappers);
                }
            }
            else if (!EqualsMember(container.Name, "parameters")
                     && !EqualsMember(container.Name, "result")
                     && !EqualsMember(container.Name, "last"))
            {
                continue;
            }

            if (!TryReadMember(source, tokens, ref cursor, out var member))
            {
                continue;
            }

            var isStage = stageKeys.Contains(key);
            string? replacement = null;
            if (EqualsMember(container.Name, "parameters"))
            {
                if (isStage && EqualsMember(member.Name, "move_x")) replacement = "stage_x";
                if (isStage && EqualsMember(member.Name, "move_y")) replacement = "stage_y";
            }
            else
            {
                if (isStage && EqualsMember(member.Name, "stage_x")) replacement = "current_stage_x";
                if (isStage && EqualsMember(member.Name, "stage_y")) replacement = "current_stage_y";
                if (EqualsMember(member.Name, "index")) replacement = "correlation_id";
                if (EqualsMember(member.Name, "command")) replacement = "type";
            }

            if (replacement != null)
            {
                replacements[member.Start] = new Replacement(member.Length, replacement);
            }
        }

        var rewritten = new StringBuilder(source.Length);
        var offset = 0;
        foreach (var replacement in replacements)
        {
            rewritten.Append(source, offset, replacement.Key - offset);
            rewritten.Append(replacement.Value.Text);
            offset = replacement.Key + replacement.Value.Length;
        }

        rewritten.Append(source, offset, source.Length - offset);
        return rewritten.ToString();
    }

    private static bool TryReadMember(
        string source,
        IReadOnlyList<Token> tokens,
        ref int cursor,
        out Member member)
    {
        member = default;
        if (cursor + 1 >= tokens.Count)
        {
            return false;
        }

        var value = tokens[cursor + 1];
        if (tokens[cursor].Kind == TokenKind.Dot && value.Kind == TokenKind.Identifier)
        {
            member = new Member(source.Substring(value.Start, value.Length), value.Start, value.Length);
            cursor += 2;
            return true;
        }

        if (tokens[cursor].Kind != TokenKind.LeftBracket || tokens[cursor].ClosingIndex < 0)
        {
            return false;
        }

        var closing = tokens[cursor].ClosingIndex;
        var memberStart = cursor + 1;
        var memberEnd = closing;
        while (memberStart < memberEnd
               && tokens[memberStart].Kind == TokenKind.LeftParenthesis
               && tokens[memberEnd - 1].Kind == TokenKind.RightParenthesis)
        {
            memberStart++;
            memberEnd--;
        }

        if (memberEnd != memberStart + 1 || tokens[memberStart].Kind != TokenKind.String)
        {
            return false;
        }

        value = tokens[memberStart];
        try
        {
            var name = new ExpressionEngine().EvaluateLiteral(
                source.Substring(value.Start, value.Length)).AsString();
            member = new Member(name, value.Start + 1, value.Length - 2);
            cursor = closing + 1;
            return true;
        }
        catch (ExpressionException)
        {
            // Loading an editable workflow must not turn an unfinished expression into a
            // migration failure. Leave invalid quoted members for ordinary workflow validation.
            return false;
        }
    }

    private static void SkipGroupingClosers(IReadOnlyList<Token> tokens, ref int cursor, ref int wrappers)
    {
        while (wrappers > 0 && cursor < tokens.Count && tokens[cursor].Kind == TokenKind.RightParenthesis)
        {
            cursor++;
            wrappers--;
        }
    }

    private static bool EqualsMember(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<Token> Tokenize(string source)
    {
        var tokens = new List<Token>();
        var brackets = new Stack<int>();
        for (var cursor = 0; cursor < source.Length;)
        {
            if (char.IsWhiteSpace(source[cursor]))
            {
                cursor++;
                continue;
            }

            var start = cursor;
            var kind = source[cursor++];
            var tokenKind = kind switch
            {
                '.' => TokenKind.Dot,
                '[' => TokenKind.LeftBracket,
                ']' => TokenKind.RightBracket,
                '(' => TokenKind.LeftParenthesis,
                ')' => TokenKind.RightParenthesis,
                _ => TokenKind.Other
            };
            if (kind == '\'' || kind == '"')
            {
                while (cursor < source.Length)
                {
                    var character = source[cursor++];
                    if (character == '\\' && cursor < source.Length) cursor++;
                    else if (character == kind) break;
                }

                tokenKind = TokenKind.String;
            }
            else if (kind == '_' || char.IsLetter(kind))
            {
                while (cursor < source.Length
                       && (source[cursor] == '_' || char.IsLetterOrDigit(source[cursor]))) cursor++;
                tokenKind = TokenKind.Identifier;
            }
            else if (char.IsDigit(kind) || kind == '.' && cursor < source.Length && char.IsDigit(source[cursor]))
            {
                while (cursor < source.Length && (char.IsDigit(source[cursor]) || source[cursor] == '.')) cursor++;
                if (cursor < source.Length && (source[cursor] == 'e' || source[cursor] == 'E'))
                {
                    cursor++;
                    if (cursor < source.Length && (source[cursor] == '+' || source[cursor] == '-')) cursor++;
                    while (cursor < source.Length && char.IsDigit(source[cursor])) cursor++;
                }

                tokenKind = TokenKind.Number;
            }

            var token = new Token(tokenKind, start, cursor - start);
            if (tokenKind == TokenKind.LeftBracket) brackets.Push(tokens.Count);
            else if (tokenKind == TokenKind.RightBracket && brackets.Count > 0) tokens[brackets.Pop()].ClosingIndex = tokens.Count;
            tokens.Add(token);
        }

        return tokens;
    }

    private enum TokenKind
    {
        Identifier,
        String,
        Number,
        Dot,
        LeftBracket,
        RightBracket,
        LeftParenthesis,
        RightParenthesis,
        Other
    }

    private sealed class Token
    {
        public Token(TokenKind kind, int start, int length) { Kind = kind; Start = start; Length = length; }
        public TokenKind Kind { get; }
        public int Start { get; }
        public int Length { get; }
        public int ClosingIndex { get; set; } = -1;
    }

    private readonly struct Member
    {
        public Member(string name, int start, int length) { Name = name; Start = start; Length = length; }
        public string Name { get; }
        public int Start { get; }
        public int Length { get; }
    }

    private readonly struct Replacement
    {
        public Replacement(int length, string text) { Length = length; Text = text; }
        public int Length { get; }
        public string Text { get; }
    }
}
