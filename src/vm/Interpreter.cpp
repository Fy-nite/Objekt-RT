#include "Interpreter.hpp"
#include "../reader/Module.hpp"  // for Opcode enum
#include <cstring>
#include <iostream>

namespace objectrt::vm {

// ============================================================================
// Constructor
// ============================================================================

Interpreter::Interpreter(const CompiledModule& mod)
    : mod_(mod)
    , static_fields_(mod.fields.size(), Value::nil())
{
    stack_.reserve(4096);
    frames_.reserve(256);
}

// ============================================================================
// Run helpers — push initial frame, then enter iterative dispatch
// ============================================================================

Value Interpreter::run() {
    if (!mod_.has_entry()) {
        std::cerr << "Interpreter: no entry point\n";
        return Value::nil();
    }
    stack_.clear();
    frames_.clear();
    return run_function(mod_.entry_function);
}

Value Interpreter::run_function(uint32_t func_idx) {
    const auto& func = mod_.get_function(func_idx);

    Frame frame;
    frame.func       = &func;
    frame.pc         = func.code.data();
    frame.stack_base = static_cast<uint32_t>(stack_.size());
    frame.locals.resize(func.num_params + func.num_locals + 1, Value::nil());
    frame.ret_func   = UINT32_MAX;
    frame.ret_pc     = 0;
    frames_.push_back(std::move(frame));

    return execute();
}

// ============================================================================
// Heap allocation
// ============================================================================

uint32_t Interpreter::alloc_object(uint32_t type_idx) {
    const auto& type = mod_.types[type_idx];
    std::vector<uint8_t> data(type.instance_size, 0);
    uint32_t handle = static_cast<uint32_t>(heap_.size());
    heap_.push_back(std::move(data));
    return handle;
}

// ============================================================================
// Iterative dispatch loop — reads raw bytecode directly.
// No decoded Instruction structs, no std::variant overhead.
//
// Call/Return are handled iteratively: Call pushes a new frame and the
// outer while loop picks it up naturally; Ret pops the frame and restores
// the caller's PC.
// ============================================================================

Value Interpreter::execute() {
    // Inline read helpers (sugar over raw bytecode access)
    auto r16 = [](const uint8_t* c, uint32_t& p) -> uint16_t {
        uint16_t v = static_cast<uint16_t>(c[p]) | (static_cast<uint16_t>(c[p+1]) << 8);
        p += 2; return v;
    };
    auto r32 = [](const uint8_t* c, uint32_t& p) -> uint32_t {
        uint32_t v = static_cast<uint32_t>(c[p])    | (static_cast<uint32_t>(c[p+1]) << 8) |
                     (static_cast<uint32_t>(c[p+2]) << 16) | (static_cast<uint32_t>(c[p+3]) << 24);
        p += 4; return v;
    };
    auto ri32 = [&](const uint8_t* c, uint32_t& p) -> int32_t { return static_cast<int32_t>(r32(c, p)); };
    auto ri64 = [&](const uint8_t* c, uint32_t& p) -> int64_t {
        uint64_t lo = r32(c, p), hi = r32(c, p);
        return static_cast<int64_t>(lo | (hi << 32));
    };
    auto rf32 = [&](const uint8_t* c, uint32_t& p) -> float {
        uint32_t bits = r32(c, p); float v; std::memcpy(&v, &bits, sizeof(v)); return v;
    };
    auto rf64 = [&](const uint8_t* c, uint32_t& p) -> double {
        uint64_t bits = static_cast<uint64_t>(r32(c, p)) | (static_cast<uint64_t>(r32(c, p)) << 32);
        double v; std::memcpy(&v, &bits, sizeof(v)); return v;
    };

    while (!frames_.empty()) {
        Frame& frame = frames_.back();
        const uint8_t* code = frame.func->code.data();
        size_t code_size    = frame.func->code.size();
        uint32_t pc         = static_cast<uint32_t>(frame.pc - code);

        while (pc < code_size) {
            if (trace_) {
                std::cout << "  [" << frame.func->debug_name << " " << pc << "] ";
            }

            uint8_t op = code[pc++];

            switch (static_cast<objectrt::Opcode>(op)) {

                // ── No-op ──────────────────────────────────────────────
                case objectrt::Opcode::Nop:
                    if (trace_) std::cout << "nop\n";
                    break;

                // ── Load constant (immediate follows opcode) ───────────
                case objectrt::Opcode::LdcI4:
                case objectrt::Opcode::Ldc: {
                    int32_t v = ri32(code, pc);
                    push(Value::from_i4(v));
                    if (trace_) std::cout << "ldc.i4 " << v << "\n";
                    break;
                }
                case objectrt::Opcode::LdcI8: {
                    int64_t v = ri64(code, pc);
                    push(Value::from_i8(v));
                    if (trace_) { std::cout << "ldc.i8 " << v << "\n"; }
                    break;
                }
                case objectrt::Opcode::LdcR4: {
                    float v = rf32(code, pc);
                    push(Value::from_r4(v));
                    if (trace_) { std::cout << "ldc.r4 " << v << "\n"; }
                    break;
                }
                case objectrt::Opcode::LdcR8: {
                    double v = rf64(code, pc);
                    push(Value::from_r8(v));
                    if (trace_) { std::cout << "ldc.r8 " << v << "\n"; }
                    break;
                }

                // ── Load string (uint16 string table index) ────────────
                case objectrt::Opcode::Ldstr: {
                    uint16_t si = r16(code, pc);
                    if (trace_) std::cout << "ldstr \"" << mod_.get_string(si) << "\"\n";
                    push(Value::from_i4(si));
                    break;
                }

                // ── Argument access (uint16 index) ─────────────────────
                case objectrt::Opcode::Ldarg: {
                    uint16_t idx = r16(code, pc);
                    push(frame.locals[idx]);
                    if (trace_) { std::cout << "ldarg " << idx << "\n"; }
                    break;
                }
                case objectrt::Opcode::Starg: {
                    uint16_t idx = r16(code, pc);
                    frame.locals[idx] = pop();
                    if (trace_) { std::cout << "starg " << idx << "\n"; }
                    break;
                }

                // ── Local variable access (uint16 index) ───────────────
                case objectrt::Opcode::Ldloc: {
                    uint16_t idx = r16(code, pc);
                    push(frame.locals[frame.func->num_params + idx]);
                    if (trace_) { std::cout << "ldloc " << idx << "\n"; }
                    break;
                }
                case objectrt::Opcode::Stloc: {
                    uint16_t idx = r16(code, pc);
                    frame.locals[frame.func->num_params + idx] = pop();
                    if (trace_) { std::cout << "stloc " << idx << "\n"; }
                    break;
                }

                // ── Arithmetic (stack machine, two pops → one push) ────
                case objectrt::Opcode::Add: { int32_t b=pop().i4, a=pop().i4; push(Value::from_i4(a+b)); if(trace_) std::cout<<"add\n"; break; }
                case objectrt::Opcode::Sub: { int32_t b=pop().i4, a=pop().i4; push(Value::from_i4(a-b)); if(trace_) std::cout<<"sub\n"; break; }
                case objectrt::Opcode::Mul: { int32_t b=pop().i4, a=pop().i4; push(Value::from_i4(a*b)); if(trace_) std::cout<<"mul\n"; break; }
                case objectrt::Opcode::Div: { int32_t b=pop().i4, a=pop().i4; push(Value::from_i4(b?a/b:0)); if(trace_) std::cout<<"div\n"; break; }
                case objectrt::Opcode::Rem: { int32_t b=pop().i4, a=pop().i4; push(Value::from_i4(b?a%b:0)); if(trace_) std::cout<<"rem\n"; break; }
                case objectrt::Opcode::Neg: { push(Value::from_i4(-pop().i4)); if(trace_) std::cout<<"neg\n"; break; }

                // ── Bitwise ────────────────────────────────────────────
                case objectrt::Opcode::And: { int32_t b=pop().i4, a=pop().i4; push(Value::from_i4(a&b)); if(trace_) std::cout<<"and\n"; break; }
                case objectrt::Opcode::Or:  { int32_t b=pop().i4, a=pop().i4; push(Value::from_i4(a|b)); if(trace_) std::cout<<"or\n"; break; }
                case objectrt::Opcode::Xor: { int32_t b=pop().i4, a=pop().i4; push(Value::from_i4(a^b)); if(trace_) std::cout<<"xor\n"; break; }
                case objectrt::Opcode::Not: { push(Value::from_i4(~pop().i4)); if(trace_) std::cout<<"not\n"; break; }

                // ── Comparison ─────────────────────────────────────────
                case objectrt::Opcode::Ceq: { int32_t b=pop().i4, a=pop().i4; push(Value::from_i4(a==b?1:0)); if(trace_) std::cout<<"ceq\n"; break; }
                case objectrt::Opcode::Cne: { int32_t b=pop().i4, a=pop().i4; push(Value::from_i4(a!=b?1:0)); if(trace_) std::cout<<"cne\n"; break; }
                case objectrt::Opcode::Cgt: { int32_t b=pop().i4, a=pop().i4; push(Value::from_i4(a>b?1:0)); if(trace_) std::cout<<"cgt\n"; break; }
                case objectrt::Opcode::Cge: { int32_t b=pop().i4, a=pop().i4; push(Value::from_i4(a>=b?1:0)); if(trace_) std::cout<<"cge\n"; break; }
                case objectrt::Opcode::Clt: { int32_t b=pop().i4, a=pop().i4; push(Value::from_i4(a<b?1:0)); if(trace_) std::cout<<"clt\n"; break; }
                case objectrt::Opcode::Cle: { int32_t b=pop().i4, a=pop().i4; push(Value::from_i4(a<=b?1:0)); if(trace_) std::cout<<"cle\n"; break; }

                // ── Stack manipulation ─────────────────────────────────
                case objectrt::Opcode::Dup:    { push(peek()); if(trace_) std::cout<<"dup\n"; break; }
                case objectrt::Opcode::Pop:    { pop(); if(trace_) std::cout<<"pop\n"; break; }
                case objectrt::Opcode::Ldnull: { push(Value::nil()); if(trace_) std::cout<<"ldnull\n"; break; }

                // ── Field access ────────────────────────────────────────
                case objectrt::Opcode::Ldfld: {
                    uint16_t fi = r16(code, pc);
                    if (fi >= mod_.fields.size()) {
                        std::cerr << "ERROR: ldfld invalid field index " << fi << "\n";
                        push(Value::nil()); break;
                    }
                    const auto& field = mod_.fields[fi];
                    Value obj = pop();
                    if (obj.tag != ValueTag::Obj) {
                        std::cerr << "ERROR: ldfld on non-object\n";
                        push(Value::nil()); break;
                    }
                    uint32_t h = obj.as_obj();
                    if (h >= heap_.size() || field.offset + sizeof(Value) > heap_[h].size()) {
                        std::cerr << "ERROR: ldfld out of bounds\n";
                        push(Value::nil()); break;
                    }
                    Value v;
                    std::memcpy(&v, &heap_[h][field.offset], sizeof(v));
                    push(v);
                    if (trace_) std::cout << "ldfld " << field.debug_name << "\n";
                    break;
                }
                case objectrt::Opcode::Stfld: {
                    uint16_t fi = r16(code, pc);
                    if (fi >= mod_.fields.size()) {
                        std::cerr << "ERROR: stfld invalid field index " << fi << "\n";
                        pop(); pop(); break;
                    }
                    const auto& field = mod_.fields[fi];
                    Value val = pop();
                    Value obj = pop();
                    if (obj.tag != ValueTag::Obj) {
                        std::cerr << "ERROR: stfld on non-object\n";
                        break;
                    }
                    uint32_t h = obj.as_obj();
                    if (h >= heap_.size() || field.offset + sizeof(Value) > heap_[h].size()) {
                        std::cerr << "ERROR: stfld out of bounds\n";
                        break;
                    }
                    std::memcpy(&heap_[h][field.offset], &val, sizeof(val));
                    if (trace_) std::cout << "stfld " << field.debug_name << "\n";
                    break;
                }
                case objectrt::Opcode::Ldsfld: {
                    uint16_t fi = r16(code, pc);
                    if (fi >= static_fields_.size()) {
                        std::cerr << "ERROR: ldsfld invalid static field index " << fi << "\n";
                        push(Value::nil()); break;
                    }
                    push(static_fields_[fi]);
                    if (trace_) std::cout << "ldsfld " << mod_.fields[fi].debug_name << "\n";
                    break;
                }
                case objectrt::Opcode::Stsfld: {
                    uint16_t fi = r16(code, pc);
                    if (fi >= static_fields_.size()) {
                        std::cerr << "ERROR: stsfld invalid static field index " << fi << "\n";
                        pop(); break;
                    }
                    static_fields_[fi] = pop();
                    if (trace_) std::cout << "stsfld " << mod_.fields[fi].debug_name << "\n";
                    break;
                }

                // ── Call (push new frame, dispatch continues in loop) ──
                case objectrt::Opcode::Call:
                case objectrt::Opcode::Callvirt: {
                    uint32_t fi = r32(code, pc);
                    if (fi >= mod_.functions.size()) {
                        std::cerr << "ERROR: invalid func idx " << fi << "\n";
                        return Value::nil();
                    }
                    if (trace_) std::cout << "call " << mod_.functions[fi].debug_name << "\n";

                    const auto& callee = mod_.functions[fi];
                    if (callee.code.empty()) {
                        push(Value::nil());
                        break;
                    }

                    Frame callee_frame;
                    callee_frame.func       = &callee;
                    callee_frame.pc         = callee.code.data();
                    callee_frame.stack_base = static_cast<uint32_t>(stack_.size());
                    callee_frame.locals.resize(callee.num_params + callee.num_locals + 1, Value::nil());
                    callee_frame.ret_func   = frame.func->self_index;
                    callee_frame.ret_pc     = pc;
                    frames_.push_back(std::move(callee_frame));

                    // Break to outer loop to enter the callee
                    goto next_frame;
                }

                // ── Return (pop frame, restore caller PC) ──────────────
                case objectrt::Opcode::Ret: {
                    Value retval = stack_.empty() ? Value::nil() : stack_.back();
                    if (!stack_.empty()) stack_.pop_back();

                    uint32_t ret_fi = frame.ret_func;
                    uint32_t ret_pc_val = frame.ret_pc;
                    frames_.pop_back();

                    if (frames_.empty()) {
                        if (trace_) std::cout << "ret (top-level)\n";
                        push(retval);
                        return retval;
                    }

                    push(retval);
                    frames_.back().pc = frames_.back().func->code.data() + ret_pc_val;

                    if (trace_) {
                        std::cout << "ret -> " << mod_.functions[ret_fi].debug_name
                                  << " @" << ret_pc_val << "\n";
                    }
                    goto next_frame;
                }

                // ── Branches (PC-relative int32 offset) ────────────────
                case objectrt::Opcode::Br: {
                    int32_t off = ri32(code, pc);
                    pc = static_cast<uint32_t>(static_cast<int32_t>(pc) + off);
                    if (trace_) std::cout << "br -> " << pc << "\n";
                    break;
                }
                case objectrt::Opcode::Brfalse: {
                    int32_t off = ri32(code, pc);
                    bool taken = (pop().i4 == 0);
                    if (taken) pc = static_cast<uint32_t>(static_cast<int32_t>(pc) + off);
                    if (trace_) std::cout << (taken ? "brfalse -> " : "brfalse (nope)")
                                          << "\n";
                    break;
                }
                case objectrt::Opcode::Brtrue: {
                    int32_t off = ri32(code, pc);
                    bool taken = (pop().i4 != 0);
                    if (taken) pc = static_cast<uint32_t>(static_cast<int32_t>(pc) + off);
                    if (trace_) std::cout << (taken ? "brtrue -> " : "brtrue (nope)")
                                          << "\n";
                    break;
                }

                // ── Object ops ───────────────────────────────────────────
                case objectrt::Opcode::Newobj: {
                    uint16_t ti = r16(code, pc);
                    if (ti >= mod_.types.size()) {
                        std::cerr << "ERROR: newobj invalid type index " << ti << "\n";
                        push(Value::nil()); break;
                    }
                    uint32_t handle = alloc_object(ti);
                    push(Value::from_obj(handle));
                    if (trace_) std::cout << "newobj " << mod_.types[ti].debug_name << "\n";
                    break;
                }
                case objectrt::Opcode::Newarr:  { r16(code, pc); push(Value::nil()); if(trace_) std::cout<<"newarr (stub)\n"; break; }
                case objectrt::Opcode::Ldelem:  { pop(); pop(); push(Value::nil()); if(trace_) std::cout<<"ldelem (stub)\n"; break; }
                case objectrt::Opcode::Stelem:  { pop(); pop(); pop(); if(trace_) std::cout<<"stelem (stub)\n"; break; }

                // ── Type ops (stubs) ───────────────────────────────────
                case objectrt::Opcode::Conv:      { r16(code, pc); if(trace_) std::cout<<"conv (stub)\n"; break; }
                case objectrt::Opcode::Castclass: { r16(code, pc); if(trace_) std::cout<<"castclass (stub)\n"; break; }
                case objectrt::Opcode::Isinst:    { r16(code, pc); if(trace_) std::cout<<"isinst (stub)\n"; break; }

                // ── Structured control flow (skip embedded blocks) ─────
                case objectrt::Opcode::If:
                case objectrt::Opcode::While: {
                    uint8_t ck = code[pc++];
                    if (ck == 0x01) pc++; // binary comparison byte
                    else if (ck >= 0x02) { uint32_t len = r32(code, pc); pc += len; }
                    if (trace_) std::cout << (op==0x19?"if":"while")<<" (skip)\n";
                    break;
                }
                case objectrt::Opcode::Try: {
                    uint32_t tl = r32(code, pc); pc += tl;
                    uint16_t cc = r16(code, pc);
                    for (uint16_t ci = 0; ci < cc; ci++) {
                        r16(code, pc); // type index
                        uint32_t bl = r32(code, pc); pc += bl;
                    }
                    if (code[pc++]) { uint32_t fl = r32(code, pc); pc += fl; }
                    if (trace_) std::cout << "try (skip)\n";
                    break;
                }
                case objectrt::Opcode::Throw:
                case objectrt::Opcode::Break:
                case objectrt::Opcode::Continue:
                    if (trace_) std::cout << (op==0x1E?"throw":op==0x1B?"break":"continue")<<"\n";
                    break;

                default:
                    if (trace_) std::cout << "??? (0x" << std::hex << (int)op << std::dec << ")\n";
                    break;
            }
        }

        // Function fell through without Ret — pop and continue
        if (!frames_.empty()) frames_.pop_back();

        next_frame:;
    }

    return Value::nil();
}

} // namespace objectrt::vm
