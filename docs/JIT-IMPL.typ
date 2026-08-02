#import "@preview/xyznote:0.5.0": *
#import "helpers.typ": *
#set text(font: "Fira Code", size: 11pt)
#show: xyznote.with(
  title: "ObjectRT ReflectionJit — Implementation Notes",
  author: "charlie santana - Finite",
  abstract: "Bytecode → C\# source → Roslyn → delegates. Disk cache and source emit.",
  createtime: "2026-07-31",
  lang: "au",
)

#set heading(numbering: "1.")

= Pipeline

```
CompiledFunction.Code (bytes)
  → IsCompilable() check (scan for unsupported opcodes)
  → GenerateMethod() → C\# source (StringBuilder)
  → CompileAssembly() → Roslyn CSharpCompilation (Release) → Assembly.Load
  → Delegate.CreateDelegate → Func<IExecutor,Value[],Value>
  → _compiled[funcIdx]
```

= Supported opcodes (v1)

All loads, stores, arithmetic, comparisons, branches, returns, static field
access, and native calls are compiled to C\#. The following opcodes are
#strong[not supported] — functions containing them fall back to the interpreter:

`ldfld` / `stfld`, `newobj` / `newarr`, `ldelem` / `stelem`,
`conv` / `castclass` / `isinst`, structured flow (`if` / `while` / `try` /
`throw` / `break` / `continue`).

The `IsCompilable()` method scans each function's bytecode before compilation.
If any unsupported opcode appears, the entire function stays interpreted.

= C\# generation

Each bytecode instruction becomes one line of C\#:

#strong[Loads]:
```csharp
s[sp++] = Value.FromI4(42);    // ldc.i4 42
s[sp++] = Value.FromR4(3.14f); // ldc.r4 3.14
s[sp++] = args[0];             // ldarg 0
```

#strong[Arithmetic]:
```csharp
{ var b = s[--sp]; var a = s[--sp];
  s[sp++] = Interpreter.Arith(a, b, Interpreter.I4Mul, Interpreter.I8Mul,
                               Interpreter.R4Mul, Interpreter.R8Mul); }
```

#strong[Comparison]:
```csharp
{ var b = s[--sp]; var a = s[--sp];
  s[sp++] = Value.FromI4(Interpreter.NumericCompare(a, b) > 0 ? 1 : 0); }
```

#strong[Branches]: `goto L_XXX;` labels at each PC offset that is a branch
target. `brtrue` / `brfalse` test `s[--sp].IsTruthy()`.

#strong[Locals and stack]: Locals live in `args[numParams + idx]` (the args
array is oversized to hold locals). The stack is `Value[] s` with `int sp`.

#strong[Calls]:
```csharp
{ var a = new object?[argc];
  a[0] = e.ValueToObject(s[--sp]); ...
  s[sp++] = e.MarshalValue(e.NativeCallHandler!("Name", a)); }
```

Generated methods have signature:
```csharp
public static Value Name(IExecutor e, Value[] args)
```

= Roslyn compilation

In-memory `CSharpCompilation` at `OptimizationLevel.Release`. References:
- `System.Private.CoreLib` (via `typeof(object)`)
- `ObjectRT.VM` (via `typeof(Value)`)
- `System.Linq` (via `typeof(Enumerable)`)
- `System.Runtime` (via `Assembly.Load`)

Each compilation gets a unique assembly name (`ObjectRT_Gen_0`, `ObjectRT_Gen_1`, …)
so multiple modules load without collision. The emitted bytes are stashed in
`s_lastEmittedBytes` for the optional disk cache.

= Disk cache

Set `Runtime.CacheDir` to a directory path. On first load:

+ Compile via Roslyn, produce DLL bytes.
+ Compute SHA-256 hash of: string table + compilable function bytecode + static
  field count.
+ Save as `{CacheDir}/{hash}.dll`.

On subsequent loads: if `{hash}.dll` exists, `Assembly.Load(bytes)` from disk
directly — no Roslyn, no parse tree, zero compilation cost. The cache is
best-effort: IO failure falls through to in-memory compilation without error.

= Source emit

Set `Runtime.EmitDir` to dump the generated C\# source to disk as
`{ModuleName}.ObjectRT.g.cs`. Useful for debugging code-gen output or
abusing the JIT as an ObjectIL → C\# compiler (take the emitted source,
drop it in a regular C\# project with an `ObjectRT.VM` reference).

= Multi-threading notes

- `s_lastEmittedBytes` / `s_asmCounter` — static fields shared across all
  executor instances. Needs `Interlocked` or instance-level tracking for
  thread safety.
- Cache writes — should use temp file + `File.Move` (atomic rename on same
  volume) to avoid half-written DLLs on concurrent compilation.
- Generated code accesses `exec.StaticFields` / `exec.Heap` — these are
  instance fields accessed from `static` generated methods. Safe when each
  thread owns its own executor instance.

= Source file

`src/ObjectRT.VM/ReflectionJit.cs`
