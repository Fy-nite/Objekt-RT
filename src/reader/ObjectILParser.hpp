#pragma once
#ifndef OBJECTIL_PARSER_HPP
#define OBJECTIL_PARSER_HPP

#include "Module.hpp"
#include <string>
#include <vector>
#include <memory>
#include <istream>
#include <unordered_map>

namespace objectrt {

// ============================================================================
// ObjectIL Text Tokenizer
// ============================================================================

enum class TokenKind {
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
    Arrow,       // ->
    OpenParen,   // (
    CloseParen,  // )
    OpenBrace,   // {
    CloseBrace,  // }
    OpenBracket, // [
    CloseBracket,// ]
    DotMetadata, // .metadata
};

struct Token {
    TokenKind kind;
    std::string text;
    size_t line;
    size_t col;
};

class Tokenizer {
public:
    explicit Tokenizer(std::istream& input);

    Token peek();
    Token advance();
    bool eof() const;

private:
    void skip_whitespace_and_comments();
    Token read_next();

    std::istream& input_;
    Token lookahead_;
    bool has_lookahead_ = false;
    size_t line_ = 1;
    size_t col_ = 1;
};

// ============================================================================
// ObjectIL Parser
// ============================================================================

class ObjectILParser {
public:
    explicit ObjectILParser(std::istream& input);

    ORBTModule parse_module();
    void parse_into(ORBTModule& mod);

private:
    Tokenizer tokenizer_;

    // Parsing helpers
    Token expect(TokenKind kind);
    Token expect_identifier();
    bool match(TokenKind kind);
    std::optional<Token> try_match(TokenKind kind);

    // Grammar rules
    void parse_module_decl(ORBTModule& mod);
    void parse_metadata_block(ORBTModule& mod);
    void parse_type_decl(ORBTModule& mod);
    void parse_member(ORBTModule& mod, TypeRecord& type);
    void parse_field(ORBTModule& mod, TypeRecord& type);
    void parse_method(ORBTModule& mod, TypeRecord& type);
    void parse_method_body(ORBTModule& mod, MethodRecord& method);

    // ── Structured control-ﬂow lowering state ─────────────────────────
    struct PendingBranch {
        size_t byte_pos;   // position of the 4-byte offset in raw_instruction_data
        int    label_id;
    };
    struct LabelSlot {
        int    label_id;
        bool   defined = false;
        size_t byte_pos = 0; // position of the label in raw_instruction_data
    };

    std::vector<PendingBranch> fixups_;
    std::unordered_map<int, LabelSlot> labels_;
    int next_label_id_ = 0;
    std::vector<int> break_targets_;   // stack of end-label ids
    std::vector<int> continue_targets_;// stack of loop-label ids

    int  fresh_label();
    void place_label(int id, std::vector<uint8_t>& code);
    void emit_i32_at(std::vector<uint8_t>& code, size_t pos, int32_t val);
    void emit_opcode(std::vector<uint8_t>& code, uint8_t op);
    void emit_i32(std::vector<uint8_t>& code, int32_t val);
    void emit_u16(std::vector<uint8_t>& code, uint16_t val);
    void resolve_fixups(std::vector<uint8_t>& code);

    // Lowering: recursive statement parser
    void parse_statement(ORBTModule& mod, MethodRecord& method);
    void parse_if(ORBTModule& mod, MethodRecord& method);
    void parse_while(ORBTModule& mod, MethodRecord& method);
    void parse_simple_instruction(ORBTModule& mod, MethodRecord& method);

    // Operand encoding helpers
    int  encode_operand(ORBTModule& mod, std::vector<uint8_t>& code,
                        int opcode, const Token& operand);
    std::vector<std::string> parse_type_list();
};

// ============================================================================
// High-level convenience
// ============================================================================

std::unique_ptr<ORBTModule> parse_oil_file(const std::string& path);
std::unique_ptr<ORBTModule> parse_oil_string(const std::string& content);

} // namespace objectrt

#endif // OBJECTIL_PARSER_HPP
