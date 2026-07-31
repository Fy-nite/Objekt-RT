#pragma once
#ifndef OBJECTRT_COMPILED_MODULE_HPP
#define OBJECTRT_COMPILED_MODULE_HPP

#include <string>
#include <vector>
#include <cstdint>
#include <unordered_map>
#include <stdexcept>
#include <functional>

#include "Error.hpp"

namespace objectrt::vm {

// ============================================================================
// VM constants
// ============================================================================

// Each Value (tagged union) stored as a field slot occupies this many bytes
// in heap object data. Must match sizeof(Value) in Interpreter.hpp.
constexpr uint32_t kFieldSlotSize = 16;

// ============================================================================
// VM-friendly type IDs — mirrors reader enums without the baggage
// ============================================================================

enum class VMTypeKind : uint8_t {
    Class     = 0x01,
    Interface = 0x02,
    Struct    = 0x03,
    Enum      = 0x04,
};

// ============================================================================
// CompiledFunction — flat bytecode for one method
// ============================================================================

struct CompiledFunction {
    std::string              debug_name;       // human-readable, for stack traces
    std::vector<uint8_t>     code;             // flat opcode + immediate stream
    uint32_t                 num_params    = 0;
    uint32_t                 num_locals    = 0;
    uint32_t                 max_stack     = 0; // pre-computed max evaluation stack depth

    // Offset ranges in the module string table that this function's code
    // references via ldc/ldstr with inline uint16 indices.
    uint32_t                 string_start  = 0;
    uint32_t                 string_count  = 0;

    // Index within the module's function table so the VM can do fast
    // dispatch without name lookups.
    uint32_t                 self_index    = 0;
};

// ============================================================================
// VMType — minimal resolved type descriptor
// ============================================================================

struct VMType {
    std::string              debug_name;
    VMTypeKind               kind          = VMTypeKind::Class;
    int32_t                  base_type     = -1;   // index or -1
    uint32_t                 field_offset  = 0;    // first field in module field table
    uint32_t                 field_count   = 0;
    uint32_t                 method_offset = 0;    // first method in function table
    uint32_t                 method_count  = 0;
    uint32_t                 instance_size = 0;    // bytes (for allocation)
};

// ============================================================================
// VMField — field with resolved layout offset
// ============================================================================

struct VMField {
    std::string              debug_name;
    uint32_t                 type_index;   // index into module type table
    uint32_t                 offset;       // byte offset within instance
};

// ============================================================================
// CompiledModule — the whole thing, flat and cache-friendly
// ============================================================================

class CompiledModule {
public:
    // ── Tables (flat vectors, good locality) ──────────────────────────
    std::vector<VMType>      types;
    std::vector<VMField>     fields;
    std::vector<CompiledFunction> functions;
    std::vector<std::string> strings;   // shared string pool

    // ── Entry point ────────────────────────────────────────────────────
    uint32_t                 entry_function = UINT32_MAX;

    // ── Debug maps (not used at runtime, dropped in release) ───────────
    std::unordered_map<std::string, uint32_t> function_map;

    // ── Fast lookups (throw on failure — legacy) ──────────────────────
    uint32_t find_function(const std::string& name) const {
        auto it = function_map.find(name);
        if (it == function_map.end())
            throw std::runtime_error("Function not found: " + name);
        return it->second;
    }

    const CompiledFunction& get_function(uint32_t idx) const {
        return functions.at(idx);
    }

    const VMType& get_type(uint32_t idx) const {
        return types.at(idx);
    }

    const std::string& get_string(uint32_t idx) const {
        return strings.at(idx);
    }

    bool has_entry() const { return entry_function < functions.size(); }

    // ── Result-based lookups (Rust-style, no exceptions) ──────────────
    Result<uint32_t> try_find_function(const std::string& name) const {
        auto it = function_map.find(name);
        if (it == function_map.end())
            return VmError(VmErrorKind::FunctionNotFound,
                           "function not found: " + name);
        return it->second;
    }

    Result<std::reference_wrapper<const CompiledFunction>>
    try_get_function(uint32_t idx) const {
        if (idx >= functions.size())
            return VmError(VmErrorKind::InvalidFunctionIndex,
                           "function index " + std::to_string(idx) +
                           " out of bounds (" + std::to_string(functions.size()) + ")");
        return std::ref(functions[idx]);
    }

    Result<std::reference_wrapper<const VMType>>
    try_get_type(uint32_t idx) const {
        if (idx >= types.size())
            return VmError(VmErrorKind::InvalidTypeIndex,
                           "type index " + std::to_string(idx) +
                           " out of bounds (" + std::to_string(types.size()) + ")");
        return std::ref(types[idx]);
    }

    Result<std::reference_wrapper<const std::string>>
    try_get_string(uint32_t idx) const {
        if (idx >= strings.size())
            return VmError(VmErrorKind::InvalidStringIndex,
                           "string index " + std::to_string(idx) +
                           " out of bounds (" + std::to_string(strings.size()) + ")");
        return std::ref(strings[idx]);
    }

    // ── Validation ─────────────────────────────────────────────────────
    bool valid() const {
        if (types.empty() && functions.empty()) return false;
        if (entry_function >= functions.size()) return false;
        return true;
    }
};

} // namespace objectrt::vm

#endif // OBJECTRT_COMPILED_MODULE_HPP
