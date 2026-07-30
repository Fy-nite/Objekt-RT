#include "ORBTReader.hpp"
#include "Module.hpp"
#include <cstring>
#include <sstream>
#include <iostream>
#include <algorithm>

namespace objectrt {

// ============================================================================
// BinaryStream implementation
// ============================================================================

BinaryStream::BinaryStream(const std::string& path) : pos_(0) {
    std::ifstream file(path, std::ios::binary | std::ios::ate);
    if (!file)
        throw std::runtime_error("Cannot open file: " + path);

    std::streamsize file_size = file.tellg();
    file.seekg(0, std::ios::beg);

    data_.resize(static_cast<size_t>(file_size));
    if (!file.read(reinterpret_cast<char*>(data_.data()), file_size))
        throw std::runtime_error("Failed to read file: " + path);
}

BinaryStream::BinaryStream(const std::vector<uint8_t>& data) : data_(data), pos_(0) {}

uint8_t BinaryStream::read_u8() {
    if (pos_ + 1 > data_.size())
        throw std::runtime_error("Unexpected end of stream");
    return data_[pos_++];
}

uint16_t BinaryStream::read_u16() {
    if (pos_ + 2 > data_.size())
        throw std::runtime_error("Unexpected end of stream");
    uint16_t val = static_cast<uint16_t>(data_[pos_])
                 | (static_cast<uint16_t>(data_[pos_ + 1]) << 8);
    pos_ += 2;
    return val;
}

uint32_t BinaryStream::read_u32() {
    if (pos_ + 4 > data_.size())
        throw std::runtime_error("Unexpected end of stream");
    uint32_t val = static_cast<uint32_t>(data_[pos_])
                 | (static_cast<uint32_t>(data_[pos_ + 1]) << 8)
                 | (static_cast<uint32_t>(data_[pos_ + 2]) << 16)
                 | (static_cast<uint32_t>(data_[pos_ + 3]) << 24);
    pos_ += 4;
    return val;
}

int32_t BinaryStream::read_i32() {
    return static_cast<int32_t>(read_u32());
}

int64_t BinaryStream::read_i64() {
    return static_cast<int64_t>(read_u64());
}

uint64_t BinaryStream::read_u64() {
    if (pos_ + 8 > data_.size())
        throw std::runtime_error("Unexpected end of stream");
    uint64_t val = static_cast<uint64_t>(data_[pos_])
                 | (static_cast<uint64_t>(data_[pos_ + 1]) << 8)
                 | (static_cast<uint64_t>(data_[pos_ + 2]) << 16)
                 | (static_cast<uint64_t>(data_[pos_ + 3]) << 24)
                 | (static_cast<uint64_t>(data_[pos_ + 4]) << 32)
                 | (static_cast<uint64_t>(data_[pos_ + 5]) << 40)
                 | (static_cast<uint64_t>(data_[pos_ + 6]) << 48)
                 | (static_cast<uint64_t>(data_[pos_ + 7]) << 56);
    pos_ += 8;
    return val;
}

float BinaryStream::read_r4() {
    uint32_t bits = read_u32();
    float val;
    std::memcpy(&val, &bits, sizeof(val));
    return val;
}

double BinaryStream::read_r8() {
    uint64_t bits = read_u64();
    double val;
    std::memcpy(&val, &bits, sizeof(val));
    return val;
}

std::string BinaryStream::read_string() {
    uint16_t len = read_u16();
    if (len == 0) return {};
    if (pos_ + len > data_.size())
        throw std::runtime_error("Unexpected end of stream reading string");
    std::string s(reinterpret_cast<const char*>(data_.data() + pos_), len);
    pos_ += len;
    return s;
}

std::vector<uint8_t> BinaryStream::read_bytes(size_t count) {
    if (pos_ + count > data_.size())
        throw std::runtime_error("Unexpected end of stream");
    auto begin = data_.begin() + static_cast<std::ptrdiff_t>(pos_);
    std::vector<uint8_t> bytes(begin, begin + static_cast<std::ptrdiff_t>(count));
    pos_ += count;
    return bytes;
}

// ============================================================================
// ORBTReader implementation
// ============================================================================

ORBTReader::ORBTReader(BinaryStream& stream) : stream_(stream) {}

ORBTModule ORBTReader::read_module() {
    ORBTModule mod;
    read_header(mod);
    read_string_pool(mod);
    read_type_table(mod);
    read_import_table(mod);
    read_export_table(mod);
    read_metadata_block(mod);
    read_method_bodies(mod);
    return mod;
}

void ORBTReader::read_header(ORBTModule& mod) {
    // Magic: 4 bytes "ORBT"
    uint32_t magic = stream_.read_u32();
    if (magic != 0x4F524254) {
        throw std::runtime_error("Invalid ORBT magic: expected 0x4F524254");
    }

    // Format version
    mod.format_version = stream_.read_u8();
    if (mod.format_version != 0x01) {
        throw std::runtime_error("Unsupported ORBT version: " + std::to_string(mod.format_version));
    }

    // Module name (length-prefixed UTF-8)
    mod.module_name = stream_.read_string();

    // Version triple (three uint16)
    mod.version.major = stream_.read_u16();
    mod.version.minor = stream_.read_u16();
    mod.version.patch = stream_.read_u16();
}

void ORBTReader::read_string_pool(ORBTModule& mod) {
    uint16_t count = stream_.read_u16();
    mod.string_pool.strings.reserve(count);
    for (uint16_t i = 0; i < count; i++) {
        mod.string_pool.strings.push_back(stream_.read_string());
    }
}

void ORBTReader::read_type_table(ORBTModule& mod) {
    uint16_t count = stream_.read_u16();
    mod.types.reserve(count);
    for (uint16_t i = 0; i < count; i++) {
        TypeRecord type;
        type.kind            = static_cast<TypeKind>(stream_.read_u8());
        type.name_index      = stream_.read_u16();
        type.namespace_index = stream_.read_u16();
        type.access          = static_cast<MemberAccess>(stream_.read_u8());
        type.flags           = static_cast<TypeFlags>(stream_.read_u8());
        type.base_type_index = stream_.read_i32();

        // Interfaces
        type.interface_count = stream_.read_u16();
        type.interface_indices.reserve(type.interface_count);
        for (uint16_t j = 0; j < type.interface_count; j++) {
            type.interface_indices.push_back(stream_.read_u16());
        }

        // Fields
        type.field_count = stream_.read_u16();
        type.fields.reserve(type.field_count);
        for (uint16_t j = 0; j < type.field_count; j++) {
            type.fields.push_back(read_field_record());
        }

        // Methods (raw records, bodies come later)
        type.method_count = stream_.read_u16();
        type.methods.reserve(type.method_count);
        for (uint16_t j = 0; j < type.method_count; j++) {
            type.methods.push_back(read_method_record(mod.string_pool));
        }

        mod.types.push_back(std::move(type));
    }
}

FieldRecord ORBTReader::read_field_record() {
    FieldRecord field;
    field.name_index = stream_.read_u16();
    field.type_index = stream_.read_u16();
    return field;
}

ParameterRecord ORBTReader::read_param_record() {
    ParameterRecord param;
    param.name_index = stream_.read_u16();
    param.type_index = stream_.read_u16();
    return param;
}

LocalRecord ORBTReader::read_local_record() {
    LocalRecord local;
    local.name_index = stream_.read_u16();
    local.type_index = stream_.read_u16();
    return local;
}

LabelRecord ORBTReader::read_label_record() {
    LabelRecord label;
    label.name_index = stream_.read_u16();
    label.pc_offset  = stream_.read_u32();
    return label;
}

MethodRecord ORBTReader::read_method_record(const StringPool& pool) {
    MethodRecord method;
    method.name_index      = stream_.read_u16();
    method.signature_index = stream_.read_u16();
    method.access          = static_cast<MemberAccess>(stream_.read_u8());
    method.flags           = static_cast<MethodFlags>(stream_.read_u8());

    // Parameters
    method.param_count = stream_.read_u16();
    method.params.reserve(method.param_count);
    for (uint16_t j = 0; j < method.param_count; j++) {
        method.params.push_back(read_param_record());
    }

    // Locals
    method.local_count = stream_.read_u16();
    method.locals.reserve(method.local_count);
    for (uint16_t j = 0; j < method.local_count; j++) {
        method.locals.push_back(read_local_record());
    }

    // Labels
    method.label_count = stream_.read_u16();
    method.labels.reserve(method.label_count);
    for (uint16_t j = 0; j < method.label_count; j++) {
        method.labels.push_back(read_label_record());
    }

    // Instruction count and raw data
    method.instr_count = stream_.read_u32();
    // We need to read the instruction data, but we don't know the size
    // The instruction data is variable-length. We'll store the raw bytes
    // and decode them later, or decode them now by parsing instructions.
    // For now, store the current offset so we can decode
    // Actually, let's decode the instructions now
    // The spec says instr_count + instruction_data follows
    // But we need to know the size to know where it ends...
    // Let's decode instruction by instruction

    // We'll store the raw data and decode simultaneously
    // First, let's record our position
    // Actually, to keep it simple and robust, let's read until we hit
    // the expected instruction count, decoding as we go.

    size_t data_start = stream_.tell();
    method.instructions.reserve(method.instr_count);

    for (uint32_t j = 0; j < method.instr_count; j++) {
        uint32_t pc = static_cast<uint32_t>(stream_.tell() - data_start);
        Instruction instr = read_instruction(pool, pc);
        method.instructions.push_back(std::move(instr));
    }

    // Also store the raw bytes for potential re-parsing
    size_t data_end = stream_.tell();
    stream_.seek(data_start);
    method.raw_instruction_data = stream_.read_bytes(data_end - data_start);

    return method;
}

// ============================================================================
// Instruction decoding
// ============================================================================

Opcode read_opcode(BinaryStream& s) {
    uint16_t op = 0;
    int table = 0;
    while (true) {
        uint8_t byte = s.read_u8();
        if (byte == 0xFF) {
            table++;
            if (table > 255)
                throw std::runtime_error("Opcode table overflow (max 256 tables)");
            continue;
        }
        op = static_cast<uint16_t>(table * 256 + byte);
        break;
    }
    return static_cast<Opcode>(op);
}

Instruction ORBTReader::read_instruction(const StringPool& pool, uint32_t pc) {
    Instruction instr;
    instr.pc_offset = pc;
    instr.opcode = read_opcode(stream_);

    switch (instr.opcode) {
        // No operand
        case Opcode::Nop:
        case Opcode::Add:
        case Opcode::Sub:
        case Opcode::Mul:
        case Opcode::Div:
        case Opcode::Rem:
        case Opcode::Neg:
        case Opcode::Ceq:
        case Opcode::Cne:
        case Opcode::Cgt:
        case Opcode::Cge:
        case Opcode::Clt:
        case Opcode::Cle:
        case Opcode::And:
        case Opcode::Xor:
        case Opcode::Or:
        case Opcode::Not:
        case Opcode::Dup:
        case Opcode::Pop:
        case Opcode::Ldnull:
        case Opcode::Ret:
        case Opcode::Break:
        case Opcode::Continue:
        case Opcode::Throw:
        case Opcode::Ldelem:
        case Opcode::Stelem:
            instr.operand = OperandNone{};
            break;

        // Immediate values
        case Opcode::LdcI4:
            instr.operand = OperandI4{stream_.read_i32()};
            break;
        case Opcode::LdcI8:
            instr.operand = OperandI8{stream_.read_i64()};
            break;
        case Opcode::LdcR4:
            instr.operand = OperandR4{stream_.read_r4()};
            break;
        case Opcode::LdcR8:
            instr.operand = OperandR8{stream_.read_r8()};
            break;
        case Opcode::Ldc:
            // Generic ldc - uses type tag, but for now treat as i4
            instr.operand = OperandI4{stream_.read_i32()};
            break;

        // String constant
        case Opcode::Ldstr:
            instr.operand = OperandString{stream_.read_u16()};
            break;

        // Index-based (args, locals)
        case Opcode::Ldarg:
        case Opcode::Starg:
        case Opcode::Ldloc:
        case Opcode::Stloc:
            instr.operand = OperandIndex{stream_.read_u16()};
            break;

        // Field reference (string pool index)
        case Opcode::Ldfld:
        case Opcode::Stfld:
        case Opcode::Ldsfld:
        case Opcode::Stsfld:
            instr.operand = OperandFieldRef{stream_.read_u16()};
            break;

        // Method reference (signature string pool index)
        case Opcode::Call:
        case Opcode::Callvirt:
            instr.operand = OperandMethodRef{stream_.read_u16()};
            break;

        // Object creation
        case Opcode::Newobj:
            instr.operand = OperandString{stream_.read_u16()};
            break;
        case Opcode::Newarr:
            instr.operand = OperandString{stream_.read_u16()};
            break;

        // Type reference
        case Opcode::Conv:
        case Opcode::Castclass:
        case Opcode::Isinst:
            instr.operand = OperandTypeRef{stream_.read_u16()};
            break;

        // Branch
        case Opcode::Br:
        case Opcode::Brtrue:
        case Opcode::Brfalse:
            instr.operand = OperandBranch{stream_.read_i32()};
            break;

        // Structured control flow
        case Opcode::If:
        case Opcode::While:
            instr.operand = read_condition_operand();
            break;

        case Opcode::Try:
            instr.operand = read_exception_handler();
            break;

        default:
            // Unknown opcode - skip (we don't know operand size)
            // For safety, store as no operand and continue
            std::cerr << "Warning: Unknown opcode 0x"
                      << std::hex << static_cast<int>(instr.opcode)
                      << std::dec << " at PC " << pc << std::endl;
            instr.operand = OperandNone{};
            break;
    }

    return instr;
}

ConditionOperand ORBTReader::read_condition_operand() {
    ConditionOperand cond;
    cond.kind = static_cast<ConditionKind>(stream_.read_u8());

    switch (cond.kind) {
        case ConditionKind::Stack:
            // No additional data
            break;
        case ConditionKind::Binary:
            cond.comparison = stream_.read_u8();
            break;
        case ConditionKind::Expression:
        case ConditionKind::Block: {
            uint32_t count = stream_.read_u32();
            cond.embedded_data = stream_.read_bytes(count);
            break;
        }
        default:
            throw std::runtime_error("Unknown condition kind: " +
                std::to_string(static_cast<uint8_t>(cond.kind)));
    }

    return cond;
}

ExceptionHandlerOperand ORBTReader::read_exception_handler() {
    ExceptionHandlerOperand eh;

    uint32_t try_len = stream_.read_u32();
    eh.try_block = stream_.read_bytes(try_len);

    uint16_t catch_count = stream_.read_u16();
    eh.catch_records.reserve(catch_count);
    for (uint16_t i = 0; i < catch_count; i++) {
        CatchRecord cr;
        cr.type_index = stream_.read_u16();
        uint32_t body_len = stream_.read_u32();
        cr.body = stream_.read_bytes(body_len);
        eh.catch_records.push_back(std::move(cr));
    }

    uint8_t has_finally = stream_.read_u8();
    eh.has_finally = (has_finally != 0);
    if (eh.has_finally) {
        uint32_t finally_len = stream_.read_u32();
        eh.finally_block = stream_.read_bytes(finally_len);
    }

    return eh;
}

void ORBTReader::read_import_table(ORBTModule& mod) {
    uint16_t count = stream_.read_u16();
    mod.imports.reserve(count);
    for (uint16_t i = 0; i < count; i++) {
        ImportEntry entry;
        entry.module_index = stream_.read_u16();
        entry.symbol_index = stream_.read_u16();
        entry.kind         = static_cast<ImportKind>(stream_.read_u8());
        entry.flags        = stream_.read_u8();
        mod.imports.push_back(entry);
    }
}

void ORBTReader::read_export_table(ORBTModule& mod) {
    uint16_t count = stream_.read_u16();
    mod.exports.reserve(count);
    for (uint16_t i = 0; i < count; i++) {
        ExportEntry entry;
        entry.name_index   = stream_.read_u16();
        entry.kind         = static_cast<ImportKind>(stream_.read_u8());
        entry.local_index  = stream_.read_u32();
        entry.module_index = stream_.read_u16();
        mod.exports.push_back(entry);
    }
}

void ORBTReader::read_metadata_block(ORBTModule& mod) {
    // The metadata block is optional. First read block_length.
    // If 0, there's no metadata.
    uint16_t block_length = stream_.read_u16();
    if (block_length == 0) {
        mod.metadata = MetadataBlock{};
        return;
    }

    // Record the end position
    size_t end_pos = stream_.tell() + block_length;

    while (stream_.tell() < end_pos) {
        MetadataEntry entry;
        uint16_t key_index = stream_.read_u16();
        uint8_t value_kind = stream_.read_u8();

        // Resolve key from string pool
        if (key_index < mod.string_pool.size()) {
            entry.key = mod.string_pool.get(key_index);
        } else {
            entry.key = "<unknown>";
        }

        if (value_kind == 0x01) {
            // String value
            std::string val = stream_.read_string();
            entry.value = val;

            // Populate convenience fields
            if (entry.key == "spec") {
                mod.metadata.spec_version = val;
            }
        } else if (value_kind == 0x02) {
            // String list
            std::vector<std::string> list;
            uint16_t entry_count = stream_.read_u16();
            list.reserve(entry_count);
            for (uint16_t i = 0; i < entry_count; i++) {
                list.push_back(stream_.read_string());
            }
            entry.value = list;

            if (entry.key == "require") {
                mod.metadata.require = list;
            } else if (entry.key == "optional") {
                mod.metadata.optional = list;
            }
        } else {
            throw std::runtime_error("Unknown metadata value kind: " +
                std::to_string(value_kind));
        }

        mod.metadata.entries.push_back(std::move(entry));
    }
}

void ORBTReader::read_method_bodies(ORBTModule& mod) {
    // Method bodies are read inline as part of type/method records
    // Nothing more to do here - they were already read in read_type_table
}

// ============================================================================
// High-level convenience
// ============================================================================

std::unique_ptr<ORBTModule> read_orbt_file(const std::string& path) {
    BinaryStream stream(path);
    ORBTReader reader(stream);
    return std::make_unique<ORBTModule>(reader.read_module());
}

// ============================================================================
// Dump / Display
// ============================================================================

static std::string operand_to_string(const Operand& operand, const StringPool& pool) {
    return std::visit([&](const auto& arg) -> std::string {
        using T = std::decay_t<decltype(arg)>;
        if constexpr (std::is_same_v<T, OperandNone>) {
            return "";
        } else if constexpr (std::is_same_v<T, OperandI4>) {
            return std::to_string(arg.value);
        } else if constexpr (std::is_same_v<T, OperandI8>) {
            return std::to_string(arg.value);
        } else if constexpr (std::is_same_v<T, OperandR4>) {
            return std::to_string(arg.value);
        } else if constexpr (std::is_same_v<T, OperandR8>) {
            return std::to_string(arg.value);
        } else if constexpr (std::is_same_v<T, OperandString>) {
            if (arg.string_index < pool.size())
                return "\"" + pool.get(arg.string_index) + "\"";
            return "<string[" + std::to_string(arg.string_index) + "]>";
        } else if constexpr (std::is_same_v<T, OperandIndex>) {
            return std::to_string(arg.index);
        } else if constexpr (std::is_same_v<T, OperandFieldRef>) {
            if (arg.string_index < pool.size())
                return pool.get(arg.string_index);
            return "<field[" + std::to_string(arg.string_index) + "]>";
        } else if constexpr (std::is_same_v<T, OperandMethodRef>) {
            if (arg.string_index < pool.size())
                return pool.get(arg.string_index);
            return "<method[" + std::to_string(arg.string_index) + "]>";
        } else if constexpr (std::is_same_v<T, OperandTypeRef>) {
            if (arg.string_index < pool.size())
                return pool.get(arg.string_index);
            return "<type[" + std::to_string(arg.string_index) + "]>";
        } else if constexpr (std::is_same_v<T, OperandBranch>) {
            return "offset(" + std::to_string(arg.pc_offset) + ")";
        } else if constexpr (std::is_same_v<T, ConditionOperand>) {
            switch (arg.kind) {
                case ConditionKind::Stack: return "stack";
                case ConditionKind::Binary: return "binary(0x" + std::to_string(arg.comparison) + ")";
                case ConditionKind::Expression: return "expr(" + std::to_string(arg.embedded_data.size()) + " bytes)";
                case ConditionKind::Block: return "block(" + std::to_string(arg.embedded_data.size()) + " bytes)";
                default: return "<unknown condition>";
            }
        } else if constexpr (std::is_same_v<T, ExceptionHandlerOperand>) {
            return "try(" + std::to_string(arg.try_block.size()) + " bytes, "
                 + std::to_string(arg.catch_records.size()) + " catches"
                 + (arg.has_finally ? ", finally" : "")
                 + ")";
        } else {
            return "<operand>";
        }
    }, operand);
}

void ORBTModule::dump(std::ostream& os, bool verbose) const {
    // Header
    os << ";; ObjectRT ORBT Module\n";
    os << ";; File format: ORBT v" << static_cast<int>(format_version) << "\n";
    os << "module " << module_name
       << " version " << version.major << "." << version.minor << "." << version.patch << "\n\n";

    // Metadata
    if (!metadata.entries.empty() || !metadata.spec_version.empty()
        || !metadata.require.empty() || !metadata.optional.empty()) {
        os << ".metadata {\n";
        if (!metadata.spec_version.empty()) {
            os << "    spec objectrt = \"" << metadata.spec_version << "\"\n";
        }
        if (!metadata.require.empty()) {
            os << "    require [\n";
            for (const auto& f : metadata.require)
                os << "        " << f << ",\n";
            os << "    ]\n";
        }
        if (!metadata.optional.empty()) {
            os << "    optional [\n";
            for (const auto& f : metadata.optional)
                os << "        " << f << ",\n";
            os << "    ]\n";
        }
        os << "}\n\n";
    }

    // Imports
    if (!imports.empty()) {
        os << ";; Imports (" << imports.size() << ")\n";
        for (size_t i = 0; i < imports.size(); i++) {
            const auto& imp = imports[i];
            os << ";;   [" << i << "] "
               << resolve(imp.module_index) << "."
               << resolve(imp.symbol_index);
            if (imp.flags & 0x01) os << " (optional)";
            os << "\n";
        }
        os << "\n";
    }

    // Exports
    if (!exports.empty()) {
        os << ";; Exports (" << exports.size() << ")\n";
        for (size_t i = 0; i < exports.size(); i++) {
            const auto& exp = exports[i];
            os << ";;   [" << i << "] "
               << resolve(exp.module_index) << "."
               << resolve(exp.name_index)
               << " -> local[" << exp.local_index << "]\n";
        }
        os << "\n";
    }

    // Types
    for (const auto& type : types) {
        // Modifiers
        if (has_flag(type.flags, TypeFlags::Abstract)) os << "abstract ";
        if (has_flag(type.flags, TypeFlags::Sealed))   os << "sealed ";

        os << type_kind_name(type.kind) << " "
           << string_pool.get(type.name_index);

        if (type.base_type_index >= 0) {
            os << " : " << type_name(types[type.base_type_index]);
        }

        os << " {\n";

        // Fields
        for (const auto& field : type.fields) {
            os << "    " << access_name(type.access) << " field "
               << resolve(field.name_index) << ": "
               << resolve(field.type_index) << "\n";
        }

        // Methods
        for (const auto& method : type.methods) {
            os << "\n";
            os << "    " << access_name(method.access);
            if (has_flag(method.flags, MethodFlags::Static))   os << " static";
            if (has_flag(method.flags, MethodFlags::Virtual))  os << " virtual";
            if (has_flag(method.flags, MethodFlags::Override)) os << " override";
            if (has_flag(method.flags, MethodFlags::Abstract)) os << " abstract";
            os << " method " << string_pool.get(method.name_index) << "(";

            // Parameters
            for (size_t p = 0; p < method.params.size(); p++) {
                if (p > 0) os << ", ";
                os << resolve(method.params[p].name_index) << ": "
                   << resolve(method.params[p].type_index);
            }
            os << ")";

            // Signature
            if (method.signature_index < string_pool.size()) {
                os << " /* sig: " << string_pool.get(method.signature_index) << " */";
            }

            os << " {\n";

            // Locals
            for (const auto& local : method.locals) {
                os << "        local " << string_pool.get(local.name_index)
                   << ": " << string_pool.get(local.type_index) << "\n";
            }

            // Labels
            if (!method.labels.empty()) {
                os << "\n";
                for (const auto& label : method.labels) {
                    os << "        ;; label " << string_pool.get(label.name_index)
                       << " @ pc=" << label.pc_offset << "\n";
                }
            }

            // Instructions
            if (verbose) {
                os << "\n";
                for (const auto& instr : method.instructions) {
                    std::string operand_str = operand_to_string(instr.operand, string_pool);
                    os << "        " << opcode_name(instr.opcode);
                    if (!operand_str.empty()) {
                        os << " " << operand_str;
                    }
                    os << "\n";
                }
            } else {
                os << "        ;; " << method.instr_count << " instruction(s)\n";
            }

            os << "    }\n";
        }

        os << "}\n\n";
    }

    // String pool dump in verbose mode
    if (verbose && string_pool.size() > 0) {
        os << ";; String pool (" << string_pool.size() << " entries)\n";
        for (size_t i = 0; i < string_pool.size(); i++) {
            os << ";;   [" << i << "] \"" << string_pool.get(static_cast<uint16_t>(i)) << "\"\n";
        }
    }
}

} // namespace objectrt
