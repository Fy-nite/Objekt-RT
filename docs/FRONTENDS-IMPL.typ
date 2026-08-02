#import "@preview/xyznote:0.5.0": *
#import "helpers.typ": *
#set text(font: "Fira Code", size: 11pt)
#show: xyznote.with(
  title: "ObjectRT Frontends — Implementation Notes",
  author: "charlie santana - Finite",
  abstract: "MonoGame bindings, NES emulator frontend, known limits and bugs fixed.",
  createtime: "2026-07-31",
  lang: "au",
)

#set heading(numbering: "1.")

= MonoGame bindings (`src/ObjectRT.MonoGame/`)

`MonoGameHost` is a static façade that connects ObjectRT scripts to a MonoGame
`Game`. Scripts call host methods through the standard `call` opcode via
`[IRHostBinding]` interfaces with source-generated dispatch.

== Setup

```csharp
MonoGameHost.Attach(game);      // in Game.Initialize
MonoGameHost.Register(rt);      // exposes MonoGame.* bindings to scripts
MonoGameHost.BeginFrame();      // top of Game.Update (captures input state)
MonoGameHost.UpdateFrame(time); // in Game.Update (updates timing values)
```

== Binding interfaces

#figure(
  align(center)[#table(
    columns: (30%, 25%, 45%),
    align: (left, left, left),
    table.header([#strong[Interface]], [#strong[Script Name]], [#strong[Methods]]),
    table.hline(),
    [`IMonoGameScreen`], [`MonoGame.Screen`], [`Clear(color)` — packed ARGB; `Width()` / `Height()`; `DeltaTime()` / `TotalTime()`],
    [`IMonoGameSprite`], [`MonoGame.Sprite`], [`Begin()` / `End()`; `Draw(tex,x,y)` — centers texture; `DrawColored` / `DrawScaled` / `DrawRotated`; `FillRect(x,y,w,h,color)` — no asset needed],
    [`IMonoGameTexture`], [`MonoGame.Texture`], [`CreateSolid(name,w,h,color)`; `CreateChecker(name,w,h,colorA,colorB)`; `Remove(name)`],
    [`IMonoGameInput`], [`MonoGame.Input`], [`KeyDown(key)` / `KeyPressed(key)` — virtual-key ints; `MouseX()` / `MouseY()`; `MouseDown(btn)` / `MousePressed(btn)` — 0=left,1=right,2=middle],
    [`IMonoGameColor`], [`MonoGame.Color`], [`Argb(a,r,g,b)` / `Rgb(r,g,b)`; named colors (`White()`, `Red()`, …)],
    [`IMonoGameMath`], [`MonoGame.Math`], [`RandomFloat()`, `RandomRange(min,max)`, `RandomInt(max)`],
    [`IMonoGameLog`], [`MonoGame.Log`], [`Log(s)`, `LogInt(v)`, `LogFloat(v)`],
  )],
  kind: table,
  caption: [MonoGame host binding interfaces],
)

Sprite draws center the texture at (x, y). Colors are packed ARGB `int32`
values. Textures are registered via `MonoGameHost.SetTexture(name, tex)`
and referenced by name in script `ldstr` / `call` chains.

== Demo (`examples/MonoGameDemo/`)

A bouncing ball driven entirely by `game.oil` through `call` opcodes into the
MonoGame bindings. The C\# side only owns the window and game loop; the script
handles all physics, rendering, and input.

= NES emulator

For a complete overview of the NES core architecture see the project README.
This section covers frontend integration and bugs discovered during
development.

== Core files (`src/NesEmulator/`)

#figure(
  align(center)[#table(
    columns: (25%, 75%),
    align: (left, left),
    table.header([#strong[File]], [#strong[What]]),
    table.hline(),
    [`Cartridge.cs`], [iNES loader, `Mirroring` enum, `Mapper` base class.],
    [`Mapper0.cs`], [NROM — no bank switching.],
    [`Mapper1.cs`], [MMC1 — serial shift register; PRG/CHR banking + mirroring control.],
    [`Mapper2.cs`], [UxROM — 16KB PRG bank switch; last bank fixed at `$C000`.],
    [`Mapper4.cs`], [MMC3 — 8KB PRG / 1–2KB CHR banking; scanline IRQ counter; PRG-RAM at `$6000`.],
    [`Cpu.cs`], [Full official opcodes + common unofficial (LAX/SAX/DCP/ISB/SLO/RLA/SRE/RRA, ANC/ALR/ARR/SBX). Table-driven cycles with page-cross and branch penalties. NMI/IRQ/BRK with correct flag semantics.],
    [`Ppu.cs`], [Pattern tables, nametables (horizontal/vertical/four-screen mirroring), palette RAM (`$3F10` ↔ `$3F00` mirror), OAM DMA, 8×8/8×16 sprites with flip/priority. Per-scanline sprite overflow (bit 5) and sprite‑0 hit (bit 6) evaluation.],
    [`Bus.cs`], [CPU address decoding: `$0000`–RAM, `$2000`–PPU, `$4014`–DMA, `$4016`/`$4017`–controllers, `$4020`+–cartridge. Controller strobe latching.],
    [`Controller.cs`], [Standard NES controller: strobe latches button states, reads shift out LSB‑first.],
    [`Nes.cs`], [Top-level: `Load` / `Reset` / `StepFrame` (~29 781 cycles) / `RunFrames`. `IrqCallback` on cartridge for MMC3 scanline IRQ.],
    [`TestRom.cs`], [Hand-assembled iNES ROM (mapper 0): boots, loads palette, fills nametable → perfect checkerboard. No copyrighted content.],
    [`Disasm6502.cs`], [Full 6502 disassembler: all official + common unofficial opcodes, branch target resolution, address-mode annotation.],
  )],
  kind: table,
  caption: [NES emulator core files],
)

== Frontend (`examples/NesDemo/`)

The game loop is pure C\# (no ObjectIL dispatch overhead):

```csharp
_nes.ReadInput();   // maps keyboard → controller
_nes.StepFrame();   // ~29 781 CPU cycles + PPU render
_nes.UploadFrame(); // framebuffer → Texture2D
_nes.Draw(768,720); // SpriteBatch draw at 3× upscale
```

#strong[Controls]: arrows = D‑Pad, X = A, Z = B, Enter = Start, Right Shift = Select,
R = soft reset, Esc = quit. Supports `--rom path.nes` and `--frames N`.

== Bugs fixed

#strong[JSR / RTS]: The 6502 `JSR` instruction pushes the address of the *last*
byte of the JSR instruction (return‑address − 1); `RTS` pops and adds 1. The
implementation was pushing `Pc` instead of `Pc − 1`, so every subroutine
returned one byte past the correct address. The test ROM (straight‑line code
with no subroutines) passed; every real game was broken.

#strong[Nametable mirroring]: The horizontal mirroring formula collapsed every
`$2000`–`$27FF` address onto physical `$2000` (`nt & 0x0800` after masking to 0x0FFF).
Fixed to keep the intra‑table offset: `0x2000 + ((addr & 0x0800) != 0 ? 0x800 : 0) + (addr & 0x3FF)`.

#strong[Sprite‑0 hit]: SMB1 polls `$2002` bit 6 in a spin loop to detect a
specific scanline for its status‑bar split. The PPU now evaluates sprite‑0
overlap with non‑transparent background pixels during frame rendering.
Reading `$2002` clears bits 5–7 (matching real hardware).

#strong[Palette `$3F10` mirror]: mirrors `$3F00` only, not the full 0x10 block.
Full‑block mirroring breaks sprite palettes in many games.

== Known limits

Frame‑based PPU: mid‑frame scroll/sprite tricks (SMB status‑bar split etc.)
are not emulated. Mappers 0/1/2/4 only. No APU/audio. Decimal mode treated
as binary. Sprite‑0 hit flag clears on `$2002` read (real hardware behavior) —
this can cause timing mismatches with games that poll aggressively in NMI
handlers.
