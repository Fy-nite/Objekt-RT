namespace NesEmulator;

/// <summary>
/// Builds a small self-contained NES test ROM (iNES, NROM/mapper 0) that:
///   1. Waits for vblank, loads a 32-byte palette.
///   2. Fills the nametable (0x2000) with alternating tiles 0/1 (960 tiles).
///   3. Enables rendering + NMI (handler is just RTI).
/// Tile 0 in CHR is a solid 8x8 block, tile 1 is empty — so the screen shows
/// a white/black checkerboard. No copyrighted content.
/// </summary>
public static class TestRom
{
    public static byte[] Create()
    {
        var prg = new byte[16384];
        Array.Fill(prg, (byte)0xEA); // NOP padding

        var code = new byte[]
        {
            0x78,             // SEI
            0xD8,             // CLD
            0xA2, 0x40,       // LDX #$40
            0x8E, 0x17, 0x40, // STX $4017   (APU frame IRQ disable)
            0xA2, 0xFF,       // LDX #$FF
            0x9A,             // TXS
            0xE8,             // INX         (X = 0)
            0x8E, 0x00, 0x20, // STX $2000   (PPUCTRL = 0)
            0x8E, 0x01, 0x20, // STX $2001   (PPUMASK = 0)
            // vwait1:
            0x2C, 0x02, 0x20, // BIT $2002
            0x10, 0xFB,       // BPL vwait1
            // set PPU address to $3F00
            0xA9, 0x3F,       // LDA #$3F
            0x8D, 0x06, 0x20, // STA $2006
            0xA9, 0x00,       // LDA #$00
            0x8D, 0x06, 0x20, // STA $2006
            // ploop: write 32 palette bytes
            0xA2, 0x00,       // LDX #$00
            0xBD, 0x5F, 0x80, // LDA pal,X
            0x8D, 0x07, 0x20, // STA $2007
            0xE8,             // INX
            0xE0, 0x20,       // CPX #$20
            0xD0, 0xF5,       // BNE ploop
            // set PPU address to $2000
            0xA9, 0x20,       // LDA #$20
            0x8D, 0x06, 0x20, // STA $2006
            0xA9, 0x00,       // LDA #$00
            0x8D, 0x06, 0x20, // STA $2006
            // fill 960 tiles, alternating 0/1
            0xA9, 0x00,       // LDA #$00
            0xA2, 0x03,       // LDX #$03
            0xA0, 0x00,       // page: LDY #$00
            0x8D, 0x07, 0x20, // row: STA $2007
            0x49, 0x01,       // EOR #$01
            0xC8,             // INY
            0xD0, 0xF8,       // BNE row
            0xCA,             // DEX
            0xD0, 0xF3,       // BNE page
            0xA0, 0xC0,       // LDY #$C0
            0x8D, 0x07, 0x20, // tail: STA $2007
            0x49, 0x01,       // EOR #$01
            0x88,             // DEY
            0xD0, 0xF8,       // BNE tail
            // enable NMI + background rendering (sprites left off: uninitialized
            // OAM would draw a stray sprite at 0,0)
            0xA9, 0x80,       // LDA #$80
            0x8D, 0x00, 0x20, // STA $2000
            0xA9, 0x08,       // LDA #$08
            0x8D, 0x01, 0x20, // STA $2001
            // loop:
            0x4C, 0x5C, 0x80, // JMP loop
        };
        Array.Copy(code, 0, prg, 0, code.Length);

        // Palette (32 bytes) at $805F
        byte[] pal =
        {
            0x0F, 0x21, 0x27, 0x30, 0x0F, 0x11, 0x22, 0x37,
            0x0F, 0x14, 0x24, 0x34, 0x0F, 0x16, 0x26, 0x36,
            0x0F, 0x02, 0x12, 0x22, 0x0F, 0x0B, 0x1B, 0x2B,
            0x0F, 0x05, 0x15, 0x25, 0x0F, 0x0A, 0x1A, 0x2A,
        };
        Array.Copy(pal, 0, prg, 0x5F, pal.Length);

        // NMI/IRQ handler at $807F: RTI
        prg[0x7F] = 0x40;

        // Interrupt vectors
        prg[0x3FFA] = 0x7F; prg[0x3FFB] = 0x80; // NMI  -> $807F
        prg[0x3FFC] = 0x00; prg[0x3FFD] = 0x80; // RESET -> $8000
        prg[0x3FFE] = 0x7F; prg[0x3FFF] = 0x80; // IRQ  -> $807F

        // CHR: tile 0 = solid block (16 x 0xFF), tile 1 = empty, rest zero.
        var chr = new byte[8192];
        Array.Fill<byte>(chr, 0x00);
        Array.Fill<byte>(chr, 0xFF, 0, 16);

        var rom = new byte[16 + prg.Length + chr.Length];
        rom[0] = (byte)'N'; rom[1] = (byte)'E'; rom[2] = (byte)'S'; rom[3] = 0x1A;
        rom[4] = 1;  // 16KB PRG
        rom[5] = 1;  // 8KB CHR
        rom[6] = 0;  // mapper 0, horizontal mirroring
        rom[7] = 0;
        Array.Copy(prg, 0, rom, 16, prg.Length);
        Array.Copy(chr, 0, rom, 16 + prg.Length, chr.Length);
        return rom;
    }
}
