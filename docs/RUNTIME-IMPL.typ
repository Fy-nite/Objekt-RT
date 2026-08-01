 #import "@preview/xyznote:0.5.0": *

#set text(font: "Fira Code", size: 11pt)
#show: xyznote.with(
  title: "ObjectRT Runtime — Implementation Notes",
  author: "charlie santana - Finite",
  abstract: "Method dispatch, host binding system, resolver chain, EmitDir/CacheDir.",
  createtime: "2026-07-31",
  lang: "au",
)

#set heading(numbering: "1.")

= Method dispatch order

When `Runtime.CallMethod<T>(qualifiedName, args)` is invoked, dispatch follows
this chain:

+ Explicit registry (`_nativeMethods`) — fastest path, `string → Delegate`
+ `INativeResolver` chain — `ClrNativeResolver`, then `InterfaceHostResolver`
+ `IExecutor` — module function map, then `NativeCallHandler` fallback

Each resolver is tried in registration order. The first non-null result wins.
The explicit registry is checked before any resolver.

= Host binding system (interface contracts)

Three cooperating pieces provide reflection-free host dispatch — suitable for
NativeAOT builds where `System.Reflection` may be trimmed:

== `[IRHostBinding("Name")]`

Marks a C\# interface as a script-facing host contract. Scripts call methods
through the standard `call` opcode using the binding name:

```csharp
[IRHostBinding("MonoGame.Screen")]
public interface IMonoGameScreen {
    void Clear(int color);
    int  Width();
}
```

```oil
call MonoGame.Screen.Clear(int32)
```

== `HostDispatchGenerator` (source generator)

A Roslyn incremental generator that reads every interface marked with
`[IRHostBinding]` and emits `HostDispatchRegistry.Register(...)` calls with
direct interface casts:

```csharp
HostDispatchRegistry.Register(
    "MonoGame.Screen", "Clear",
    (object h, object?[] a) => {
        ((IMonoGameScreen)h).Clear((int)a[0]!);
        return null;
    });
```

Zero reflection at runtime — the casts are hardwired. This is what keeps host
bindings working when `AllowReflection = false`.

== `InterfaceHostResolver`

An `INativeResolver` that dispatches through two paths in order:

+ Source-generated hardwired adapter from `HostDispatchRegistry` — direct
  interface cast, no reflection.
+ Reflection fallback on the registered interface — convenient during
  development or when the generator is not wired into the project.

`AllowReflection = false` forces generated-only dispatch. The NesDemo
frontend proves this is functional — it runs with reflection disabled.

Register implementations via:

```csharp
rt.RegisterHost("MonoGame.Screen", impl, typeof(IMonoGameScreen));
```

#strong[Note]: `RegisterHost<T>` infers the concrete class type, not the
interface. Use the explicit `(name, impl, typeof(IFace))` overload.

== `ClrNativeResolver`

Reflection-based resolver for CLR static methods. Registered types are
discovered by method name + argument count. `AllowReflection = false`
disables it (NativeAOT toggle). Caches resolved delegates per qualified name.

= Void-native null collision

`TryResolveNative` returns `out bool resolved`. A void method returning `null`
is correctly classified as #strong["resolved and returned null"], not as
#strong["unresolved"]. Resolver returning null delegate = not found.
This was a hard bug: void methods looked like dispatch misses.

= EmitDir / CacheDir

```csharp
Runtime.EmitDir  = "./generated";   // writes {Module}.ObjectRT.g.cs
Runtime.CacheDir = "./cache";       // caches compiled {hash}.dll
```

Set once at startup. `EmitDir` dumps the generated C\# source for every module
compiled through `ReflectionJit`. `CacheDir` saves Roslyn-compiled assemblies
keyed by SHA-256 hash of module content — subsequent runs skip Roslyn entirely
and just `Assembly.Load` the cached DLL from disk.

= Namespace gotchas

- Inside `namespace ObjectRT.MonoGame`, the simple name `Runtime` binds to
  `ObjectRT.Runtime` (the namespace), not `ObjectRT.Runtime.Runtime` (the class).
  Use `using ObjectRTRuntime = ObjectRT.Runtime.Runtime;`.
- `RegisterHost<T>` infers the concrete class type, not the interface.
  Use the explicit overload: `rt.RegisterHost("Name", impl, typeof(IFace))`.
- Generated code needs `#nullable enable` plus `#pragma warning disable`
  suppressions for CS8600, CS8601, CS8602, CS8603, CS8604, CS8605, CS8625
  or the consuming project warns on the generated hard casts.

= Source files

```
src/ObjectRT.Runtime/
├── Runtime.cs                    — host, module loading, method dispatch, JIT config
├── ClrNativeResolver.cs          — reflection-based static method resolution
├── InterfaceHostResolver.cs      — host interface dispatch (generated + reflection)
├── INativeResolver.cs            — resolver interface
├── IRHostBindingAttribute.cs     — [IRHostBinding] attribute
├── HostDispatchRegistry.cs       — generated adapter registry
├── IRClassBindingAttribute.cs    — [IRClassBinding] for script proxies
├── IRMethodBindingAttribute.cs   — [IRMethodBinding] for method overrides
├── IRRuntimeBinding.cs           — late-bound call handle
├── ProxyRegistry.cs              — source-generated proxy registration
└── JitMode.cs                    — interpreter/reflection/LLVM enum
```
