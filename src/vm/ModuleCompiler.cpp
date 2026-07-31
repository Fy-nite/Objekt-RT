#include "ModuleCompiler.hpp"
#include "../reader/Module.hpp"

#include <algorithm>
#include <cstring>
#include <iostream>
#include <sstream>
#include <unordered_set>

namespace objectrt::vm {

// ============================================================================
// Helpers
// ============================================================================

// Returns the full method name as "TypeName.MethodName".
static std::string method_full_name(
    const objectrt::ORBTModule& src,
    const objectrt::TypeRecord& type,
    const objectrt::MethodRecord& method)
{
    return src.resolve(type.name_index) + "." + src.resolve(method.name_index);
}

// Returns the full field name as "TypeName.fieldname".
static std::string field_full_name(
    const objectrt::ORBTModule& src,
    const objectrt::TypeRecord& type,
    const objectrt::FieldRecord& field)
{
    return src.resolve(type.name_index) + "." + src.resolve(field.name_index);
}

// ============================================================================
// Constructor
// ============================================================================

ModuleCompiler::ModuleCompiler() = default;

// ============================================================================
// Resolution tables — maps reader string pool names to flat indices
// ============================================================================

void ModuleCompiler::build_resolution_tables(const objectrt::ORBTModule& src) {
    type_map_.clear();
    field_map_.clear();
    resolved_funcs_.clear();
    func_map_.clear();

    // Types
    for (uint32_t ti = 0; ti < src.types.size(); ti++) {
        const auto& type = src.types[ti];
        std::string tname = src.resolve(type.name_index);
        type_map_[tname] = ti;

        // Fields
        for (uint32_t fi = 0; fi < type.fields.size(); fi++) {
            std::string fname = field_full_name(src, type, type.fields[fi]);
            field_map_[fname] = static_cast<uint32_t>(field_map_.size());
        }

        // Methods — collect for function table
        for (uint32_t mi = 0; mi < type.methods.size(); mi++) {
            ResolvedFunction rf;
            rf.full_name = method_full_name(src, type, type.methods[mi]);
            rf.old_method_idx = mi;
            rf.new_index = static_cast<uint32_t>(resolved_funcs_.size());
            func_map_[rf.full_name] = rf.new_index;
            resolved_funcs_.push_back(rf);
        }
    }
}

// ============================================================================
// Operand size in the NEW VM bytecode (for pass-1 PC computation)
// ============================================================================

uint32_t ModuleCompiler::compute_operand_size(const objectrt::Instruction& instr) const {
    switch (instr.opcode) {
        // No operand
        case objectrt::Opcode::Nop:
        case objectrt::Opcode::Add:
        case objectrt::Opcode::Sub:
        case objectrt::Opcode::Mul:
        case objectrt::Opcode::Div:
        case objectrt::Opcode::Rem:
        case objectrt::Opcode::Neg:
        case objectrt::Opcode::Ceq:
        case objectrt::Opcode::Cne:
        case objectrt::Opcode::Cgt:
        case objectrt::Opcode::Cge:
        case objectrt::Opcode::Clt:
        case objectrt::Opcode::Cle:
        case objectrt::Opcode::And:
        case objectrt::Opcode::Xor:
        case objectrt::Opcode::Or:
        case objectrt::Opcode::Not:
        case objectrt::Opcode::Dup:
        case objectrt::Opcode::Pop:
        case objectrt::Opcode::Ldnull:
        case objectrt::Opcode::Ret:
        case objectrt::Opcode::Break:
        case objectrt::Opcode::Continue:
        case objectrt::Opcode::Throw:
        case objectrt::Opcode::Ldelem:
        case objectrt::Opcode::Stelem:
            return 0;

        // 4-byte immediates
        case objectrt::Opcode::LdcI4:
        case objectrt::Opcode::Ldc:
        case objectrt::Opcode::LdcR4:
            return 4;

        // 8-byte immediates
        case objectrt::Opcode::LdcI8:
        case objectrt::Opcode::LdcR8:
            return 8;

        // 2-byte indices (arg, local, string, type, field)
        case objectrt::Opcode::Ldarg:
        case objectrt::Opcode::Starg:
        case objectrt::Opcode::Ldloc:
        case objectrt::Opcode::Stloc:
        case objectrt::Opcode::Ldstr:
        case objectrt::Opcode::Newobj:
        case objectrt::Opcode::Newarr:
        case objectrt::Opcode::Conv:
        case objectrt::Opcode::Castclass:
        case objectrt::Opcode::Isinst:
        case objectrt::Opcode::Ldfld:
        case objectrt::Opcode::Stfld:
        case objectrt::Opcode::Ldsfld:
        case objectrt::Opcode::Stsfld:
            return 2;

        // 4-byte references (function table indices)
        case objectrt::Opcode::Call:
        case objectrt::Opcode::Callvirt:
            return 4;

        // 4-byte branch offset
        case objectrt::Opcode::Br:
        case objectrt::Opcode::Brtrue:
        case objectrt::Opcode::Brfalse:
            return 4;

        // Structured — approximate as fixed size for PC computation
        // In practice these contain embedded sub-bytecode
        case objectrt::Opcode::If:
        case objectrt::Opcode::While:
            // 1 (kind) + possible embedded data
            return 5; // kind + uint32 size (rough)

        case objectrt::Opcode::Try:
            return 9; // rough: try_len + optional catch/finally

        default:
            return 0;
    }
}

// ============================================================================
// Encoding helpers
// ============================================================================

void ModuleCompiler::emit_u8(CompileState& s, uint8_t v) {
    s.code.push_back(v);
}

void ModuleCompiler::emit_u16(CompileState& s, uint16_t v) {
    s.code.push_back(static_cast<uint8_t>(v & 0xFF));
    s.code.push_back(static_cast<uint8_t>((v >> 8) & 0xFF));
}

void ModuleCompiler::emit_u32(CompileState& s, uint32_t v) {
    s.code.push_back(static_cast<uint8_t>(v & 0xFF));
    s.code.push_back(static_cast<uint8_t>((v >> 8) & 0xFF));
    s.code.push_back(static_cast<uint8_t>((v >> 16) & 0xFF));
    s.code.push_back(static_cast<uint8_t>((v >> 24) & 0xFF));
}

void ModuleCompiler::emit_i32(CompileState& s, int32_t v) {
    emit_u32(s, static_cast<uint32_t>(v));
}

void ModuleCompiler::emit_i64(CompileState& s, int64_t v) {
    emit_u32(s, static_cast<uint32_t>(v & 0xFFFFFFFF));
    emit_u32(s, static_cast<uint32_t>((static_cast<uint64_t>(v) >> 32) & 0xFFFFFFFF));
}

void ModuleCompiler::emit_f32(CompileState& s, float v) {
    uint32_t bits;
    std::memcpy(&bits, &v, sizeof(bits));
    emit_u32(s, bits);
}

void ModuleCompiler::emit_f64(CompileState& s, double v) {
    uint64_t bits;
    std::memcpy(&bits, &v, sizeof(bits));
    emit_i64(s, static_cast<int64_t>(bits));
}

// ============================================================================
// Compile one method
// ============================================================================

Result<CompiledFunction> ModuleCompiler::compile_method(
    const objectrt::ORBTModule& src,
    const objectrt::TypeRecord& type,
    const objectrt::MethodRecord& method,
    const std::string& full_name)
{
    CompiledFunction func;
    func.debug_name = full_name;
    func.num_params = method.param_count;
    func.num_locals = method.local_count;
    func.max_stack  = 0;

    CompileState state;

    // If there are no decoded instructions but there is raw bytecode,
    // pass it through directly (from ObjectIL parser's basic encoding).
    if (method.instructions.empty() && !method.raw_instruction_data.empty()) {
        func.code = method.raw_instruction_data;
        func.max_stack = 8;
        return func;
    }

    if (method.instructions.empty()) {
        // No body — emit just a ret
        emit_u8(state, static_cast<uint8_t>(objectrt::Opcode::Ret));
        func.code = std::move(state.code);
        return func;
    }

    // ── Pass 1: compute new PC for each old instruction ───────────────
    // Map: instruction index → new PC offset
    std::vector<uint32_t> new_pc_by_idx;
    new_pc_by_idx.reserve(method.instructions.size());
    uint32_t new_pc = 0;

    for (size_t i = 0; i < method.instructions.size(); i++) {
        new_pc_by_idx.push_back(new_pc);
        new_pc += 1 + compute_operand_size(method.instructions[i]);
    }
    uint32_t total_new_size = new_pc;

    // Build old PC → new PC mapping via instruction index
    auto old_pc_to_new = [&](uint32_t old_pc) -> uint32_t {
        // Binary search for the instruction at this old PC
        size_t lo = 0, hi = method.instructions.size();
        while (lo < hi) {
            size_t mid = lo + (hi - lo) / 2;
            if (method.instructions[mid].pc_offset < old_pc)
                lo = mid + 1;
            else
                hi = mid;
        }
        if (lo < method.instructions.size() && method.instructions[lo].pc_offset == old_pc)
            return new_pc_by_idx[lo];
        // If not found, it might be inside a structured block;
        // approximate by clamping
        if (lo >= method.instructions.size())
            return total_new_size;
        return new_pc_by_idx[lo];
    };

    // ── Pass 2: emit bytecode ─────────────────────────────────────────
    state.code.reserve(total_new_size);

    for (size_t i = 0; i < method.instructions.size(); i++) {
        const auto& instr = method.instructions[i];

        // Emit opcode byte (table 0 — single byte)
        uint8_t op = static_cast<uint8_t>(static_cast<uint16_t>(instr.opcode) & 0xFF);
        emit_u8(state, op);

        // Stack depth tracking (simple greedy approach)
        // Track net stack effect for max depth computation
        // This is a simplified heuristic; a full dataflow would be more accurate
        switch (instr.opcode) {
            case objectrt::Opcode::LdcI4:
            case objectrt::Opcode::LdcI8:
            case objectrt::Opcode::LdcR4:
            case objectrt::Opcode::LdcR8:
            case objectrt::Opcode::Ldc:
            case objectrt::Opcode::Ldstr:
            case objectrt::Opcode::Ldarg:
            case objectrt::Opcode::Ldloc:
            case objectrt::Opcode::Ldfld:
            case objectrt::Opcode::Ldsfld:
            case objectrt::Opcode::Ldnull:
            case objectrt::Opcode::Dup:
            case objectrt::Opcode::Newobj:
            case objectrt::Opcode::Newarr:
                state.current_depth++;
                break;
            case objectrt::Opcode::Starg:
            case objectrt::Opcode::Stloc:
            case objectrt::Opcode::Stfld:
            case objectrt::Opcode::Stsfld:
            case objectrt::Opcode::Pop:
            case objectrt::Opcode::Ret:
            case objectrt::Opcode::Throw:
                if (state.current_depth > 0) state.current_depth--;
                break;
            case objectrt::Opcode::Add:
            case objectrt::Opcode::Sub:
            case objectrt::Opcode::Mul:
            case objectrt::Opcode::Div:
            case objectrt::Opcode::Rem:
            case objectrt::Opcode::Ceq:
            case objectrt::Opcode::Cne:
            case objectrt::Opcode::Cgt:
            case objectrt::Opcode::Cge:
            case objectrt::Opcode::Clt:
            case objectrt::Opcode::Cle:
            case objectrt::Opcode::And:
            case objectrt::Opcode::Xor:
            case objectrt::Opcode::Or:
                // pop 2, push 1 → net -1
                state.current_depth--;
                break;
            case objectrt::Opcode::Neg:
            case objectrt::Opcode::Not:
                // pop 1, push 1 → net 0
                break;
            case objectrt::Opcode::Call:
            case objectrt::Opcode::Callvirt: {
                // Pop args, push return value (if not void)
                // For now, assume 1 arg → net 0, 0 args → net +1
                // A proper implementation reads the method signature
                if (state.current_depth > 0)
                    state.current_depth--; // rough: pop at least 1 arg
                break;
            }
            default:
                break;
        }
        if (state.current_depth > state.max_stack_depth)
            state.max_stack_depth = state.current_depth;

        // ── Emit operand ──────────────────────────────────────────────
        switch (instr.opcode) {
            // ── No operand ─────────────────────────────────────────────
            case objectrt::Opcode::Nop:
            case objectrt::Opcode::Add:
            case objectrt::Opcode::Sub:
            case objectrt::Opcode::Mul:
            case objectrt::Opcode::Div:
            case objectrt::Opcode::Rem:
            case objectrt::Opcode::Neg:
            case objectrt::Opcode::Ceq:
            case objectrt::Opcode::Cne:
            case objectrt::Opcode::Cgt:
            case objectrt::Opcode::Cge:
            case objectrt::Opcode::Clt:
            case objectrt::Opcode::Cle:
            case objectrt::Opcode::And:
            case objectrt::Opcode::Xor:
            case objectrt::Opcode::Or:
            case objectrt::Opcode::Not:
            case objectrt::Opcode::Dup:
            case objectrt::Opcode::Pop:
            case objectrt::Opcode::Ldnull:
            case objectrt::Opcode::Ret:
            case objectrt::Opcode::Break:
            case objectrt::Opcode::Continue:
            case objectrt::Opcode::Throw:
            case objectrt::Opcode::Ldelem:
            case objectrt::Opcode::Stelem:
                break;

            // ── Immediate values (passthrough) ────────────────────────
            case objectrt::Opcode::LdcI4:
            case objectrt::Opcode::Ldc: {
                auto val = std::get<objectrt::OperandI4>(instr.operand);
                emit_i32(state, val.value);
                break;
            }
            case objectrt::Opcode::LdcI8: {
                auto val = std::get<objectrt::OperandI8>(instr.operand);
                emit_i64(state, val.value);
                break;
            }
            case objectrt::Opcode::LdcR4: {
                auto val = std::get<objectrt::OperandR4>(instr.operand);
                emit_f32(state, val.value);
                break;
            }
            case objectrt::Opcode::LdcR8: {
                auto val = std::get<objectrt::OperandR8>(instr.operand);
                emit_f64(state, val.value);
                break;
            }

            // ── String constant (→ string table index) ────────────────
            case objectrt::Opcode::Ldstr: {
                auto val = std::get<objectrt::OperandString>(instr.operand);
                // Emit string table index (uint16)
                // The string table is built by the caller from all ldstr references
                emit_u16(state, val.string_index);
                break;
            }

            // ── Arg/local index (passthrough) ──────────────────────────
            case objectrt::Opcode::Ldarg:
            case objectrt::Opcode::Starg:
            case objectrt::Opcode::Ldloc:
            case objectrt::Opcode::Stloc: {
                auto val = std::get<objectrt::OperandIndex>(instr.operand);
                emit_u16(state, val.index);
                break;
            }

            // ── Field reference (→ field table index) ─────────────────
            case objectrt::Opcode::Ldfld:
            case objectrt::Opcode::Stfld:
            case objectrt::Opcode::Ldsfld:
            case objectrt::Opcode::Stsfld: {
                auto val = std::get<objectrt::OperandFieldRef>(instr.operand);
                // The old operand is a string pool index; look up the field table index
                std::string field_ref = src.resolve(val.string_index);
                auto it = field_map_.find(field_ref);
                if (it == field_map_.end()) {
                    if (state.error.empty())
                        state.error = "unresolved field '" + field_ref + "' in " + func.debug_name;
                    emit_u16(state, 0);
                } else {
                    emit_u16(state, static_cast<uint16_t>(it->second));
                }
                break;
            }

            // ── Method reference (→ function table index) ─────────────
            case objectrt::Opcode::Call:
            case objectrt::Opcode::Callvirt: {
                auto val = std::get<objectrt::OperandMethodRef>(instr.operand);
                std::string method_ref = src.resolve(val.string_index);
                auto it = func_map_.find(method_ref);
                if (it == func_map_.end()) {
                    if (state.error.empty())
                        state.error = "unresolved method '" + method_ref + "' in " + func.debug_name;
                    emit_u32(state, 0);
                } else {
                    emit_u32(state, it->second);
                }
                break;
            }

            // ── Object/array creation (→ type table index) ────────────
            case objectrt::Opcode::Newobj:
            case objectrt::Opcode::Newarr: {
                auto val = std::get<objectrt::OperandString>(instr.operand);
                std::string type_ref = src.resolve(val.string_index);
                auto it = type_map_.find(type_ref);
                if (it == type_map_.end()) {
                    if (state.error.empty())
                        state.error = "unresolved type '" + type_ref + "' in " + func.debug_name;
                    emit_u16(state, 0);
                } else {
                    emit_u16(state, static_cast<uint16_t>(it->second));
                }
                break;
            }

            // ── Type reference (→ type table index) ───────────────────
            case objectrt::Opcode::Conv:
            case objectrt::Opcode::Castclass:
            case objectrt::Opcode::Isinst: {
                auto val = std::get<objectrt::OperandTypeRef>(instr.operand);
                std::string type_ref = src.resolve(val.string_index);
                auto it = type_map_.find(type_ref);
                if (it == type_map_.end()) {
                    if (state.error.empty())
                        state.error = "unresolved type '" + type_ref + "' in " + func.debug_name;
                    emit_u16(state, 0);
                } else {
                    emit_u16(state, static_cast<uint16_t>(it->second));
                }
                break;
            }

            // ── Branch (recompute PC-relative offset) ─────────────────
            case objectrt::Opcode::Br:
            case objectrt::Opcode::Brtrue:
            case objectrt::Opcode::Brfalse: {
                auto val = std::get<objectrt::OperandBranch>(instr.operand);
                // Compute old target PC
                // Old instruction size: 1 (opcode) + 4 (branch operand) = 5
                uint32_t old_instr_size = 5;
                uint32_t old_target = instr.pc_offset + old_instr_size + val.pc_offset;

                // Look up new target PC
                uint32_t new_target = old_pc_to_new(old_target);

                // New branch is at state.code.size() - 1 (we just emitted opcode)
                uint32_t new_branch_pc = new_pc_by_idx[i];
                uint32_t new_instr_size = 1 + 4; // opcode + int32
                int32_t new_offset = static_cast<int32_t>(new_target - (new_branch_pc + new_instr_size));
                emit_i32(state, new_offset);
                break;
            }

            // ── Structured control flow ───────────────────────────────
            case objectrt::Opcode::If:
            case objectrt::Opcode::While: {
                const auto& cond = std::get<objectrt::ConditionOperand>(instr.operand);
                emit_u8(state, static_cast<uint8_t>(cond.kind));
                if (cond.kind == objectrt::ConditionKind::Binary) {
                    emit_u8(state, cond.comparison);
                } else if (cond.kind == objectrt::ConditionKind::Expression ||
                           cond.kind == objectrt::ConditionKind::Block) {
                    emit_u32(state, static_cast<uint32_t>(cond.embedded_data.size()));
                    state.code.insert(state.code.end(),
                        cond.embedded_data.begin(), cond.embedded_data.end());
                }
                break;
            }

            case objectrt::Opcode::Try: {
                const auto& eh = std::get<objectrt::ExceptionHandlerOperand>(instr.operand);
                emit_u32(state, static_cast<uint32_t>(eh.try_block.size()));
                state.code.insert(state.code.end(),
                    eh.try_block.begin(), eh.try_block.end());
                emit_u16(state, static_cast<uint16_t>(eh.catch_records.size()));
                for (const auto& cr : eh.catch_records) {
                    emit_u16(state, cr.type_index);
                    emit_u32(state, static_cast<uint32_t>(cr.body.size()));
                    state.code.insert(state.code.end(),
                        cr.body.begin(), cr.body.end());
                }
                emit_u8(state, eh.has_finally ? 1 : 0);
                if (eh.has_finally) {
                    emit_u32(state, static_cast<uint32_t>(eh.finally_block.size()));
                    state.code.insert(state.code.end(),
                        eh.finally_block.begin(), eh.finally_block.end());
                }
                break;
            }

            default:
                break;
        }
    }

    if (!state.error.empty())
        return VmError(VmErrorKind::UnresolvedField, state.error, func.debug_name);

    func.code = std::move(state.code);
    func.max_stack = state.max_stack_depth + 8; // pad for safety
    return func;
}

// ============================================================================
// Main compilation entry point
// ============================================================================

Result<CompiledModule> ModuleCompiler::compile(const objectrt::ORBTModule& src) {
    CompiledModule mod;

    // 1. Build name→index resolution tables
    build_resolution_tables(src);

    // 2. Compile types (produce VMType + VMField records)
    mod.types.reserve(src.types.size());
    mod.fields.reserve(field_map_.size());
    mod.functions.reserve(resolved_funcs_.size());
    mod.function_map.reserve(resolved_funcs_.size());

    struct PendingField {
        std::string name;
        uint32_t type_idx;
        uint32_t declaring_type;
    };
    std::vector<PendingField> pending_fields;
    uint32_t field_idx = 0;

    for (uint32_t ti = 0; ti < src.types.size(); ti++) {
        const auto& src_type = src.types[ti];

        VMType vmt;
        vmt.debug_name = src.resolve(src_type.name_index);
        vmt.kind = static_cast<VMTypeKind>(static_cast<uint8_t>(src_type.kind));
        vmt.base_type = src_type.base_type_index;
        vmt.field_offset = field_idx;
        vmt.field_count = src_type.field_count;
        vmt.method_offset = 0; // set below
        vmt.method_count = src_type.method_count;
        vmt.instance_size = src_type.field_count * kFieldSlotSize;

        // Find method offset in the function table
        for (uint32_t mi = 0; mi < src_type.methods.size(); mi++) {
            std::string fname = method_full_name(src, src_type, src_type.methods[mi]);
            auto it = func_map_.find(fname);
            if (it != func_map_.end()) {
                if (mi == 0) vmt.method_offset = it->second;
            }
        }

        mod.types.push_back(std::move(vmt));

        // Collect fields
        for (uint32_t fi = 0; fi < src_type.fields.size(); fi++) {
            const auto& src_field = src_type.fields[fi];
            std::string fname = field_full_name(src, src_type, src_field);

            VMField vmf;
            vmf.debug_name = src.resolve(src_field.name_index);
            vmf.type_index = 0; // TODO: resolve field type
            vmf.offset = fi * kFieldSlotSize;

            mod.fields.push_back(std::move(vmf));
            field_idx++;
        }
    }

    // 3. Compile functions
    for (size_t ri = 0; ri < resolved_funcs_.size(); ri++) {
        const auto& rf = resolved_funcs_[ri];
        bool found = false;

        for (const auto& src_type : src.types) {
            for (uint32_t mi = 0; mi < src_type.methods.size(); mi++) {
                std::string fname = method_full_name(src, src_type, src_type.methods[mi]);
                if (fname == rf.full_name) {
                    auto cf_result = compile_method(src, src_type, src_type.methods[mi], rf.full_name);
                    if (!cf_result)
                        return VmError(VmErrorKind::UnresolvedField,
                                       "compilation of '" + rf.full_name + "' failed: " +
                                       cf_result.error().message,
                                       rf.full_name);
                    CompiledFunction cf = std::move(cf_result).value();
                    cf.self_index = rf.new_index;
                    mod.functions.push_back(std::move(cf));
                    mod.function_map[rf.full_name] = rf.new_index;
                    found = true;
                    break;
                }
            }
            if (found) break;
        }

        if (!found) {
            // Fallback: empty function that just returns
            CompiledFunction cf;
            cf.debug_name = rf.full_name;
            cf.self_index = rf.new_index;
            cf.code.push_back(static_cast<uint8_t>(objectrt::Opcode::Ret));
            mod.functions.push_back(std::move(cf));
            mod.function_map[rf.full_name] = rf.new_index;
        }
    }

    // 4. Copy string table from source
    mod.strings = src.string_pool.strings;

    // 5. Set entry point
    std::string entry_name = src.module_name + ".Main";
    auto entry_it = mod.function_map.find(entry_name);
    if (entry_it != mod.function_map.end()) {
        mod.entry_function = entry_it->second;
    } else {
        // Try just "Main"
        entry_it = mod.function_map.find("Main");
        if (entry_it != mod.function_map.end()) {
            mod.entry_function = entry_it->second;
        } else if (!mod.functions.empty()) {
            mod.entry_function = 0; // first function
        }
    }

    return mod;
}

// ============================================================================
// Convenience wrapper
// ============================================================================

Result<CompiledModule> compile_module(const objectrt::ORBTModule& src) {
    ModuleCompiler compiler;
    return compiler.compile(src);
}

} // namespace objectrt::vm
