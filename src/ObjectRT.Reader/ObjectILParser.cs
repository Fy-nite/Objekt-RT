using System.Globalization;
using ObjectRT.Abstractions;

namespace ObjectRT.Reader;

/// <summary>Parses ObjectIL text format into an ORBTModule.</summary>
public class ObjectILParser
{
    private readonly ObjectILTokenizer _tokenizer;

    public ObjectILParser(string input)
    {
        _tokenizer = new ObjectILTokenizer(input);
    }

    public ORBTModule ParseModule()
    {
        var mod = new ORBTModule();
        ParseInto(mod);
        return mod;
    }

    public void ParseInto(ORBTModule mod)
    {
        ParseModuleDecl(mod);

        if (_tokenizer.PeekToken().Kind == TokenKind.DotMetadata)
            ParseMetadataBlock(mod);

        while (_tokenizer.PeekToken().Kind != TokenKind.Eof)
            ParseTypeDecl(mod);
    }

    // ── Parsing helpers ─────────────────────────────────────────────

    private Token Expect(TokenKind kind)
    {
        var t = _tokenizer.AdvanceToken();
        if (t.Kind != kind)
            throw new FormatException($"Expected {kind} but got '{t.Text}' at {t.Line}:{t.Col}");
        return t;
    }

    private Token ExpectIdentifier()
    {
        var t = _tokenizer.AdvanceToken();
        if (t.Kind != TokenKind.Identifier && t.Kind != TokenKind.Keyword)
            throw new FormatException($"Expected identifier but got '{t.Text}' at {t.Line}:{t.Col}");
        return t;
    }

    private bool Match(TokenKind kind)
    {
        if (_tokenizer.PeekToken().Kind == kind)
        {
            _tokenizer.AdvanceToken();
            return true;
        }
        return false;
    }

    private Token? TryMatch(TokenKind kind)
    {
        if (_tokenizer.PeekToken().Kind == kind)
            return _tokenizer.AdvanceToken();
        return null;
    }

    // ── Attribute parsing ──────────────────────────────────────────

    /// <summary>
    /// Parse one or more <c>@Attribute</c> annotations before a type or member
    /// declaration. Returns the collected attribute records.
    /// </summary>
    private List<AttributeRecord> ParseAttributes(ORBTModule mod)
    {
        var attrs = new List<AttributeRecord>();
        while (_tokenizer.PeekToken().Kind == TokenKind.Annotation)
        {
            var tok = _tokenizer.AdvanceToken();
            // Strip leading '@' from the token text
            var name = tok.Text[1..];
            var nameIdx = mod.StringPool.Add(name);
            var args = new List<ushort>();

            if (_tokenizer.PeekToken().Kind == TokenKind.OpenParen)
            {
                _tokenizer.AdvanceToken();
                while (_tokenizer.PeekToken().Kind != TokenKind.CloseParen
                       && _tokenizer.PeekToken().Kind != TokenKind.Eof)
                {
                    var arg = _tokenizer.AdvanceToken();
                    // String or integer literal; store its text in the string pool.
                    args.Add(mod.StringPool.Add(arg.Text));
                    TryMatch(TokenKind.Comma);
                }
                Expect(TokenKind.CloseParen);
            }

            attrs.Add(new AttributeRecord(nameIdx, args));
        }
        return attrs;
    }

    // ── Grammar rules ───────────────────────────────────────────────

    private void ParseModuleDecl(ORBTModule mod)
    {
        Expect(TokenKind.Keyword); // "module"
        var name = ExpectIdentifier();
        mod.ModuleName = name.Text;

        Expect(TokenKind.Keyword); // "version"

        var a = _tokenizer.AdvanceToken();
        ushort major, minor, patch = 0;

        if (a.Kind == TokenKind.Float)
        {
            var parts = a.Text.Split('.');
            major = ushort.Parse(parts[0]);
            minor = parts.Length > 1 ? ushort.Parse(parts[1]) : (ushort)0;
            if (_tokenizer.PeekToken().Kind == TokenKind.Dot)
            {
                _tokenizer.AdvanceToken();
                var c = _tokenizer.AdvanceToken();
                if (c.Kind == TokenKind.Integer)
                    patch = ushort.Parse(c.Text);
                else throw new FormatException("Expected patch version number after '.'");
            }
        }
        else if (a.Kind == TokenKind.Integer)
        {
            major = ushort.Parse(a.Text);
            Expect(TokenKind.Dot);
            var b = _tokenizer.AdvanceToken();
            if (b.Kind == TokenKind.Float)
            {
                var parts = b.Text.Split('.');
                minor = ushort.Parse(parts[0]);
                patch = parts.Length > 1 ? ushort.Parse(parts[1]) : (ushort)0;
            }
            else if (b.Kind == TokenKind.Integer)
            {
                minor = ushort.Parse(b.Text);
                Expect(TokenKind.Dot);
                patch = ushort.Parse(Expect(TokenKind.Integer).Text);
            }
            else throw new FormatException("Expected version number");
        }
        else throw new FormatException("Expected version number");

        mod.Version = new ModuleVersion(major, minor, patch);
        mod.FormatVersion = 0x01;
    }

    private void ParseMetadataBlock(ORBTModule mod)
    {
        Expect(TokenKind.DotMetadata);
        Expect(TokenKind.OpenBrace);

        while (_tokenizer.PeekToken().Kind != TokenKind.CloseBrace && _tokenizer.PeekToken().Kind != TokenKind.Eof)
        {
            var key = ExpectIdentifier();
            var keyStr = key.Text;

            if (keyStr == "spec")
            {
                ExpectIdentifier(); // "objectrt"
                TryMatch(TokenKind.Equals);
                var ver = Expect(TokenKind.String);
                mod.Metadata.SpecVersion = ver.Text;
                mod.Metadata.Entries.Add(new MetadataEntry("spec", ver.Text));
            }
            else if (keyStr is "require" or "optional")
            {
                Expect(TokenKind.OpenBracket);
                var features = new List<string>();
                while (_tokenizer.PeekToken().Kind != TokenKind.CloseBracket && _tokenizer.PeekToken().Kind != TokenKind.Eof)
                {
                    features.Add(ExpectIdentifier().Text);
                    TryMatch(TokenKind.Comma);
                }
                Expect(TokenKind.CloseBracket);

                if (keyStr == "require") mod.Metadata.Require = features;
                else mod.Metadata.Optional = features;
                mod.Metadata.Entries.Add(new MetadataEntry(keyStr, features));
            }
        }

        Expect(TokenKind.CloseBrace);
    }

    private void ParseTypeDecl(ORBTModule mod)
    {
        var type = new TypeRecord
        {
            Flags = TypeFlags.None,
            Access = MemberAccess.Public,
            BaseTypeIndex = -1,
            NamespaceIndex = 0,
        };

        // @Attributes before the type declaration.
        type.Attributes = ParseAttributes(mod);

        while (_tokenizer.PeekToken().Kind == TokenKind.Keyword)
        {
            var kw = _tokenizer.PeekToken().Text;
            if (kw == "abstract") { _tokenizer.AdvanceToken(); type.Flags |= TypeFlags.Abstract; }
            else if (kw == "sealed") { _tokenizer.AdvanceToken(); type.Flags |= TypeFlags.Sealed; }
            else break;
        }

        var kindTok = Expect(TokenKind.Keyword);
        type.Kind = kindTok.Text switch
        {
            "class" => TypeKind.Class,
            "interface" => TypeKind.Interface,
            "struct" => TypeKind.Struct,
            "enum" => TypeKind.Enum,
            _ => throw new FormatException($"Expected type kind at {kindTok.Line}"),
        };

        var nameTok = ExpectIdentifier();
        type.NameIndex = mod.StringPool.Add(nameTok.Text);

        // Base type / interface list: class Foo : Base, IFace { ... }
        if (_tokenizer.PeekToken().Kind == TokenKind.Colon)
        {
            _tokenizer.AdvanceToken();
            var first = true;
            while (true)
            {
                var baseName = ExpectIdentifier().Text;
                if (first)
                {
                    // The first entry is the base type when it resolves to a
                    // type declared in this module; otherwise it is treated as
                    // an interface reference (external bases cannot be indexed).
                    type.BaseTypeIndex = FindTypeIndex(mod, baseName);
                    first = false;
                }
                else
                {
                    type.InterfaceIndices.Add(mod.StringPool.Add(baseName));
                    type.InterfaceCount++;
                }
                if (TryMatch(TokenKind.Comma) == null) break;
            }
        }

        if (_tokenizer.PeekToken().Text == "implements")
        {
            _tokenizer.AdvanceToken();
            while (true)
            {
                type.InterfaceIndices.Add(mod.StringPool.Add(ExpectIdentifier().Text));
                type.InterfaceCount++;
                if (TryMatch(TokenKind.Comma) == null) break;
            }
        }

        Expect(TokenKind.OpenBrace);

        while (_tokenizer.PeekToken().Kind != TokenKind.CloseBrace && _tokenizer.PeekToken().Kind != TokenKind.Eof)
            ParseMember(mod, type);

        Expect(TokenKind.CloseBrace);
        mod.Types.Add(type);
    }

    private int FindTypeIndex(ORBTModule mod, string name)
    {
        for (int i = 0; i < mod.Types.Count; i++)
        {
            if (mod.Resolve(mod.Types[i].NameIndex) == name)
                return i;
        }
        return -1;
    }

    private void ParseMember(ORBTModule mod, TypeRecord type)
    {
        var access = MemberAccess.Public;
        var mflags = MethodFlags.None;

        // @Attributes before the member declaration.
        var attributes = ParseAttributes(mod);

        while (_tokenizer.PeekToken().Kind == TokenKind.Keyword)
        {
            switch (_tokenizer.PeekToken().Text)
            {
                case "public":    access = MemberAccess.Public; _tokenizer.AdvanceToken(); break;
                case "private":   access = MemberAccess.Private; _tokenizer.AdvanceToken(); break;
                case "protected": access = MemberAccess.Protected; _tokenizer.AdvanceToken(); break;
                case "internal":  access = MemberAccess.Internal; _tokenizer.AdvanceToken(); break;
                case "static":    mflags |= MethodFlags.Static; _tokenizer.AdvanceToken(); break;
                case "virtual":   mflags |= MethodFlags.Virtual; _tokenizer.AdvanceToken(); break;
                case "override":  mflags |= MethodFlags.Override; _tokenizer.AdvanceToken(); break;
                case "abstract":  mflags |= MethodFlags.Abstract; _tokenizer.AdvanceToken(); break;
                default: goto done;
            }
        }
        done:

        var next = _tokenizer.PeekToken();
        if (next.Text == "field")
        {
            type.Access = access;
            ParseField(mod, type);
        }
        else if (next.Text == "method")
        {
            type.Access = access;
            ParseMethod(mod, type);
            if (type.Methods.Count > 0)
            {
                type.Methods[^1].Flags |= mflags;
                type.Methods[^1].Access = access;
                type.Methods[^1].Attributes.AddRange(attributes);
            }
        }
        else if (next.Text == "constructor")
        {
            _tokenizer.AdvanceToken();
            type.Access = access;

            var method = new MethodRecord
            {
                Access = access,
                Flags = MethodFlags.None,
                NameIndex = mod.StringPool.Add(".ctor"),
            };

            Expect(TokenKind.OpenParen);
            while (_tokenizer.PeekToken().Kind != TokenKind.CloseParen)
            {
                var pname = ExpectIdentifier();
                Expect(TokenKind.Colon);
                var ptype = ReadTypeName();
                method.Params.Add(new ParameterRecord(
                    mod.StringPool.Add(pname.Text),
                    mod.StringPool.Add(ptype)));
                TryMatch(TokenKind.Comma);
            }
            Expect(TokenKind.CloseParen);

            method.SignatureIndex = method.NameIndex;
            method.ParamCount = (ushort)method.Params.Count;
            method.Attributes.AddRange(attributes);
            ParseMethodBody(mod, method);
            type.Methods.Add(method);
            type.MethodCount++;
        }
        else throw new FormatException($"Expected member declaration at {next.Line}, got '{next.Text}'");
    }

    private void ParseField(ORBTModule mod, TypeRecord type)
    {
        _tokenizer.AdvanceToken();
        var name = ExpectIdentifier();
        Expect(TokenKind.Colon);
        var typeName = ReadTypeName();
        type.Fields.Add(new FieldRecord(mod.StringPool.Add(name.Text), mod.StringPool.Add(typeName)));
        type.FieldCount++;
    }

    /// <summary>
    /// Reads a type name: an identifier optionally followed by [] array suffixes
    /// (e.g. "int", "int[]", "string[][]"). Function types are not supported yet.
    /// </summary>
    private string ReadTypeName()
    {
        var name = ExpectIdentifier().Text;
        while (_tokenizer.PeekToken().Kind == TokenKind.OpenBracket)
        {
            _tokenizer.AdvanceToken();
            Expect(TokenKind.CloseBracket);
            name += "[]";
        }
        return name;
    }

    /// <summary>
    /// Records the current `#line` source info for the upcoming bytecode,
    /// collapsing consecutive instructions that share the same source location.
    /// </summary>
    private void RecordSourceLine(MethodRecord method, List<byte> code)
    {
        int line = _tokenizer.SourceLine;
        int col = _tokenizer.SourceColumn;
        if (method.LineMappings.Count > 0)
        {
            var last = method.LineMappings[^1];
            if (last.Line == line && last.Column == col)
                return;
        }
        method.LineMappings.Add(new SourceMapEntry((uint)code.Count, line, col, _tokenizer.SourceText));
    }

    private void ParseMethod(ORBTModule mod, TypeRecord type)
    {
        _tokenizer.AdvanceToken();

        var method = new MethodRecord { Access = MemberAccess.Public, Flags = MethodFlags.None };
        method.NameIndex = mod.StringPool.Add(ExpectIdentifier().Text);

        Expect(TokenKind.OpenParen);
        while (_tokenizer.PeekToken().Kind != TokenKind.CloseParen)
        {
            var pname = ExpectIdentifier();
            Expect(TokenKind.Colon);
            var ptype = ReadTypeName();
            method.Params.Add(new ParameterRecord(
                mod.StringPool.Add(pname.Text),
                mod.StringPool.Add(ptype)));
            TryMatch(TokenKind.Comma);
        }
        Expect(TokenKind.CloseParen);
        method.ParamCount = (ushort)method.Params.Count;

        Expect(TokenKind.Arrow);
        method.SignatureIndex = mod.StringPool.Add(ExpectIdentifier().Text);

        ParseMethodBody(mod, method);
        type.Methods.Add(method);
        type.MethodCount++;
    }

    // ── Method body ─────────────────────────────────────────────────

    private struct PendingBranch(int bytePos, int labelId)
    {
        public int BytePos = bytePos;
        public int LabelId = labelId;
    }

    private struct LabelSlot
    {
        public int LabelId;
        public bool Defined;
        public int BytePos;
    }

    private readonly List<PendingBranch> _fixups = new();
    private readonly Dictionary<int, LabelSlot> _labels = new();
    private int _nextLabelId;
    private readonly Stack<int> _breakTargets = new();
    private readonly Stack<int> _continueTargets = new();

    private void ParseMethodBody(ORBTModule mod, MethodRecord method)
    {
        Expect(TokenKind.OpenBrace);

        _fixups.Clear();
        _labels.Clear();
        _nextLabelId = 0;
        _breakTargets.Clear();
        _continueTargets.Clear();

        var code = new List<byte>();

        // Local variables
        while (_tokenizer.PeekToken().Text == "local")
        {
            _tokenizer.AdvanceToken();
            var lname = ExpectIdentifier();
            Expect(TokenKind.Colon);
            var ltype = ReadTypeName();
            method.Locals.Add(new LocalRecord(mod.StringPool.Add(lname.Text), mod.StringPool.Add(ltype)));
            method.LocalCount++;
        }

        while (_tokenizer.PeekToken().Kind != TokenKind.CloseBrace && _tokenizer.PeekToken().Kind != TokenKind.Eof)
            ParseStatement(mod, method, code);

        ResolveFixups(code);
        method.RawInstructionData = code.ToArray();

        Expect(TokenKind.CloseBrace);
    }

    private void ParseStatement(ORBTModule mod, MethodRecord method, List<byte> code)
    {
        if (_tokenizer.PeekToken().Kind is TokenKind.Eof or TokenKind.CloseBrace) return;

        // Record the source mapping before emitting this statement's bytes.
        // PeekToken() above has already skipped any pending `// #line` comment,
        // so the tokenizer's SourceLine/SourceColumn/SourceText are current.
        RecordSourceLine(method, code);

        var next = _tokenizer.PeekToken();

        if (next.Text == "if") { _tokenizer.AdvanceToken(); ParseIf(mod, method, code); return; }
        if (next.Text == "while") { _tokenizer.AdvanceToken(); ParseWhile(mod, method, code); return; }

        if (next.Text == "break")
        {
            _tokenizer.AdvanceToken();
            if (_breakTargets.Count == 0) throw new FormatException($"break outside loop at {next.Line}:{next.Col}");
            int endLabel = _breakTargets.Peek();
            code.Add(0x32); // br
            _fixups.Add(new PendingBranch(code.Count, endLabel));
            EmitI32(code, 0);
            method.InstrCount++;
            return;
        }

        if (next.Text == "continue")
        {
            _tokenizer.AdvanceToken();
            if (_continueTargets.Count == 0) throw new FormatException($"continue outside loop at {next.Line}:{next.Col}");
            int loopLabel = _continueTargets.Peek();
            code.Add(0x32); // br
            if (_labels.TryGetValue(loopLabel, out var slot) && slot.Defined)
            {
                int brEnd = code.Count + 4;
                EmitI32(code, slot.BytePos - brEnd);
            }
            else
            {
                _fixups.Add(new PendingBranch(code.Count, loopLabel));
                EmitI32(code, 0);
            }
            method.InstrCount++;
            return;
        }

        ParseSimpleInstruction(mod, method, code);
    }

    private void ParseIf(ORBTModule mod, MethodRecord method, List<byte> code)
    {
        Expect(TokenKind.OpenParen);
        var cond = ExpectIdentifier();
        if (cond.Text != "stack") throw new FormatException($"Expected 'stack' condition in if at {cond.Line}");
        Expect(TokenKind.CloseParen);

        int elseLabel = FreshLabel();
        int endLabel = FreshLabel();

        code.Add(0x34); // brfalse
        _fixups.Add(new PendingBranch(code.Count, elseLabel));
        EmitI32(code, 0);
        method.InstrCount++;

        Expect(TokenKind.OpenBrace);
        while (_tokenizer.PeekToken().Kind != TokenKind.CloseBrace && _tokenizer.PeekToken().Kind != TokenKind.Eof)
            ParseStatement(mod, method, code);
        Expect(TokenKind.CloseBrace);

        if (_tokenizer.PeekToken().Text == "else")
        {
            _tokenizer.AdvanceToken();
            code.Add(0x32); // br
            _fixups.Add(new PendingBranch(code.Count, endLabel));
            EmitI32(code, 0);
            method.InstrCount++;
            PlaceLabel(elseLabel, code);
            Expect(TokenKind.OpenBrace);
            while (_tokenizer.PeekToken().Kind != TokenKind.CloseBrace && _tokenizer.PeekToken().Kind != TokenKind.Eof)
                ParseStatement(mod, method, code);
            Expect(TokenKind.CloseBrace);
        }
        else PlaceLabel(elseLabel, code);

        PlaceLabel(endLabel, code);
    }

    private void ParseWhile(ORBTModule mod, MethodRecord method, List<byte> code)
    {
        Expect(TokenKind.OpenParen);
        var cond = ExpectIdentifier();
        if (cond.Text != "stack") throw new FormatException($"Expected 'stack' condition in while at {cond.Line}");
        Expect(TokenKind.CloseParen);

        int loopLabel = FreshLabel();
        int endLabel = FreshLabel();

        _breakTargets.Push(endLabel);
        _continueTargets.Push(loopLabel);
        PlaceLabel(loopLabel, code);

        // dup the stack value so brfalse doesn't consume the counter
        code.Add(0x22); // dup
        method.InstrCount++;

        code.Add(0x34); // brfalse
        _fixups.Add(new PendingBranch(code.Count, endLabel));
        EmitI32(code, 0);
        method.InstrCount++;

        Expect(TokenKind.OpenBrace);
        while (_tokenizer.PeekToken().Kind != TokenKind.CloseBrace && _tokenizer.PeekToken().Kind != TokenKind.Eof)
            ParseStatement(mod, method, code);
        Expect(TokenKind.CloseBrace);

        code.Add(0x32); // br
        int brEnd = code.Count + 4;
        EmitI32(code, _labels[loopLabel].BytePos - brEnd);
        method.InstrCount++;

        _breakTargets.Pop();
        _continueTargets.Pop();
        PlaceLabel(endLabel, code);
    }

    private void ParseSimpleInstruction(ORBTModule mod, MethodRecord method, List<byte> code)
    {
        var mnemonic = ExpectIdentifier();
        int mnLine = mnemonic.Line;

        int opcode = OpcodeExtensions.FromMnemonic(mnemonic.Text);
        if (opcode < 0)
        {
            while (_tokenizer.PeekToken().Kind != TokenKind.Eof
                   && _tokenizer.PeekToken().Kind != TokenKind.CloseBrace
                   && _tokenizer.PeekToken().Line == mnLine)
                _tokenizer.AdvanceToken();
            return;
        }

        code.Add((byte)opcode);

        // call / callvirt / callnative — operand is a method name, optionally
        // followed by a parenthesized parameter type list. We encode the
        // method name as a string-pool index plus the parameter count (U16 +
        // U16) so the interpreter can resolve the target at runtime: module
        // function first, then the host native dispatch.
        if (opcode is 0x16 or 0x17 or 0x35)
        {
            ushort nameIdx = 0;
            ushort paramCount = 0;

            if (_tokenizer.PeekToken().Kind != TokenKind.Eof
                && _tokenizer.PeekToken().Kind != TokenKind.CloseBrace
                && _tokenizer.PeekToken().Kind != TokenKind.OpenBrace
                && _tokenizer.PeekToken().Line == mnLine)
            {
                nameIdx = mod.StringPool.Add(_tokenizer.AdvanceToken().Text);

                if (_tokenizer.PeekToken().Kind == TokenKind.OpenParen)
                {
                    _tokenizer.AdvanceToken();
                    while (_tokenizer.PeekToken().Kind != TokenKind.CloseParen
                           && _tokenizer.PeekToken().Kind != TokenKind.Eof)
                    {
                        var t = _tokenizer.PeekToken();
                        if (t.Kind is TokenKind.Identifier or TokenKind.Keyword)
                            paramCount++;
                        _tokenizer.AdvanceToken();
                    }
                    if (_tokenizer.PeekToken().Kind == TokenKind.CloseParen)
                        _tokenizer.AdvanceToken();
                }
            }

            EmitU16(code, nameIdx);
            EmitU16(code, paramCount);

            // Skip the rest of the line (e.g. "-> retType").
            while (_tokenizer.PeekToken().Kind != TokenKind.Eof
                   && _tokenizer.PeekToken().Kind != TokenKind.CloseBrace
                   && _tokenizer.PeekToken().Line == mnLine)
                _tokenizer.AdvanceToken();

            method.InstrCount++;
            return;
        }

        bool operandRead = false;

        if (_tokenizer.PeekToken().Kind != TokenKind.Eof
            && _tokenizer.PeekToken().Kind != TokenKind.CloseBrace
            && _tokenizer.PeekToken().Kind != TokenKind.OpenBrace
            && _tokenizer.PeekToken().Line == mnLine)
        {
            var operand = _tokenizer.AdvanceToken();
            // Field references are qualified as "Type::field" in the text IR —
            // join the three tokens so the operand is the full field reference.
            // ModuleCompiler keys fields as "Type.field" (dot), so normalize.
            if (opcode is 0x0F or 0x2A or 0x10 or 0x11 // ldfld, stfld, ldsfld, stsfld
                && _tokenizer.PeekToken().Kind == TokenKind.DoubleColon
                && _tokenizer.PeekToken().Line == mnLine)
            {
                _tokenizer.AdvanceToken(); // ::
                var fieldTok = _tokenizer.AdvanceToken();
                operand = new Token(TokenKind.Identifier, operand.Text + "." + fieldTok.Text, operand.Line, operand.Col);
            }
            operandRead = EncodeOperand(mod, code, opcode, operand);
            if (!operandRead)
            {
                while (_tokenizer.PeekToken().Kind != TokenKind.Eof && _tokenizer.PeekToken().Line == mnLine)
                    _tokenizer.AdvanceToken();
            }
            else if (opcode == 0x12)
            {
                // newobj: consume the trailing ".constructor(...)" suffix so it
                // doesn't leak into the next statement.
                while (_tokenizer.PeekToken().Kind != TokenKind.Eof
                       && _tokenizer.PeekToken().Kind != TokenKind.CloseBrace
                       && _tokenizer.PeekToken().Line == mnLine)
                    _tokenizer.AdvanceToken();
            }
        }

        method.InstrCount++;
    }

    // ── Operand encoding ────────────────────────────────────────────

    private static bool EncodeOperand(ORBTModule mod, List<byte> code, int opcode, Token operand)
    {
        switch (opcode)
        {
            case 0x01: // ldc (alias)
            case 0x2B: // ldc.i4
                EmitI32(code, int.Parse(operand.Text));
                return true;

            case 0x2C: // ldc.i8
            {
                long val = long.Parse(operand.Text);
                for (int i = 0; i < 8; i++)
                    code.Add((byte)((val >> (i * 8)) & 0xFF));
                return true;
            }
            case 0x2D: // ldc.r4
            {
                float val = float.Parse(operand.Text, CultureInfo.InvariantCulture);
                var bits = BitConverter.SingleToInt32Bits(val);
                EmitI32(code, bits);
                return true;
            }
            case 0x2E: // ldc.r8
            {
                double val = double.Parse(operand.Text, CultureInfo.InvariantCulture);
                var bits = BitConverter.DoubleToInt64Bits(val);
                for (int i = 0; i < 8; i++)
                    code.Add((byte)((bits >> (i * 8)) & 0xFF));
                return true;
            }
            // uint16 index operands
            case 0x03: case 0x04: // ldarg, starg
            case 0x05: case 0x06: // ldloc, stloc
            case 0x02:            // ldstr
            case 0x1F: case 0x20: case 0x21: // conv, castclass, isinst
            case 0x0F: case 0x2A: // ldfld, stfld
            case 0x10: case 0x11: // ldsfld, stsfld
            {
                ushort idx;
                if (operand.Kind == TokenKind.Integer)
                    idx = ushort.Parse(operand.Text);
                else
                    idx = mod.StringPool.Add(operand.Text);
                EmitU16(code, idx);
                return true;
            }
            case 0x12: case 0x13: // newobj, newarr
            {
                // Dotted identifiers include the ctor suffix: "Delegate.constructor".
                // Strip it so the type resolves ("Delegate").
                string name = operand.Text;
                if (opcode == 0x12)
                {
                    int ci = name.IndexOf(".constructor", StringComparison.Ordinal);
                    if (ci >= 0) name = name[..ci];
                }
                EmitU16(code, mod.StringPool.Add(name));
                return true;
            }
            case 0x32: case 0x33: case 0x34: // br, brtrue, brfalse
            {
                int off = int.Parse(operand.Text);
                EmitI32(code, off);
                return true;
            }
            default:
                return false;
        }
    }

    // ── Bytecode emission helpers ───────────────────────────────────

    private int FreshLabel() => _nextLabelId++;

    private void PlaceLabel(int id, List<byte> code)
    {
        _labels[id] = new LabelSlot { LabelId = id, Defined = true, BytePos = code.Count };
    }

    private void ResolveFixups(List<byte> code)
    {
        foreach (var fx in _fixups)
        {
            if (!_labels.TryGetValue(fx.LabelId, out var slot) || !slot.Defined)
                throw new FormatException($"Unresolved label {fx.LabelId}");
            int brEnd = fx.BytePos + 4;
            int offset = slot.BytePos - brEnd;
            EmitI32At(code, fx.BytePos, offset);
        }
        _fixups.Clear();
    }

    private static void EmitI32At(List<byte> code, int pos, int val)
    {
        code[pos + 0] = (byte)(val & 0xFF);
        code[pos + 1] = (byte)((val >> 8) & 0xFF);
        code[pos + 2] = (byte)((val >> 16) & 0xFF);
        code[pos + 3] = (byte)((val >> 24) & 0xFF);
    }

    private static void EmitI32(List<byte> code, int val)
    {
        code.Add((byte)(val & 0xFF));
        code.Add((byte)((val >> 8) & 0xFF));
        code.Add((byte)((val >> 16) & 0xFF));
        code.Add((byte)((val >> 24) & 0xFF));
    }

    private static void EmitU16(List<byte> code, ushort val)
    {
        code.Add((byte)(val & 0xFF));
        code.Add((byte)((val >> 8) & 0xFF));
    }
}

// ── Convenience ─────────────────────────────────────────────────────────

public static class OilFileReader
{
    public static ORBTModule ParseFile(string path)
    {
        var content = File.ReadAllText(path);
        var parser = new ObjectILParser(content);
        return parser.ParseModule();
    }

    public static ORBTModule ParseString(string content)
    {
        var parser = new ObjectILParser(content);
        return parser.ParseModule();
    }
}
