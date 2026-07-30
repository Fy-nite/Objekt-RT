#pragma once
#ifndef OBJECTRT_INTERPRETER_HPP
#define OBJECTRT_INTERPRETER_HPP

#include "CompiledModule.hpp"
#include <cstdint>
#include <vector>
#include <string>
#include <iostream>

namespace objectrt::vm {

// ============================================================================
// Minimal stack-based interpreter for CompiledModule.
//
// The dispatch loop reads directly from the flat bytecode vector with a
// switch on raw opcode bytes — no decoded Instruction structs, no variant
// dispatch. This is the hot path.
//
// Value representation: 64-bit tagged union (simple boxed model).
// ============================================================================

enum class ValueTag : uint8_t {
    Nil     = 0,
    I4      = 1,
    I8      = 2,
    R4      = 3,
    R8      = 4,
    Obj     = 5,  // heap object handle (placeholder)
};

struct Value {
    ValueTag tag;
    union {
        int32_t  i4;
        int64_t  i8;
        float    r4;
        double   r8;
        uint64_t raw;
    };

    Value() : tag(ValueTag::Nil), raw(0) {}
    static Value from_i4(int32_t v)  { Value x; x.tag = ValueTag::I4; x.i4 = v; return x; }
    static Value from_i8(int64_t v)  { Value x; x.tag = ValueTag::I8; x.i8 = v; return x; }
    static Value from_r4(float v)    { Value x; x.tag = ValueTag::R4; x.r4 = v; return x; }
    static Value from_r8(double v)   { Value x; x.tag = ValueTag::R8; x.r8 = v; return x; }
    static Value nil()               { Value x; x.tag = ValueTag::Nil; x.raw = 0; return x; }
    static Value from_obj(uint32_t v){ Value x; x.tag = ValueTag::Obj; x.raw = v; return x; }
    uint32_t as_obj() const          { return static_cast<uint32_t>(raw); }
};

// ============================================================================
// Call frame
// ============================================================================

struct Frame {
    const CompiledFunction* func = nullptr;
    const uint8_t*          pc   = nullptr;       // program counter
    std::vector<Value>      locals;                // args + locals
    uint32_t                stack_base = 0;        // index into global stack
    uint32_t                ret_pc     = 0;        // return offset in caller
    uint32_t                ret_func   = 0;        // caller function index
};

// ============================================================================
// Interpreter
// ============================================================================

class Interpreter {
public:
    explicit Interpreter(const CompiledModule& mod);

    // Run from the module's entry point.
    // Returns the top-of-stack value (or nil for void).
    Value run();

    // Run a specific function by index.
    Value run_function(uint32_t func_idx);

    // Accessors
    const CompiledModule& module() const { return mod_; }
    void set_trace(bool t) { trace_ = t; }

private:
    const CompiledModule& mod_;
    bool trace_ = false;

    // Execution stack (global, grows up)
    std::vector<Value> stack_;

    // Call frame stack
    std::vector<Frame> frames_;

    // Heap — each object is a byte buffer sized by the type's instance_size
    std::vector<std::vector<uint8_t>> heap_;

    // Static field storage — one Value per module field
    std::vector<Value> static_fields_;

    // Execute from the current frame's PC (iterative dispatch)
    Value execute();

    // Allocate a new object of the given type index on the heap.
    // Returns the handle (index into heap_).
    uint32_t alloc_object(uint32_t type_idx);

    // Value stack operations
    void push(Value v) { stack_.push_back(v); }
    Value pop() {
        if (stack_.empty()) { std::cerr << "FATAL: stack underflow\n"; return Value::nil(); }
        Value v = stack_.back();
        stack_.pop_back();
        return v;
    }
    Value peek(int depth = 0) const {
        return stack_[stack_.size() - 1 - depth];
    }
};

} // namespace objectrt::vm

#endif // OBJECTRT_INTERPRETER_HPP
