// )
#import "@preview/xyznote:0.5.0": *
#import "helpers.typ": *
#set text(font: "Fira Code", size: 11pt)
#show: xyznote.with(
  title: "ObjectRT Version 1",
  author: "charlie santana - Finite",
  abstract: "The official ObjectRT V1 spec.",
  createtime: "2024-11-27",
  lang: "au",
  bibliography-style: "ieee",
  preface: [
    
= What this document is

This document defines the ObjectRT Version 1 specification, including:

- the ObjectRT abstract machine and runtime model.
- the instruction set and execution semantics.
- type system and storage model.
- the required behaviour of conforming runtime implementations.

= What this document is not

This document does not specify:

- how a conforming implementation is written or engineered.
- optimisation strategies or heuristics.
- a source programming language or its syntax.
- development tools, debuggers, or profiling facilities.
- an intermediate representation for program transformation or analysis pipelines.
  ] //Annotate this line to delete the preface page.
)



#divider()
#linebreak()
#set heading(numbering: "1.")

#linebreak()

= Introduction

ObjectRT is a typed, object-oriented runtime instruction format designed
for the execution, interpretation, and dynamic translation of programs.
Originally conceived as the runtime side of the ObjectIR project, ObjectRT
splits away from the intermediate-representation layer to focus solely on
the concerns of a portable execution engine: a stack-machine instruction
set, a concrete type system, storage semantics, and a well-defined
runtime environment.

ObjectRT programs are not intended to be transformed, analysed, or
round-tripped through tooling pipelines — those concerns belong to the IR
layer. Instead, ObjectRT is the format that a virtual machine, interpreter,
or JIT compiler executes directly. It prioritises deterministic execution
semantics, a compact instruction encoding, and a clear boundary between
the runtime and the language-specific frontend that produces ObjectRT
code.

This specification defines the observable behaviour of ObjectRT programs
at runtime. It intentionally does not prescribe how an implementation
achieves that behaviour, allowing interpreters, virtual machines, just-in-
time compilers, and other execution engines to conform to the same
specification.


== Goals

ObjectRT core's primary goals are:

- Be a minimal, portable target for language frontends and compilers to emit.
- Define a complete runtime model with well-specified execution semantics.
- Support object-oriented programming semantics at the runtime level.
- Remain language and host-platform agnostic.
- Be deterministic and portable across implementations.
- Enable efficient interpretation, JIT compilation, and AOT compilation.
- Define runtime behaviour independently of any specific implementation.

== Use Cases

ObjectRT is designed for scenarios where a language runtime is needed:

- Implementing language runtimes, interpreters, and virtual machines.
- Serving as a compilation target for language frontends and compilers.
- Embedding executable program logic in applications and game engines.
- Visual scripting backends that require a well-defined runtime model.
- Serialising and deserialising executable code for distributed or dynamic systems.
- Educational runtime implementations and language prototyping.

= Formats
ObjectRT can come in many formats, most notably the ORBT binary format
(see `fob-encoding.typ`), the ObjectIL text format (see `ObjectIL.typ`),
and the JSON format.

= types

ObjectRT defines a fixed set of primitive types. Type names in operands
are #strong[case-insensitive];: conforming tooling must normalise them to
lower-case before lookup, so `System.String`, `system.string`, and
`string` are all equivalent.


#figure(
  align(center)[#table(
    columns: 3,
    align: (left,left,left,),
    table.header([#strong[Primary IR name(s)];], [#strong[Width /
      Semantics];], [],),
    table.hline(),
    [`void`], [No value; return type only.], [],
    [`bool`], [Boolean true/false.], [],
    [`int8`], [8-bit signed integer.], [],
    [`uint8`], [8-bit unsigned integer.], [],
    [`int16`], [16-bit signed integer.], [],
    [`uint16`], [16-bit unsigned integer.], [],
    [`int32`], [32-bit signed integer.], [],
    [`uint32`], [32-bit unsigned integer.], [],
    [`int64`], [64-bit signed integer.], [],
    [`uint64`], [64-bit unsigned integer.], [],
    [`float32`, `single`], [32-bit IEEE 754 float.], [],
    [`float64`, `double`], [64-bit IEEE 754 float.], [],
    [`char`], [Unicode scalar (UTF-16 unit).], [],
    [`string`], [Immutable Unicode character sequence.], [],
    [`object`], [Top type; any value.], [],
    [`decimal`], [High-precision fixed-point decimal.], [],
    [`datetime`], [Date and time instant.], [],
    [`timespan`], [Duration / time interval.], [],
    [`guid`], [128-bit globally unique identifier.], [],
  )]
  , kind: table
  )

= Instruction Set Reference

ObjectRT uses a stack machine execution model. Each method invocation
creates a new call frame with an evaluation stack of untyped values,
a named argument table, a local variable table, a pending exception slot,
and a program counter.

For the binary encoding of each instruction's opcode and more information, see the
separate *ORBT v1 Encoding Reference* (`fob-encoding.typ`).

== Stack Manipulation

#instruction(
  "nop",
  "---",
  "None",
  "No-operation; advances the program counter only."
)

#instruction(
  "dup",
  "v -> v, v",
  "Stack underflow",
  "Duplicate the top value on the evaluation stack."
)

#instruction(
  "pop",
  "v ->",
  "Stack underflow",
  "Discard the top value on the evaluation stack."
)

#instruction(
  "ldnull",
  "-> null",
  "None",
  "Push null onto the evaluation stack."
)

== Load Constants

#instruction(
  "ldc",
  "-> i32",
  "None",
  "Push a 32-bit integer constant onto the stack. Backward-compatible alias for `ldc.i4` (identical encoding and behaviour). Operand: bare integer."
)

#instruction(
  "ldc.i4",
  "-> i32",
  "None",
  "Push a 32-bit integer constant onto the stack. Operand: bare integer."
)

#instruction(
  "ldc.i8",
  "-> i64",
  "None",
  "Push a 64-bit integer constant onto the stack. Operand: bare integer."
)

#instruction(
  "ldc.r4",
  "-> f32",
  "None",
  "Push a 32-bit float constant onto the stack. Operand: bare number."
)

#instruction(
  "ldc.r8",
  "-> f64",
  "None",
  "Push a 64-bit float constant onto the stack. Operand: bare number."
)

#instruction(
  "ldstr",
  "-> string",
  "None",
  "Push a string constant onto the stack. Operand: \"...\""
)

== Arguments and Local Variables

#instruction(
  "ldarg",
  "-> value",
  "None",
  "Push a method argument onto the stack. Operand: parameter name or 0-based index. Index 0 / name 'this' is the receiver."
)

#instruction(
  "starg",
  "value ->",
  "Stack underflow",
  "Pop a value and store it in the named argument slot."
)

#instruction(
  "ldloc",
  "-> value",
  "None",
  "Push a local variable onto the stack by name."
)

#instruction(
  "stloc",
  "value ->",
  "Stack underflow",
  "Pop a value and store it in the named local variable."
)

== Field Access

#instruction(
  "ldfld",
  "obj -> value",
  "Null reference",
  "Pop an object (or use the implicit receiver) and push the value of the named instance field. If the object has a host backing, the runtime should read the corresponding property on the host object before consulting the field store."
)

#instruction(
  "stfld",
  "obj, value ->",
  "Null reference",
  "Pop a value, then pop an object; store the value in the named instance field (and on the host backing, if present)."
)

#instruction(
  "ldsfld",
  "-> value",
  "None",
  "Push the value of a static field. Operand: field name."
)

#instruction(
  "stsfld",
  "value ->",
  "Stack underflow",
  "Pop a value and store it in the named static field."
)

== Arithmetic

#instruction(
  "add",
  "a b -> result",
  "Stack underflow",
  "Pop b, pop a, push a + b. In float mode, also performs string concatenation when either operand is a string."
)

#instruction(
  "sub",
  "a b -> result",
  "Stack underflow",
  "Pop b, pop a, push a - b."
)

#instruction(
  "mul",
  "a b -> result",
  "Stack underflow",
  "Pop b, pop a, push a * b."
)

#instruction(
  "div",
  "a b -> result",
  "Stack underflow",
  "Pop b, pop a, push a / b."
)

#instruction(
  "rem",
  "a b -> result",
  "Stack underflow",
  "Pop b, pop a, push a mod b."
)

#instruction(
  "neg",
  "v -> result",
  "Stack underflow",
  "Pop v, push -v."
)

Mode selection: if either operand is floating-point (or a string for add),
both are promoted to 64-bit float; otherwise 64-bit signed integer
arithmetic is used.

== Logical

#instruction(
  "not",
  "v -> bool",
  "Stack underflow",
  "Pop v, push the boolean negation: !ToBool(v)."
)

#instruction(
  "and",
  "a b -> result",
  "Stack underflow",
  "Pop b, pop a, push a & b (bitwise AND). Integer operands only."
)

#instruction(
  "xor",
  "a b -> result",
  "Stack underflow",
  "Pop b, pop a, push a ^ b (bitwise XOR). Integer operands only."
)

#instruction(
  "or",
  "a b -> result",
  "Stack underflow",
  "Pop b, pop a, push a | b (bitwise OR). Integer operands only."
)

== Comparison

Each opcode pops b then a and pushes a bool.

#instruction(
  "ceq",
  "a b -> bool",
  "Stack underflow",
  "Push true if a == b, otherwise false."
)

#instruction(
  "cne",
  "a b -> bool",
  "Stack underflow",
  "Push true if a != b, otherwise false."
)

#instruction(
  "cgt",
  "a b -> bool",
  "Stack underflow",
  "Push true if a > b, otherwise false."
)

#instruction(
  "cge",
  "a b -> bool",
  "Stack underflow",
  "Push true if a >= b, otherwise false."
)

#instruction(
  "clt",
  "a b -> bool",
  "Stack underflow",
  "Push true if a < b, otherwise false."
)

#instruction(
  "cle",
  "a b -> bool",
  "Stack underflow",
  "Push true if a <= b, otherwise false."
)

String comparisons use ordinal (byte-by-byte) ordering. Boolean values are
supported for ceq and cne only. All other operand pairs are promoted to
64-bit float before comparison.

== Type Conversion and Testing

#instruction(
  "conv",
  "value -> converted",
  "Conversion failure",
  "Pop a value and push it converted to the named target type according to the coercion rules."
)

#instruction(
  "castclass",
  "obj -> obj",
  "InvalidCastError",
  "Assert that the top-of-stack managed object has the given type name; raise a cast error if it does not."
)

#instruction(
  "isinst",
  "value -> bool",
  "None",
  "Pop a value; push true if it is a managed object with the given type name, otherwise false."
)

== Type Objects

#instruction(
  "ldtype",
  "-> type",
  "TypeNotFoundError",
  "Push the type object for the named type onto the evaluation stack. Type objects are singletons — repeated loads of the same type yield the same object — and are instances of the built-in class `Class`. See §Type Objects and Reflection under Runtime Extensions."
)

== Object and Array Operations

#instruction(
  "newobj",
  "-> obj",
  "TypeNotFoundError",
  "Create a new managed object of the named type and push it. If the type is a registered host type, the runtime should also construct the corresponding host backing object."
)

#instruction(
  "newarr",
  "-> array",
  "None",
  "Push a new, empty array. Optionally with a type argument to create a typed array (e.g. newarr int)."
)

#instruction(
  "ldelem",
  "array index -> value",
  "IndexOutOfRange",
  "Pop an index (0-based integer), pop an array; push array[index]."
)

#instruction(
  "stelem",
  "array index value ->",
  "IndexOutOfRange",
  "Pop a value, pop an index, pop an array; set array[index] = value."
)

== Method Calls

#instruction(
  "call",
  "args... -> [result]",
  "MissingMethodError",
  "Pop arguments left-to-right, then pop the receiver (for instance methods); invoke the target method. Operand: \"Namespace.Type.Method(Args) -> ReturnType\"."
)

#instruction(
  "callvirt",
  "args... obj -> [result]",
  "MissingMethodError, NullReference",
  "Same as call, but dispatch is virtual: the actual type of the receiver determines which override is executed."
)

#instruction(
  "callnative",
  "args... -> [result]",
  "UnresolvedMethodError",
  "Like `call`, but resolution goes directly to the native handler, bypassing the module function map. Same bytecode encoding as `call`/`callvirt` (method-name string index + parameter count); kept as a backward-compatible alias for hosts that need to force native dispatch."
)

#instruction(
  "ret",
  "[value] ->",
  "None",
  "Return from the current frame. If a value is present on the evaluation stack it is transferred to the caller's stack."
)

== Control Flow

#instruction(
  "br",
  "->",
  "None",
  "Unconditional branch. Operand: signed offset (int32) relative to the end of this instruction. The offset is typically resolved from a label by the encoder."
)

#instruction(
  "brtrue",
  "value ->",
  "None",
  "Pop a value; branch if truthy. Operand: signed offset (int32) relative to the end of this instruction. The offset is taken if ToBool(value) is true."
)

#instruction(
  "brfalse",
  "value ->",
  "None",
  "Pop a value; branch if falsy. Operand: signed offset (int32) relative to the end of this instruction. The offset is taken if ToBool(value) is false."
)

#instruction(
  "if",
  "(condition-dependent)",
  "None",
  "Evaluate the condition. If truthy, execute the thenBlock; otherwise execute the optional elseBlock."
)

#instruction(
  "while",
  "(condition-dependent)",
  "None",
  "Repeatedly evaluate the condition and execute the body. A break inside the body exits the loop; a continue skips to the next condition evaluation."
)

#instruction(
  "break",
  "---",
  "None",
  "Exit the nearest enclosing while loop."
)

#instruction(
  "continue",
  "---",
  "None",
  "Skip to the next iteration of the nearest enclosing while loop."
)

#instruction(
  "try",
  "---",
  "None",
  "Execute the tryBlock. If an exception is raised, test each catchBlock in order. Execute the optional finallyBlock in all cases."
)

#instruction(
  "throw",
  "value -> (raises exception)",
  "None",
  "Pop a value and raise it as a pending exception on the current frame."
)


= Storage

The abstract machine provides a temporary storage area that is distinct
from the evaluation stack and local variables.

Temporary slots are addressed by index and are intended for transient
intermediate values. They do not form part of the program state and are
not visible outside the currently executing method.

= Module Loading

Implementations may support dynamic module loading (analogous to
`dlopen`). Modules are designed to be *joined*: the loader gathers all
import and export tables across loaded modules, resolves cross-references,
and merges everything into a single unified namespace. After loading, no
distinction exists between local and imported symbols — all existing
instructions (`call`, `newobj`, `ldfld`, etc.) work identically against
the merged module state.

Resolution proceeds as follows:

1. The loader collects the export tables of all currently loaded modules.
2. For each import entry in the newly loaded module, the loader searches
   the combined export tables for a matching `(module_index, name_index)`
   pair.
3. On match, the import entry is replaced with a direct reference to the
   resolved type, method, or field in the unified module state.
4. On mismatch, an optional import resolves to a null sentinel; a required
   import causes the load to fail.
5. Once all imports are resolved, the new module's types, methods, and
   fields are merged into the unified state. The instruction stream
   requires no patching — all operands already reference the correct
   indices after resolution.

= Runtime Extensions

A conforming runtime may extend the base ObjectRT model with additional
interoperability mechanisms. This section defines several optional
extensions that a runtime *may* implement.

== Type Annotations

Types may carry zero or more annotation records (`AttributeRecord`) attached
at parse time. An annotation consists of a name and an ordered list of
positional argument strings.

In the ObjectIL text format, annotations are written with the `@`
prefix immediately before the type keyword:

```
@DllImport("kernel32.dll")
class Kernel32 { ... }
```

Annotations are not instruction operands — they are declarative metadata
consumed by the runtime during module loading.

== Native Library Bindings (`@DllImport`)

A runtime *may* recognise the `@DllImport` annotation on type
declarations. Each `static method` within an `@DllImport`-annotated class
defines a binding to a native function exported by the named library.

The method signature in the declaration dictates the parameter and return
types expected at the native boundary. The runtime maps ObjectIR primitive
types to their host-ABI equivalents:

#figure(
  align(center)[#table(
    columns: 3,
    align: (left, left, left),
    table.header([#strong[IR type]], [#strong[C\# type]], [#strong[C/C++ type (Windows)]]),
    table.hline(),
    [`int32`], [`int`], [`int32_t` / `int`],
    [`uint32`], [`uint`], [`uint32_t`],
    [`int64`], [`long`], [`int64_t`],
    [`float32`], [`float`], [`float`],
    [`float64`], [`double`], [`double`],
    [`string`], [`string` (LPWStr)], [`const wchar_t*`],
    [`void`], [`void`], [`void`],
    [`bool`], [`bool`], [`BOOL` (int)],
    [`int8`], [`sbyte`], [`int8_t`],
  )],
  kind: table,
  caption: [Native ABI type mapping for `@DllImport` bindings],
)

The method body declared in the source is a placeholder — the runtime
bypasses it and dispatches directly to the native function. Every
`call QualifiedType.MethodName(...)` whose qualified type name matches
an `@DllImport`-annotated class resolves against the native binding table
before the module function map is consulted.

== Host Object Bindings (`@NativeBinding`)

A runtime *may* recognise the `@NativeBinding("name")` annotation on type
declarations. This marks the class as a logical namespace through which
scripts access methods on objects supplied by the host program at
registration time.

Unlike `@DllImport`, the method bodies are not linked to native exports
— the runtime dispatches calls through a host-provided object reference
that implements a matching interface contract. The binding name is the
first argument to the annotation and becomes the qualified prefix for
script calls:

```
@NativeBinding("MonoGame.Sprite")
class SpriteBindings {
    static method Draw(tex: string, x: float32, y: float32) -> void { ret }
}
```

Scripts call: `call MonoGame.Sprite.Draw(string, float32, float32)`.

This extension allows the same ObjectIL module to target different hosts
simply by changing which implementation objects are registered under well-
known binding names. The type metadata is self-describing — no external
configuration or C\# source generation is required for the binding contract
to be understood by a conforming runtime.

== Type Objects and Reflection

*Status: proposed extension. Documented to lock down the design before
the ORBT v2 format work; not yet implemented by any reference runtime.*

The module format already stores every type's metadata (`TypeRecord`) in
the type table. A *type object* (class object) is that metadata
materialised as a first-class runtime value, so a class can be referred to
as a value, passed around, and reflected upon — "class as a class".

The instruction `ldtype` (see §Type Objects in the Instruction Set
Reference) pushes the type object for the named type:

```
ldtype Program            // -> type object for Program
call Class.Name() -> string
call IO.Println(object) -> void
```

The model has three rules:

1. *Singletons.* There is exactly one type object per type per runtime.
   `ldtype` on the same type twice yields the same object (reference
   equal), so two loads of `Program` produce identical values.
2. *One metaclass.* Every type object is an instance of the fixed
   built-in class `Class`. There are no user-defined metaclasses and no
   metaclass hierarchy: `Class` is a leaf in the type system, and type
   objects are not themselves further reflected upon. This is the model
   .NET (`RuntimeType`) and Java (`Class`) converged on — first-class
   type values without Smalltalk's meta-meta recursion.
3. *Descriptor, not owner.* A type object is a *view* over the type's
   metadata. Static state remains in runtime static storage addressed by
   `ldsfld`/`stsfld`; the type object exposes static members but does not
   own them. (Making statics instance state of the class object — turning
   every `ldsfld` into `ldfld` on the class object — is deliberately out
   of scope; it requires a metaclass storage model this spec does not
   define.)

The built-in `Class` type exposes the following members (all non-static,
called on the type object):

#figure(
  align(center)[#table(
    columns: (32%, 68%),
    align: (left, left),
    table.header([#strong[Member]], [#strong[Description]]),
    table.hline(),
    [`Name() -> string`],       [Simple (unqualified) type name.],
    [`Namespace() -> string`],  [Namespace, or empty string.],
    [`BaseType() -> Class`],    [Type object of the base type, or null.],
    [`Kind() -> int32`],        [Type kind: 1 = class, 2 = interface, 3 = struct, 4 = enum.],
    [`IsAbstract() -> bool`],   [True if the type is abstract.],
    [`IsStatic() -> bool`],     [True if the type declares no instance members.],
    [`Fields() -> string[]`],   [Declared field names (instance and static).],
    [`Methods() -> string[]`],  [Declared method names with signatures.],
    [`Attributes() -> string[]`], [Annotation/attribute names on the type.],
    [`Invoke(name, args...) -> object`], [Dynamic invocation of a static member by name. Requires `Reflection.Full`.],
  )],
  kind: table,
  caption: [Members of the built-in Class type],
)

Type-object inspection requires the `Reflection.Basic` capability;
dynamic invocation via `Invoke` requires `Reflection.Full`. Modules that
use type objects should declare the capability they need in their
`.metadata` `require`/`optional` lists (see ObjectIL.typ, §Module
Metadata).

== Call Resolution with Extensions

When both `@DllImport` and `@NativeBinding` annotations are present in a
module, the runtime resolves a `call` opcode as follows:

1. Check the module function map (script-defined methods).
2. Check the native binding table (host-registered `@NativeBinding` types).
3. Check the DllImport table (native library exports).
4. Return an unresolved error to the caller.

This ordering ensures that module-local functions always take priority,
host-provided methods override library functions, and native exports
serve as the final fallback.