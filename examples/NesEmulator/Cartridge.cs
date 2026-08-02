namespace NesEmulator;

/// <summary>Mirroring modes for nametable access.</summary>
public enum Mirroring
{
    Horizontal = 0,   // NT0 & NT1 share, NT2 & NT3 share
    Vertical = 1,     // NT0 & NT2 share, NT1 & NT3 share
    FourScreen = 2,   // no mirroring
    Single0 = 3,      // all four map to NT0 ($2000)
    Single1 = 4,      // all four map to NT1 ($2400)
}

/// <summary>iNES (.nes) cartridge: header, PRG/CHR, mapper, mirroring.</summary>
public sealed class Cartridge
{
    public byte[] PrgRom { get; }
    public byte[] Chr { get; }          // CHR ROM, or CHR RAM when the ROM declares none
    public bool ChrWritable { get; }
    public Mirroring Mirroring { get; set; }
    public Mapper Mapper { get; }

    public Cartridge(byte[] data)
    {
        if (data.Length < 16 || data[0] != 'N' || data[1] != 'E' || data[2] != 'S' || data[3] != 0x1A)
            throw new InvalidDataException("Not an iNES ROM image");

        int prgBanks = data[4];
        int chrBanks = data[5];
        int mapperId = ((data[7] & 0xF0) >> 4) | (data[6] >> 4);
        bool hasTrainer = (data[6] & 0x04) != 0;
        Mirroring = (data[6] & 0x08) != 0
            ? NesEmulator.Mirroring.FourScreen
            : (data[6] & 0x01) != 0 ? NesEmulator.Mirroring.Vertical : NesEmulator.Mirroring.Horizontal;

        if (prgBanks == 0)
            throw new InvalidDataException("ROM has no PRG banks");

        int offset = 16;
        if (hasTrainer) offset += 512;

        PrgRom = new byte[prgBanks * 16384];
        Array.Copy(data, offset, PrgRom, 0, PrgRom.Length);
        offset += PrgRom.Length;

        if (chrBanks > 0)
        {
            Chr = new byte[chrBanks * 8192];
            Array.Copy(data, offset, Chr, 0, Chr.Length);
            ChrWritable = false;
        }
        else
        {
            Chr = new byte[8192]; // CHR RAM
            ChrWritable = true;
        }

        Mapper = mapperId switch
        {
            0 => new Mapper0(this),
            1 => new Mapper1(this),
            2 => new Mapper2(this),
            4 => new Mapper4(this),
            _ => throw new NotSupportedException($"Mapper {mapperId} is not supported (0/1/2/4 only)"),
        };
    }

    public byte ReadPrg(ushort addr) => Mapper.ReadPrg(addr);
    public void WritePrg(ushort addr, byte v) => Mapper.WritePrg(addr, v);
    public byte ReadChr(ushort addr) => Mapper.ReadChr(addr);
    public void WriteChr(ushort addr, byte v) => Mapper.WriteChr(addr, v);

    /// <summary>Raised by IRQ-capable mappers (MMC3) to trigger a CPU interrupt.</summary>
    public Action? IrqCallback { get; set; }

    /// <summary>Per-scanline tick for IRQ mappers (called by the PPU).</summary>
    public void ScanlineTick() => Mapper.ScanlineTick();

    /// <summary>Ask the host CPU to take an IRQ.</summary>
    public void TriggerIrq() => IrqCallback?.Invoke();
}

// ── Mappers ──────────────────────────────────────────────────────────

public abstract class Mapper
{
    protected Cartridge Cart;
    protected Mapper(Cartridge cart) => Cart = cart;
    public abstract byte ReadPrg(ushort addr);
    public abstract void WritePrg(ushort addr, byte v);
    public abstract byte ReadChr(ushort addr);
    public abstract void WriteChr(ushort addr, byte v);

    /// <summary>Called once per visible scanline while rendering is enabled (IRQ-capable mappers).</summary>
    public virtual void ScanlineTick() { }
}

/// <summary>NROM — no bank switching. 16KB PRG mirrored to $8000 and $C000.</summary>
public sealed class Mapper0 : Mapper
{
    public Mapper0(Cartridge cart) : base(cart) { }

    public override byte ReadPrg(ushort addr)
    {
        if (Cart.PrgRom.Length == 16384)
            return Cart.PrgRom[addr & 0x3FFF];
        return Cart.PrgRom[addr - 0x8000];
    }

    public override void WritePrg(ushort addr, byte v) { }
    public override byte ReadChr(ushort addr) => Cart.Chr[addr];
    public override void WriteChr(ushort addr, byte v)
    {
        if (Cart.ChrWritable) Cart.Chr[addr] = v;
    }
}

/// <summary>UxROM — 16KB PRG bank switch at $8000-$BFFF, last bank fixed at $C000.</summary>
public sealed class Mapper2 : Mapper
{
    private int _bank;

    public Mapper2(Cartridge cart) : base(cart) { }

    public override byte ReadPrg(ushort addr)
    {
        if (addr >= 0xC000)
            return Cart.PrgRom[Cart.PrgRom.Length - 0x4000 + (addr - 0xC000)];
        return Cart.PrgRom[_bank * 0x4000 + (addr - 0x8000)];
    }

    public override void WritePrg(ushort addr, byte v)
    {
        if (addr < 0xC000)
            _bank = v & 0x0F;
    }

    public override byte ReadChr(ushort addr) => Cart.Chr[addr];
    public override void WriteChr(ushort addr, byte v)
    {
        if (Cart.ChrWritable) Cart.Chr[addr] = v;
    }
}

/// <summary>MMC1 — serial shift register; PRG/CHR bank switching + mirroring control.</summary>
public sealed class Mapper1 : Mapper
{
    private byte _shift = 0x10;
    private byte _control = 0x0C;
    private byte _chrBank0, _chrBank1, _prgBank;

    public Mapper1(Cartridge cart) : base(cart) { }

    public override byte ReadPrg(ushort addr)
    {
        int mode = (_control >> 2) & 3;
        int idx;
        if (mode <= 1)
        {
            idx = (_prgBank >> 1) * 0x8000 + (addr - 0x8000);
        }
        else if (mode == 2)
        {
            idx = addr >= 0xC000
                ? Cart.PrgRom.Length - 0x4000 + (addr - 0xC000)
                : addr - 0x8000;
        }
        else
        {
            idx = addr >= 0xC000
                ? _prgBank * 0x4000 + (addr - 0xC000)
                : Cart.PrgRom.Length - 0x8000 + (addr - 0x8000);
        }
        return Cart.PrgRom[idx % Cart.PrgRom.Length];
    }

    public override void WritePrg(ushort addr, byte v)
    {
        if ((v & 0x80) != 0)
        {
            _shift = 0x10;
            _control |= 0x0C;
            return;
        }

        bool complete = (_shift & 1) != 0;
        _shift = (byte)((_shift >> 1) | ((v & 1) << 4));
        if (!complete) return;

        byte val = _shift;
        _shift = 0x10;

        switch ((addr >> 13) & 3)
        {
            case 0:
                _control = val;
                Cart.Mirroring = (_control & 3) switch
                {
                    0 => NesEmulator.Mirroring.Single0,
                    1 => NesEmulator.Mirroring.Single1,
                    2 => NesEmulator.Mirroring.Vertical,
                    _ => NesEmulator.Mirroring.Horizontal,
                };
                break;
            case 1: _chrBank0 = (byte)(val & 0x1F); break;
            case 2: _chrBank1 = (byte)(val & 0x1F); break;
            case 3: _prgBank = (byte)(val & 0x0F); break;
        }
    }

    public override byte ReadChr(ushort addr)
    {
        if ((_control & 0x10) != 0)
        {
            int bank = addr >= 0x1000 ? _chrBank1 : _chrBank0;
            return Cart.Chr[bank * 0x1000 + (addr & 0xFFF)];
        }
        return Cart.Chr[(_chrBank0 & 0x1E) * 0x1000 + addr];
    }

    public override void WriteChr(ushort addr, byte v)
    {
        if (!Cart.ChrWritable) return;
        if ((_control & 0x10) != 0)
        {
            int bank = addr >= 0x1000 ? _chrBank1 : _chrBank0;
            Cart.Chr[bank * 0x1000 + (addr & 0xFFF)] = v;
        }
        else
        {
            Cart.Chr[(_chrBank0 & 0x1E) * 0x1000 + addr] = v;
        }
    }
}
