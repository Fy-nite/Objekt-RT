#pragma once
#ifndef OBJECTRT_VM_ERROR_HPP
#define OBJECTRT_VM_ERROR_HPP

#include <cstdint>
#include <string>
#include <utility>
#include <iostream>
#include <cstdlib>

namespace objectrt::vm {

// ============================================================================
// VmErrorKind — exhaustive error taxonomy for the VM
// ============================================================================

enum class VmErrorKind : uint8_t {
    // Stack errors
    StackUnderflow,
    StackOverflow,

    // Type errors
    TypeMismatch,
    NotAnObject,
    InvalidValueTag,

    // Index / bounds errors
    InvalidFieldIndex,
    InvalidFunctionIndex,
    InvalidTypeIndex,
    InvalidStringIndex,
    InvalidObjectHandle,
    OutOfBounds,

    // Bytecode errors
    InvalidOpcode,
    MalformedBytecode,
    CodeOutOfBounds,

    // Arithmetic
    DivisionByZero,

    // Compile errors
    UnresolvedField,
    UnresolvedMethod,
    UnresolvedType,
    UnresolvedEntryPoint,
    DuplicateFunction,

    // Runtime errors
    FunctionNotFound,
    RuntimeError,
    InternalError,
};

inline const char* vm_error_kind_name(VmErrorKind kind) {
    switch (kind) {
        case VmErrorKind::StackUnderflow:        return "StackUnderflow";
        case VmErrorKind::StackOverflow:         return "StackOverflow";
        case VmErrorKind::TypeMismatch:          return "TypeMismatch";
        case VmErrorKind::NotAnObject:           return "NotAnObject";
        case VmErrorKind::InvalidValueTag:       return "InvalidValueTag";
        case VmErrorKind::InvalidFieldIndex:     return "InvalidFieldIndex";
        case VmErrorKind::InvalidFunctionIndex:  return "InvalidFunctionIndex";
        case VmErrorKind::InvalidTypeIndex:      return "InvalidTypeIndex";
        case VmErrorKind::InvalidStringIndex:    return "InvalidStringIndex";
        case VmErrorKind::InvalidObjectHandle:   return "InvalidObjectHandle";
        case VmErrorKind::OutOfBounds:           return "OutOfBounds";
        case VmErrorKind::InvalidOpcode:         return "InvalidOpcode";
        case VmErrorKind::MalformedBytecode:     return "MalformedBytecode";
        case VmErrorKind::CodeOutOfBounds:       return "CodeOutOfBounds";
        case VmErrorKind::DivisionByZero:        return "DivisionByZero";
        case VmErrorKind::UnresolvedField:       return "UnresolvedField";
        case VmErrorKind::UnresolvedMethod:      return "UnresolvedMethod";
        case VmErrorKind::UnresolvedType:        return "UnresolvedType";
        case VmErrorKind::UnresolvedEntryPoint:  return "UnresolvedEntryPoint";
        case VmErrorKind::DuplicateFunction:     return "DuplicateFunction";
        case VmErrorKind::FunctionNotFound:      return "FunctionNotFound";
        case VmErrorKind::RuntimeError:          return "RuntimeError";
        case VmErrorKind::InternalError:         return "InternalError";
        default:                                 return "Unknown";
    }
}

// ============================================================================
// VmError — rich error with kind, message, source location, and PC
// ============================================================================

struct VmError {
    VmErrorKind kind;
    std::string message;
    std::string location;   // function / caller name
    uint32_t    pc = 0;     // program counter (0 if N/A)

    VmError(VmErrorKind k, std::string msg)
        : kind(k), message(std::move(msg)) {}

    VmError(VmErrorKind k, std::string msg, std::string loc)
        : kind(k), message(std::move(msg)), location(std::move(loc)) {}

    VmError(VmErrorKind k, std::string msg, std::string loc, uint32_t pc_)
        : kind(k), message(std::move(msg)), location(std::move(loc)), pc(pc_) {}

    std::string to_string() const {
        std::string s = vm_error_kind_name(kind);
        s += ": " + message;
        if (!location.empty()) s += " (at " + location + ")";
        if (pc != 0) s += " [pc=" + std::to_string(pc) + "]";
        return s;
    }

    void print() const {
        std::cerr << "VM Error: " << to_string() << "\n";
    }
};

// ============================================================================
// Result<T, E> — Rust-style Result type
//
// Either holds a value of type T (Ok) or an error of type E (Err).
// Marked [[nodiscard]] so callers cannot silently ignore errors.
// ============================================================================

template<typename T, typename E = VmError>
class [[nodiscard]] Result {
    enum class State { Value, Error } state_;
    union Storage {
        T value;
        E error;
        Storage() {}
        ~Storage() {}
    } storage_;

    void destroy() {
        switch (state_) {
            case State::Value: storage_.value.~T(); break;
            case State::Error: storage_.error.~E(); break;
        }
    }

public:
    // Construct Ok
    Result(T val) : state_(State::Value) {
        new (&storage_.value) T(std::move(val));
    }

    // Construct Err
    Result(E err) : state_(State::Error) {
        new (&storage_.error) E(std::move(err));
    }

    ~Result() { destroy(); }

    // Move only (no copy — errors carry unique context)
    Result(Result&& other) noexcept : state_(other.state_) {
        switch (other.state_) {
            case State::Value:
                new (&storage_.value) T(std::move(other.storage_.value));
                break;
            case State::Error:
                new (&storage_.error) E(std::move(other.storage_.error));
                break;
        }
        other.state_ = State::Value;
        new (&other.storage_.value) T{};
    }

    Result(const Result&) = delete;
    Result& operator=(const Result&) = delete;
    Result& operator=(Result&&) = delete;

    // ── Queries ───────────────────────────────────────────────────────
    bool is_ok()  const { return state_ == State::Value; }
    bool is_err() const { return state_ == State::Error; }
    explicit operator bool() const { return is_ok(); }

    // ── Value access (panics on error — use after is_ok() check) ──────
    T& value() & {
        return storage_.value;
    }
    const T& value() const& {
        return storage_.value;
    }
    T&& value() && {
        return std::move(storage_.value);
    }

    // ── Error access ─────────────────────────────────────────────────
    E& error() & {
        return storage_.error;
    }
    const E& error() const& {
        return storage_.error;
    }
    E&& error() && {
        return std::move(storage_.error);
    }

    // ── Rust-style unwrap: panic (abort) on error ────────────────────
    T unwrap(const char* msg = "unwrap failed") {
        if (state_ == State::Error) {
            std::cerr << "VM PANIC: " << msg << "\n  " << storage_.error.to_string() << "\n";
            std::abort();
        }
        return std::move(storage_.value);
    }
};

// ============================================================================
// Macros for error propagation (Rust ? operator equivalent)
//
// Usage:
//   VM_TRY(expr);                        — check Result, return Err on failure
//   Value v; VM_TRY_ASSIGN(v, pop());    — unwrap Result into variable
// ============================================================================

#define VM_TRY(expr)                                                      \
    do {                                                                  \
        auto _vm_r_##__LINE__ = (expr);                                    \
        if (!_vm_r_##__LINE__) return std::move(_vm_r_##__LINE__).error(); \
    } while (0)

#define VM_TRY_ASSIGN(var, expr)                                           \
    do {                                                                   \
        auto _vm_r_##__LINE__ = (expr);                                     \
        if (!_vm_r_##__LINE__) return std::move(_vm_r_##__LINE__).error();  \
        (var) = std::move(_vm_r_##__LINE__).value();                        \
    } while (0)

} // namespace objectrt::vm

#endif // OBJECTRT_VM_ERROR_HPP
