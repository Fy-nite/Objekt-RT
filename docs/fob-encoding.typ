// )
#import "@preview/xyznote:0.5.0": *
#show: xyznote.with(
  title: "ObjectRT — ORBT V1 Encoding Reference",
  author: "charlie santana - Finite",
  abstract: "Binary opcode map and encoding scheme for the ORBT V1 format.",
  createtime: "2026-07-29",
  lang: "au",
)

#set heading(numbering: "1.")

= ORBT V1 Binary Encoding

This document defines the binary encoding of instructions in the ORBT V1
format — the compact binary representation of ObjectRT modules. It is a
companion to the main ObjectRT V1 specification and describes only the
encoding layer, not instruction semantics.

= File Layout

A ORBT V1 file consists of six sequential sections:

#figure(
  align(center)[#table(
    columns: (20%, 80%),
    align: (left, left),
    table.header([#strong[Section]], [#strong[Contents]]),
    table.hline(),
    [Header], [
      4-byte magic `0x4F524254` (`ORBT`), 1-byte format version
      (`0x01`), module name (length-prefixed UTF-8), version triple
      (three `uint16`).
    ],
    [String pool], [
      A `uint16` count followed by a length-prefixed table of interned
      UTF-8 strings referenced by index throughout the rest of the file.
    ],
    [Type table], [
      A `uint16` count followed by sequential type records: kind byte,
      name index, namespace index, access byte, flags byte, base type
      index, interface count + interface index list, field count + field
      records, method count + method records, attribute count + attribute
      records. Method bodies are embedded inline within each method
      record.
    ],
    [Import table], [
      Declarations of external symbols required by this module. Each
      entry identifies a module name, a symbol name, and the kind of
      symbol (type, method, or field). Imports are resolved at load
      time against the export tables of other modules; after resolution
      the module is merged into a unified namespace and all existing
      instructions reference the resolved definitions directly.
    ],
    [Export table], [
      Declarations of symbols this module exposes to other modules.
      Each entry maps an exported name to a local type, method, or
      field. The loader uses this table to resolve import requests
      from other modules during the merge process.
    ],
    [Metadata block], [
      A length-prefixed block declaring spec version targeting and
      runtime capability requirements (see §Metadata Block Format). A
      zero length means no entries; the module is then treated as
      targeting ObjectRT v1.0 with no feature requirements.
    ],
  )],
  kind: table,
  caption: [ORBT V1 file sections],
)

= String Encoding

All text in an ORBT file uses the same length-prefixed UTF-8 encoding:
the module name in the header, every entry in the string pool, and
string values in the metadata block.

#figure(
  align(center)[#table(
    columns: (30%, 70%),
    align: (left, left),
    table.header([#strong[Field]], [#strong[Description]]),
    table.hline(),
    [`length (uint16)`], [Byte count of the UTF-8 data that follows (`0x0000`--`0xFFFF`)],
    [`data (bytes)`],    [UTF-8 encoded text],
  )],
  kind: table,
  caption: [Length-prefixed string encoding],
)

- The length is a *byte* count, not a character count. UTF-8 encodes
  each code point in 1--4 bytes, so non-ASCII text takes more bytes than
  characters; the `uint16` length caps a string at 65,535 bytes. A
  length of `0x0000` is a valid empty string.
- Strings are not NUL-terminated. The length is authoritative, and the
  data may contain embedded NUL bytes.
- No byte-order mark is written; the UTF-8 data starts immediately after
  the length.
- Encoders must emit well-formed UTF-8. The C\# reader decodes with
  .NET's UTF-8 decoder, which replaces malformed sequences with U+FFFD;
  the C++ reader copies the raw bytes into a `std::string` without
  validation.
- All multi-byte fields in the file, including this length, are stored
  little-endian.

Instructions never inline text. Operands that carry text — notably
`ldstr`, plus the name/type indices in field, method, label, attribute,
import, and export records — hold a `uint16` index into the string pool;
the string data itself lives in the pool (see String Pool Format).

= Opcode Encoding Scheme

Opcodes are usually one byte but can be multiple bytes, a instruction can contain any amount of `0xFF` bytes, a `0xFF` byte indicates that the next byte needs to be read as the instruction

Example:
  
`0x05` = instruction with opcode 5 in first table

`0xFF 0x05` = instruction with opcode 5 in second table  

`0xFF 0xFF 0x05` = instruction with opcode 5 in third table
== Table Layout

Table 0 is the main instruction table, preserving the original single-byte
assignments as `00 II`. Tables 1--255 are extension tables.

#figure(
  align(center)[#table(
    columns: (14%, 23%, 14%, 23%),
    align: (left, left, left, left),
    table.header(
      [#strong[Opcode]], [#strong[Mnemonic]], [#strong[Opcode]], [#strong[Mnemonic]],
    ),
    table.hline(),
[`00`], [`nop`],        [`1B`], [`break`],
[`01`], [`ldc`],        [`1C`], [`continue`],
[`02`], [`ldstr`],      [`1D`], [`try`],
[`03`], [`ldarg`],      [`1E`], [`throw`],
[`04`], [`starg`],      [`1F`], [`conv`],
[`05`], [`ldloc`],      [`20`], [`castclass`],
[`06`], [`stloc`],      [`21`], [`isinst`],
[`07`], [`add`],        [`22`], [`dup`],       
[`08`], [`sub`],        [`23`], [`pop`],
[`09`], [`mul`],        [`24`], [`ldnull`],    
[`0A`], [`div`],        [`25`], [`not`],
[`0B`], [`rem`],        [`26`], [`cgt`],       
[`0C`], [`neg`],        [`27`], [`cge`],
[`0D`], [`ceq`],        [`28`], [`clt`],       
[`0E`], [`cne`],        [`29`], [`cle`],
[`0F`], [`ldfld`],      [`2A`], [`stfld`],     
[`10`], [`ldsfld`],     [`2B`], [`ldc.i4`],
[`11`], [`stsfld`],     [`2C`], [`ldc.i8`],    
[`12`], [`newobj`],     [`2D`], [`ldc.r4`],
[`13`], [`newarr`],     [`2E`], [`ldc.r8`],    
[`14`], [`ldelem`],     [`2F`], [`and`],
[`15`], [`stelem`],     [`30`], [`xor`],       
[`16`], [`call`],       [`31`], [`or`],
[`17`], [`callvirt`],   [`32`], [`br`],        
[`18`], [`ret`],        [`33`], [`brtrue`],
[`19`], [`if`],         [`34`], [`brfalse`],
[`1A`], [`while`],      [`35`], [`callnative`],
[`36`], [`ldlen`],      [],     [],
  )],
  kind: table,
  caption: [ORBT V1 opcode map — table 0 (main instruction table)],
)

Table 0 ends at `0x36` (`ldlen`). New opcodes are allocated in
extension tables using the `0xFF` prefix scheme.

== Table 1 (Type Objects)

#figure(
  align(center)[#table(
    columns: (14%, 30%, 56%),
    align: (left, left, left),
    table.header([#strong[Opcode]], [#strong[Mnemonic]], [#strong[Operand]]),
    table.hline(),
    [`01`], [`ldtype`], [`uint16` type name string index],
  )],
  kind: table,
  caption: [ORBT extension table 1 — type object opcodes],
)

`ldtype` is encoded as the two-byte sequence `0xFF 0x01`. Its flat value
under the `table * 256 + opcode` convention is `0x0101`.

= String Pool Format

The string pool is preceded by a `uint16` count and consists of a
contiguous block of concatenated, length-prefixed UTF-8 strings. Each
entry:

#figure(
  align(center)[#table(
    columns: (30%, 70%),
    align: (left, left),
    table.header([#strong[Field]], [#strong[Description]]),
    table.hline(),
    [`count (uint16)`],  [Number of strings in the pool],
    [`length (uint16)`], [Byte count of the string data (0--65,535)],
    [`data (bytes)`],    [UTF-8 encoded string content],
  )],
  kind: table,
  caption: [String pool format],
)

Strings are referenced by their 0-based index in declaration order.

= Type Record Format

The type table is preceded by a `uint16` type count. Each type in the
type table is encoded as:

#figure(
  align(center)[#table(
    columns: (30%, 70%),
    align: (left, left),
    table.header([#strong[Field]], [#strong[Description]]),
    table.hline(),
    [`kind (byte)`],          [`0x01` = Class, `0x02` = Interface, `0x03` = Struct, `0x04` = Enum],
    [`name_index (uint16)`],  [Index of type name in string pool],
    [`namespace_index (uint16)`], [Index of namespace in string pool],
    [`access (byte)`],        [`0x01` = Public, `0x02` = Private, `0x03` = Protected, `0x04` = Internal],
    [`flags (byte)`],         [Bit flags: `0x01` = Abstract, `0x02` = Sealed],
    [`base_type_index (int32)`], [Index into type table, or `-1` for none],
    [`interface_count (uint16)`], [Number of implemented interfaces],
    [`interface_indices`],    [Array of `uint16` indexes into type table],
    [`field_count (uint16)`], [Number of fields],
    [`field_records`],        [Array of field records (see below)],
    [`method_count (uint16)`], [Number of methods],
    [`method_records`],       [Array of method records (see below)],
    [`attribute_count (uint16)`], [Number of type-level attributes],
    [`attribute_records`],    [Array of attribute records (see Attribute Record Format)],
  )],
  kind: table,
  caption: [Type record format],
)

= Field Record Format

Each field in a type's field record array is encoded as:

#figure(
  align(center)[#table(
    columns: (30%, 70%),
    align: (left, left),
    table.header([#strong[Field]], [#strong[Description]]),
    table.hline(),
    [`name_index (uint16)`], [Index of field name in string pool],
    [`type_index (uint16)`], [Index of field type name in string pool],
    [`flags (byte)`],        [Bit flags — ORBT v2 addition: `0x01` = Static.],
  )],
  kind: table,
  caption: [Field record format],
)

In ORBT v1 a field record is exactly four bytes (`name_index`,
`type_index`); static-ness is implicit, encoded only by the choice of
opcode (`ldsfld`/`stsfld` vs `ldfld`/`stfld`). ORBT v2 adds the explicit
`flags` byte so that type objects and reflection (see ObjectRT.typ,
§Type Objects and Reflection) can enumerate static members from metadata
alone, without scanning instruction streams. Files that use the `flags`
byte must set format version `0x02` in the header; v1 readers reject
unknown format versions.

= Attribute Record Format

Both types and methods carry a count-prefixed attribute list. Each
attribute record is encoded as:

#figure(
  align(center)[#table(
    columns: (30%, 70%),
    align: (left, left),
    table.header([#strong[Field]], [#strong[Description]]),
    table.hline(),
    [`name_index (uint16)`], [Index of the attribute name in the string pool],
    [`arg_count (uint16)`],  [Number of attribute arguments],
    [`arg_indices`],         [Array of `uint16` indexes into the string pool],
  )],
  kind: table,
  caption: [Attribute record format],
)

Type-level attributes appear at the end of each type record; method-level
attributes appear inside each method record, between the label table and
the instruction data.

= Import Table Format

The import table declares external symbols that this module depends on.
Each import entry identifies a source module, a symbol name, and the kind
of symbol expected. Imports are referenced by their 0-based index
throughout the instruction stream.

#figure(
  align(center)[#table(
    columns: (30%, 70%),
    align: (left, left),
    table.header([#strong[Field]], [#strong[Description]]),
    table.hline(),
    [`module_index (uint16)`],  [Index of the source module name in the string pool],
    [`symbol_index (uint16)`],  [Index of the symbol name in the string pool],
    [`kind (byte)`],            [`0x01` = Type, `0x02` = Method, `0x03` = Field],
    [`flags (byte)`],           [`0x01` = Optional (no error if unresolved)],
  )],
  kind: table,
  caption: [Import record format],
)

The import table is preceded by a count:

```
import_count (uint16) — number of import records (may be `0x0000`)
import_records — array of import records
```

During module loading, the runtime walks the import table and attempts to
resolve each entry against the export tables of all currently loaded
modules. On a match, the import entry is replaced with a direct reference
to the resolved definition in the merged module state. If an import is
marked `Optional`, resolution failure is not an error; the unresolved
index maps to a null sentinel at runtime. Non-optional imports that cannot
be resolved cause a module load error.

Once all imports are resolved, the module's types, methods, and fields are
merged into a unified namespace. All existing instructions (`call`,
`newobj`, `ldfld`, etc.) operate on this merged state — no special
import-referencing instructions are needed.

= Export Table Format

The export table declares symbols this module makes available to other
modules at load time. Each entry maps an exported name to a local
definition.

#figure(
  align(center)[#table(
    columns: (30%, 70%),
    align: (left, left),
    table.header([#strong[Field]], [#strong[Description]]),
    table.hline(),
    [`name_index (uint16)`],  [Index of the exported symbol name in the string pool],
    [`kind (byte)`],          [`0x01` = Type, `0x02` = Method, `0x03` = Field],
    [`local_index (uint32)`], [Index into the local type/method/field table],
    [`module_index (uint16)`], [Index of this module's name in the string pool],
  )],
  kind: table,
  caption: [Export record format],
)

The export table is preceded by a count:

```
export_count (uint16) — number of export records (may be `0x0000`)
export_records — array of export records
```

When a module is loaded, the loader searches the export tables of all
loaded modules to resolve import entries. After resolution, all modules
are merged into a single unified namespace — imported symbols are
indistinguishable from locally defined ones.

= Metadata Block Format

The metadata block is optional. It communicates spec version targeting
and runtime capability requirements to the loader. If the block is absent,
the module is treated as targeting ObjectRT v1.0 with no required or
optional features.

The metadata block is encoded as a length-prefixed sequence of key-value
pairs:

#figure(
  align(center)[#table(
    columns: (30%, 70%),
    align: (left, left),
    table.header([#strong[Field]], [#strong[Description]]),
    table.hline(),
    [`block_length (uint16)`], [Total byte count of the metadata entries that follow (0 indicates no metadata).],
  )],
  kind: table,
  caption: [Metadata block header],
)

After the header, zero or more metadata entries follow sequentially:

#figure(
  align(center)[#table(
    columns: (30%, 70%),
    align: (left, left),
    table.header([#strong[Field]], [#strong[Description]]),
    table.hline(),
    [`key_index (uint16)`],   [Index of the entry key in the string pool],
    [`value_kind (byte)`],    [`0x01` = String, `0x02` = String list (count-prefixed)],
    [`value_data`],           [Value payload (see below)],
  )],
  kind: table,
  caption: [Metadata entry format],
)

=== Defined Keys

#figure(
  align(center)[#table(
    columns: 3,
    align: (left, left, left),
    table.header([#strong[Key]], [#strong[Value kind]], [#strong[Description]]),
    table.hline(),
    [`spec`],    [`0x01 (String)`], [ObjectRT spec version target, e.g. `"1.0"`.],
    [`require`], [`0x02 (String list)`], [Required feature paths, e.g. `["Memory.Managed", "Reflection.Basic"]`.],
    [`optional`],[`0x02 (String list)`], [Optional feature paths, e.g. `["JIT.Optimizing", "SIMD.Vector128"]`.],
  )],
  kind: table,
  caption: [Metadata keys and their value types],
)

When `value_kind` is `0x01` (String), the value payload is a single
length-prefixed UTF-8 string:

#figure(
  align(center)[#table(
    columns: (30%, 70%),
    align: (left, left),
    table.header([#strong[Field]], [#strong[Description]]),
    table.hline(),
    [`string_length (uint16)`], [Byte count of string data],
    [`string_data (bytes)`],    [UTF-8 encoded string content],
  )],
  kind: table,
  caption: [String value payload],
)

When `value_kind` is `0x02` (String list), the value payload is a
count-prefixed array of length-prefixed strings:

#figure(
  align(center)[#table(
    columns: (30%, 70%),
    align: (left, left),
    table.header([#strong[Field]], [#strong[Description]]),
    table.hline(),
    [`entry_count (uint16)`],   [Number of strings in the list],
    [`string_records`],         [Array of `(string_length, string_data)` pairs],
  )],
  kind: table,
  caption: [String list value payload],
)

The feature namespace for `require` and `optional` entries is defined in
the ObjectIL specification (`ObjectIL.typ`, §Module Metadata).

= Method Record Format

#figure(
  align(center)[#table(
    columns: (30%, 70%),
    align: (left, left),
    table.header([#strong[Field]], [#strong[Description]]),
    table.hline(),
    [`name_index (uint16)`],     [Index of method name in string pool],
    [`signature_index (uint16)`], [Index of full signature string in string pool],
    [`access (byte)`],           [Access modifier flags],
    [`flags (byte)`],            [`0x01` = Static, `0x02` = Virtual, `0x04` = Override, `0x08` = Abstract],
    [`param_count (uint16)`],    [Number of parameters],
    [`param_records`],           [Array of `(name_index, type_index)` pairs],
    [`local_count (uint16)`],    [Number of local variables],
    [`local_records`],           [Array of `(name_index, type_index)` pairs],
    [`label_count (uint16)`],    [Number of labels (may be `0x0000`)],
    [`label_table`],             [Array of label records (see Label Table Format)],
    [`attribute_count (uint16)`], [Number of method-level attributes],
    [`attribute_records`],       [Array of attribute records (see Attribute Record Format)],
    [`instr_count (uint32)`],    [Number of instructions in the body],
    [`instruction_data`],        [Serialised instruction stream (opcode chain + operands)],
  )],
  kind: table,
  caption: [Method record format],
)

Method bodies are encoded inline within their type record's method
records; there is no separate method-body section at the end of the file.

= Label Table Format

Each method body may include a label table that maps symbolically named
labels to their resolved PC offsets within the method. Branch instructions
(`br`, `brtrue`, `brfalse`) reference resolved offsets; the label table
enables the encoder to compute these offsets before emitting the
instruction stream.

If `label_count` is zero, the label table is absent and no labels are
defined for the method.

#figure(
  align(center)[#table(
    columns: (30%, 70%),
    align: (left, left),
    table.header([#strong[Field]], [#strong[Description]]),
    table.hline(),
    [`name_index (uint16)`], [Index of label name in string pool],
    [`pc_offset (uint32)`],  [Absolute PC offset of the label within the method body],
  )],
  kind: table,
  caption: [Label record format],
)

Labels are resolved at encode time. For each branch that targets a label,
the encoder computes the signed `int32` PC-relative offset:

```
offset = target_pc - (branch_pc + branch_instruction_length)
```

The result is stored as the branch operand (see Instruction Operand
Encoding). During execution the runtime adds the offset to the program
counter to reach the target.

= Instruction Operand Encoding

Each instruction begins with its variable-length opcode chain. The operand
encoding that follows depends on the instruction:

#figure(
  align(center)[#table(
    columns: (20%, 40%, 40%),
    align: (left, left, left),
    table.header(
      [#strong[Instruction group]], [#strong[Operand encoding]], [#strong[Notes]],
    ),
    table.hline(),
    [`ldc`, `ldc.i4`, `ldc.i8`, `ldc.r4`, `ldc.r8`], [Immediate value (fixed-width)], [i4=4 bytes (`ldc` and `ldc.i4`), i8=8 bytes, r4=4 bytes, r8=8 bytes],
    [`ldstr`], [Length-prefixed UTF-8 string], [`uint16` length + data],
    [`ldarg`, `starg`, `ldloc`, `stloc`], [`uint16` index], [0-based index into arg/local table],
    [`ldfld`, `stfld`, `ldsfld`, `stsfld`], [`uint16` field name string index], [Index into string pool],
    [`call`, `callvirt`, `callnative`], [`uint16` name string index + `uint16` parameter count], [Method-name string index into string pool; parameter count for argument popping],
    [`newobj`], [`uint16` type name string index], [Index into string pool],
    [`newarr`], [`uint16` element type string index], [`0xFFFF` = untyped],
    [`br`, `brtrue`, `brfalse`], [`int32` PC-relative offset], [Signed offset from end of instruction to target],
    [`if`, `while`], [Structured operand block], [See condition operand layout below],
    [`try`], [Structured operand block], [See exception handler layout below],
    [`conv`, `castclass`, `isinst`, `ldtype`], [`uint16` type name string index], [Index into string pool],
    [All others], [No operand], [],
  )],
  kind: table,
  caption: [Instruction operand encoding by group],
)

== Condition Operand Layout

For `if` and `while`, the operand is a structured block:

```
kind: byte          // 0x00=stack, 0x01=binary, 0x02=expression, 0x03=block
// For binary kind:
comparison: byte    // opcode of comparison instruction
// For expression/block kind:
instr_count: uint32
instruction_data    // embedded instruction stream
```

== Exception Handler Operand Layout

For `try`, the operand is structured as:

```
try_block_len: uint32
try_block:         // embedded instruction stream
catch_count: uint16
catch_records:     // array of { type_index: uint16, body_len: uint32, body }
has_finally: uint8 // 0x00 or 0x01
finally_block_len: uint32  // present only if has_finally == 0x01
finally_block:              // present only if has_finally == 0x01
```