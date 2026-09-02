# ObjectRT

A typed, object-oriented runtime and virtual machine for executing ObjectIL (`.oil`) bytecode. ObjectRT is a .NET-hosted execution engine with a stack-machine instruction set, an interpreter, a Roslyn-based reflection JIT, and a pluggable native binding system — all designed to be embedded in host applications as a scripting runtime.

**License:** MIT © 2026 Finite

---

## What Is This?

ObjectRT is the runtime counterpart to the [ObjectIL](docs/ObjectIL.typ) instruction format. It provides:

- A **stack-based bytecode interpreter** for `.oil` / `.orbt` modules
- A **Roslyn reflection JIT** that compiles hot functions to native C# delegates at load time
- A **host binding system** so C# applications can expose APIs to scripts (and vice versa)
- A **bundler** that wraps a compiled module + host into a standalone self-contained executable
- A **Debug Adapter Protocol (DAP) server** for editor-integrated debugging
- A **source generator** for zero-reflection proxy classes

It is used as the execution backend for the [Contract language](https://github.com/Fy-nite/Contract) and can be used independently as a general-purpose embedded scripting VM, a language host for custom programming languages and more.

---

## Architecture at a Glance

```
┌──────────────────────────────────────────────────────────┐
│                     Host Application                      │
│  (MonoGame, NES emulator, game engine, CLI, etc.)        │
├──────────────────────────────────────────────────────────┤
│                     ObjectRT Runtime                      │
│  ┌─────────────┐  ┌─────────────┐  ┌─────────────────┐  │
│  │ Interpreter │  │ReflectionJit│  │  Future: LLVM   │  │
│  └──────┬──────┘  └──────┬──────┘  └────────┬────────┘  │
│         │                │                   │            │
│         └────────┬───────┘───────────────────┘            │
│                  ▼                                        │
│              ExecutorBase  (shared heap, statics, strings)│
├──────────────────────────────────────────────────────────┤
│                    Native Bindings                        │
│  ClrNativeResolver · InterfaceHostResolver ·              │
│  NativeBindingResolver · DllImportResolver                │
├──────────────────────────────────────────────────────────┤
│              CompiledModule  (flat bytecode)              │
│              ModuleCompiler  (.oil → VM representation)   │
│              ORBT binary format / .oil source             │
└──────────────────────────────────────────────────────────┘
```

---

## Execution Backends

ObjectRT supports pluggable execution backends through the `IExecutor` interface:

| Backend | Enum | Description |
|---|---|---|
| **Interpreter** | `JitMode.Interpreter` | Iterative bytecode dispatch loop (default). Zero dependencies, sandboxable via `MaxSteps`. |
| **Reflection JIT** | `JitMode.Reflection` | Compiles each function to C# source at load time via Roslyn. Falls back to the interpreter for unsupported opcodes. Supports assembly caching. |
| **LLVM JIT** | `JitMode.LLVM` | *Reserved / planned.* NativeAOT-safe compiled backend via LLVMSharp. |

Set the mode on the runtime before loading a module:

```csharp
var rt = new Runtime();
rt.Mode = JitMode.Reflection;  // compile via Roslyn
rt.LoadModuleFile("game.oil");
```

---

## The Value Type

All VM values are 16-byte tagged unions (`ValueTag`):

| Tag | Type | Description |
|---|---|---|
| `Nil` | — | Null / uninitialized |
| `I4` | `int` | 32-bit integer |
| `I8` | `long` | 64-bit integer |
| `R4` | `float` | 32-bit float |
| `R8` | `double` | 64-bit float |
| `Obj` | heap handle | Reference to a heap-allocated object |
| `Str` | string handle | Interned string in the executor's string table |

Arithmetic is tag-aware with automatic type promotion (mixed int/long → double).

---

## Host Binding System

Scripts call into the host through four resolver layers, tried in order:

1. **Explicit native methods** — registered directly via `Runtime.RegisterNative()`
2. **`InterfaceHostResolver`** — C# interfaces marked with `[IRHostBinding]`; dispatch is source-generated (zero-reflection, NativeAOT-safe)
3. **`NativeBindingResolver`** — reads `@NativeBinding` annotations from module metadata
4. **`ClrNativeResolver`** — reflection-based discovery of static CLR methods
5. **`DllImportResolver`** — bridges `@DllImport` calls to native P/Invoke libraries via Roslyn-generated bridge assemblies
6. **VM fallback** — the interpreter/JIT handles any remaining module functions

### Registering Host Objects

```csharp
var rt = new Runtime();

// Register a C# object under a binding name
rt.RegisterHost("MonoGame.Screen", screenImpl, typeof(IScreen));

// Scripts can then call:
//   call MonoGame.Screen.Clear(color)
```

### Strongly-Typed Proxies

Use the source generator to avoid reflection entirely:

```csharp
[IRClassBinding("Calculator")]
public interface ICalc
{
    int Add(int a, int b);
    int Subtract(int a, int b);
}

// Source generator produces a zero-reflection proxy automatically
var calc = rt.Bind<ICalc>();
int sum = calc.Add(3, 4);
```

---

## Writing ObjectIL Scripts

ObjectIL (`.oil`) is a stack-machine assembly format. Here's a minimal program:

```
module Hello version 1.0.0

class Program {
    static method Main() -> void {
        ldc.i4 42
        ldc.i4 58
        add
        ret
    }
}
```

### Key Opcodes

| Opcode | Description |
|---|---|
| `ldc.i4 N` | Push 32-bit integer constant |
| `ldc.r4 N` | Push 32-bit float constant |
| `ldarg name` | Push function argument |
| `starg name` | Store to function argument |
| `ldsfld Type.field` | Load static field |
| `stsfld Type.field` | Store static field |
| `call Method(args) -> ret` | Call a module or host function |
| `add`, `sub`, `mul`, `div`, `mod` | Arithmetic |
| `if (stack) { ... } else { ... }` | Conditional branch |
| `while (stack) { ... }` | Loop |
| `dup`, `pop`, `swap` | Stack manipulation |
| `ret` | Return top of stack |

### Module Metadata

```
.metadata {
    spec objectrt = "1.0"
    require [
        Memory.Managed,
    ]
}
```

---

## CLI

The `objectrt` CLI can scan, compile, run, and bundle `.oil` files:

```bash
# Scan a module (dump types and methods)
objectrt -s game.oil

# Compile to .orbt binary format
objectrt -c game.oil -o game.orbt

# Run a module (interpreter)
objectrt game.oil

# Run with the reflection JIT
objectrt -j game.oil

# Call a specific method with arguments
objectrt game.oil -m Calculator.Add 3 4

# Emit generated C# source for debugging
objectrt game.oil --emit ./generated/

# Bundle into a standalone executable
objectrt game.oil -b --rid win-x64,linux-x64,osx-arm64
```

---

## Bundling

`BundleDriver` produces self-contained executables from compiled `.orbt` modules:

- The module is embedded as a manifest resource in a generated C# host
- Binding assemblies are copied alongside the bundle
- Supports framework-dependent or self-contained (per-RID) publishing
- Works with any `IHostedRuntime` implementation — both the generic `ObjectRT.Runtime.Runtime` and the Contract language runtime

```csharp
var spec = new BundleSpec
{
    HostType = typeof(Runtime),
    BindingAssemblyPaths = ["MyBindings.dll"],
    Rids = ["win-x64", "linux-x64"],
    SingleFile = true,
};

BundleDriver.Bundle(spec, module, outputPath);
```

---

## Debugging (DAP)

ObjectRT ships with a Debug Adapter Protocol server that supports:

- **Breakpoints** (set / clear by file + line)
- **Stepping** (step in, step over, step out, continue)
- **Variable inspection** (locals, statics, arguments, stack)
- **Source maps** (bytecode offset → original source line)

The DAP server is language-agnostic — a host supplies an `IDapProgramLoader` that compiles sources and wires up the runtime; the adapter drives debugging through `InterpreterDebugState`.

---

## Project Structure

```
ObjectRT/
├── src/
│   ├── ObjectRT.Abstractions/     Core interfaces (IHostedRuntime, IHostedRuntimeSetup)
│   ├── ObjectRT.Runtime/          Runtime host, resolvers, bundling, reflection
│   ├── ObjectRT.VM/               Interpreter, JIT, compiler, Value, ExecutorBase
│   ├── ObjectRT.SourceGenerator/  Roslyn generators for proxy classes
│   ├── ObjectRT.Cli/              CLI entry point
│   ├── ObjectRT.Dap/              Debug Adapter Protocol server
│   └── csharp/                    NativeAOT static library target
├── examples/
│   ├── *.oil                      ObjectIL example scripts
│   ├── MonoGameDemo/              MonoGame host integration demo
│   ├── NesDemo/                   NES emulator host demo
│   ├── NesEmulator/               .NET NES emulator (host library)
│   └── ScriptingDemo/             Minimal scripting host demo
├── stdlib/                        Standard library (IO bindings)
├── docs/                          Typst specification documents
│   ├── ObjectRT.typ               Full runtime spec
│   ├── ObjectIL.typ               Instruction set spec
│   ├── VM-IMPL.typ                VM implementation notes
│   ├── JIT-IMPL.typ               JIT implementation notes
│   ├── RUNTIME-IMPL.typ           Runtime implementation notes
│   └── fob-encoding.typ           ORBT binary format spec
└── test.oil                       Test script
```

---

## Building

ObjectRT targets **.NET 10** (`net10.0`).

```bash
# Build the CLI
dotnet build src/ObjectRT.Cli/ObjectRT.Cli.csproj

# Build everything
dotnet build ObjectRT.slnx

# Run an example
dotnet run --project src/ObjectRT.Cli -- examples/hello.oil
```

---

## Documentation

Detailed specifications live in `docs/` (Typst format, with compiled PDFs):

- **[ObjectRT V1 Spec](docs/ObjectRT.typ)** — abstract machine, type system, storage model, execution semantics
- **[ObjectIL Spec](docs/ObjectIL.typ)** — instruction set and text format
- **[FOB Encoding](docs/fob-encoding.typ)** — ORBT binary format
- **[VM Implementation Notes](docs/VM-IMPL.typ)** — executor architecture, Value struct, call dispatch
- **[JIT Implementation Notes](docs/JIT-IMPL.typ)** — Roslyn reflection JIT internals
- **[Runtime Implementation Notes](docs/RUNTIME-IMPL.typ)** — resolvers, bundling, host integration
- **[Frontends Implementation Notes](docs/FRONTENDS-IMPL.typ)** — language frontend design

---

## Related Projects

- **[Contract lang](https://github.com/Fy-nite/Contract)** — the Contract language compiler, which uses ObjectRT as its runtime backend

