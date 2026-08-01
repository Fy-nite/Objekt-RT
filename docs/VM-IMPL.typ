#import "@preview/xyznote:0.5.0": *

#set text(font: "Fira Code", size: 11pt)
#show: xyznote.with(
  title: "ObjectRT VM — Implementation Notes",
  author: "charlie santana - Finite",
  abstract: "Pluggable executors, Value struct, tag-aware arithmetic, call dispatch unification.",
  createtime: "2026-07-31",
  lang: "au",
)

#set heading(numbering: "1.")

= IExecutor Architecture

The VM supports pluggable execution backends through the `IExecutor` interface:

```csharp
IExecutor (interface)
├── RunFunction(uint idx, Value[] args) → Result<Value>
├── Run() → Result<Value>
├── Reset(clearHeap, clearStatics)
├── NativeCallHandler { get; set; }
├── InternString(string) → uint
├── MarshalValue(object?) → Value
└── ValueToObject(Value) → object?

ExecutorBase (abstract, shared state)
├── Heap, StaticFields, string table, marshaling, AllocObject()
│
├── Interpreter — iterative bytecode dispatch loop
└── ReflectionJit — bytecode → C# → Roslyn → delegates
```

All three backends (`Interpreter`, `ReflectionJit`, future `LLVMJit`) extend
`ExecutorBase`, which provides the shared heap, static fields, string table,
and value marshaling. The host wires a `NativeCallHandler` into the executor
before any script code runs; the `call` opcode delegates to it when a method
name is not found in the module's function map.

To add a new backend, extend `ExecutorBase`, implement `RunFunction` and
`Reset`, and register it in `Runtime.CreateExecutor()` under a `JitMode` variant.

== JitMode

```csharp
public enum JitMode { Interpreter = 0, Reflection = 1, LLVM = 2 }
```

Set `Runtime.Mode` before `LoadModule`. The `LLVM` slot is reserved for a
NativeAOT-safe compiled backend (LLVMSharp or similar).

= Value struct

`LayoutKind.Explicit, Size=16`. Tagged union: `Nil`, `I4` (int), `I8` (long),
`R4` (float), `R8` (double), `Obj` (heap handle), `Str` (interned string handle).

#strong[Critical]: `LayoutKind.Explicit` structs cannot hold object references
overlapping value fields — the CLR throws `TypeLoadException` at runtime.
Strings are interned handles in the executor's string table (`_stringMap` /
`_strings`), following the same handle pattern as heap objects.

= Tag-aware arithmetic

Arithmetic and comparison are tag-aware with int / long / float / double promotion:

- `Arith(a, b, opI4, opI8, opR4, opR8)` — same-tag pairs stay in their type;
  mixed pairs widen to `double`.
- `NumericCompare(a, b)` — returns -1, 0, or 1 per type-aware comparison.
- `IsTruthy()` — non-zero numeric, non-null string, non-nil object → true.
  Used by `brtrue`/`brfalse` and the `if`/`while` structured-flow stubs.

#strong[Bug note (2026-07)]: The original interpreter was int-only (`Pop().I4`
everywhere). A multi-file edit to upgrade arithmetic silently failed to apply
to the interpreter's dispatch cases. Reflection-based host dispatch auto-widened
`Int32 → Single`, masking the bug. The generated dispatcher's hard `(float)a[0]!`
cast exposed it immediately when the host binding system was switched to
generated-only dispatch. #strong[Always grep affected files after multi-edits.]

= Call opcode unification

`call`, `callvirt`, and `callnative` all share the same bytecode encoding:
#strong[U16 method-name string-pool index + U16 parameter count].

Resolution is deferred to runtime:
+ Look up the method name in the module function map (script function).
+ If not found, delegate to `NativeCallHandler` (host method).

All four layers were updated in lockstep for this change:
- `ObjectILParser.cs` — all three mnemonics parse identically
- `ModuleCompiler.cs` — passes name + count through (was U32 resolved index)
- `ORBTReader.cs` — decodes as `OperandNativeCall`
- `Interpreter.cs` — function-map lookup, then native fallback

Scripts use `call` for everything. `callnative` is kept as a backward-compat
alias with the same encoding and dispatch path.

= Source files

```
src/ObjectRT.VM/
├── IExecutor.cs         — pluggable executor interface
├── ExecutorBase.cs      — shared state (heap, statics, strings, marshal)
├── Interpreter.cs       — iterative bytecode dispatch (tag-aware arithmetic)
├── ReflectionJit.cs     — bytecode → C# → Roslyn → delegates, disk cache
├── Value.cs             — 16-byte tagged union
├── CompiledModule.cs    — flat VM module (types, fields, functions, strings)
├── ModuleCompiler.cs    — ORBTModule → CompiledModule
└── VmError.cs           — error kinds + Result<T> type
```
