using System.Globalization;

namespace ObjectRT.Reader;

// ── Token kinds ──────────────────────────────────────────────────────────

public enum TokenKind
{
    Eof,
    Identifier,
    Integer,
    Float,
    String,
    Keyword,
    Dot,
    Comma,
    Colon,
    Semicolon,
    Equals,      // =
    Arrow,       // ->
    OpenParen,
    CloseParen,
    OpenBrace,
    CloseBrace,
    OpenBracket,
    CloseBracket,
    DotMetadata, // .metadata
    Annotation,  // @attr
}

public record Token(TokenKind Kind, string Text, int Line, int Col);

// ── Tokenizer ────────────────────────────────────────────────────────────

public class ObjectILTokenizer
{
    private readonly string _input;
    private int _pos;
    private int _line = 1;
    private int _col = 1;
    private Token? _lookahead;
    private bool _hasLookahead;

    private static readonly HashSet<string> Keywords = new()
    {
        "module", "version", "class", "interface", "struct", "enum",
        "field", "method", "constructor", "local", "static", "virtual",
        "override", "abstract", "private", "public", "protected",
        "internal", "if", "else", "while", "break", "continue",
        "try", "catch", "finally", "throw", "for", "return",
        "implements", "in", "with", "stack", "true", "false", "null",
        "metadata", "spec", "require", "optional",
    };

    public ObjectILTokenizer(string input)
    {
        _input = input;
    }

    private char Peek() => _pos < _input.Length ? _input[_pos] : '\0';
    private char Peek2() => _pos + 1 < _input.Length ? _input[_pos + 1] : '\0';
    private char Advance()
    {
        char c = _input[_pos++];
        if (c == '\n') { _line++; _col = 1; }
        else _col++;
        return c;
    }

    public Token PeekToken()
    {
        if (!_hasLookahead)
        {
            _lookahead = ReadNext();
            _hasLookahead = true;
        }
        return _lookahead!;
    }

    public Token AdvanceToken()
    {
        if (_hasLookahead)
        {
            _hasLookahead = false;
            return _lookahead!;
        }
        return ReadNext();
    }

    public bool Eof => _pos >= _input.Length && !_hasLookahead;

    private static bool IsIdentStart(char c) => char.IsLetter(c) || c == '_' || c == '`';
    private static bool IsIdentCont(char c) => char.IsLetterOrDigit(c) || c == '_' || c == '`' || c == '.' || c == '<' || c == '>';

    private void SkipWhitespaceAndComments()
    {
        while (_pos < _input.Length)
        {
            char c = Peek();
            if (c == ' ' || c == '\t') { Advance(); continue; }
            if (c == '\n') { Advance(); continue; }
            if (c == '\r') { Advance(); continue; }
            // Line comment
            if (c == '/' && _pos + 1 < _input.Length && _input[_pos + 1] == '/')
            {
                Advance(); Advance(); // skip //
                while (_pos < _input.Length)
                {
                    if (Peek() == '\n') break;
                    Advance();
                }
                continue;
            }
            break;
        }
    }

    private Token ReadNext()
    {
        SkipWhitespaceAndComments();

        if (_pos >= _input.Length)
            return new Token(TokenKind.Eof, "", _line, _col);

        int tokLine = _line, tokCol = _col;
        char c = Peek();

        // Detect .metadata
        if (c == '.' && _pos + 1 < _input.Length && IsIdentStart(_input[_pos + 1]))
        {
            var text = Advance().ToString();
            while (_pos < _input.Length && IsIdentCont(Peek()))
                text += Advance();
            return text == ".metadata"
                ? new Token(TokenKind.DotMetadata, text, tokLine, tokCol)
                : new Token(TokenKind.Identifier, text, tokLine, tokCol);
        }

        // Identifier or keyword
        if (IsIdentStart(c))
        {
            var text = "";
            while (_pos < _input.Length && IsIdentCont(Peek()))
                text += Advance();
            var kind = Keywords.Contains(text) ? TokenKind.Keyword : TokenKind.Identifier;
            return new Token(kind, text, tokLine, tokCol);
        }

        // String literal
        if (c == '"')
        {
            Advance(); // skip opening "
            var text = "";
            while (_pos < _input.Length)
            {
                char ch = Advance();
                if (ch == '"') break;
                if (ch == '\\')
                {
                    char esc = Advance();
                    text += esc switch
                    {
                        'n' => '\n',
                        'r' => '\r',
                        't' => '\t',
                        '"' => '"',
                        '\\' => '\\',
                        _ => esc,
                    };
                }
                else
                {
                    text += ch;
                }
            }
            return new Token(TokenKind.String, text, tokLine, tokCol);
        }

        // Number
        if (char.IsDigit(c) || (c == '-' && _pos + 1 < _input.Length && char.IsDigit(_input[_pos + 1])))
        {
            var text = "";
            bool isFloat = false;

            if (c == '-') text += Advance();

            while (_pos < _input.Length && char.IsDigit(Peek()))
                text += Advance();

            if (_pos < _input.Length && Peek() == '.')
            {
                isFloat = true;
                text += Advance();
                while (_pos < _input.Length && char.IsDigit(Peek()))
                    text += Advance();
            }

            return new Token(isFloat ? TokenKind.Float : TokenKind.Integer, text, tokLine, tokCol);
        }

        // -> arrow
        if (c == '-' && _pos + 1 < _input.Length && _input[_pos + 1] == '>')
        {
            Advance(); Advance();
            return new Token(TokenKind.Arrow, "->", tokLine, tokCol);
        }

        // @Annotation — only consumed when followed by an identifier start.
        if (c == '@' && _pos + 1 < _input.Length && IsIdentStart(_input[_pos + 1]))
        {
            Advance(); // consume the @
            var sb = new System.Text.StringBuilder("@");
            while (_pos < _input.Length && IsIdentCont(Peek()))
                sb.Append(Advance());
            return new Token(TokenKind.Annotation, sb.ToString(), tokLine, tokCol);
        }

        // Single-char tokens
        char single = Advance();
        return single switch
        {
            '.' => new Token(TokenKind.Dot, ".", tokLine, tokCol),
            ',' => new Token(TokenKind.Comma, ",", tokLine, tokCol),
            ':' => new Token(TokenKind.Colon, ":", tokLine, tokCol),
            ';' => new Token(TokenKind.Semicolon, ";", tokLine, tokCol),
            '=' => new Token(TokenKind.Equals, "=", tokLine, tokCol),
            '(' => new Token(TokenKind.OpenParen, "(", tokLine, tokCol),
            ')' => new Token(TokenKind.CloseParen, ")", tokLine, tokCol),
            '{' => new Token(TokenKind.OpenBrace, "{", tokLine, tokCol),
            '}' => new Token(TokenKind.CloseBrace, "}", tokLine, tokCol),
            '[' => new Token(TokenKind.OpenBracket, "[", tokLine, tokCol),
            ']' => new Token(TokenKind.CloseBracket, "]", tokLine, tokCol),
            '@' => new Token(TokenKind.Annotation, "@", tokLine, tokCol),
            _ => throw new FormatException($"Unexpected character '{single}' at {tokLine}:{tokCol}"),
        };
    }
}
