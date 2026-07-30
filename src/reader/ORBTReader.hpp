#pragma once
#ifndef ORBT_READER_HPP
#define ORBT_READER_HPP

#include "Module.hpp"
#include <string>
#include <vector>
#include <fstream>
#include <memory>

namespace objectrt {

// ============================================================================
// Binary stream reader helper
// ============================================================================

class BinaryStream {
public:
    explicit BinaryStream(const std::string& path);
    explicit BinaryStream(const std::vector<uint8_t>& data);

    // Read primitives in little-endian
    uint8_t   read_u8();
    uint16_t  read_u16();
    uint32_t  read_u32();
    int32_t   read_i32();
    int64_t   read_i64();
    uint64_t  read_u64();
    float     read_r4();
    double    read_r8();

    // Read a length-prefixed UTF-8 string (uint16 length + data)
    std::string read_string();

    // Read raw bytes
    std::vector<uint8_t> read_bytes(size_t count);

    // Position
    size_t tell() const { return pos_; }
    void seek(size_t pos) { pos_ = pos; }
    void skip(size_t count) { pos_ += count; }
    size_t size() const { return data_.size(); }
    bool eof() const { return pos_ >= data_.size(); }

private:
    std::vector<uint8_t> data_;
    size_t pos_ = 0;
};

// ============================================================================
// ORBT Binary Reader
// ============================================================================

class ORBTReader {
public:
    explicit ORBTReader(BinaryStream& stream);

    // Read a complete ORBT module
    ORBTModule read_module();

private:
    BinaryStream& stream_;

    // Section readers
    void read_header(ORBTModule& mod);
    void read_string_pool(ORBTModule& mod);
    void read_type_table(ORBTModule& mod);
    void read_import_table(ORBTModule& mod);
    void read_export_table(ORBTModule& mod);
    void read_metadata_block(ORBTModule& mod);
    void read_method_bodies(ORBTModule& mod);

    // Sub-readers
    MethodRecord read_method_record(const StringPool& pool);
    FieldRecord read_field_record();
    ParameterRecord read_param_record();
    LocalRecord read_local_record();
    LabelRecord read_label_record();
    Instruction read_instruction(const StringPool& pool, uint32_t pc);
    ConditionOperand read_condition_operand();
    ExceptionHandlerOperand read_exception_handler();
};

// ============================================================================
// High-level convenience
// ============================================================================

std::unique_ptr<ORBTModule> read_orbt_file(const std::string& path);

} // namespace objectrt

#endif // ORBT_READER_HPP
