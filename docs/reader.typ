// )
#import "@preview/xyznote:0.5.0": *
#show: xyznote.with(
  title: "ObjectRT Module Reader",
  author: "charlie santana - Finite",
  abstract: "CLI tool and C++ library for reading ObjectIL (.oil) and ORBT (.orbt) module files.",
  createtime: "2026-07-30",
  lang: "au",
)

#set heading(numbering: "1.")
#set text(font: "Fira Code", size: 10pt)

= Introduction

The ObjectRT Module Reader is a C++ tool that reads both the ObjectIL text
format (`.oil`) and the ORBT binary format (`.orbt`) and produces a
human-readable dump of the module's structure and contents. It serves as
both a standalone diagnostic utility and a reference implementation of the
ObjectIL and ORBT parsing logic.

Source code lives under `src/reader/`.

= Building

The reader is built as part of the main ObjectRT project via Meson.

```
cd build
meson setup ..   # if not already configured
ninja objectrt-reader
```

The reader requires no additional dependencies beyond what the main project
already uses (glib-2.0, gobject-2.0, luajit).

= Usage

```
objectrt-reader [options] <file>
```

== Options

#figure(
  align(center)[#table(
    columns: (25%, 75%),
    align: (left, left),
    table.header([#strong[Option]], [#strong[Description]]),
    table.hline(),
    [`-v`, `--verbose`], [Show detailed output including the full string pool and all decoded instructions with operands.],
    [`-h`, `--help`],    [Print the help message and exit.],
  )],
  kind: table,
  caption: [CLI options],
)

== Input Format Detection

The reader automatically detects the input format:

- **ORBT binary**: reads the first 4 bytes — if they match the magic
  `0x4F524254` (`ORBT`), the file is treated as ORBT.
- **ObjectIL text**: detected by the `.oil` or `.oir` file extension, or
  by checking if the file starts with the word `module`.
- If neither check passes, the tool reports an unrecognised format and
  exits.

== Examples

```
# Read an ObjectIL file (brief output)
objectrt-reader examples/hello.oil

# Read an ORBT binary file with full instruction dump
objectrt-reader -v examples/hello.orbt

# Read and redirect to a file
objectrt-reader module.orbt > dump.oil
```

= Output Format

The reader emits ObjectIL-like text that mirrors the structure of the
input module. Non-verbose output shows:

- Module name and version.
- Metadata block (spec version, required/optional features).
- Import and export table summaries (commented out with `;;`).
- Type declarations with their fields and method signatures.

Verbose output (`-v`) additionally shows:

- Every decoded instruction with its mnemonic and operand.
- The full string pool contents (commented out at the end).

=== Example output

```
; Reading ObjectIL text: examples/hello.oil

module Hello version 1.0.0

.metadata {
    spec objectrt = "1.0"

    require [
        Memory.Managed,
        Reflection.Basic,
    ]
}

class Program {
    private field counter: int32

    static method Main() -> void {
        ;; 3 instruction(s)
    }
}
```

= Architecture

The reader is organised into four files:

#figure(
  align(center)[#table(
    columns: (25%, 25%, 50%),
    align: (left, left, left),
    table.header([#strong[File]], [#strong[Role]], [#strong[Description]]),
    table.hline(),
    [`Module.hpp`],    [Data model],    [Enums, opcodes, operand types, and the `ORBTModule` class that represents a fully-parsed module.],
    [`ORBTReader.hpp`],  [Binary reader],  [`BinaryStream` helper + `ORBTReader` class that parses the ORBT binary format section by section.],
    [`ORBTReader.cpp`],  [Binary reader],  [Implementation of the binary format deserialisation and the `dump()` method that renders a module back to ObjectIL text.],
    [`ObjectILParser.hpp`], [Text parser], [Tokenizer and recursive-descent parser for the ObjectIL text format.],
    [`ObjectILParser.cpp`], [Text parser], [Implementation of the tokeniser, grammar rules, and AST construction.],
    [`main.cpp`],      [CLI entry],     [Argument parsing, format detection, and orchestration.],
  )],
  kind: table,
  caption: [Source files],
)

The pipeline is:

1. The CLI detects the file format (binary vs text).
2. For ORBT: a `BinaryStream` wraps the file, an `ORBTReader` reads
   section-by-section into an `ORBTModule`.
3. For ObjectIL: a `Tokenizer` produces tokens, an `ObjectILParser`
   drives a recursive-descent parse into an `ORBTModule`.
4. Either way, the result is the same `ORBTModule` type.
5. `ORBTModule::dump()` serialises the module back to ObjectIL-like text.

// #figure(
//   align(center)[```ascii
//   .oil file ──► ObjectILParser ──┐
//                                   ├──► ORBTModule ──► dump() ──► stdout
//   .orbt file ──► ORBTReader ─────┘
//   ```],
//   kind: block,
//   caption: [Data flow through the reader],
// )

== Module.hpp — Data Model

All public types live in the `objectrt` namespace.

=== Enums

#figure(
  align(center)[#table(
    columns: (25%, 25%, 50%),
    align: (left, left, left),
    table.header([#strong[Enum]], [#strong[Values]], [#strong[Purpose]]),
    table.hline(),
    [`TypeKind`],     [`Class`, `Interface`, `Struct`, `Enum`], [Kind of a type declaration.],
    [`MemberAccess`], [`Public`, `Private`, `Protected`, `Internal`], [Access modifier for members.],
    [`TypeFlags`],    [`None`, `Abstract`, `Sealed`], [Modifier flags on a type.],
    [`MethodFlags`],  [`None`, `Static`, `Virtual`, `Override`, `Abstract`], [Modifier flags on a method.],
    [`ImportKind`],   [`Type`, `Method`, `Field`], [Kind of an imported symbol.],
    [`ConditionKind`],[`Stack`, `Binary`, `Expression`, `Block`], [Condition operand variant for `if`/`while`.],
    [`Opcode`],       [53 opcodes], [Every instruction the ObjectRT virtual machine understands.],
  )],
  kind: table,
  caption: [Public enums],
)

=== Core Structures

`ORBTModule`
: The top-level container. Holds the module name, version triple,
  string pool, type table, import/export tables, and metadata block.
  Provides `dump(ostream)` for serialisation and `resolve(index)` /
  `type_name(type)` convenience methods.

`StringPool`
: A vector of interned UTF-8 strings referenced by `uint16_t` index
  throughout the module. The ORBT binary stores strings length-prefixed;
  ObjectIL text creates pool entries as names are encountered during
  parsing.

`TypeRecord`
: Describes one type (class, interface, struct, or enum). Contains the
  type's string-pool indices for name and namespace, access modifier,
  flags, base type index, interface list, and arrays of fields and
  methods.

`MethodRecord`
: Describes one method. Contains name and signature string-pool indices,
  access modifier, method flags, parameter and local variable lists,
  label table, and decoded instructions.

`Instruction`
: A single decoded instruction. Stores the `Opcode`, the program counter
  offset, and the decoded `Operand` as a `std::variant`.

`MetadataBlock`
: Holds the parsed contents of the `.metadata { ... }` block: spec
  version targeting, required feature paths, and optional feature paths.

=== Operand Types

The `Operand` variant covers every instruction operand encoding:

#figure(
  align(center)[#table(
    columns: (20%, 30%, 50%),
    align: (left, left, left),
    table.header([#strong[Type]], [#strong[Encoding]], [#strong[Used by]]),
    table.hline(),
    [`OperandNone`],     [—],               [`nop`, `add`, `ret`, `dup`, `pop`, etc.],
    [`OperandI4`],       [`int32`],          [`ldc.i4`],
    [`OperandI8`],       [`int64`],          [`ldc.i8`],
    [`OperandR4`],       [`float`],          [`ldc.r4`],
    [`OperandR8`],       [`double`],         [`ldc.r8`],
    [`OperandString`],   [`uint16` pool idx],[`ldstr`, `newobj`, `newarr`],
    [`OperandIndex`],    [`uint16` index],   [`ldarg`, `starg`, `ldloc`, `stloc`],
    [`OperandFieldRef`], [`uint16` pool idx],[`ldfld`, `stfld`, `ldsfld`, `stsfld`],
    [`OperandMethodRef`],[`uint16` pool idx],[`call`, `callvirt`],
    [`OperandTypeRef`],  [`uint16` pool idx],[`conv`, `castclass`, `isinst`],
    [`OperandBranch`],   [`int32` offset],   [`br`, `brtrue`, `brfalse`],
    [`ConditionOperand`],[structured],       [`if`, `while`],
    [`ExceptionHandlerOperand`],[structured],[`try`],
  )],
  kind: table,
  caption: [Operand variant types and their encodings],
)

== ORBTReader — Binary Format Reader

=== BinaryStream

The `BinaryStream` class wraps either a file path or an in-memory byte
vector and provides little-endian read operations for all primitive types
used in the ORBT format.

#figure(
  align(center)[#table(
    columns: (30%, 70%),
    align: (left, left),
    table.header([#strong[Method]], [#strong[Description]]),
    table.hline(),
    [`read_u8()`],        [Read a single byte.],
    [`read_u16()`],       [Read a 16-bit unsigned integer (little-endian).],
    [`read_u32()`],       [Read a 32-bit unsigned integer.],
    [`read_i32()`],       [Read a signed 32-bit integer.],
    [`read_i64()`],       [Read a signed 64-bit integer.],
    [`read_u64()`],       [Read a 64-bit unsigned integer.],
    [`read_r4()`],        [Read a 32-bit IEEE 754 float.],
    [`read_r8()`],        [Read a 64-bit IEEE 754 double.],
    [`read_string()`],    [Read a `uint16` length-prefixed UTF-8 string.],
    [`read_bytes(n)`],    [Read `n` raw bytes.],
  )],
  kind: table,
  caption: [BinaryStream read methods],
)

=== ORBTReader

The `ORBTReader` class reads an `ORBTModule` from a `BinaryStream`. It
processes the file in section order as defined by the ORBT V1 Encoding
Reference (`fob-encoding.typ`):

1. **Header** — validates magic (`ORBT`), reads format version, module
   name, and version triple.
2. **String pool** — reads a count-prefixed array of length-prefixed
   UTF-8 strings.
3. **Type table** — reads type records, each containing fields and
   method records (including parameter lists, local variables, labels,
   and instruction streams).
4. **Import table** — reads external symbol dependencies (module name,
   symbol name, kind, optional flag).
5. **Export table** — reads externally visible symbols (exported name,
   kind, local index, module index).
6. **Metadata block** — reads an optional block of key-value metadata
   entries (spec version, required features, optional features).

=== Opcode Decoding

Opcodes use a variable-length encoding scheme: `0xFF` bytes extend to
higher tables. The decoder accumulates table levels until it encounters
a non-`0xFF` byte:

```
0x05          → opcode 5  in table 0
0xFF 0x05     → opcode 5  in table 1
0xFF 0xFF 0x05 → opcode 5 in table 2
```

The `read_opcode()` helper implements this logic, returning an `Opcode`
enum value. Table 0 currently contains 53 opcodes (`0x00`–`0x34`).

=== Instruction Operand Decoding

After the opcode, the reader dispatches on opcode to decode the
appropriate operand:

- Immediate values (`ldc.i4`/`.i8`/`.r4`/`.r8`) read fixed-width values.
- String references (`ldstr`, `newobj`) read a `uint16` string pool index.
- Index references (`ldarg`, `starg`, `ldloc`, `stloc`) read a `uint16`.
- Field/method references read a `uint16` string pool index.
- Branch instructions (`br`, `brtrue`, `brfalse`) read an `int32`
  PC-relative offset.
- Structured operations (`if`, `while`) read a `ConditionOperand`.
- Exception handling (`try`) reads an `ExceptionHandlerOperand`.

== ObjectILParser — Text Format Parser

=== Tokenizer

The `Tokenizer` class converts an `istream` into a sequence of `Token`
values. Each token carries a `TokenKind` enum, the matched text, and
source position (line, column).

#figure(
  align(center)[#table(
    columns: (20%, 80%),
    align: (left, left),
    table.header([#strong[TokenKind]], [#strong[Description]]),
    table.hline(),
    [`Eof`],          [End of input.],
    [`Identifier`],   [A user-defined name or qualified name.],
    [`Integer`],      [A numeric literal without a decimal point.],
    [`Float`],        [A numeric literal containing a decimal point.],
    [`String`],       [A double-quoted string literal (escape sequences processed).],
    [`Keyword`],      [One of the reserved keywords (`module`, `class`, `if`, etc.).],
    [`Dot`],          [`.` — namespace/field separator.],
    [`Comma`],        [`,` — list separator.],
    [`Colon`],        [`:` — type annotation separator.],
    [`Arrow`],        [`->` — return type indicator.],
    [`DotMetadata`],  [`.metadata` — metadata block introducer.],
    [`OpenParen`],    [`(`],
    [`CloseParen`],   [`)`],
    [`OpenBrace`],    [`{`],
    [`CloseBrace`],   [`}`],
    [`OpenBracket`],  [`[`],
    [`CloseBracket`], [`]`],
  )],
  kind: table,
  caption: [Token types],
)

The tokeniser handles:

- **Single-line comments**: `//` through end of line, treated as
  whitespace.
- **String escape sequences**: `\"`, `\\`, `\n`, `\r`, `\t`.
- **Numeric literals**: integers (`42`, `-7`) and floats (`3.14`, `2.5`).
- **Identifier characters**: letters, digits, underscores, backticks,
  dots, and angle brackets (for generics like `List<DebugInfo>`).

=== Parser Grammar

The `ObjectILParser` is a recursive-descent parser that implements the
ObjectIL grammar defined in `ObjectIL.typ`. The entry points are:

`parse_module()`
: Parses the complete file. Calls `parse_module_decl()`, optionally
  `parse_metadata_block()`, then loops over `parse_type_decl()` until
  EOF.

`parse_module_decl()`
: Parses `module <name> version <major>.<minor>.<patch>`.

`parse_metadata_block()`
: Parses `.metadata { spec ... require [...] optional [...] }`.

`parse_type_decl()`
: Parses `[abstract] [sealed] class <name> [implements <types>] { ... }`.
  Delegates to `parse_member()` for each field, method, or constructor.

`parse_member()`
: Collects access modifiers (`public`/`private`/`protected`/`internal`)
  and method modifiers (`static`/`virtual`/`override`/`abstract`), then
  dispatches to `parse_field()`, `parse_method()`, or `constructor`.

`parse_field()`
: Parses `field <name>: <type>`.

`parse_method()`
: Parses `method <name>(<params>) -> <rettype> { <body> }`.

`parse_method_body()`
: Parses local variable declarations followed by instruction lines.

`parse_instruction()`
: Reads the mnemonic and skips operand tokens to end of line.

The parsed types, methods, fields, parameters, and locals are stored
directly into the `ORBTModule`'s string pool and type/field/method
records, exactly as the ORBT reader does.

= VM-Friendly Layout

The tooling representation (`ORBTModule`) stores every instruction as a
decoded `Instruction` struct with a `std::variant<Operand>` — convenient
for analysis but not suitable for a VM interpreter loop. The project
includes a VM-friendly compilation pipeline under `src/vm/`.

== Architecture

// #figure(
//   align(center)[```ascii
//   .oil ──► ObjectILParser ──┐
//                              ├──► ORBTModule ──► ModuleCompiler ──► CompiledModule ──► Interpreter
//   .orbt ──► ORBTReader ─────┘         (tooling)        (compile)        (VM-ready)      (execute)
//   ```],
//   kind: block,
//   caption: [Three-layer pipeline: tooling → compiler → interpreter],
// )

== CompiledModule — What changes

#figure(
  align(center)[#table(
    columns: (30%, 35%, 35%),
    align: (left, left, left),
    table.header([#strong[Aspect]], [#strong[ORBTModule (tooling)]], [#strong[CompiledModule (VM)]]),
    table.hline(),
    [Instructions],  [`std::vector<Instruction>` with `std::variant<Operand>`], [`std::vector<uint8_t>` flat bytecode],
    [Method refs],   [`uint16_t` string pool index],  [`uint32_t` function table index],
    [Field refs],    [`uint16_t` string pool index],  [`uint16_t` field table index],
    [Type refs],     [`uint16_t` string pool index],  [`uint16_t` type table index],
    [Branch offsets], [Stored as `int32_t` in `OperandBranch`], [Recomputed to new PC-relative values],
    [String pool],   [Per-module `std::vector<std::string>`], [Same, shared by index],
    [Max stack],     [Not computed], [Pre-computed per function],
  )],
  kind: table,
  caption: [Key differences between tooling and VM representation],
)

== Source files

#figure(
  align(center)[#table(
    columns: (25%, 25%, 50%),
    align: (left, left, left),
    table.header([#strong[File]], [#strong[Role]], [#strong[Description]]),
    table.hline(),
    [`CompiledModule.hpp`], [Data model], [Lean types: `CompiledModule`, `CompiledFunction`, `VMType`, `VMField`. No variants, no decoded instruction structs.],
    [`ModuleCompiler.hpp`], [Compiler],   [Resolves all string-pool references to flat table indices, emits flat bytecode, recomputes branch offsets.],
    [`ModuleCompiler.cpp`], [Compiler],   [Two-pass encoding: pass 1 computes new PC positions, pass 2 emits opcode + operand bytes with resolved references.],
    [`Interpreter.hpp`],    [Runtime],    [Tagged `Value` union, `Frame` struct, iterative dispatch interface.],
    [`Interpreter.cpp`],    [Runtime],    [Switch-on-raw-bytecode dispatch loop. No variant dispatch, no decoded structs.],
  )],
  kind: table,
  caption: [VM source files],
)

== Bytecode format

The VM bytecode uses the same opcode numbering as the ORBT specification
(table 0, single-byte opcodes). Operands are encoded inline as raw bytes:

#figure(
  align(center)[#table(
    columns: (20%, 20%, 30%, 30%),
    align: (left, left, left, left),
    table.header([#strong[Opcode group]], [#strong[Opcode size]], [#strong[Operand encoding]], [#strong[Example bytes]]),
    table.hline(),
    [`ldc.i4`],  [1], [4 bytes int32 LE],   [`2B 2A 00 00 00`],
    [`ldc.i8`],  [1], [8 bytes int64 LE],   [`2C XX...`],
    [`ldc.r4`],  [1], [4 bytes float IEEE], [`2D XX...`],
    [`ldc.r8`],  [1], [8 bytes double IEEE],[`2E XX...`],
    [`ldarg` / `ldloc`], [1], [2 bytes uint16 LE], [`03 XX XX`],
    [`call` / `callvirt`], [1], [4 bytes uint32 LE], [`16 XX XX XX XX`],
    [`br` / `brtrue` / `brfalse`], [1], [4 bytes int32 LE (PC-relative)], [`32 XX XX XX XX`],
    [others],    [1], [0 bytes],           [`07` (add), `18` (ret)],
  )],
  kind: table,
  caption: [VM bytecode operand encoding],
)

== Iterative dispatch

The interpreter uses a single dispatch loop with `goto next_frame` for
call/return, avoiding C++ recursion:

1. `call` pushes a new `Frame` with the callee's bytecode pointer and
   saved return address, then jumps to `next_frame` to enter the callee.
2. `ret` pops the frame, pushes the return value onto the caller's stack,
   restores the caller's PC, and jumps to `next_frame`.
3. The outer `while (!frames_.empty())` loop picks up whichever frame is
   on top — caller or callee — naturally.

This means the interpreter never recurses into itself. The call stack
is the frame vector, and it can be inspected or unwound at any point.

== Example

The `--run` flag compiles and executes the module:

```
$ objectrt-reader --run examples/hello.oil
; Compiled module: 1 functions, 1 types, 3 strings
; Entry point: Program.Main [12 bytes]
; Executing...
; Execution complete (result: 100)
```

The `--trace` flag adds per-instruction tracing:

```
$ objectrt-reader --trace examples/hello.oil
  [Program.Main 0] ldc.i4 42
  [Program.Main 5] ldc.i4 58
  [Program.Main 10] add
  [Program.Main 11] ret (top-level)
; Execution complete (result: 100)
```

= Future Work

- **Binary encoding**: An encoder that writes `ORBTModule` → ORBT binary.
- **Full instruction decoding in ObjectIL parser**: The text parser
  currently does basic encoding for `ldc.i4` and a few opcodes. A complete
  implementation should decode (and re-encode) all operands properly.
- **Validation**: Add semantic validation — duplicate type/field/method
  names, unresolved type references, method signature mismatches.
- **Export round-trip**: `ORBTModule` → ObjectIL text → re-parse.
- **Heap and objects**: `newobj`, `ldfld`, `stfld`, `callvirt` are stubs
  in the interpreter. A full implementation needs a garbage-collected
  heap with type-aware field layout.
- **Structured control flow**: The interpreter skips `if`/`while`/`try`
  embedded blocks. A production VM would need to execute them.
- **JIT compilation**: The flat bytecode layout is designed for easy
  lowering to native code via a template JIT or interpreter generator.
