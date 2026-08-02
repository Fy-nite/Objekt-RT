namespace NesEmulator;

/// <summary>
/// MMC3 (TxROM) — the late-NES workhorse mapper.
///   - PRG: two 8KB switchable banks + fixed last bank (two modes).
///   - CHR: six registers — mode A: 2KB+2KB+four 1KB; mode B: four 1KB+2KB+2KB.
///   - PRG-RAM ($6000-$7FFF), mirroring control, scanline IRQ counter.
/// The IRQ counter is ticked once per visible scanline by the PPU (A12-edge
/// approximation) — enough for games that split the screen by scanline.
/// </summary>
public sealed class Mapper4 : Mapper
{
    private readonly byte[] _bank = new byte[8];
    private byte _bankSelect;
    private bool _chrMode;   // bank select bit 6
    private bool _prgMode;   // bank select bit 7
    private bool _prgRamEnabled = true;
    private bool _prgRamWrite = true;
    private readonly byte[] _prgRam = new byte[0x2000];

    private byte _irqLatch, _irqCounter;
    private bool _irqEnabled, _irqReload;

    public Mapper4(Cartridge cart) : base(cart) { }

    private int Prg8KBanks => Cart.PrgRom.Length / 0x2000;
    private int Chr1KBanks => Cart.Chr.Length / 0x400;

    // ── PRG ──────────────────────────────────────────────────────

    public override byte ReadPrg(ushort addr)
    {
        if (addr >= 0x6000 && addr < 0x8000)
            return _prgRamEnabled ? _prgRam[addr - 0x6000] : (byte)0;
        if (addr < 0x8000) return 0;

        if (addr < 0xA000)
            return _prgMode
                ? Cart.PrgRom[addr - 0x8000]                                   // fixed first bank
                : Cart.PrgRom[_bank[6] * 0x2000 + (addr - 0x8000)];
        if (addr < 0xC000)
            return Cart.PrgRom[_bank[7] * 0x2000 + (addr - 0xA000)];
        if (addr < 0xE000)
            return _prgMode
                ? Cart.PrgRom[_bank[7] * 0x2000 + (addr - 0xC000)]
                : Cart.PrgRom[Cart.PrgRom.Length - 0x4000 + (addr - 0xC000)]; // second-to-last fixed
        return Cart.PrgRom[Cart.PrgRom.Length - 0x2000 + (addr - 0xE000)];    // last fixed
    }

    public override void WritePrg(ushort addr, byte v)
    {
        if (addr >= 0x6000 && addr < 0x8000)
        {
            if (_prgRamEnabled && _prgRamWrite)
                _prgRam[addr - 0x6000] = v;
            return;
        }
        if (addr < 0x8000) return;

        switch (addr & 0xE001)
        {
            case 0x8000:
                _bankSelect = v;
                _chrMode = (v & 0x40) != 0;
                _prgMode = (v & 0x80) != 0;
                break;
            case 0x8001:
            {
                int reg = _bankSelect & 7;
                if (reg < 6)
                {
                    bool is2K = _chrMode ? reg >= 4 : reg <= 1;
                    int mask = is2K ? Chr1KBanks / 2 - 1 : Chr1KBanks - 1;
                    _bank[reg] = (byte)(v & Math.Max(0, mask));
                }
                else
                {
                    _bank[reg] = (byte)(v & (Prg8KBanks - 1));
                }
                break;
            }
            case 0xA000:
                _mirror = (v & 1) != 0;
                Cart.Mirroring = _mirror ? Mirroring.Horizontal : Mirroring.Vertical;
                break;
            case 0xA001:
                _prgRamEnabled = (v & 0x80) != 0;
                _prgRamWrite = (v & 0x40) == 0;
                break;
            case 0xC000:
                _irqLatch = v;
                break;
            case 0xC001:
                _irqReload = true;
                break;
            case 0xE000:
                _irqEnabled = false;   // disable + acknowledge
                _irqReload = false;
                break;
            case 0xE001:
                _irqEnabled = true;
                break;
        }
    }

    private bool _mirror;

    // ── CHR ──────────────────────────────────────────────────────

    public override byte ReadChr(ushort addr)
    {
        int bank = _chrMode ? ChrMode1(addr) : ChrMode0(addr);
        return Cart.Chr[bank * 0x400 + (addr & 0x3FF)];
    }

    public override void WriteChr(ushort addr, byte v)
    {
        if (!Cart.ChrWritable) return;
        int bank = _chrMode ? ChrMode1(addr) : ChrMode0(addr);
        Cart.Chr[bank * 0x400 + (addr & 0x3FF)] = v;
    }

    /// <summary>Mode 0: $0000-$07FF = 2KB banks (R0,R1); $1000-$1FFF = 1KB banks (R2-R5).</summary>
    private int ChrMode0(ushort addr)
    {
        if (addr < 0x0800)
            return _bank[(addr & 0x800) == 0 ? 0 : 1] * 2 + ((addr >> 10) & 1);
        return _bank[2 + ((addr - 0x1000) >> 10)];
    }

    /// <summary>Mode 1: $0000-$0FFF = 1KB banks (R0-R3); $1000-$1FFF = 2KB banks (R4,R5).</summary>
    private int ChrMode1(ushort addr)
    {
        if (addr < 0x1000)
            return _bank[addr >> 10];
        return _bank[4 + ((addr - 0x1000) >> 11)] * 2 + ((addr >> 10) & 1);
    }

    // ── Scanline IRQ ─────────────────────────────────────────────

    /// <summary>
    /// Called once per visible scanline by the PPU while rendering is enabled.
    /// Approximates the MMC3 A12-edge counter; fires Cart.TriggerIrq() when
    /// the counter reaches zero.
    /// </summary>
    public override void ScanlineTick()
    {
        if (!_irqEnabled) return;

        if (_irqCounter == 0 || _irqReload)
        {
            _irqCounter = _irqLatch;
            _irqReload = false;
        }
        else
        {
            _irqCounter--;
        }

        if (_irqCounter == 0)
            Cart.TriggerIrq();
    }
}
