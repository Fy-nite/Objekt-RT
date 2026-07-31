namespace NesEmulator;

/// <summary>NES controller state.</summary>
public sealed class Controller
{
    public bool A, B, Select, Start, Up, Down, Left, Right;

    private byte _shift;
    private int _index;

    /// <summary>Write strobe: bit0=1 latches buttons + resets shift counter.</summary>
    public void Write(byte v)
    {
        if ((v & 1) != 0)
        {
            _shift = (byte)(
                (A ? 1 : 0) | (B ? 2 : 0) | (Select ? 4 : 0) | (Start ? 8 : 0)
                | (Up ? 16 : 0) | (Down ? 32 : 0) | (Left ? 64 : 0) | (Right ? 128 : 0));
            _index = 0;
        }
    }

    /// <summary>Read the next bit: button states shift out LSB-first, then 1s.</summary>
    public byte Read()
    {
        byte b;
        if (_index < 8)
        {
            b = (byte)((_shift >> _index) & 1);
            _index++;
        }
        else
        {
            b = 1;
        }
        return b;
    }
}

/// <summary>CPU address bus: RAM, PPU registers, OAM DMA, controllers, cartridge.</summary>
public sealed class Bus
{
    public Cpu? Cpu;
    public Ppu? Ppu;
    public Cartridge? Cart;
    public Controller Controller1 { get; } = new();
    public Controller Controller2 { get; } = new();

    // Diagnostics
    public int DmaCount;
    public int SpriteBufferWriteCount;

    /// <summary>Optional write tracer: fires on every CPU write address+value.</summary>
    public Action<ushort, byte>? WriteTrace;

    private readonly byte[] _ram = new byte[0x800];

    public byte DebugRam(int addr) => _ram[addr & 0x7FF];

    public byte Read(ushort addr)
    {
        if (addr < 0x2000) return _ram[addr & 0x7FF];
        if (addr < 0x4000) return Ppu!.ReadReg((ushort)(0x2000 + (addr & 7)));
        if (addr == 0x4016) return Controller1.Read();
        if (addr == 0x4017) return Controller2.Read();
        if (addr >= 0x4020 && Cart != null) return Cart.ReadPrg(addr);
        return 0;
    }

    public void Write(ushort addr, byte v)
    {
        WriteTrace?.Invoke(addr, v);

        if (addr < 0x2000)
        {
            _ram[addr & 0x7FF] = v;
            if (addr >= 0x0200 && addr < 0x0300)
                SpriteBufferWriteCount++;
            return;
        }
        if (addr < 0x4000) { Ppu!.WriteReg((ushort)(0x2000 + (addr & 7)), v); return; }

        switch (addr)
        {
            case 0x4014:
            {
                // OAM DMA: copy 256 bytes from CPU RAM page to OAM.
                DmaCount++;
                int start = v << 8;
                var oam = Ppu!.Oam;
                for (int i = 0; i < 256; i++)
                    oam[i] = Read((ushort)(start + i));
                Cpu!.DmaCycles(513);
                break;
            }
            case 0x4016:
                Controller1.Write(v);
                Controller2.Write(v);
                break;
            default:
                if (addr >= 0x4020 && Cart != null) Cart.WritePrg(addr, v);
                break;
        }
    }
}
