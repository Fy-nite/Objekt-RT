namespace NesEmulator;

/// <summary>
/// Picture Processing Unit. Frame-based renderer: the full frame is drawn
/// from nametables / OAM / palettes when vblank begins. Mid-frame PPU tricks
/// (scanline effects like SMB status bars) are not emulated.
/// </summary>
public sealed class Ppu
{
    private readonly Cpu _cpu;

    public byte[] Vram = new byte[0x4000];     // pattern tables + nametables (mirroring applied on access)
    private readonly byte[] _palRam = new byte[32];
    public byte[] Oam = new byte[256];

    public byte OamAddr;
    public byte PpuCtrl, PpuMask, PpuStatus;
    public ushort PpuAddr;
    public bool AddrLatch;
    public byte ScrollX, ScrollY;

    public bool Vblank;
    public int Scanline;
    public int Cycle;

    public Cartridge? Cart;

    private byte _readBuffer;

    /// <summary>256x240 framebuffer in 0xAARRGGBB.</summary>
    public readonly uint[] FrameBuffer = new uint[256 * 240];

    private readonly byte[] _bgIndex = new byte[256 * 240];

    public Ppu(Cpu cpu) => _cpu = cpu;

    public void Reset()
    {
        PpuCtrl = PpuMask = PpuStatus = 0;
        PpuAddr = 0;
        AddrLatch = false;
        ScrollX = ScrollY = 0;
        OamAddr = 0;
        Vblank = false;
        Scanline = 0;
        Cycle = 0;
        Array.Clear(FrameBuffer, 0, FrameBuffer.Length);
        Array.Clear(_bgIndex, 0, _bgIndex.Length);
    }

    // ── Registers ($2000-$2007) ────────────────────────────────────

    public void WriteReg(ushort addr, byte v)
    {
        switch (addr)
        {
            case 0x2000: PpuCtrl = v; break;
            case 0x2001: PpuMask = v; break;
            case 0x2003: OamAddr = v; break;
            case 0x2004: Oam[OamAddr++] = v; break;
            case 0x2005:
                if (!AddrLatch) ScrollX = v;
                else ScrollY = v;
                AddrLatch = !AddrLatch;
                break;
            case 0x2006:
                if (!AddrLatch) PpuAddr = (ushort)(((v & 0x3F) << 8) | (PpuAddr & 0xFF));
                else PpuAddr = (ushort)((PpuAddr & 0xFF00) | v);
                AddrLatch = !AddrLatch;
                break;
            case 0x2007:
                WriteVram(PpuAddr, v);
                IncrementAddr();
                break;
        }
    }

    public byte ReadReg(ushort addr)
    {
        switch (addr)
        {
            case 0x2002:
            {
                byte s = PpuStatus;
                // Real hardware: reading $2002 clears vblank, overflow, and
                // sprite-0 hit (bits 5-7). The hit re-fires on the next
                // scanline where sprite 0 overlaps background.
                PpuStatus &= 0x1F;
                Vblank = false;
                AddrLatch = false;
                return s;
            }
            case 0x2004: return Oam[OamAddr];
            case 0x2007:
            {
                ushort a = PpuAddr;
                IncrementAddr();
                byte r;
                if ((a & 0x3FFF) >= 0x3F00)
                    r = ReadVram(a);
                else
                {
                    r = _readBuffer;
                    _readBuffer = ReadVram(a);
                }
                return r;
            }
            default: return 0;
        }
    }

    private void IncrementAddr()
    {
        PpuAddr = (ushort)(PpuAddr + ((PpuCtrl & 0x04) != 0 ? 32 : 1));
    }

    // ── VRAM access ───────────────────────────────────────────────

    public byte ReadVram(ushort addr)
    {
        addr &= 0x3FFF;
        if (addr >= 0x3F00) return ReadPalette(addr);
        if (addr < 0x2000) return Cart?.ReadChr(addr) ?? 0;
        return Vram[MapNametable(addr)];
    }

    public void WriteVram(ushort addr, byte v)
    {
        addr &= 0x3FFF;
        if (addr >= 0x3F00) { WritePalette(addr, v); return; }
        if (addr < 0x2000) { Cart?.WriteChr(addr, v); return; }
        Vram[MapNametable(addr)] = v;
    }

    private byte ReadPalette(ushort addr)
    {
        int idx = addr & 0x1F;
        if (idx == 0x10) idx = 0x00; // $3F10 mirrors $3F00 only
        return _palRam[idx];
    }

    private void WritePalette(ushort addr, byte v)
    {
        int idx = addr & 0x1F;
        if (idx == 0x10)
        {
            _palRam[0x00] = v;
            _palRam[0x10] = v;
        }
        else
        {
            _palRam[idx] = v;
        }
    }

    /// <summary>Map a $2000-$3EFF address to its physical nametable slot.</summary>
    private ushort MapNametable(ushort addr)
    {
        int ntOff = addr & 0x03FF;
        return (Cart?.Mirroring ?? Mirroring.Horizontal) switch
        {
            Mirroring.Vertical => (ushort)(0x2000 + ((addr & 0x0400) != 0 ? 0x400 : 0) + ntOff),
            Mirroring.FourScreen => (ushort)(addr & 0x0FFF),
            Mirroring.Single0 => (ushort)(0x2000 + ntOff),
            Mirroring.Single1 => (ushort)(0x2400 + ntOff),
            _ => (ushort)(0x2000 + ((addr & 0x0800) != 0 ? 0x800 : 0) + ntOff), // horizontal
        };
    }

    // ── Clocks (3 PPU cycles per CPU cycle) ───────────────────────

    public void Clock(int cycles)
    {
        for (int i = 0; i < cycles; i++)
        {
            Cycle++;
            if (Cycle >= 341)
            {
                Cycle = 0;

                // Per-scanline sprite evaluation for the CURRENT scanline
                // (BEFORE incrementing — covers scanlines 0-239 once each).
                bool render = (PpuMask & 0x18) != 0;
                if (Scanline < 240 && render)
                    EvaluateSprites(Scanline);
                if (Scanline < 240 && render)
                    Cart?.ScanlineTick();

                Scanline++;

                if (Scanline == 241)
                {
                    Vblank = true;
                    PpuStatus |= 0x80;
                    if ((PpuCtrl & 0x80) != 0)
                        _cpu.Nmi();
                }
                else if (Scanline >= 262)
                {
                    Scanline = 0;
                    Vblank = false;
                    PpuStatus &= 0x7F; // clear vblank at start of new frame
                }
            }
        }
    }

    // ── Frame rendering ───────────────────────────────────────────

    /// <summary>
    /// Per-scanline sprite overflow check (bit 5 of $2002). Only scans OAM
    /// for sprite counts — sprite-0 hit is handled in RenderSprites once per
    /// frame since it depends on the final rendered background.
    /// </summary>
    private void EvaluateSprites(int scanline)
    {
        bool big = (PpuCtrl & 0x20) != 0;
        int height = big ? 16 : 8;
        int count = 0;

        for (int i = 0; i < 64; i++)
        {
            int y = Oam[i * 4];
            if (y <= scanline && y + height > scanline)
            {
                count++;
                if (count > 8)
                {
                    PpuStatus |= 0x20; // sprite overflow
                    OverflowFires++;
                    return;
                }
            }
        }
    }

    /// <summary>How many times sprite overflow latched (diagnostics).</summary>
    public int OverflowFires;

    /// <summary>Render the full frame from nametables / OAM / palettes.</summary>
    public void RenderFrame()
    {

        bool bgOn = (PpuMask & 0x08) != 0;
        bool spOn = (PpuMask & 0x10) != 0;

        if (bgOn) RenderBackground();
        else
        {
            uint bg = PaletteUint(_palRam[0]);
            Array.Fill(FrameBuffer, bg);
            Array.Clear(_bgIndex, 0, _bgIndex.Length);
        }

        if (spOn) RenderSprites();
    }

    private void RenderBackground()
    {
        int patternBase = (PpuCtrl & 0x10) != 0 ? 0x1000 : 0x0000;
        int sx = ScrollX, sy = ScrollY;

        for (int py = 0; py < 240; py++)
        {
            int yy = (py + sy) & 0x1FF;
            int tileY = (yy & 0xFF) >> 3;
            int ntQuadY = (yy >> 8) & 1;

            for (int px = 0; px < 256; px++)
            {
                int xx = (px + sx) & 0x1FF;
                int tileX = (xx & 0xFF) >> 3;
                int ntQuad = ntQuadY << 1 | ((xx >> 8) & 1);

                ushort nt = MapNametable((ushort)(0x2000 + ntQuad * 0x400));
                byte tile = ReadVram((ushort)(nt + tileY * 32 + tileX));

                byte attr = ReadVram((ushort)(nt + 0x3C0 + (tileY >> 2) * 8 + (tileX >> 2)));
                int palIdx = (attr >> (((tileY & 2) << 1) | (tileX & 2))) & 3;

                int row = yy & 7;
                int col = xx & 7;
                ushort pat = (ushort)(patternBase + tile * 16 + row);
                byte low = ReadVram(pat);
                byte high = ReadVram((ushort)(pat + 8));
                int pixel = (((high >> (7 - col)) & 1) << 1) | ((low >> (7 - col)) & 1);

                int colorIdx = pixel == 0 ? _palRam[0] : _palRam[palIdx * 4 + pixel];
                FrameBuffer[py * 256 + px] = PaletteUint((byte)colorIdx);
                _bgIndex[py * 256 + px] = (byte)colorIdx;
            }
        }
    }

    private void RenderSprites()
    {
        bool big = (PpuCtrl & 0x20) != 0;
        int patBase = big ? 0 : ((PpuCtrl & 0x08) != 0 ? 0x1000 : 0x0000);
        int height = big ? 16 : 8;

        int s0y = Oam[0];
        int s0x = Oam[3];

        for (int i = 0; i < 64; i++)
        {
            int o = i * 4;
            int y = Oam[o];
            int tile = Oam[o + 1];
            int attr = Oam[o + 2];
            int x = Oam[o + 3];

            bool flipH = (attr & 0x40) != 0;
            bool flipV = (attr & 0x80) != 0;
            bool behind = (attr & 0x10) != 0;
            int palIdx = attr & 3;
            int realTile = big ? (((tile & 1) << 8) | (tile & 0xFE)) : tile;

            for (int r = 0; r < height; r++)
            {
                int py = y + r;
                if (py < 0 || py >= 240) continue;

                int row = flipV ? (height - 1 - r) : r;
                int tileIdx = big ? realTile + (row >= 8 ? 1 : 0) : realTile;
                ushort pat = (ushort)(patBase + tileIdx * 16 + (row & 7));
                byte low = ReadVram(pat);
                byte high = ReadVram((ushort)(pat + 8));

                for (int c = 0; c < 8; c++)
                {
                    int px = x + c;
                    if (px < 0 || px >= 256) continue;

                    int col = flipH ? (7 - c) : c;
                    int pixel = (((high >> (7 - col)) & 1) << 1) | ((low >> (7 - col)) & 1);
                    if (pixel == 0) continue;

                    int fb = py * 256 + px;
                    if (behind && _bgIndex[fb] != 0) continue;

                    // Sprite-0 hit: first non-transparent overlap of sprite 0 with
                    // a non-transparent background pixel.
                    if (i == 0
                        && _bgIndex[fb] != 0              // bg not transparent
                        && s0y < 240)                     // not off-screen
                    {
                        PpuStatus |= 0x40;                 // sprite-0 hit (bit 6)
                    }

                    FrameBuffer[fb] = PaletteUint(_palRam[0x10 + palIdx * 4 + pixel]);
                }
            }
        }
    }

    private static uint PaletteUint(byte idx)
    {
        idx &= 0x3F;
        uint c = NsPalette[idx];
        return 0xFF000000 | ((c & 0xFF) << 16) | (c & 0xFF00) | ((c >> 16) & 0xFF);
    }

    // Standard 64-color NES palette (24-bit RGB, little-endian stored).
    private static readonly uint[] NsPalette =
    {
        0x666666, 0x002A88, 0x1412A7, 0x3B00A4, 0x5C007E, 0x6E0040, 0x6C0600, 0x561D00,
        0x333500, 0x0B4800, 0x005200, 0x004F08, 0x00404D, 0x000000, 0x000000, 0x000000,
        0xADADAD, 0x155FD9, 0x4240FF, 0x7527FE, 0xA01ACC, 0xB71E7B, 0xB53120, 0x994E00,
        0x6B6D00, 0x388700, 0x0C9300, 0x008F32, 0x007C8D, 0x000000, 0x000000, 0x000000,
        0xFFFEFF, 0x64B0FF, 0x9290FF, 0xC676FF, 0xF36AFF, 0xFE6ECC, 0xFE8170, 0xEA9E22,
        0xBCBE00, 0x88D800, 0x5CE430, 0x45E082, 0x48CDDE, 0x4F4F4F, 0x000000, 0x000000,
        0xFFFEFF, 0xC0DFFF, 0xD3D2FF, 0xE8C8FF, 0xFBC2FF, 0xFEC4EA, 0xFECCC5, 0xF7D8A5,
        0xE4E594, 0xCFEF96, 0xBDF4AB, 0xB3F3CC, 0xB5EBF2, 0xB8B8B8, 0x000000, 0x000000,
    };
}
