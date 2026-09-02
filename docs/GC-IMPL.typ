#import "@preview/xyznote:0.5.0": *

#set text(font: "Fira Code", size: 11pt)
#show: xyznote.with(
  title: "ObjectRT GC — Implementation Notes",
  author: "charlie santana - Finite",
  abstract: "V1 precise stop-the-world mark-sweep, free-handle list, external-handle bridging, safepoints, and seams for compaction / generational.",
  createtime: "2026-09-03",
  lang: "au",
)

#set heading(numbering: "1.")

= Overview

V1 is a *precise, non-moving, single-heap, cooperative stop-the-world mark-sweep* with a free-handle list. No compaction, no generations. The design leaves a handle-abstraction seam (`VMHeap.GetHeapBuffer`) so V2 compacting and V3 generational/incremental do not require rewriting `Value`.

Invariants enforced:

+ *Handles are indices.* `Value.Raw` with `Tag==Obj && !IsExternal` is a `uint` index into `VMHeap` slots (`ExecutorState.cs`). No moving in V1.
+ *Precise.* Every pointer is a `Value` tag test; heap buffers are defined as `Value[InstanceSize/16]` (`Value.cs:16`, `CompiledModule.cs:FieldSlotSize=16`).
+ *Cooperative STW.* GC only runs when every *executing* interpreter is parked or in native. Idle interpreters are GC-safe.
+ *External handles are weak slots.* `List<object?> _externals` is a weak table; a slot is alive iff its handle is reachable from VM roots.

Build order (seams first):

```
PR1 VMHeap (no GC) -> PR2 Safepoints (no collection) -> PR3 Mark/Sweep (explicit) -> PR4 Pressure/OOM -> PR5 Tests
```

= Heap sizing vs GC triggering

`List<byte[]?> Heap` contains null holes. `Heap.Count` (aliased as `HeapCapacitySlots`) is *physical slots ever bumped*, not pressure. All pressure uses bytes.

#table(
  columns: 3,
  align: (left, left, left),
  table.header([*Metric*], [*Definition*], [*Source*]),
  [`HeapCapacitySlots`], [ `Heap.Count` — slots ever bumped, includes null holes ], [`VMHeap` ],
  [`FreeSlots`], [ `VMHeap._freeHandles.Count` ], [`VMHeap` ],
  [`AllocatedBytes`], [ `sum Heap[i].Length` for non-null slots (live+dead before sweep) ], [`VMHeap._allocatedBytes` ],
  [`LiveBytes`], [ sum for marked live after GC ], [ GC sweep ],
  [`LiveSlots`], [ `Capacity - FreeSlots` post-sweep (derived) ], [ derived ],
  [`NextGCThreshold`], [ adaptive byte threshold ], [ `MarkSweepGC._nextThreshold` ],
  [`MaximumHeapSizeBytes`], [ hard cap sum live buffers, 0=uncapped ], [ `HeapOptions` ],
  [`InitialHeapCapacitySlots`], [ `List` capacity reservation, ~2048 ≈ 32 KiB slots ], [ `HeapOptions` ],
)

- *No* `AllocatedSlots` public — ambiguous before sweep.
- `InitialHeapCapacitySlots` is *capacity*, not memory; `MaximumHeapSizeBytes` is logical bytes.
- Trigger is `AllocatedBytes >= NextGCThreshold` or explicit `GC.Collect()`.
- After each GC: `Next = clamp(Live*GrowthFactor, Live+MinHeadroom, InitialThreshold)` (`GCOptions`).

= Public configuration

All public GC vocabulary lives in `ObjectRT.Abstractions` (no `VM -> Runtime` inversion):

```
ObjectRT.Abstractions/GC/
  HeapOptions.cs          { InitialHeapCapacitySlots = 2048, MaximumHeapSizeBytes = 0 }
  GCOptions.cs            { Collector=MarkSweep, InitialThreshold=64 KiB, GrowthFactor=2.0, MinHeadroom=16 KiB }
  GCCollectorKind.cs      { MarkSweep }
  GCReason.cs             { Explicit, Threshold, AllocationFailure, OOMProbe }
  GCStats.cs              { CollectionCount, TotalPause, LastPause, AllocatedBytes, LiveBytes, HeapCapacityBytes/Slots, FreeSlots, ReclaimedBytes/SlotsLast, LastReason, LastCollectionUtc }
  RuntimeOptions.cs       { Heap, GC }
ObjectRT.Abstractions ← ObjectRT.VM, ObjectRT.Runtime
```

`Runtime` surface (additive):

```csharp
public Runtime(RuntimeOptions? opts = null)
public GCStats GCStats { get; }
public bool CollectGC(GCReason reason = Explicit)
```

`GCStats` setters are public snapshot (`Clone()`); `VM` updates them.

= GC abstraction

`Interpreter` knows *safepoints*, not *which* GC exists:

```csharp
// ObjectRT.VM/GC/MarkSweepGC.cs — internal
class MarkSweepGC {
  GCStats Stats { get; }
  long NextThreshold { get; }
  bool ShouldCollect(long allocatedBytes) => allocated >= NextThreshold;
  bool Collect(ExecutorState state, GCReason reason) // STW, returns true if ran
}
```

`ExecutorState` owns `VMHeap`, `Coordinator`, `GC`, `GCStats`. `ExecutorBase.AllocObject` is the single pressure site.

Future `Collector=Generational` swaps `MarkSweepGC` without touching `Interpreter.cs`. Generational collection will primarily require write-barrier integration at VMHeap write sites, plus collector-specific root/young-generation handling.

= VM heap abstraction

`ObjectRT.VM/Memory/VMHeap.cs` — the seam for V2 handle table:

```csharp
byte[]? GetHeapBuffer(uint handle)
bool    TryGetBuffer(uint handle, out byte[]? buf)
Span<byte> GetHeapSpan(uint handle)
void    SetHeapBuffer(uint handle, byte[]? buf)
uint    Allocate(uint instanceSize) // pop FreeHandles or bump, +AllocatedBytes
void    Free(uint handle)           // null slot, -AllocatedBytes, push FreeHandles
void    Clear()
(int capacity, int free, long allocated) SnapshotStats()
```

All 18 `Heap[` sites migrated:

- `Interpreter.cs:ldfld/stfld` (`MemoryMarshal.Read/Write` via `State.TryGetHeapBuffer`), `ReadDelegate`, `Reset:State.ClearHeap`
- `ReflectionJit.cs:ldfld/stfld` emitted `x.GetHeapBuffer`, `Reset:State.ClearHeap`
- `StructMarshaller.cs:Pack/Unpack` `TryGetHeapBuffer`
- `Runtime.cs:AllocateObject/GetField/SetField` via `AllocObject`/`TryGetHeapBuffer`
- `ExecutorBase.cs:AllocObject` via `State.VMHeap.Allocate`

#strong[V1] `handle == index`; #strong[V2] `handle -> Table[handle].phys`; #strong[V3] `handle -> Table[handle]{phys,gen,flags,age}`. `Value.Raw` never changes.

= Handle semantics V1

`Value.FromObj(handle)` stores raw index; `ExternalHandleFlag=0x80000000` distinguishes CLR handles (`ExecutorState.cs:90`). Hole = `null` slot. `ObjectTypes` removed on free, added on alloc. `0x40000000` reserved for future generation bits. No handle table in V1.

= Stale-handle / ABA

Live invariant:

```
live(handle) ⇔ reachable from (StaticFields ∪ ⋃ Stacks/Frames/Exception/DirectStack ∪ Scavenge(live externals)) transitively via Tag==Obj
```

Scan: `MarkValue` on `Obj` marks heap handle `marked[h]=true` or marks external `liveExternals[idx]=true`. After initial drain, scavenge live externals' `object[]`/`List<object>` elements for boxed `uint` heap handles and `ThreadHandle.DelegateHandle` (via reflection to avoid `VM->Runtime` cycle); push them and re-drain to fixpoint. Sweep only frees `!marked && buf!=null`; pushes handle; clears `ObjectTypes`. External sweep nulls `!liveExternal` slots (stable indices, handle becomes invalid, `GetExternal` returns null).

Sufficient: any live boxed `uint` in a live `object[]` keeps its target alive (scavenged). Dead container's boxed handles are ignored (container unreachable). Host globals in `InterfaceHostResolver._hosts` are *not* VM roots — long-lived stashes must go via `_externals` container.

= Stop-the-world

One `SafepointCoordinator` per `ExecutorState` (`ExecutorState.cs:Coordinator`), shared by all `Interpreter`s over that state. `ConcurrentDictionary<Interpreter,byte> _live`, `volatile _gcRequested`, `ManualResetEventSlim _gcDone`, `Monitor _lock`.

+ *Ownership:* only holder of `_lock` may set `Requested` and drive GC.
+ *Registration:* `Interpreter` ctor `Register`; `Runtime` wraps ephemeral interpreters (`CallMethodViaVm` re-entry, `InvokeDelegate`, `SpawnThread`/`StartThread` lambdas, `ResetExecutor`/`LoadModule` replacement) with `try/finally Unregister`. Primary interpreter stays registered.
+ *State machine:* `Running --poll--> Parked` if `Requested && !IsInNative`; `InNative` (around `DirectNativeCall`/`NativeCallHandler`) is *logically parked* (stack stable). #strong[Strengthened invariant:] `InNative` is GC-safe only if native cannot mutate VM heap/roots except via safepoint-aware re-entry (`CallMethodViaVm`/`InvokeDelegate` which `Register`+park).
+ *Poll:* `Interpreter.Execute` inner loop top `CheckSafepoint()` before `DebugState.CheckPause`; `RunFunction` entry also polls. Spawn during GC parks immediately in `Register`.
+ *Rendezvous:* `RequestStop` sets `Requested`+`Reset`, `WaitForWorldStopped` waits until every `IsExecuting` interpreter is `IsParked||IsInNative` (idle interpreters are GC-safe, not counted). Timeout 5s -> fail.
+ *Resume:* `Resume` clears `Requested`, `Set`, `PulseAll`.

Testable invariants: `ParkedCount <= LiveCount`, `Requested => all Running eventually Parked|InNative`, `no AllocObject while Requested && !holding lock`, `after Collect Requested==false && Done.IsSet`.

= Root enumeration

`MarkSweepGC` takes snapshot over `Coordinator.LiveSnapshot()` after `WorldStopped` (no lock needed for stacks; STW guarantees no `Push`/`Pop`).

#table(
  columns: 2,
  table.header([*Root*], [*Owner -> GC access*]),
  [`StaticFields`], [`ExecutorState.StaticFields` -> `IRootSet.EnumerateStaticFields()` ],
  [`_stack`], [`Interpreter._stack` -> `StackForGC` ],
  [`Frame.Locals`], [`Interpreter._frames[].Locals` -> `FramesForGC` ],
  [`ExceptionFrame.PendingException`], [`Interpreter._exceptionHandlers` -> `ExceptionHandlersForGC` ],
  [`_directStack` window], [`Interpreter._directStack` -> `DirectStackForGC` when `IsInNative` (conservative 256) ],
  [`Heap fields`], [ `Heap` buffers themselves (transitive via `Drain` stride 16) ],
)

`Heap` itself and `ObjectTypes`/`_strings`/`vtables` are *not* roots.

= External handles

`_externals: List<object?>` is a *weak-slot table* (`liveExternals[]`). Slot alive iff its `Flag|idx` handle is reachable from VM roots. VM->CLR via `ValueToObject` boxes `uint` handle; `InternExternal` wraps. CLR->VM via `MarshalValue` and scavenge.

Tables:

- `_externals` + `object[]` (`newarr`), `List<object>` (`List.Map`), `ThreadHandle.DelegateHandle` (reflection) — scavenge sources.
- `_strings` — not GC'd V1 (intern table).
- Host globals (`InterfaceHostResolver._hosts`) — strong CLR roots, weak VM roots (not enumerated).

Example `arr[0]=heapHandle` where `arr` handle is in `StaticFields`: `arr` handle marked liveExternal -> scavenged -> `heapHandle` marked -> heap kept.

= Allocation / GC interaction

```
AllocObject(typeIdx):
  instanceSize = Types[typeIdx].InstanceSize
  if Maximum>0 && Allocated+size > Maximum:
     Collect(AllocationFailure)
     if Allocated+size > Maximum -> return VmError OutOfBounds
  if ShouldCollect(Allocated):
     Collect(Threshold)
  handle = VMHeap.Allocate(size) // pop FreeHandles or bump
  RecordObjectType(handle, typeIdx)
  return handle
Collect(reason):
  RequestStop; WaitForWorldStopped
  try { mark roots; Drain; Scavenge live externals; Drain fixpoint; sweep; recalc NextThreshold; stats }
  finally { Resume }
```

Single large object larger than `NextThreshold`: one GC then bump regardless. Explicit `rt.CollectGC(Explicit)` even below threshold. `AllocatedBytes` excludes free holes.

= GC statistics

`GCStats` snapshot updated in `MarkSweepGC.UpdateStats` (`MarkSweepGC.cs:UpdateStats`):

```
CollectionCount, TotalPause, LastPause, AllocatedBytes, LiveBytes,
HeapCapacitySlots, FreeSlots, HeapCapacityBytes, ReclaimedBytes/SlotsLast,
LastReason, LastCollectionUtc
```

Exposed via `Runtime.GCStats`, `ExecutorBase.GCStats`, `ExecutorState.GCStats`. Guard detailed logging with `ORTRT_GC_DEBUG=1`.

= V1 test plan

Explicit `rt.CollectGC(Explicit)` unless noted:

- *Unreachable*: allocate `Foo` via `AllocateObject`, no root -> after GC `Free==1`, `Allocated==0`.
- *Static root*: `stsfld Program.root` -> `Free==0`, `Live>0`.
- *Graph*: `A.next=B, B.next=C` via `Program.root=A` -> keep 3.
- *Cycle*: `A<->B` no root -> collect both; `A<->B` with `Program.a=A` -> keep both.
- *Locals/Frames*: fat method locals across `call` boundary.
- *Multi-interpreter*: primary `Program.root`, secondary `Interpreter` stack root -> both live; drop secondary stack -> one freed. (`Coordinator.LiveSnapshot` union).
- *External*: `object[] {heapHandle}` rooted via `Program.arr` -> keep; drop arr -> free.
- *Host-global not keep*: `InterfaceHostResolver` stash ignored.
- *Repeated*: 3× GC idempotent, `Free` stable.
- *Free reuse*: freed `h1` next alloc `h2==h1` LIFO, `ObjectTypes` overwritten, buffer zeroed.
- *Deep*: 5000 `next` chain from `Program.head` keeps all (iterative worklist no overflow); drop head frees all.
- *Threshold*: lower `_nextThreshold=32` via reflection, 10× alloc -> `Capacity` bounded, `Free>0`.
- *OOM*: live 32 + `Maximum=32`, next alloc -> `OutOfBounds`.
- *STW race*: 4 threads alloc + explicit `Collect` concurrently, `Parked==Live` invariant, no lost handles.

Passed 18/18 in GCTest.

= File layout

```
ObjectRT.Abstractions/GC/
  GCOptions.cs, HeapOptions.cs, GCCollectorKind.cs, GCReason.cs, GCStats.cs, RuntimeOptions.cs
ObjectRT.VM/
  IExecutor.cs, ExecutorBase.cs (+AllocObject pressure), ExecutorState.cs (+VMHeap, Coordinator, GC, Stats, TryGetHeapBuffer),
  Value.cs, CompiledModule.cs, Interpreter.cs (+IsParked/IsInNative/CheckSafepoint), ReflectionJit.cs, StructMarshaller.cs
  Memory/VMHeap.cs
  GC/MarkSweepGC.cs
  GC/Safepoint/SafepointCoordinator.cs
ObjectRT.Runtime/
  Runtime.cs (+GCStats/CollectGC, HeapOptions/GCOptions passthrough, Register/Unregister ephemeral interpreters)
docs/
  GC-IMPL.typ  — this file
  VM-IMPL.typ, RUNTIME-IMPL.typ — updated with GC refs
```

= Future seams

V2 handle table: `Value.Raw` unchanged; `VMHeap.GetHeapBuffer` dereferences `Table[handle].phys` (physical move only). Reserve `0x40000000` generation bit.

V3 generational: add `VMHeap.WriteField` barrier (card/remembered set) + `SafepointCoordinator` young-gen handling. Interpreter stays GC-agnostic (`Value` unchanged).

PR ordering frozen: #strong[PR1 VMHeap -> PR2 Safepoints -> PR3 Mark/Sweep -> PR4 Pressure/OOM -> PR5 Tests].
