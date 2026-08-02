namespace NesEmulator;

/// <summary>
/// Top-level NES machine. Load a ROM, reset, then step whole frames:
/// each frame runs ~29781 CPU cycles while the PPU is clocked 3x.
/// </summary>
public sealed class Nes
{
    public Bus Bus { get; } = new();
    public Cpu Cpu { get; }
    public Ppu Ppu { get; }
    public Cartridge? Cartridge { get; private set; }
    public Controller Controller1 => Bus.Controller1;
    public Controller Controller2 => Bus.Controller2;

    /// <summary>The current frame's 256x240 framebuffer (0xAARRGGBB).</summary>
    public uint[] Frame => Ppu.FrameBuffer;

    public bool FrameRendered;

    public Nes()
    {
        Cpu = new Cpu(Bus);
        Ppu = new Ppu(Cpu);
        Bus.Cpu = Cpu;
        Bus.Ppu = Ppu;
    }

    public void Load(byte[] rom)
    {
        Cartridge = new Cartridge(rom);
        Cartridge.IrqCallback = Cpu.Irq;
        Bus.Cart = Cartridge;
        Ppu.Cart = Cartridge;
    }

    public void Reset()
    {
        Cpu.Reset();
        Ppu.Reset();
        FrameRendered = false;
    }

    /// <summary>Run one full frame (~29781 CPU cycles).</summary>
    public void StepFrame()
    {
        ulong target = Cpu.Cycles + 29781;
        while (Cpu.Cycles < target)
        {
            Cpu.Step();
            Ppu.Clock(3);
        }
        Ppu.RenderFrame();
        FrameRendered = true;
    }

    /// <summary>Run a fixed number of frames.</summary>
    public void RunFrames(int count)
    {
        for (int i = 0; i < count; i++)
            StepFrame();
    }

    // ── Trace / debug ─────────────────────────────────────────────

    /// <summary>
    /// Step the machine cycle-by-cycle until a stop condition fires.
    /// Returns the number of full frames completed.
    /// </summary>
    /// <param name="stop">Called before each CPU instruction. Return true to stop.</param>
    /// <param name="maxFrames">Safety cap, 0 = no limit.</param>
    public int TraceUntil(Func<Nes, bool> stop, int maxFrames = 0)
    {
        var c = Cpu;
        var p = Ppu;
        int frame = 0;
        ulong frameCycle = c.Cycles;

        var shouldStop = false;

        c.TraceStep = (op, pc, a, x, y, s, flags, cyc) =>
        {
            if (stop(this)) shouldStop = true;
            return !shouldStop; // false = stop this instruction
        };

        try
        {
            while (true)
            {
                c.Step();
                p.Clock(3);

                if (shouldStop)
                    break;

                if (c.Cycles - frameCycle >= 29781)
                {
                    frame++;
                    frameCycle = c.Cycles;
                    p.RenderFrame();
                    FrameRendered = true;
                }
            }
        }
        finally
        {
            c.TraceStep = null;
        }

        return frame;
    }

    /// <summary>
    /// Step until the CPU executes an instruction at a given address.
    /// </summary>
    public int TraceUntilPc(ushort pc, int maxFrames = 30)
        => TraceUntil(n => n.Cpu.Pc == pc, maxFrames);

    /// <summary>
    /// Step until a given PPU scanline + cycle.
    /// </summary>
    public int TraceUntilScanline(int scanline, int maxFrames = 30)
        => TraceUntil(n => n.Ppu.Scanline == scanline && n.Ppu.Cycle >= 0, maxFrames);

    /// <summary>
    /// Step until the CPU writes a given value to a given address.
    /// </summary>
    public int TraceUntilStore(ushort addr, byte val, int maxFrames = 30)
    {
        bool seen = false;
        var b = Bus;
        try
        {
            Bus.WriteTrace = (a, v) => { if (a == addr && v == val) seen = true; };
            return TraceUntil(_ => seen, maxFrames);
        }
        finally { Bus.WriteTrace = null; }
    }

    /// <summary>Dump key state to a multi-line string for diagnostics.</summary>
    public string DumpState()
    {
        var c = Cpu;
        var p = Ppu;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"frame-rendered={FrameRendered} scanline={p.Scanline} cycle={p.Cycle}");
        sb.AppendLine($"pc=0x{c.Pc:X4} a=0x{c.A:X2} x=0x{c.X:X2} y=0x{c.Y:X2} s=0x{c.S:X2} p=0x{c.P:X2}");
        sb.AppendLine($"ppuctrl=0x{p.PpuCtrl:X2} ppumask=0x{p.PpuMask:X2} ppustatus=0x{p.PpuStatus:X2} ppuaddr=0x{p.PpuAddr:X4}");
        sb.AppendLine($"ctrl1-A={Bus.Controller1.A} B={Bus.Controller1.B} sel={Bus.Controller1.Select} start={Bus.Controller1.Start} up={Bus.Controller1.Up} dn={Bus.Controller1.Down} lt={Bus.Controller1.Left} rt={Bus.Controller1.Right}");
        sb.AppendLine($"cycles={c.Cycles} nmi-count={c.NmiCount} dma-count={Bus.DmaCount}");
        var oamY = string.Join(" ", Enumerable.Range(0, 8).Select(i => $"{p.Oam[i * 4]:X2}"));
        sb.AppendLine($"oam-y[0..7]={oamY}");
        return sb.ToString();
    }
}
