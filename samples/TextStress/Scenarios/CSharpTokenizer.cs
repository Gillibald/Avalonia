using System;
using System.Collections.Generic;

namespace TextStress.Scenarios
{
    internal enum TokenKind
    {
        Plain,
        Keyword,
        Type,
        Identifier,
        String,
        Number,
        Comment,
        DocComment,
        Preprocessor,
        Punctuation
    }

    internal readonly record struct Token(int Start, int Length, TokenKind Kind);

    /// <summary>
    /// A line-based C# colourizer, good enough to give source the run structure of an
    /// editor's syntax highlighting: keywords, types, literals, comments. Block comments and
    /// verbatim strings carry their state from line to line.
    /// </summary>
    internal sealed class CSharpTokenizer
    {
        private static readonly HashSet<string> s_keywords = new(StringComparer.Ordinal)
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class",
            "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event",
            "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if",
            "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new",
            "null", "object", "operator", "out", "override", "params", "private", "protected", "public",
            "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static",
            "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong",
            "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while", "var",
            "get", "set", "init", "value", "async", "await", "partial", "record", "when", "where", "yield",
            "nameof", "not", "and", "or", "with"
        };

        private enum State
        {
            Code,
            BlockComment,
            VerbatimString
        }

        private State _state;

        public List<Token> TokenizeLine(string line)
        {
            var tokens = new List<Token>();
            var i = 0;

            void Add(int start, int end, TokenKind kind)
            {
                if (end > start)
                {
                    tokens.Add(new Token(start, end - start, kind));
                }
            }

            if (_state == State.BlockComment)
            {
                var end = line.IndexOf("*/", StringComparison.Ordinal);

                if (end < 0)
                {
                    Add(0, line.Length, TokenKind.Comment);
                    return tokens;
                }

                Add(0, end + 2, TokenKind.Comment);
                i = end + 2;
                _state = State.Code;
            }
            else if (_state == State.VerbatimString)
            {
                var end = FindVerbatimEnd(line, 0);

                if (end < 0)
                {
                    Add(0, line.Length, TokenKind.String);
                    return tokens;
                }

                Add(0, end, TokenKind.String);
                i = end;
                _state = State.Code;
            }

            var firstNonSpace = 0;

            while (firstNonSpace < line.Length && char.IsWhiteSpace(line[firstNonSpace]))
            {
                firstNonSpace++;
            }

            if (i == 0 && firstNonSpace < line.Length && line[firstNonSpace] == '#')
            {
                Add(firstNonSpace, line.Length, TokenKind.Preprocessor);
                return tokens;
            }

            while (i < line.Length)
            {
                var c = line[i];

                if (char.IsWhiteSpace(c))
                {
                    i++;
                    continue;
                }

                var start = i;

                if (c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                {
                    var kind = i + 2 < line.Length && line[i + 2] == '/' ? TokenKind.DocComment : TokenKind.Comment;
                    Add(i, line.Length, kind);
                    break;
                }

                if (c == '/' && i + 1 < line.Length && line[i + 1] == '*')
                {
                    var end = line.IndexOf("*/", i + 2, StringComparison.Ordinal);

                    if (end < 0)
                    {
                        Add(i, line.Length, TokenKind.Comment);
                        _state = State.BlockComment;
                        break;
                    }

                    Add(i, end + 2, TokenKind.Comment);
                    i = end + 2;
                    continue;
                }

                if (c == '@' && i + 1 < line.Length && line[i + 1] == '"' ||
                    c == '$' && i + 2 < line.Length && line[i + 1] == '@' && line[i + 2] == '"')
                {
                    var open = line.IndexOf('"', i);
                    var end = FindVerbatimEnd(line, open + 1);

                    if (end < 0)
                    {
                        Add(i, line.Length, TokenKind.String);
                        _state = State.VerbatimString;
                        break;
                    }

                    Add(i, end, TokenKind.String);
                    i = end;
                    continue;
                }

                if (c == '"' || c == '\'' || c == '$' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    var quote = c == '$' ? '"' : c;
                    var j = c == '$' ? i + 2 : i + 1;

                    while (j < line.Length && line[j] != quote)
                    {
                        j += line[j] == '\\' ? 2 : 1;
                    }

                    i = Math.Min(j + 1, line.Length);
                    Add(start, i, TokenKind.String);
                    continue;
                }

                if (char.IsDigit(c))
                {
                    while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '.' || line[i] == '_'))
                    {
                        i++;
                    }

                    Add(start, i, TokenKind.Number);
                    continue;
                }

                if (char.IsLetter(c) || c == '_' || c == '@')
                {
                    i++;

                    while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_'))
                    {
                        i++;
                    }

                    var word = line.Substring(start, i - start);
                    var kind = s_keywords.Contains(word)
                        ? TokenKind.Keyword
                        : char.IsUpper(word[0]) ? TokenKind.Type : TokenKind.Identifier;
                    Add(start, i, kind);
                    continue;
                }

                while (i < line.Length && !char.IsLetterOrDigit(line[i]) && !char.IsWhiteSpace(line[i]) &&
                       line[i] is not ('"' or '\'' or '_' or '@' or '$') &&
                       !(line[i] == '/' && i + 1 < line.Length && line[i + 1] is '/' or '*'))
                {
                    i++;
                }

                if (i == start)
                {
                    i++;
                }

                Add(start, i, TokenKind.Punctuation);
            }

            return tokens;
        }

        private static int FindVerbatimEnd(string line, int from)
        {
            var j = from;

            while (j < line.Length)
            {
                if (line[j] == '"')
                {
                    if (j + 1 < line.Length && line[j + 1] == '"')
                    {
                        j += 2;
                        continue;
                    }

                    return j + 1;
                }

                j++;
            }

            return -1;
        }
    }
}
