#include "ObjectILParser.hpp"
#include "Module.hpp"
#include <fstream>
#include <sstream>
#include <cctype>
#include <stdexcept>
#include <iostream>
#include <tuple>
#include <cstring>

namespace objectrt {

// ============================================================================
// Tokenizer implementation
// ============================================================================

Tokenizer::Tokenizer(std::istream& input) : input_(input) {}

static bool is_ident_start(char c) {
    return std::isalpha(c) || c == '_' || c == '`';
}

static bool is_ident_cont(char c) {
    return std::isalnum(c) || c == '_' || c == '.' || c == '`' || c == '<' || c == '>';
}

// Keywords
static bool is_keyword(const std::string& s) {
    static const char* keywords[] = {
        "module", "version", "class", "interface", "struct", "enum",
        "field", "method", "constructor", "local", "static", "virtual",
        "override", "abstract", "private", "public", "protected",
        "internal", "if", "else", "while", "break", "continue",
        "try", "catch", "finally", "throw", "for", "return",
        "implements", "in", "with", "stack", "true", "false", "null",
        "metadata", "spec", "require", "optional",
    };
    for (const char* kw : keywords) {
        if (s == kw) return true;
    }
    return false;
}

void Tokenizer::skip_whitespace_and_comments() {
    while (input_.good()) {
        char c = static_cast<char>(input_.peek());
        if (c == ' ' || c == '\t') {
            input_.get(); col_++;
            continue;
        }
        if (c == '\n') {
            input_.get(); line_++; col_ = 1;
            continue;
        }
        if (c == '\r') {
            input_.get(); col_++;
            continue;
        }
        // Line comment
        if (c == '/' && input_.peek() && input_.peek() == '/') {
            input_.get(); input_.get(); col_ += 2;
            while (input_.good()) {
                c = static_cast<char>(input_.get());
                col_++;
                if (c == '\n') { line_++; col_ = 1; break; }
                if (c == '\r') continue;
            }
            continue;
        }
        break;
    }
}

Token Tokenizer::peek() {
    if (!has_lookahead_) {
        lookahead_ = read_next();
        has_lookahead_ = true;
    }
    return lookahead_;
}

Token Tokenizer::advance() {
    if (has_lookahead_) {
        has_lookahead_ = false;
        return lookahead_;
    }
    return read_next();
}

bool Tokenizer::eof() const {
    return input_.eof() && !has_lookahead_;
}

Token Tokenizer::read_next() {
    skip_whitespace_and_comments();

    if (input_.eof()) {
        return {TokenKind::Eof, "", line_, col_};
    }

    char c = static_cast<char>(input_.peek());
    size_t tok_line = line_;
    size_t tok_col = col_;

    // Detect .metadata  — consume the dot first, then check the next char
    if (c == '.') {
        input_.get(); col_++;
        if (input_.good() && is_ident_start(static_cast<char>(input_.peek()))) {
            std::string text = ".";
            while (input_.good() && is_ident_cont(static_cast<char>(input_.peek()))) {
                text += static_cast<char>(input_.get()); col_++;
            }
            if (text == ".metadata") {
                return {TokenKind::DotMetadata, text, tok_line, tok_col};
            }
            // Otherwise treat as identifier
            return {TokenKind::Identifier, text, tok_line, tok_col};
        }
        // Not followed by an identifier — it's a plain Dot
        return {TokenKind::Dot, ".", tok_line, tok_col};
    }

    // Identifier or keyword
    if (is_ident_start(c)) {
        std::string text;
        while (input_.good() && is_ident_cont(static_cast<char>(input_.peek()))) {
            text += static_cast<char>(input_.get()); col_++;
        }
        TokenKind kind = is_keyword(text) ? TokenKind::Keyword : TokenKind::Identifier;
        return {kind, text, tok_line, tok_col};
    }

    // String literal
    if (c == '"') {
        input_.get(); col_++;
        std::string text;
        while (input_.good()) {
            char ch = static_cast<char>(input_.get()); col_++;
            if (ch == '"') break;
            if (ch == '\\') {
                char esc = static_cast<char>(input_.get()); col_++;
                switch (esc) {
                    case 'n': text += '\n'; break;
                    case 'r': text += '\r'; break;
                    case 't': text += '\t'; break;
                    case '"': text += '"'; break;
                    case '\\': text += '\\'; break;
                    default: text += esc; break;
                }
            } else {
                text += ch;
            }
        }
        return {TokenKind::String, text, tok_line, tok_col};
    }

    // Number
    if (std::isdigit(c) || (c == '-' && input_.peek() && std::isdigit(static_cast<char>(input_.peek())))) {
        std::string text;
        bool is_float = false;
        if (c == '-') {
            text += static_cast<char>(input_.get()); col_++;
        }
        while (input_.good() && std::isdigit(static_cast<char>(input_.peek()))) {
            text += static_cast<char>(input_.get()); col_++;
        }
        if (input_.good() && input_.peek() == '.') {
            is_float = true;
            text += static_cast<char>(input_.get()); col_++;
            while (input_.good() && std::isdigit(static_cast<char>(input_.peek()))) {
                text += static_cast<char>(input_.get()); col_++;
            }
        }
        return {is_float ? TokenKind::Float : TokenKind::Integer, text, tok_line, tok_col};
    }

    // Single-char tokens
    char single = static_cast<char>(input_.get()); col_++;

    // Handle -> as a two-character sequence
    if (single == '-' && input_.good() && static_cast<char>(input_.peek()) == '>') {
        input_.get(); col_++;
        return {TokenKind::Arrow, "->", tok_line, tok_col};
    }

    // If '-' then check if it's followed by a digit → negative number
    if (single == '-' && input_.good() && std::isdigit(static_cast<char>(input_.peek()))) {
        std::string text = "-";
        while (input_.good() && std::isdigit(static_cast<char>(input_.peek()))) {
            text += static_cast<char>(input_.get()); col_++;
        }
        bool is_float = false;
        if (input_.good() && input_.peek() == '.') {
            is_float = true;
            text += static_cast<char>(input_.get()); col_++;
            while (input_.good() && std::isdigit(static_cast<char>(input_.peek()))) {
                text += static_cast<char>(input_.get()); col_++;
            }
        }
        return {is_float ? TokenKind::Float : TokenKind::Integer, text, tok_line, tok_col};
    }

    switch (single) {
        case '.': return {TokenKind::Dot, ".", tok_line, tok_col};
        case ',': return {TokenKind::Comma, ",", tok_line, tok_col};
        case ':': return {TokenKind::Colon, ":", tok_line, tok_col};
        case ';': return {TokenKind::Semicolon, ";", tok_line, tok_col};
        case '(': return {TokenKind::OpenParen, "(", tok_line, tok_col};
        case ')': return {TokenKind::CloseParen, ")", tok_line, tok_col};
        case '{': return {TokenKind::OpenBrace, "{", tok_line, tok_col};
        case '}': return {TokenKind::CloseBrace, "}", tok_line, tok_col};
        case '[': return {TokenKind::OpenBracket, "[", tok_line, tok_col};
        case ']': return {TokenKind::CloseBracket, "]", tok_line, tok_col};
        default:
            throw std::runtime_error("Unexpected character '" + std::string(1, single) + "' at line " +
                std::to_string(tok_line) + ":" + std::to_string(tok_col));
    }
}

// ============================================================================
// ObjectILParser implementation
// ============================================================================

ObjectILParser::ObjectILParser(std::istream& input) : tokenizer_(input) {}

Token ObjectILParser::expect(TokenKind kind) {
    Token t = tokenizer_.advance();
    if (t.kind != kind) {
        throw std::runtime_error("Expected " + std::to_string(static_cast<int>(kind)) +
            " but got '" + t.text + "' at line " + std::to_string(t.line) +
            ":" + std::to_string(t.col));
    }
    return t;
}

Token ObjectILParser::expect_identifier() {
    Token t = tokenizer_.advance();
    if (t.kind != TokenKind::Identifier && t.kind != TokenKind::Keyword) {
        throw std::runtime_error("Expected identifier but got '" + t.text +
            "' at line " + std::to_string(t.line) + ":" + std::to_string(t.col));
    }
    return t;
}

bool ObjectILParser::match(TokenKind kind) {
    if (tokenizer_.peek().kind == kind) {
        tokenizer_.advance();
        return true;
    }
    return false;
}

std::optional<Token> ObjectILParser::try_match(TokenKind kind) {
    if (tokenizer_.peek().kind == kind) {
        return tokenizer_.advance();
    }
    return std::nullopt;
}

ORBTModule ObjectILParser::parse_module() {
    ORBTModule mod;
    parse_into(mod);
    return mod;
}

void ObjectILParser::parse_into(ORBTModule& mod) {
    parse_module_decl(mod);

    // Optional .metadata block
    if (tokenizer_.peek().kind == TokenKind::DotMetadata) {
        parse_metadata_block(mod);
    }

    // Type declarations
    while (tokenizer_.peek().kind != TokenKind::Eof) {
        parse_type_decl(mod);
    }
}

void ObjectILParser::parse_module_decl(ORBTModule& mod) {
    expect(TokenKind::Keyword); // "module"
    Token name = expect_identifier();
    mod.module_name = name.text;

    expect(TokenKind::Keyword); // "version"

    // Version numbers may be tokenized as Integer ("1") or Float ("1.0").
    // Handle both flexibly.  The spec says version is major.minor.patch,
    // but `1.0` (two-component) is common — default patch to 0.
    auto read_version_triple = [&]() -> std::tuple<uint16_t, uint16_t, uint16_t> {
        auto read_uint16 = [](const Token& t) -> uint16_t {
            return static_cast<uint16_t>(std::stoul(t.text));
        };

        Token a = tokenizer_.advance();
        if (a.kind == TokenKind::Float) {
            // "major.minor" — may be followed by .patch if the float
            // consumed only the first two components (e.g. "1.0.0" is
            // tokenized as Float("1.0") + Dot + Integer("0")).
            size_t dot = a.text.find('.');
            uint16_t major = static_cast<uint16_t>(std::stoul(a.text.substr(0, dot)));
            uint16_t minor = static_cast<uint16_t>(std::stoul(a.text.substr(dot + 1)));
            uint16_t patch = 0;
            if (tokenizer_.peek().kind == TokenKind::Dot) {
                tokenizer_.advance(); // consume '.'
                Token c = tokenizer_.advance();
                if (c.kind != TokenKind::Integer)
                    throw std::runtime_error("Expected patch version number after '.'");
                patch = read_uint16(c);
            }
            return {major, minor, patch};
        }
        if (a.kind != TokenKind::Integer)
            throw std::runtime_error("Expected version number");
        uint16_t major = read_uint16(a);
        expect(TokenKind::Dot);

        Token b = tokenizer_.advance();
        if (b.kind == TokenKind::Float) {
            // "minor.patch"
            size_t dot = b.text.find('.');
            uint16_t minor = static_cast<uint16_t>(std::stoul(b.text.substr(0, dot)));
            uint16_t patch = static_cast<uint16_t>(std::stoul(b.text.substr(dot + 1)));
            return {major, minor, patch};
        }
        if (b.kind != TokenKind::Integer)
            throw std::runtime_error("Expected version number");
        uint16_t minor = read_uint16(b);
        expect(TokenKind::Dot);
        uint16_t patch = read_uint16(expect(TokenKind::Integer));
        return {major, minor, patch};
    };

    auto [maj, min, pat] = read_version_triple();
    mod.version.major = maj;
    mod.version.minor = min;
    mod.version.patch = pat;
    mod.format_version = 0x01;
}

void ObjectILParser::parse_metadata_block(ORBTModule& mod) {
    expect(TokenKind::DotMetadata);
    expect(TokenKind::OpenBrace);

    while (tokenizer_.peek().kind != TokenKind::CloseBrace
           && tokenizer_.peek().kind != TokenKind::Eof) {
        Token key = expect_identifier();
        std::string key_str = key.text;

        if (key_str == "spec") {
            Token spec_key = expect_identifier(); // "objectrt"
            expect(TokenKind::String); // "=" isn't a keyword, so expect string directly
            // Actually the grammar says: spec objectrt = "<version>"
            // Let me handle the "=" sign too
            // Hmm, the spec shows: spec objectrt = "1.0"
            // But this might not have the = token literally
            // Let me check what tokens we see...

            // Actually looking at the spec, it shows:
            // spec objectrt = "1.0"
            // So there's an equals sign (which isn't a defined token...)
            // Let me just handle it flexibly
            if (tokenizer_.peek().text == "=") {
                tokenizer_.advance(); // skip =
            }
            Token ver = expect(TokenKind::String);
            mod.metadata.spec_version = ver.text;

            MetadataEntry entry;
            entry.key = "spec";
            entry.value = ver.text;
            mod.metadata.entries.push_back(std::move(entry));
        }
        else if (key_str == "require" || key_str == "optional") {
            expect(TokenKind::OpenBracket);
            std::vector<std::string> features;
            while (tokenizer_.peek().kind != TokenKind::CloseBracket
                   && tokenizer_.peek().kind != TokenKind::Eof) {
                Token feature = expect_identifier();
                features.push_back(feature.text);
                try_match(TokenKind::Comma);
            }
            expect(TokenKind::CloseBracket);

            if (key_str == "require") {
                mod.metadata.require = features;
            } else {
                mod.metadata.optional = features;
            }

            MetadataEntry entry;
            entry.key = key_str;
            entry.value = features;
            mod.metadata.entries.push_back(std::move(entry));
        }
    }

    expect(TokenKind::CloseBrace);
}

void ObjectILParser::parse_type_decl(ORBTModule& mod) {
    TypeRecord type;
    type.flags = TypeFlags::None;
    type.access = MemberAccess::Public; // default
    type.base_type_index = -1;
    type.namespace_index = 0; // empty namespace by default

    // Optional abstract/sealed modifiers
    while (tokenizer_.peek().kind == TokenKind::Keyword) {
        const std::string& kw = tokenizer_.peek().text;
        if (kw == "abstract") {
            tokenizer_.advance();
            type.flags = type.flags | TypeFlags::Abstract;
        } else if (kw == "sealed") {
            tokenizer_.advance();
            type.flags = type.flags | TypeFlags::Sealed;
        } else {
            break;
        }
    }

    // Type kind (class, interface, struct, enum)
    Token kind_tok = expect(TokenKind::Keyword);
    if (kind_tok.text == "class") {
        type.kind = TypeKind::Class;
    } else if (kind_tok.text == "interface") {
        type.kind = TypeKind::Interface;
    } else if (kind_tok.text == "struct") {
        type.kind = TypeKind::Struct;
    } else if (kind_tok.text == "enum") {
        type.kind = TypeKind::Enum;
    } else {
        throw std::runtime_error("Expected type kind (class/interface/struct/enum) at line " +
            std::to_string(kind_tok.line));
    }

    // Type name
    Token name_tok = expect_identifier();
    type.name_index = static_cast<uint16_t>(mod.string_pool.size());
    mod.string_pool.strings.push_back(name_tok.text);

    // Optional implements clause
    if (tokenizer_.peek().text == "implements") {
        tokenizer_.advance(); // skip "implements"
        type.interface_indices = std::vector<uint16_t>(); // empty for now
        // Parse comma-separated type list
        while (true) {
            Token iface = expect_identifier();
            // We don't resolve yet - just store name
            uint16_t idx = static_cast<uint16_t>(mod.string_pool.size());
            mod.string_pool.strings.push_back(iface.text);
            type.interface_indices.push_back(idx);
            type.interface_count++;
            if (!try_match(TokenKind::Comma)) break;
        }
    }

    // Open brace
    expect(TokenKind::OpenBrace);

    // Members
    while (tokenizer_.peek().kind != TokenKind::CloseBrace
           && tokenizer_.peek().kind != TokenKind::Eof) {
        parse_member(mod, type);
    }

    expect(TokenKind::CloseBrace);

    mod.types.push_back(std::move(type));
}

void ObjectILParser::parse_member(ORBTModule& mod, TypeRecord& type) {
    // Peek at the next few tokens to determine if it's a field or method
    // Fields: [access] [static] field <name>: <type>
    // Methods: [access] [static] [virtual] [override] [abstract] method <name>(...) -> <type> { ... }
    // Constructors: constructor(...) { ... }

    // Collect modifiers
    MemberAccess access = MemberAccess::Public;
    MethodFlags mflags = MethodFlags::None;
    bool is_static = false;
    bool is_virtual = false;
    bool is_override = false;
    bool is_abstract = false;

    while (tokenizer_.peek().kind == TokenKind::Keyword) {
        const std::string& kw = tokenizer_.peek().text;
        if (kw == "public")    { access = MemberAccess::Public; tokenizer_.advance(); }
        else if (kw == "private")   { access = MemberAccess::Private; tokenizer_.advance(); }
        else if (kw == "protected") { access = MemberAccess::Protected; tokenizer_.advance(); }
        else if (kw == "internal")  { access = MemberAccess::Internal; tokenizer_.advance(); }
        else if (kw == "static")    { is_static = true; tokenizer_.advance(); }
        else if (kw == "virtual")   { is_virtual = true; tokenizer_.advance(); }
        else if (kw == "override")  { is_override = true; tokenizer_.advance(); }
        else if (kw == "abstract")  { is_abstract = true; tokenizer_.advance(); }
        else break;
    }

    if (is_static)   mflags = mflags | MethodFlags::Static;
    if (is_virtual)  mflags = mflags | MethodFlags::Virtual;
    if (is_override) mflags = mflags | MethodFlags::Override;
    if (is_abstract) mflags = mflags | MethodFlags::Abstract;

    // Check what kind of member
    Token next = tokenizer_.peek();
    if (next.text == "field") {
        // Apply access to field
        type.access = access;
        parse_field(mod, type);
    } else if (next.text == "method") {
        type.access = access;
        TypeRecord temp_type;
        temp_type.access = access;
        parse_method(mod, type);
        // Apply method flags
        if (!type.methods.empty()) {
            type.methods.back().flags = type.methods.back().flags | mflags;
            type.methods.back().access = access;
        }
    } else if (next.text == "constructor") {
        tokenizer_.advance(); // skip "constructor"
        type.access = access;

        MethodRecord method;
        method.access = access;
        method.flags = MethodFlags::None;
        method.name_index = static_cast<uint16_t>(mod.string_pool.size());
        mod.string_pool.strings.push_back(".ctor");
        method.signature_index = method.name_index;

        // Parameter list
        expect(TokenKind::OpenParen);
        while (tokenizer_.peek().kind != TokenKind::CloseParen) {
            ParameterRecord param;
            Token pname = expect_identifier();
            expect(TokenKind::Colon);
            Token ptype = expect_identifier();
            param.name_index = static_cast<uint16_t>(mod.string_pool.size());
            mod.string_pool.strings.push_back(pname.text);
            param.type_index = static_cast<uint16_t>(mod.string_pool.size());
            mod.string_pool.strings.push_back(ptype.text);
            method.params.push_back(param);
            try_match(TokenKind::Comma);
        }
        expect(TokenKind::CloseParen);
        method.param_count = static_cast<uint16_t>(method.params.size());

        // Body
        parse_method_body(mod, method);

        type.methods.push_back(std::move(method));
        type.method_count++;
    } else {
        throw std::runtime_error("Expected member declaration (field/method/constructor) at line " +
            std::to_string(next.line) + ", got '" + next.text + "'");
    }
}

void ObjectILParser::parse_field(ORBTModule& mod, TypeRecord& type) {
    // Consume "field"
    tokenizer_.advance(); // skip "field"

    Token name = expect_identifier();
    expect(TokenKind::Colon);
    Token type_name = expect_identifier();

    FieldRecord field;
    field.name_index = static_cast<uint16_t>(mod.string_pool.size());
    mod.string_pool.strings.push_back(name.text);
    field.type_index = static_cast<uint16_t>(mod.string_pool.size());
    mod.string_pool.strings.push_back(type_name.text);

    type.fields.push_back(field);
    type.field_count++;
}

void ObjectILParser::parse_method(ORBTModule& mod, TypeRecord& type) {
    // Consume "method"
    tokenizer_.advance(); // skip "method"

    MethodRecord method;
    method.access = MemberAccess::Public;
    method.flags = MethodFlags::None;
    method.signature_index = 0;

    // Method name
    Token name = expect_identifier();
    method.name_index = static_cast<uint16_t>(mod.string_pool.size());
    mod.string_pool.strings.push_back(name.text);

    // Parameter list
    expect(TokenKind::OpenParen);
    while (tokenizer_.peek().kind != TokenKind::CloseParen) {
        ParameterRecord param;
        Token pname = expect_identifier();
        expect(TokenKind::Colon);
        Token ptype = expect_identifier();
        param.name_index = static_cast<uint16_t>(mod.string_pool.size());
        mod.string_pool.strings.push_back(pname.text);
        param.type_index = static_cast<uint16_t>(mod.string_pool.size());
        mod.string_pool.strings.push_back(ptype.text);
        method.params.push_back(param);
        try_match(TokenKind::Comma);
    }
    expect(TokenKind::CloseParen);
    method.param_count = static_cast<uint16_t>(method.params.size());

    // Arrow and return type
    expect(TokenKind::Arrow);
    Token ret_type = expect_identifier();
    // Store return type name in string pool (for signature)
    uint16_t ret_idx = static_cast<uint16_t>(mod.string_pool.size());
    mod.string_pool.strings.push_back(ret_type.text);
    method.signature_index = ret_idx;

    // Body
    parse_method_body(mod, method);

    type.methods.push_back(std::move(method));
    type.method_count++;
}

// Map from ObjectIL mnemonic to opcode value (table 0)
static int mnemonic_to_opcode(const std::string& mnemonic) {
    struct OpMap { const char* name; uint8_t op; };
    static const OpMap map[] = {
        {"nop", 0x00}, {"ldc", 0x01}, {"ldstr", 0x02},
        {"ldarg", 0x03}, {"starg", 0x04}, {"ldloc", 0x05}, {"stloc", 0x06},
        {"add", 0x07}, {"sub", 0x08}, {"mul", 0x09}, {"div", 0x0A}, {"rem", 0x0B}, {"neg", 0x0C},
        {"ceq", 0x0D}, {"cne", 0x0E},
        {"ldfld", 0x0F}, {"ldsfld", 0x10}, {"stsfld", 0x11},
        {"newobj", 0x12}, {"newarr", 0x13}, {"ldelem", 0x14}, {"stelem", 0x15},
        {"call", 0x16}, {"callvirt", 0x17}, {"ret", 0x18},
        {"if", 0x19}, {"while", 0x1A},
        {"break", 0x1B}, {"continue", 0x1C},
        {"try", 0x1D}, {"throw", 0x1E},
        {"conv", 0x1F}, {"castclass", 0x20}, {"isinst", 0x21},
        {"dup", 0x22}, {"pop", 0x23}, {"ldnull", 0x24},
        {"not", 0x25}, {"cgt", 0x26}, {"cge", 0x27}, {"clt", 0x28}, {"cle", 0x29},
        {"stfld", 0x2A},
        {"ldc.i4", 0x2B}, {"ldc.i8", 0x2C}, {"ldc.r4", 0x2D}, {"ldc.r8", 0x2E},
        {"and", 0x2F}, {"xor", 0x30}, {"or", 0x31},
        {"br", 0x32}, {"brtrue", 0x33}, {"brfalse", 0x34},
    };
    for (const auto& entry : map) {
        if (mnemonic == entry.name) return entry.op;
    }
    return -1;
}

// ============================================================================
// Label / ﬁxup helpers
// ============================================================================


int ObjectILParser::fresh_label() {
    return next_label_id_++;
}

void ObjectILParser::place_label(int id, std::vector<uint8_t>& code) {
    auto it = labels_.find(id);
    if (it == labels_.end()) {
        LabelSlot s;
        s.label_id = id;
        s.defined   = true;
        s.byte_pos  = code.size();
        labels_[id] = s;
    } else {
        it->second.defined  = true;
        it->second.byte_pos = code.size();
    }
}

void ObjectILParser::emit_i32_at(std::vector<uint8_t>& code, size_t pos, int32_t val) {
    code[pos + 0] = static_cast<uint8_t>(val & 0xFF);
    code[pos + 1] = static_cast<uint8_t>((val >> 8) & 0xFF);
    code[pos + 2] = static_cast<uint8_t>((val >> 16) & 0xFF);
    code[pos + 3] = static_cast<uint8_t>((val >> 24) & 0xFF);
}

void ObjectILParser::emit_opcode(std::vector<uint8_t>& code, uint8_t op) {
    code.push_back(op);
}

void ObjectILParser::emit_i32(std::vector<uint8_t>& code, int32_t val) {
    code.push_back(static_cast<uint8_t>(val & 0xFF));
    code.push_back(static_cast<uint8_t>((val >> 8) & 0xFF));
    code.push_back(static_cast<uint8_t>((val >> 16) & 0xFF));
    code.push_back(static_cast<uint8_t>((val >> 24) & 0xFF));
}

void ObjectILParser::emit_u16(std::vector<uint8_t>& code, uint16_t val) {
    code.push_back(static_cast<uint8_t>(val & 0xFF));
    code.push_back(static_cast<uint8_t>((val >> 8) & 0xFF));
}

void ObjectILParser::resolve_fixups(std::vector<uint8_t>& code) {
    for (const auto& fx : fixups_) {
        auto it = labels_.find(fx.label_id);
        if (it == labels_.end() || !it->second.defined) {
            throw std::runtime_error("Unresolved label " + std::to_string(fx.label_id));
        }
        size_t target  = it->second.byte_pos;
        size_t br_end  = fx.byte_pos + 4; // end of branch instruction (past the 4-byte offset)
        int32_t offset = static_cast<int32_t>(target - br_end);
        emit_i32_at(code, fx.byte_pos, offset);
    }
    fixups_.clear();
}

// ============================================================================
// Operand encoding helpers
// ============================================================================

// Encode a single-token operand for the given opcode.
// Returns the number of bytes emitted, or -1 if the token wasn't consumed.
int ObjectILParser::encode_operand(ORBTModule& mod, std::vector<uint8_t>& code,
                                   int opcode, const Token& operand)
{
    switch (opcode) {
        case 0x2B: { // ldc.i4
            int32_t val = static_cast<int32_t>(std::stol(operand.text));
            emit_i32(code, val);
            return 4;
        }
        case 0x2C: { // ldc.i8
            int64_t val = static_cast<int64_t>(std::stoll(operand.text));
            for (int i = 0; i < 8; i++) {
                code.push_back(static_cast<uint8_t>((val >> (i * 8)) & 0xFF));
            }
            return 8;
        }
        case 0x2D: { // ldc.r4
            float val = std::stof(operand.text);
            uint32_t bits;
            memcpy(&bits, &val, sizeof(bits));
            emit_i32(code, static_cast<int32_t>(bits));
            return 4;
        }
        case 0x2E: { // ldc.r8
            double val = std::stod(operand.text);
            uint64_t bits;
            memcpy(&bits, &val, sizeof(bits));
            for (int i = 0; i < 8; i++) {
                code.push_back(static_cast<uint8_t>((bits >> (i * 8)) & 0xFF));
            }
            return 8;
        }
        // uint16 index operands
        case 0x03: case 0x04: // ldarg, starg
        case 0x05: case 0x06: // ldloc, stloc
        case 0x02:            // ldstr
        case 0x12: case 0x13: // newobj, newarr
        case 0x1F: case 0x20: case 0x21: // conv, castclass, isinst
        case 0x0F: case 0x2A: // ldfld, stfld
        case 0x10: case 0x11: // ldsfld, stsfld
        {
            uint16_t idx;
            if (operand.kind == TokenKind::Integer) {
                idx = static_cast<uint16_t>(std::stoul(operand.text));
            } else {
                // Store name in string pool and use its index
                // (placeholder — proper resolution happens in ModuleCompiler)
                idx = static_cast<uint16_t>(mod.string_pool.size());
                mod.string_pool.strings.push_back(operand.text);
            }
            emit_u16(code, idx);
            return 2;
        }
        // uint32 function table index (call/callvirt)
        case 0x16: case 0x17: {
            uint32_t idx = static_cast<uint32_t>(mod.string_pool.size());
            mod.string_pool.strings.push_back(operand.text);
            code.push_back(static_cast<uint8_t>(idx & 0xFF));
            code.push_back(static_cast<uint8_t>((idx >> 8) & 0xFF));
            code.push_back(static_cast<uint8_t>((idx >> 16) & 0xFF));
            code.push_back(static_cast<uint8_t>((idx >> 24) & 0xFF));
            return 4;
        }
        // Branch with numeric offset — emit raw int32
        case 0x32: case 0x33: case 0x34: { // br, brtrue, brfalse
            int32_t off = static_cast<int32_t>(std::stol(operand.text));
            emit_i32(code, off);
            return 4;
        }
        default:
            return -1; // not consumed
    }
}

// ============================================================================
// Method body — recursive statement parser that lowers if/while to br’s
// ============================================================================

void ObjectILParser::parse_method_body(ORBTModule& mod, MethodRecord& method) {
    expect(TokenKind::OpenBrace);

    // Reset lowering state for this method
    fixups_.clear();
    labels_.clear();
    next_label_id_ = 0;
    break_targets_.clear();
    continue_targets_.clear();

    // Local variables
    while (tokenizer_.peek().text == "local") {
        tokenizer_.advance(); // skip "local"
        Token lname = expect_identifier();
        expect(TokenKind::Colon);
        Token ltype = expect_identifier();

        LocalRecord local;
        local.name_index = static_cast<uint16_t>(mod.string_pool.size());
        mod.string_pool.strings.push_back(lname.text);
        local.type_index = static_cast<uint16_t>(mod.string_pool.size());
        mod.string_pool.strings.push_back(ltype.text);
        method.locals.push_back(local);
        method.local_count++;
    }

    // Parse body statements
    while (tokenizer_.peek().kind != TokenKind::CloseBrace
           && tokenizer_.peek().kind != TokenKind::Eof)
    {
        parse_statement(mod, method);
    }

    // Resolve any pending forward branches
    resolve_fixups(method.raw_instruction_data);

    expect(TokenKind::CloseBrace);
}

// ============================================================================
// parse_statement — dispatches to the right handler
// ============================================================================

void ObjectILParser::parse_statement(ORBTModule& mod, MethodRecord& method) {
    if (tokenizer_.peek().kind == TokenKind::Eof ||
        tokenizer_.peek().kind == TokenKind::CloseBrace)
        return;

    Token next = tokenizer_.peek();

    // Structured control flow keywords
    if (next.text == "if") {
        tokenizer_.advance(); // consume "if"
        parse_if(mod, method);
        return;
    }
    if (next.text == "while") {
        tokenizer_.advance(); // consume "while"
        parse_while(mod, method);
        return;
    }

    // break / continue — only valid inside a while body
    if (next.text == "break") {
        tokenizer_.advance();
        if (break_targets_.empty())
            throw std::runtime_error("break outside loop at line " +
                std::to_string(next.line) + ":" + std::to_string(next.col));
        int end_label = break_targets_.back();
        emit_opcode(method.raw_instruction_data, 0x32); // br
        // Forward branch to end_label (not defined yet) → record fixup
        fixups_.push_back({method.raw_instruction_data.size(), end_label});
        emit_i32(method.raw_instruction_data, 0); // placeholder
        method.instr_count++;
        return;
    }
    if (next.text == "continue") {
        tokenizer_.advance();
        if (continue_targets_.empty())
            throw std::runtime_error("continue outside loop at line " +
                std::to_string(next.line) + ":" + std::to_string(next.col));
        int loop_label = continue_targets_.back();
        emit_opcode(method.raw_instruction_data, 0x32); // br
        // Backward branch to loop_label (already defined) → compute now
        auto it = labels_.find(loop_label);
        if (it != labels_.end() && it->second.defined) {
            size_t target = it->second.byte_pos;
            size_t br_end = method.raw_instruction_data.size() + 4;
            int32_t offset = static_cast<int32_t>(target - br_end);
            emit_i32(method.raw_instruction_data, offset);
        } else {
            // Shouldn't happen — loop label is placed before body
            fixups_.push_back({method.raw_instruction_data.size(), loop_label});
            emit_i32(method.raw_instruction_data, 0);
        }
        method.instr_count++;
        return;
    }

    // Fall through: simple instruction
    parse_simple_instruction(mod, method);
}

// ============================================================================
// if (stack) { ... } else { ... }  →  brfalse / br
// ============================================================================

void ObjectILParser::parse_if(ORBTModule& mod, MethodRecord& method) {
    // Expect (stack)
    expect(TokenKind::OpenParen);
    Token cond = expect_identifier();
    if (cond.text != "stack")
        throw std::runtime_error("Expected 'stack' condition in if at line " +
            std::to_string(cond.line));
    expect(TokenKind::CloseParen);

    int else_label = fresh_label();
    int end_label  = fresh_label();

    // brfalse <else_label>
    emit_opcode(method.raw_instruction_data, 0x34); // brfalse
    fixups_.push_back({method.raw_instruction_data.size(), else_label});
    emit_i32(method.raw_instruction_data, 0); // placeholder
    method.instr_count++;

    // Then-body
    expect(TokenKind::OpenBrace);
    while (tokenizer_.peek().kind != TokenKind::CloseBrace
           && tokenizer_.peek().kind != TokenKind::Eof)
    {
        parse_statement(mod, method);
    }
    expect(TokenKind::CloseBrace);

    // Check for else
    if (tokenizer_.peek().text == "else") {
        tokenizer_.advance(); // consume "else"

        // br <end_label> (skip past else-body)
        emit_opcode(method.raw_instruction_data, 0x32); // br
        fixups_.push_back({method.raw_instruction_data.size(), end_label});
        emit_i32(method.raw_instruction_data, 0); // placeholder
        method.instr_count++;

        // Place else label
        place_label(else_label, method.raw_instruction_data);

        // Else-body
        expect(TokenKind::OpenBrace);
        while (tokenizer_.peek().kind != TokenKind::CloseBrace
               && tokenizer_.peek().kind != TokenKind::Eof)
        {
            parse_statement(mod, method);
        }
        expect(TokenKind::CloseBrace);
    } else {
        // No else — just place the else label (which is the end)
        place_label(else_label, method.raw_instruction_data);
    }

    // Place end label
    place_label(end_label, method.raw_instruction_data);
}

// ============================================================================
// while (stack) { ... }  →  brfalse / br (loop)
// ============================================================================

void ObjectILParser::parse_while(ORBTModule& mod, MethodRecord& method) {
    // Expect (stack)
    expect(TokenKind::OpenParen);
    Token cond = expect_identifier();
    if (cond.text != "stack")
        throw std::runtime_error("Expected 'stack' condition in while at line " +
            std::to_string(cond.line));
    expect(TokenKind::CloseParen);

    int loop_label = fresh_label();
    int end_label  = fresh_label();

    // Push break/continue targets
    break_targets_.push_back(end_label);
    continue_targets_.push_back(loop_label);

    // Place loop label
    place_label(loop_label, method.raw_instruction_data);

    // brfalse <end_label>
    emit_opcode(method.raw_instruction_data, 0x34); // brfalse
    fixups_.push_back({method.raw_instruction_data.size(), end_label});
    emit_i32(method.raw_instruction_data, 0); // placeholder
    method.instr_count++;

    // Body
    expect(TokenKind::OpenBrace);
    while (tokenizer_.peek().kind != TokenKind::CloseBrace
           && tokenizer_.peek().kind != TokenKind::Eof)
    {
        parse_statement(mod, method);
    }
    expect(TokenKind::CloseBrace);

    // br <loop_label> (back to condition check)
    emit_opcode(method.raw_instruction_data, 0x32); // br
    // Backward branch — compute directly
    {
        size_t target = labels_[loop_label].byte_pos;
        size_t br_end = method.raw_instruction_data.size() + 4;
        int32_t offset = static_cast<int32_t>(target - br_end);
        emit_i32(method.raw_instruction_data, offset);
    }
    method.instr_count++;

    // Pop break/continue targets
    break_targets_.pop_back();
    continue_targets_.pop_back();

    // Place end label
    place_label(end_label, method.raw_instruction_data);
}

// ============================================================================
// Encode a simple instruction line (one mnemonic + optional operand)
// ============================================================================

void ObjectILParser::parse_simple_instruction(ORBTModule& mod, MethodRecord& method) {
    Token mnemonic = expect_identifier();
    size_t mnemonic_line = mnemonic.line;

    int opcode = mnemonic_to_opcode(mnemonic.text);
    if (opcode < 0) {
        // Unknown mnemonic — skip to end of line
        while (tokenizer_.peek().kind != TokenKind::Eof &&
               tokenizer_.peek().kind != TokenKind::CloseBrace &&
               tokenizer_.peek().line == mnemonic_line)
        {
            tokenizer_.advance();
        }
        return;
    }

    emit_opcode(method.raw_instruction_data, static_cast<uint8_t>(opcode));

    // Read the operand token if on the same line
    bool operand_read = false;
    if (tokenizer_.peek().kind != TokenKind::Eof &&
        tokenizer_.peek().kind != TokenKind::CloseBrace &&
        tokenizer_.peek().kind != TokenKind::OpenBrace &&
        tokenizer_.peek().line == mnemonic_line)
    {
        Token operand = tokenizer_.advance();

        int consumed = encode_operand(mod, method.raw_instruction_data, opcode, operand);
        if (consumed < 0) {
            // Operand not consumed — skip remaining inline tokens
            while (tokenizer_.peek().kind != TokenKind::Eof &&
                   tokenizer_.peek().line == mnemonic_line)
            {
                tokenizer_.advance();
            }
        } else {
            operand_read = true;
        }
    }

    // For call/callvirt with a method ref that spans the line,
    // consume remaining inline tokens as comment/ignored
    if ((opcode == 0x16 || opcode == 0x17) && operand_read) {
        while (tokenizer_.peek().kind != TokenKind::Eof &&
               tokenizer_.peek().kind != TokenKind::CloseBrace &&
               tokenizer_.peek().line == mnemonic_line)
        {
            tokenizer_.advance();
        }
    }

    method.instr_count++;
}

std::vector<std::string> ObjectILParser::parse_type_list() {
    std::vector<std::string> types;
    while (true) {
        Token t = expect_identifier();
        types.push_back(t.text);
        if (!try_match(TokenKind::Comma)) break;
    }
    return types;
}

// ============================================================================
// High-level convenience
// ============================================================================

std::unique_ptr<ORBTModule> parse_oil_file(const std::string& path) {
    std::ifstream file(path);
    if (!file) {
        throw std::runtime_error("Cannot open file: " + path);
    }
    ObjectILParser parser(file);
    return std::make_unique<ORBTModule>(parser.parse_module());
}

std::unique_ptr<ORBTModule> parse_oil_string(const std::string& content) {
    std::istringstream stream(content);
    ObjectILParser parser(stream);
    return std::make_unique<ORBTModule>(parser.parse_module());
}

} // namespace objectrt
