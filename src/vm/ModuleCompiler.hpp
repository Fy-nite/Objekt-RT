#pragma once
#ifndef OBJECTRT_MODULE_COMPILER_HPP
#define OBJECTRT_MODULE_COMPILER_HPP

#include "CompiledModule.hpp"
#include "../reader/Module.hpp"

#include <memory>
#include <vector>

namespace objectrt::vm {

// ============================================================================
// Compiles an ORBTModule (tooling representation) into a
// CompiledModule (VM-friendly representation).
//
// Resolution performed:
//   - Method references  → flat function table indices (uint32)
//   - Type references    → flat type table indices (uint16)
//   - Field references   → flat field table indices (uint16)
//   - String pool        → deduplicated, indexed string table
//   - Branch targets     → recomputed PC-relative offsets
//   - Max stack depth    → pre-computed per function
// ============================================================================

class ModuleCompiler {
public:
    ModuleCompiler();

    CompiledModule compile(const objectrt::ORBTModule& src);

private:
    // ── Resolution tables built during compilation ────────────────────
    struct ResolvedFunction {
        std::string full_name;     // "Type.Method"
        uint32_t     old_method_idx; // index into source TypeRecord.methods
        uint32_t     new_index;    // index into CompiledModule.functions
    };

    std::unordered_map<std::string, uint32_t> type_map_;     // "TypeName" → type index
    std::unordered_map<std::string, uint32_t> field_map_;    // "Type.field" → field index
    std::vector<ResolvedFunction>             resolved_funcs_;
    std::unordered_map<std::string, uint32_t> func_map_;     // "Type.Method" → function index

    // ── Per-function compilation state ───────────────────────────────
    struct CompileState {
        std::vector<uint8_t>     code;
        std::vector<uint32_t>    old_to_new_pc;
        uint32_t                 old_first_pc = 0;
        uint32_t                 max_stack_depth = 0;
        uint32_t                 current_depth = 0;
    };

    // ── Compilation helpers ──────────────────────────────────────────
    void build_resolution_tables(const objectrt::ORBTModule& src);
    CompiledFunction compile_method(
        const objectrt::ORBTModule& src,
        const objectrt::TypeRecord& type,
        const objectrt::MethodRecord& method,
        const std::string& full_name
    );
    void compile_into(
        CompileState& state,
        const objectrt::ORBTModule& src,
        const objectrt::Instruction& instr
    );

    // Encoding helpers
    void emit_u8(CompileState& s, uint8_t v);
    void emit_u16(CompileState& s, uint16_t v);
    void emit_u32(CompileState& s, uint32_t v);
    void emit_i32(CompileState& s, int32_t v);
    void emit_i64(CompileState& s, int64_t v);
    void emit_f32(CompileState& s, float v);
    void emit_f64(CompileState& s, double v);

    uint32_t resolve_old_pc(const CompileState& state, uint32_t old_pc) const;
    uint32_t compute_operand_size(const objectrt::Instruction& instr) const;
};

// ============================================================================
// Convenience
// ============================================================================

CompiledModule compile_module(const objectrt::ORBTModule& src);

} // namespace objectrt::vm

#endif // OBJECTRT_MODULE_COMPILER_HPP
