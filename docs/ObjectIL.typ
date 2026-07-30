// )
#import "@preview/xyznote:0.5.0": *
// #import "helpers.typ": *
#set text(font: "Fira Code", size: 11pt)
#show: xyznote.with(
  title: "ObjectIL Version 1",
  author: "charlie santana - Finite",
  abstract: "The ObjectIL text format specification.",
  createtime: "2026-07-29",
  lang: "au",
  bibliography-style: "ieee",
  preface: [
    
= What this document is

This document defines the ObjectIL Version 1 text serialisation format,
including:

- the complete grammar and lexical rules.
- module, type, and member declaration syntax.
- instruction operand formatting conventions.
- the method reference syntax for call sites.
- the relationship between ObjectIL and the ObjectRT runtime.

= What this document is not

This document does not specify:

- instruction execution semantics — those are defined in the ObjectRT V1
  specification (`ObjectRT.typ`).
- the binary encoding of ObjectRT modules — see the ORBT V1 Encoding
  Reference (`fob-encoding.typ`).
- how a conforming parser or runtime is implemented.
  ] //Annotate this line to delete the preface page.
)



#divider()
#linebreak()
#set heading(numbering: "1.")

#linebreak()

= Introduction

ObjectIL (Object *Instruction Language*) is the text serialisation format
for ObjectRT modules. It is a human-readable representation of the
stack-machine bytecode that the ObjectRT runtime executes directly.

ObjectIL is *not* an intermediate representation — it is not designed for
program transformation, analysis, or round-tripping through optimisation
pipelines. Those concerns belong to the ObjektIR project. ObjectIL is the
format you write when targeting the ObjectRT runtime by hand, and the
canonical text interchange format for ObjectRT tooling.

The ObjectRT runtime specification (`ObjectRT.typ`) defines the semantics
of every instruction and the behaviour of the abstract machine. This
document defines the concrete syntax used to express those instructions
as text.

ObjectIL files conventionally use the `.oil` extension.

= Lexical Structure

== Whitespace and Newlines

Whitespace (spaces and tabs) separates tokens. Newlines terminate
instruction lines but are insignificant elsewhere — braces, parentheses,
and keywords are not newline-sensitive.

== Comments

A comment begins with `//` and extends to the end of the line. Comments
are treated as whitespace.

```example
// This is a comment
ldc.i4 42  // inline comment
```

== Identifiers

Identifiers are case-sensitive and may contain letters, digits,
underscores, dots (`.`), backticks (\`\`), and angle brackets (`<`, `>`).
Dots separate namespace and type qualifiers. Angle brackets denote generic
type parameters in type references.

Valid identifiers:
```
Program
System.String
List`1
List<DebugInfo>
Item.value
IO.Println
```

== Keywords

The following identifiers are reserved and may not be used as user-defined
names:

#figure(
  align(center)[#table(
    columns: 4,
    align: (left, left, left, left),
    table.header(
      [#strong[Keyword]], [#strong[]], [#strong[]], [#strong[]],
    ),
    table.hline(),
    [`module`],  [`version`],  [`class`],     [`interface`],
    [`struct`],  [`enum`],     [`field`],      [`method`],
    [`constructor`], [`local`], [`static`],    [`virtual`],
    [`override`], [`abstract`], [`private`],   [`public`],
    [`protected`], [`internal`], [`if`],       [`else`],
    [`while`],   [`break`],    [`continue`],   [`try`],
    [`catch`],   [`finally`],  [`throw`],      [`for`],
    [`return`],  [`implements`], [`in`],       [`with`],
    [`stack`],   [`true`],     [`false`],      [`null`],
    [`metadata`], [`spec`],    [`require`],    [`optional`],
  )],
  kind: table,
  caption: [ObjectIL reserved keywords],
)

Unlisted keywords in the table above are reserved for future versions and
must not be used as identifiers in v1. A conforming parser must reject
programs that use keywords as identifiers.

== String Literals

String literals are enclosed in double quotes (`"..."`) and support the
following escape sequences:

#figure(
  align(center)[#table(
    columns: 2,
    align: (left, left),
    table.header([#strong[Sequence]], [#strong[Meaning]]),
    table.hline(),
    [`\"`],  [Literal double quote],
    [`\\`],  [Literal backslash],
    [`\n`],  [Newline (U+000A)],
    [`\r`],  [Carriage return (U+000D)],
    [`\t`],  [Tab (U+0009)],
  )],
  kind: table,
  caption: [String escape sequences],
)

== Numeric Literals

Integer literals are sequences of digits optionally preceded by `-`.
Floating-point literals contain a decimal point (`.`) optionally followed
by fractional digits.

```
42       // int32 literal
-7       // negative integer
3.14     // float literal
2.5      // float literal
```

== Type Names

Type names follow the identifier rules and may be fully qualified (e.g.
`System.String`, `IO.Println`). Primitive type names are case-insensitive
when used as operands — `int32`, `Int32`, and `INT32` are equivalent.

The complete set of primitive types is defined in the ObjectRT V1
specification (`ObjectRT.typ`, §Types).

= Module Structure

An ObjectIL source file represents a single module. Every file must begin
with a `module` declaration.

== Grammar

```
module <identifier> version <major>.<minor>.<patch>
<type-declaration>*
```

- `<identifier>` is the module name.
- `<major>`, `<minor>`, `<patch>` are non-negative integers forming the
  version triple.

A file must contain exactly one `module` declaration, and it must be the
first non-comment token sequence in the file.

=== Example

```
module MyProgram version 1.0.0

.metadata {
    spec objectrt = "1.0"

    require [
        Memory.Managed,
        Reflection.Basic,
    ]

    optional [
        JIT.Optimizing,
        SIMD.Vector128,
    ]
}

class Program {
    static method Main() -> void {
        ret
    }
}
```

= Module Metadata

A module may declare a `.metadata` block between the `module` declaration
and the first type declaration. The metadata block communicates version
targeting and runtime capability requirements.

== Grammar

```
.metadata {
    spec objectrt = "<version-string>"

    require [ <feature>, ... ]
    optional [ <feature>, ... ]
}
```

All metadata fields are optional. If `.metadata` is absent, the runtime
assumes `spec objectrt = "1.0"` and no required or optional features.

=== spec

```
spec objectrt = "1.0"
```

The `spec` field declares the ObjectRT specification version that this
module targets. A conforming runtime must compare this value against its
own supported version range and may reject the module if the version is
incompatible. The version string follows semantic versioning
(`<major>.<minor>`).

=== require

```
require [
    Memory.Managed,
    Reflection.Basic,
]
```

The `require` field lists runtime features that the module depends on. A
conforming runtime must provide **all** features in this list, or fail to
load the module. Each feature is a dot-separated path into a hierarchical
feature namespace.

=== optional

```
optional [
    JIT.Optimizing,
    SIMD.Vector128,
]
```

The `optional` field lists runtime features that the module *may* take
advantage of if available. A runtime that does not provide an optional
feature must still be able to execute the module correctly, though
possibly with degraded performance or capability.

== Feature Namespace

The following feature paths are defined in ObjectIL v1:

#figure(
  align(center)[#table(
    columns: 2,
    align: (left, left),
    table.header([#strong[Feature]], [#strong[Description]]),
    table.hline(),
    [`Memory.Managed`],     [Runtime-managed memory with garbage collection.],
    [`Memory.Unmanaged`],   [Manual memory allocation and deallocation.],
    [`Reflection.None`],    [No reflection capabilities; type metadata is absent at runtime.],
    [`Reflection.Basic`],   [Type inspection: enumerate fields, methods, and their signatures at runtime.],
    [`Reflection.Full`],    [Type inspection + dynamic dispatch and member invocation by name.],
    [`JIT.Optimizing`],     [JIT compilation with optimisation passes beyond naive translation.],
    [`SIMD.Vector128`],     [128-bit SIMD operations (SSE, NEON equivalents).],
    [`SIMD.Vector256`],     [256-bit SIMD operations (AVX equivalents).],
    [`Threading.Basic`],    [Thread creation and synchronisation primitives.],
    [`Exception.Detailed`], [Rich exception metadata: source line numbers, function call chains, and formatted stack traces.],
    [`Debug.Symbols`],      [Debug information preserved in the module for source-level debugging.],
  )],
  kind: table,
  caption: [ObjectIL v1 feature namespace],
)

Implementations may define additional feature paths outside this list.
Unknown features in `require` or `optional` must be accepted and ignored
by conforming parsers.

= Type Declarations

Types are declared with an optional set of modifiers followed by a `class`
keyword (or `interface`, `struct`, `enum` — reserved in v1).

== Grammar

```
[abstract] [sealed] class <identifier>
    [implements <type-list>] {
    <member-declaration>*
}
```

- `abstract` — the class cannot be instantiated directly.
- `sealed` — the class cannot be inherited from.
- `implements` — followed by a comma-separated list of interface type
  names (reserved in v1).
- `interface`, `struct`, and `enum` are reserved as type kind keywords
  but their full syntax is not defined in v1.

== Examples

```
class Program {
    // ...
}

abstract class BaseType {
    // ...
}
```

= Member Declarations

== Fields

Fields are declared with an optional access modifier and an optional
`static` modifier.

```
[<access-modifier>] [static] field <identifier>: <type>
```

- `<access-modifier>` is one of `private`, `public`, `protected`,
  `internal`.
- `static` marks the field as belonging to the type rather than an
  instance.
- The field name is an identifier, unique within the declaring type.
- The type is any valid type name.

=== Examples

```
field x: float32
static field counter: int32
private field name: System.String
public static field Instance: Program
```

== Methods

Methods are declared with optional modifiers, a name, a parameter list, a
return type, and a body.

```
[<access-modifier>] [static] [virtual] [override] [abstract]
    method <identifier>(<param-list>) -> <return-type> {
    <method-body>
}
```

- `static` — the method is not invoked on an instance; no implicit
  `this` argument.
- `virtual` — the method may be overridden in a derived type.
- `override` — the method overrides a virtual method from a base type.
- `abstract` — the method has no body and must be overridden by a
  concrete derived type. Abstract methods may only appear in abstract
  classes.
- The return type `-> <type>` uses `->` followed by a type name.
- For void methods, the return type is `void`.

=== Parameter List

```
(<param-name>: <type>, <param-name>: <type>, ...)
```

Parameters are positional. Each parameter has a name and a type separated
by `:`. The instance parameter `this` is implicit for non-static methods
and is available as argument index 0.

== Constructors

Constructors are special methods that initialise a new instance.

```
constructor(<param-list>) {
    <method-body>
}
```

Constructors have no explicit return type — they always return `void` and
are named implicitly after the enclosing type.

=== Examples

```
method Add(a: int32, b: int32) -> int32 {
    ldarg a
    ldarg b
    add
    ret
}

static method Main() -> void {
    ldstr "hello"
    call IO.Println(object) -> void
    ret
}

constructor(x: float32, y: float32) {
    ldarg this
    ldarg x
    stfld Vector2.x
    ldarg this
    ldarg y
    stfld Vector2.y
    ret
}
```

= Method Bodies

A method body is a sequence of local variable declarations followed by
instructions, optionally structured with control flow blocks.

== Local Variables

```
local <identifier>: <type>
```

Locals must be declared before use within the same method body. Each
declaration introduces a single variable.

== Instructions

Each instruction occupies one line and consists of a mnemonic followed by
zero or more operands, separated by whitespace.

```
<mnemonic> [<operand>...]
```

An operand is one of:

- A bare integer or float literal (`42`, `3.14`)
- A string literal (`"hello"`)
- An identifier or qualified name (`Program.Main`, `Item.value`)
- A method reference (see §Method Reference Syntax)
- A condition keyword (`stack`)
- A structured block (see §Structured Control Flow)
- An inline comment after the operand is discarded

== Structured Control Flow

ObjectIL supports structured control flow constructs that nest blocks
inside `if`, `while`, and `try` instructions. These are not separate
instructions — they are syntactic forms of the `if`, `while`, and `try`
instructions.

=== if / else

```
if (<condition>) {
    <then-body>
}
else {
    <else-body>
}
```

The `else` branch is optional. The `<condition>` is the bare keyword
`stack`, meaning the runtime pops the top of the evaluation stack and
coerces it to boolean.

=== while

```
while (<condition>) {
    <body>
}
```

The condition evaluates before each iteration. `break` exits the loop and
`continue` skips to the next iteration.

=== break / continue

```
break
continue
```

These are standalone instructions valid only inside a `while` body.

=== try / catch / finally

```
try {
    <try-body>
}
catch (<type>) {
    <catch-body>
}
finally {
    <finally-body>
}
```

Planned syntax — the exact form of `catch` variable binding is reserved
for a future version.

= Instruction Operand Reference

This section documents how each instruction's operands are formatted in
ObjectIL text. For the full execution semantics of each instruction,
see the ObjectRT V1 specification (`ObjectRT.typ`, §Instruction Set
Reference).

== Stack Manipulation

#figure(
  align(center)[#table(
    columns: 3,
    align: (left, left, left),
    table.header([#strong[Instruction]], [#strong[Operand]], [#strong[Description]]),
    table.hline(),
    [`nop`], [—], [No operation.],
    [`dup`], [—], [Duplicate top stack value.],
    [`pop`], [—], [Discard top stack value.],
    [`ldnull`], [—], [Push `null`.],
  )],
  kind: table,
  caption: [Stack manipulation instructions],
)

== Load Constants

#figure(
  align(center)[#table(
    columns: 3,
    align: (left, left, left),
    table.header([#strong[Instruction]], [#strong[Operand]], [#strong[Description]]),
    table.hline(),
    [`ldc.i4`], [`<integer>`], [Push 32-bit integer constant.],
    [`ldc.i8`], [`<integer>`], [Push 64-bit integer constant.],
    [`ldc.r4`], [`<number>`], [Push 32-bit float constant.],
    [`ldc.r8`], [`<number>`], [Push 64-bit float constant.],
    [`ldstr`], [`"<string>"`], [Push string constant.],
  )],
  kind: table,
  caption: [Load constant instructions],
)

== Arguments and Local Variables

#figure(
  align(center)[#table(
    columns: 3,
    align: (left, left, left),
    table.header([#strong[Instruction]], [#strong[Operand]], [#strong[Description]]),
    table.hline(),
    [`ldarg`], [`<name>`], [Push argument by name or 0-based index.],
    [`starg`], [`<name>`], [Pop and store to named argument slot.],
    [`ldloc`], [`<name>`], [Push local variable by name.],
    [`stloc`], [`<name>`], [Pop and store to named local variable.],
  )],
  kind: table,
  caption: [Argument and local variable instructions],
)

== Field Access

#figure(
  align(center)[#table(
    columns: 3,
    align: (left, left, left),
    table.header([#strong[Instruction]], [#strong[Operand]], [#strong[Description]]),
    table.hline(),
    [`ldfld`], [`<Type.field>`], [Pop object, push instance field value.],
    [`stfld`], [`<Type.field>`], [Pop value, pop object, store.],
    [`ldsfld`], [`<Type.field>`], [Push static field value.],
    [`stsfld`], [`<Type.field>`], [Pop value, store in static field.],
  )],
  kind: table,
  caption: [Field access instructions],
)

- `<Type.field>` is the declaring type name, a dot, and the field name —
  e.g. `Item.value`, `Globals.counter`.

== Arithmetic

#figure(
  align(center)[#table(
    columns: 3,
    align: (left, left, left),
    table.header([#strong[Instruction]], [#strong[Operand]], [#strong[Description]]),
    table.hline(),
    [`add`], [—], [Pop b, pop a, push a + b.],
    [`sub`], [—], [Pop b, pop a, push a - b.],
    [`mul`], [—], [Pop b, pop a, push a \* b.],
    [`div`], [—], [Pop b, pop a, push a / b.],
    [`rem`], [—], [Pop b, pop a, push a mod b.],
    [`neg`], [—], [Pop v, push -v.],
  )],
  kind: table,
  caption: [Arithmetic instructions],
)

== Logical

#figure(
  align(center)[#table(
    columns: 3,
    align: (left, left, left),
    table.header([#strong[Instruction]], [#strong[Operand]], [#strong[Description]]),
    table.hline(),
    [`not`], [—], [Pop v, push !ToBool(v).],
    [`and`], [—], [Pop b, pop a, push a & b (bitwise).],
    [`xor`], [—], [Pop b, pop a, push a ^ b (bitwise).],
    [`or`], [—], [Pop b, pop a, push a \| b (bitwise).],
  )],
  kind: table,
  caption: [Logical instructions],
)

== Comparison

#figure(
  align(center)[#table(
    columns: 3,
    align: (left, left, left),
    table.header([#strong[Instruction]], [#strong[Operand]], [#strong[Description]]),
    table.hline(),
    [`ceq`], [—], [Push true if a == b.],
    [`cne`], [—], [Push true if a != b.],
    [`cgt`], [—], [Push true if a > b.],
    [`cge`], [—], [Push true if a >= b.],
    [`clt`], [—], [Push true if a < b.],
    [`cle`], [—], [Push true if a <= b.],
  )],
  kind: table,
  caption: [Comparison instructions],
)

== Type Conversion and Testing

#figure(
  align(center)[#table(
    columns: 3,
    align: (left, left, left),
    table.header([#strong[Instruction]], [#strong[Operand]], [#strong[Description]]),
    table.hline(),
    [`conv`], [`<type>`], [Pop value, convert to target type, push.],
    [`castclass`], [`<type>`], [Assert top-of-stack is given type name.],
    [`isinst`], [`<type>`], [Pop value, push true if type matches.],
  )],
  kind: table,
  caption: [Type conversion and testing instructions],
)

== Object and Array Operations

#figure(
  align(center)[#table(
    columns: 3,
    align: (left, left, left),
    table.header([#strong[Instruction]], [#strong[Operand]], [#strong[Description]]),
    table.hline(),
    [`newobj`], [`<type>`], [Create new managed object.],
    [`newarr`], [`[<type>]`], [Create new empty array, optionally typed.],
    [`ldelem`], [—], [Pop index, pop array, push element.],
    [`stelem`], [—], [Pop value, pop index, pop array, store.],
  )],
  kind: table,
  caption: [Object and array operation instructions],
)

== Method Calls

#figure(
  align(center)[#table(
    columns: 3,
    align: (left, left, left),
    table.header([#strong[Instruction]], [#strong[Operand]], [#strong[Description]]),
    table.hline(),
    [`call`], [`<method-ref>`], [Call static or instance method.],
    [`callvirt`], [`<method-ref>`], [Virtual call — dispatch by receiver type.],
    [`ret`], [—], [Return from current frame.],
  )],
  kind: table,
  caption: [Method call instructions],
)

For the method reference syntax, see §Method Reference Syntax below.

== Branching

#figure(
  align(center)[#table(
    columns: 3,
    align: (left, left, left),
    table.header([#strong[Instruction]], [#strong[Operand]], [#strong[Description]]),
    table.hline(),
    [`br`], [`<label>` or `<offset>`], [Unconditional branch.],
    [`brtrue`], [`<label>` or `<offset>`], [Pop value; branch if truthy.],
    [`brfalse`], [`<label>` or `<offset>`], [Pop value; branch if falsy.],
  )],
  kind: table,
  caption: [Branch instructions],
)

Labels and numeric offsets are encoder-resolved constructs not expected
in hand-written ObjectIL — the structured `if` and `while` instructions
are the preferred form.

= Method Reference Syntax

The operand to `call` and `callvirt` uses the following syntax:

```
<declaring-type>.<method-name>(<param-type>, ...) -> <return-type>
```

- `<declaring-type>` is the fully qualified name of the type that declares
  the method.
- `<method-name>` is the method name.
- `<param-type>` is a comma-separated list of parameter type names.
- `<return-type>` is the return type name after `->`.

For constructors, the method name is `constructor` or the type name
itself followed by `.constructor`:

```
Item.constructor(int32, string)
```

=== Examples

```
call Program.Factorial(int32) -> int32
call IO.Println(object) -> void
callvirt Vector2.Length() -> float32
call Program.Add(int32, int32) -> int32
```

= Name Resolution

ObjectIL is a text format — every reference uses human-readable names.
The binary ORBT format replaces those names with indices into tables
(string pool, type table, field/method records within a type). It is
the **parser's responsibility** to resolve names and emit the
corresponding indices during serialisation.

This section describes how names in ObjectIL text map to table entries
in the binary encoding.

== Module and Type Resolution

The `module` declaration and each `class` declaration register entries
in the module's symbol table:

| ObjectIL text | Binary encoding |
|---|---|
| `module MyLib version 1.0.0` | Module name `"MyLib"` stored in string pool. Version triple encoded in header. |
| `class Program { ... }` | Type record added to type table. `name_index` points to `"Program"` in string pool. |
| `class Item { ... }` | Type record added to type table. `name_index` points to `"Item"` in string pool. |

A type name used as an operand — e.g. `newobj Item` or `castclass Item`
— is resolved by scanning the type table for a matching name. If no
match is found, the module fails to parse.

== Field Resolution

A field reference operand uses the syntax `<Type.field>`.

```
ldfld Item.value
stsfld Globals.counter
```

The parser splits on the dot:

1. **Left of the last dot** — the declaring type (`Item`, `Globals`).
   Resolved against the type table.
2. **Right of the last dot** — the field name (`value`, `counter`).
   Resolved against the declaring type's field records.

In the binary encoding, this pair becomes:
- `declaring_type_index` — index into the type table.
- `field_index` — index into the declaring type's field record array.

== Method Reference Resolution

A method reference operand uses the syntax:

```
<declaring-type>.<method-name>(<param-type>, ...) -> <return-type>
```

```
call Program.Factorial(int32) -> int32
```

The parser resolves it in three passes:

1. **Declaring type** (`Program`) — scanned against the type table.
   If the type is not found, the reference is invalid.
2. **Method name** (`Factorial`) — scanned against the declaring type's
   method records. The method's parameter count and types must match
   the signature in parentheses.
3. **Return type** (`int32`) — validated against the method record's
   declared return type. A mismatch is a parse error.

In the binary encoding, this resolves to:
- `declaring_type_index` — index into the type table.
- `method_index` — index into the declaring type's method record array.
- The parameter types in the reference are used for overload
  disambiguation by the parser but are not stored separately in the
  binary — the method index uniquely identifies the target.

=== Constructor References

Constructors use the same resolution model. The text name `constructor`
is treated as a special method name within the type:

```
Item.constructor(int32, string)
```

In the binary encoding, constructors are stored as method records with
the name `".ctor"` in the string pool.

== String Pool Mapping

Every distinct name that appears in an ObjectIL source — type names,
field names, method names, parameter names, local variable names —
produces an entry in the string pool during serialisation. The binary
encoding then references these entries by their pool index rather than
by name.

This means:

- A name that appears multiple times (e.g. `int32`) produces exactly
  one string pool entry and is referenced by index everywhere.
- The parser is responsible for interning duplicate strings.
- String pool indices are assigned in declaration order during
  serialisation; the deserialiser reconstructs the name from the pool
  entry at the same index.

== Scope and Locality

Some names are purely local and never enter the string pool or type
table:

- **Local variable names** and **parameter names** are stored only
  within their parent method's record as local/param name indices into
  the string pool.
- **Labels** (for `br`/`brtrue`/`brfalse`) are resolved to PC offsets
  during encoding and are not present in the binary at all.

= File Format Summary

A complete ObjectIL file has the following shape:

```
module <Name> version X.Y.Z

[abstract] [sealed] class <Name> [implements <Types>] {
    [<access>] [static] field <name>: <type>

    [<access>] [static] [virtual] [override] [abstract]
        method <Name>(<params>) -> <ReturnType> {
        [local <name>: <type>]
        <instruction>
        ...
    }

    [<access>] constructor(<params>) {
        ...
    }
}
```

= Examples

Complete ObjectIL example programs are provided in the `examples/`
directory alongside this specification. The examples demonstrate:

- Module and class declarations
- Instance and static fields
- Method calls, including recursion and cross-module calls
- Structured `if`/`else` and `while` loops
- Integer and floating-point arithmetic
- All comparison and logical operations
- Stack manipulation (dup, pop, ldnull)

These examples use the `.oil` extension convention.

= File Extension

The canonical file extension for ObjectIL source files is `.oil`.

Parsers should accept both `.oil` and legacy `.oir` files by convention,
but `.oil` is the standard extension for ObjectIL v1.
