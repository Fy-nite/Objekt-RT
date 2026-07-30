#pragma once
#ifndef ORBT_MODULE_HPP
#define ORBT_MODULE_HPP

#include <string>
#include <vector>
#include <cstdint>
#include <variant>
#include <memory>
#include <unordered_map>
#include <optional>
#include <stdexcept>

namespace objectrt {

// ============================================================================
// Enums
// ============================================================================

enum class TypeKind : uint8_t {
    Class     = 0x01,
    Interface = 0x02,
    Struct    = 0x03,
    Enum      = 0x04,
};

inline const char* type_kind_name(TypeKind k) {
    switch (k) {
        case TypeKind::Class:     return "class";
        case TypeKind::Interface: return "interface";
        case TypeKind::Struct:    return "struct";
        case TypeKind::Enum:      return "enum";
        default:                  return "unknown";
    }
}

enum class MemberAccess : uint8_t {
    Public    = 0x01,
    Private   = 0x02,
    Protected = 0x03,
    Internal  = 0x04,
};

inline const char* access_name(MemberAccess a) {
    switch (a) {
        case MemberAccess::Public:    return "public";
        case MemberAccess::Private:   return "private";
        case MemberAccess::Protected: return "protected";
        case MemberAccess::Internal:  return "internal";
        default:                      return "unknown";
    }
}

enum class TypeFlags : uint8_t {
    None     = 0x00,
    Abstract = 0x01,
    Sealed   = 0x02,
};

inline TypeFlags operator|(TypeFlags a, TypeFlags b) {
    return static_cast<TypeFlags>(static_cast<uint8_t>(a) | static_cast<uint8_t>(b));
}
inline bool has_flag(TypeFlags f, TypeFlags flag) {
    return (static_cast<uint8_t>(f) & static_cast<uint8_t>(flag)) != 0;
}

enum class MethodFlags : uint8_t {
    None     = 0x00,
    Static   = 0x01,
    Virtual  = 0x02,
    Override = 0x04,
    Abstract = 0x08,
};

inline MethodFlags operator|(MethodFlags a, MethodFlags b) {
    return static_cast<MethodFlags>(static_cast<uint8_t>(a) | static_cast<uint8_t>(b));
}
inline bool has_flag(MethodFlags f, MethodFlags flag) {
    return (static_cast<uint8_t>(f) & static_cast<uint8_t>(flag)) != 0;
}

enum class ImportKind : uint8_t {
    Type   = 0x01,
    Method = 0x02,
    Field  = 0x03,
};

enum class MetadataValueKind : uint8_t {
    String     = 0x01,
    StringList = 0x02,
};

// ============================================================================
// Opcodes
// ============================================================================

enum class Opcode : uint16_t {
    // Table 0
    Nop       = 0x00,
    Ldc       = 0x01,
    Ldstr     = 0x02,
    Ldarg     = 0x03,
    Starg     = 0x04,
    Ldloc     = 0x05,
    Stloc     = 0x06,
    Add       = 0x07,
    Sub       = 0x08,
    Mul       = 0x09,
    Div       = 0x0A,
    Rem       = 0x0B,
    Neg       = 0x0C,
    Ceq       = 0x0D,
    Cne       = 0x0E,
    Ldfld     = 0x0F,
    Ldsfld    = 0x10,
    Stsfld    = 0x11,
    Newobj    = 0x12,
    Newarr    = 0x13,
    Ldelem    = 0x14,
    Stelem    = 0x15,
    Call      = 0x16,
    Callvirt  = 0x17,
    Ret       = 0x18,
    If        = 0x19,
    While     = 0x1A,
    Break     = 0x1B,
    Continue  = 0x1C,
    Try       = 0x1D,
    Throw     = 0x1E,
    Conv      = 0x1F,
    Castclass = 0x20,
    Isinst    = 0x21,
    Dup       = 0x22,
    Pop       = 0x23,
    Ldnull    = 0x24,
    Not       = 0x25,
    Cgt       = 0x26,
    Cge       = 0x27,
    Clt       = 0x28,
    Cle       = 0x29,
    Stfld     = 0x2A,
    LdcI4     = 0x2B,
    LdcI8     = 0x2C,
    LdcR4     = 0x2D,
    LdcR8     = 0x2E,
    And       = 0x2F,
    Xor       = 0x30,
    Or        = 0x31,
    Br        = 0x32,
    Brtrue    = 0x33,
    Brfalse   = 0x34,
};

inline const char* opcode_name(Opcode op) {
    switch (op) {
        case Opcode::Nop:       return "nop";
        case Opcode::Ldc:       return "ldc";
        case Opcode::Ldstr:     return "ldstr";
        case Opcode::Ldarg:     return "ldarg";
        case Opcode::Starg:     return "starg";
        case Opcode::Ldloc:     return "ldloc";
        case Opcode::Stloc:     return "stloc";
        case Opcode::Add:       return "add";
        case Opcode::Sub:       return "sub";
        case Opcode::Mul:       return "mul";
        case Opcode::Div:       return "div";
        case Opcode::Rem:       return "rem";
        case Opcode::Neg:       return "neg";
        case Opcode::Ceq:       return "ceq";
        case Opcode::Cne:       return "cne";
        case Opcode::Ldfld:     return "ldfld";
        case Opcode::Ldsfld:    return "ldsfld";
        case Opcode::Stsfld:    return "stsfld";
        case Opcode::Newobj:    return "newobj";
        case Opcode::Newarr:    return "newarr";
        case Opcode::Ldelem:    return "ldelem";
        case Opcode::Stelem:    return "stelem";
        case Opcode::Call:      return "call";
        case Opcode::Callvirt:  return "callvirt";
        case Opcode::Ret:       return "ret";
        case Opcode::If:        return "if";
        case Opcode::While:     return "while";
        case Opcode::Break:     return "break";
        case Opcode::Continue:  return "continue";
        case Opcode::Try:       return "try";
        case Opcode::Throw:     return "throw";
        case Opcode::Conv:      return "conv";
        case Opcode::Castclass: return "castclass";
        case Opcode::Isinst:    return "isinst";
        case Opcode::Dup:       return "dup";
        case Opcode::Pop:       return "pop";
        case Opcode::Ldnull:    return "ldnull";
        case Opcode::Not:       return "not";
        case Opcode::Cgt:       return "cgt";
        case Opcode::Cge:       return "cge";
        case Opcode::Clt:       return "clt";
        case Opcode::Cle:       return "cle";
        case Opcode::Stfld:     return "stfld";
        case Opcode::LdcI4:     return "ldc.i4";
        case Opcode::LdcI8:     return "ldc.i8";
        case Opcode::LdcR4:     return "ldc.r4";
        case Opcode::LdcR8:     return "ldc.r8";
        case Opcode::And:       return "and";
        case Opcode::Xor:       return "xor";
        case Opcode::Or:        return "or";
        case Opcode::Br:        return "br";
        case Opcode::Brtrue:    return "brtrue";
        case Opcode::Brfalse:   return "brfalse";
        default:                return "???";
    }
}

// ============================================================================
// Operand types for instructions
// ============================================================================

struct OperandNone {};
struct OperandI4 { int32_t value; };
struct OperandI8 { int64_t value; };
struct OperandR4 { float value; };
struct OperandR8 { double value; };
struct OperandString { uint16_t string_index; };
struct OperandIndex { uint16_t index; };
struct OperandFieldRef { uint16_t string_index; };
struct OperandMethodRef { uint16_t string_index; };
struct OperandTypeRef { uint16_t string_index; };
struct OperandBranch { int32_t pc_offset; };

// Condition for if/while
enum class ConditionKind : uint8_t {
    Stack      = 0x00,
    Binary     = 0x01,
    Expression = 0x02,
    Block      = 0x03,
};

struct ConditionOperand {
    ConditionKind kind;
    uint8_t comparison; // for binary kind: opcode of comparison
    std::vector<uint8_t> embedded_data; // for expression/block kind
};

// Exception handler for try
struct CatchRecord {
    uint16_t type_index;
    std::vector<uint8_t> body;
};

struct ExceptionHandlerOperand {
    std::vector<uint8_t> try_block;
    std::vector<CatchRecord> catch_records;
    bool has_finally;
    std::vector<uint8_t> finally_block;
};

using Operand = std::variant<
    OperandNone,
    OperandI4,
    OperandI8,
    OperandR4,
    OperandR8,
    OperandString,
    OperandIndex,
    OperandFieldRef,
    OperandMethodRef,
    OperandTypeRef,
    OperandBranch,
    ConditionOperand,
    ExceptionHandlerOperand
>;

struct Instruction {
    Opcode opcode;
    Operand operand;
    uint32_t pc_offset;
};

// ============================================================================
// Data structures
// ============================================================================

struct StringPool {
    std::vector<std::string> strings;

    const std::string& get(uint16_t index) const {
        if (index >= strings.size())
            throw std::out_of_range("String pool index out of range");
        return strings[index];
    }

    size_t size() const { return strings.size(); }
};

struct FieldRecord {
    uint16_t name_index  = 0;
    uint16_t type_index  = 0;
};

struct ParameterRecord {
    uint16_t name_index  = 0;
    uint16_t type_index  = 0;
};

struct LocalRecord {
    uint16_t name_index  = 0;
    uint16_t type_index  = 0;
};

struct LabelRecord {
    uint16_t name_index  = 0;
    uint32_t pc_offset   = 0;
};

struct MethodRecord {
    uint16_t name_index         = 0;
    uint16_t signature_index    = 0;
    MemberAccess access         = MemberAccess::Public;
    MethodFlags flags           = MethodFlags::None;
    uint16_t param_count        = 0;
    std::vector<ParameterRecord> params;
    uint16_t local_count        = 0;
    std::vector<LocalRecord> locals;
    uint16_t label_count        = 0;
    std::vector<LabelRecord> labels;
    uint32_t instr_count        = 0;
    std::vector<Instruction> instructions;
    std::vector<uint8_t> raw_instruction_data;
};

struct TypeRecord {
    TypeKind kind                    = TypeKind::Class;
    uint16_t name_index              = 0;
    uint16_t namespace_index         = 0;
    MemberAccess access              = MemberAccess::Public;
    TypeFlags flags                  = TypeFlags::None;
    int32_t base_type_index          = -1;
    uint16_t interface_count         = 0;
    std::vector<uint16_t> interface_indices;
    uint16_t field_count             = 0;
    std::vector<FieldRecord> fields;
    uint16_t method_count            = 0;
    std::vector<MethodRecord> methods;
};

struct ImportEntry {
    uint16_t module_index = 0;
    uint16_t symbol_index = 0;
    ImportKind kind       = ImportKind::Type;
    uint8_t flags         = 0;
};

struct ExportEntry {
    uint16_t name_index   = 0;
    ImportKind kind       = ImportKind::Type;
    uint32_t local_index  = 0;
    uint16_t module_index = 0;
};

struct MetadataEntry {
    std::string key;
    std::variant<std::string, std::vector<std::string>> value;
};

struct MetadataBlock {
    std::vector<MetadataEntry> entries;
    std::string spec_version; // convenience
    std::vector<std::string> require;
    std::vector<std::string> optional;
};

// ============================================================================
// Module representation
// ============================================================================

struct ModuleVersion {
    uint16_t major;
    uint16_t minor;
    uint16_t patch;
};

class ORBTModule {
public:
    // Header
    std::string module_name;
    uint8_t format_version;
    ModuleVersion version;

    // Tables
    StringPool string_pool;
    std::vector<TypeRecord> types;
    std::vector<ImportEntry> imports;
    std::vector<ExportEntry> exports;

    // Metadata
    MetadataBlock metadata;

    // Helpers
    const std::string& resolve(uint16_t string_index) const {
        return string_pool.get(string_index);
    }

    std::string type_name(const TypeRecord& type) const {
        std::string ns = string_pool.get(type.namespace_index);
        std::string name = string_pool.get(type.name_index);
        if (ns.empty()) return name;
        return ns + "." + name;
    }

    void dump(std::ostream& os, bool verbose = false) const;
};

// Forward declare instruction decoder
class InstructionDecoder {
public:
    static std::vector<Instruction> decode(
        const std::vector<uint8_t>& data,
        const StringPool& pool,
        uint32_t start_offset = 0
    );
};

} // namespace objectrt

#endif // ORBT_MODULE_HPP
