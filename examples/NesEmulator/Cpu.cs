namespace NesEmulator;

/// <summary>
/// MOS 6502 CPU. Official opcode set plus the common unofficial ones.
/// Decimal mode is treated as binary (SED/CLD accepted; most NES games
/// never enable decimal mode).
/// </summary>
public sealed class Cpu
{
    private const byte C = 0x01, Z = 0x02, I = 0x04, D = 0x08, B = 0x10, U = 0x20, V = 0x40, N = 0x80;

    public byte A, X, Y, S;
    public byte P = 0x24;
    public ushort Pc;
    public ulong Cycles;

    private readonly Bus _bus;

    public Cpu(Bus bus) => _bus = bus;

    public void Reset()
    {
        A = X = Y = 0;
        S = 0xFD;
        P = 0x24; // I set, U set
        Pc = Read16(0xFFFC);
        Cycles += 8;
    }

    // ── Memory helpers ───────────────────────────────────────────

    private byte Read(ushort addr) => _bus.Read(addr);
    private void Write(ushort addr, byte v) => _bus.Write(addr, v);
    private byte Fetch() => Read(Pc++);
    private ushort Fetch16() { ushort v = Read16(Pc); Pc += 2; return v; }
    private ushort Read16(ushort addr) => (ushort)(Read(addr) | (Read((ushort)(addr + 1)) << 8));

    private void Push(byte v) => Write((ushort)(0x100 + S--), v);
    private byte Pop() => Read((ushort)(0x100 + ++S));

    // ── Addressing modes ─────────────────────────────────────────

    private ushort Zp() => Fetch();
    private ushort ZpX() => (byte)(Fetch() + X);
    private ushort ZpY() => (byte)(Fetch() + Y);
    private ushort Abs() => Fetch16();

    private ushort AbsX(bool penalty = true)
    {
        ushort a = Fetch16();
        if (penalty && ((a & 0xFF) + X) > 0xFF) Cycles++;
        return (ushort)(a + X);
    }

    private ushort AbsY(bool penalty = true)
    {
        ushort a = Fetch16();
        if (penalty && ((a & 0xFF) + Y) > 0xFF) Cycles++;
        return (ushort)(a + Y);
    }

    private ushort IndX()
    {
        byte zp = (byte)(Fetch() + X);
        return Read16(zp);
    }

    private ushort IndY(bool penalty = true)
    {
        byte zp = Fetch();
        ushort a = Read16(zp);
        if (penalty && ((a & 0xFF) + Y) > 0xFF) Cycles++;
        return (ushort)(a + Y);
    }

    /// <summary>JMP (ind) — with the page-boundary hardware bug.</summary>
    private ushort Ind()
    {
        ushort ptr = Fetch16();
        byte lo = Read(ptr);
        byte hi = Read((ushort)((ptr & 0xFF00) | ((ptr + 1) & 0xFF)));
        return (ushort)(lo | (hi << 8));
    }

    // ── Flag helpers ─────────────────────────────────────────────

    private bool Flag(byte m) => (P & m) != 0;
    private void SetFlag(byte m, bool v) => P = v ? (byte)(P | m) : (byte)(P & ~m);
    private void SetNZ(byte v) => P = (byte)((P & ~(N | Z)) | (v == 0 ? Z : 0) | (v & N));

    private void Branch(bool cond)
    {
        sbyte off = (sbyte)Fetch();
        if (!cond) return;
        Cycles++;
        ushort target = (ushort)(Pc + off);
        if ((target & 0xFF00) != (Pc & 0xFF00)) Cycles++;
        Pc = target;
    }

    // ── Operations ───────────────────────────────────────────────

    private void Adc(byte v)
    {
        int sum = A + v + (Flag(C) ? 1 : 0);
        SetFlag(C, sum > 0xFF);
        SetFlag(V, (~(A ^ v) & (A ^ (byte)sum) & 0x80) != 0);
        A = (byte)sum;
        SetNZ(A);
    }

    private void Sbc(byte v)
    {
        int sum = A - v - (Flag(C) ? 0 : 1);
        SetFlag(C, sum >= 0);
        SetFlag(V, ((A ^ v) & (A ^ (byte)sum) & 0x80) != 0);
        A = (byte)sum;
        SetNZ(A);
    }

    private void CmpRegister(byte reg, byte v)
    {
        int d = reg - v;
        SetFlag(C, d >= 0);
        SetNZ((byte)d);
    }

    private void Rmw(ushort addr, Func<byte, byte> op)
    {
        byte v = Read(addr);
        byte r = op(v);
        Write(addr, r);
        SetNZ(r);
    }

    private void RmwAcc(Func<byte, byte> op)
    {
        A = op(A);
        SetNZ(A);
    }

    // ── Interrupts ───────────────────────────────────────────────

    private void DoInterrupt(ushort vector)
    {
        Push((byte)(Pc >> 8));
        Push((byte)Pc);
        Push((byte)((P | 0x20) & ~0x10)); // B clear, U set
        SetFlag(I, true);
        Pc = Read16(vector);
        Cycles += 7;
    }

    public void Nmi() { NmiCount++; DoInterrupt(0xFFFA); }

    /// <summary>Number of NMIs delivered (diagnostics).</summary>
    public ulong NmiCount;

    /// <summary>
    /// Optional trace callback. Fires BEFORE each instruction executes.
    /// Return false to stop execution immediately.
    /// </summary>
    public Func<byte, ushort, byte, byte, byte, byte, byte, ulong, bool>? TraceStep;

    public void Irq()
    {
        if (!Flag(I)) DoInterrupt(0xFFFE);
    }

    /// <summary>OAM DMA busy time.</summary>
    public void DmaCycles(int c) => Cycles += (ulong)c;

    // ── Execution ────────────────────────────────────────────────

    /// <summary>Execute exactly one instruction.</summary>
    public void Step()
    {
        byte op = _bus.Read(Pc);

        if (TraceStep is not null)
        {
            if (!TraceStep(op, Pc, A, X, Y, S, P, Cycles))
                return;
        }
        Fetch(); // eat the opcode byte
        Cycles += CyclesTable[op];

        switch (op)
        {
            // ── Loads ────────────────────────────────────────────
            case 0xA9: A = Fetch(); SetNZ(A); break;                     // LDA #imm
            case 0xA5: A = Read(Zp()); SetNZ(A); break;                  // LDA zp
            case 0xB5: A = Read(ZpX()); SetNZ(A); break;                 // LDA zp,X
            case 0xAD: A = Read(Abs()); SetNZ(A); break;                 // LDA abs
            case 0xBD: A = Read(AbsX()); SetNZ(A); break;                // LDA abs,X
            case 0xB9: A = Read(AbsY()); SetNZ(A); break;                // LDA abs,Y
            case 0xA1: A = Read(IndX()); SetNZ(A); break;                // LDA (zp,X)
            case 0xB1: A = Read(IndY()); SetNZ(A); break;                // LDA (zp),Y

            case 0xA2: X = Fetch(); SetNZ(X); break;                     // LDX #imm
            case 0xA6: X = Read(Zp()); SetNZ(X); break;
            case 0xB6: X = Read(ZpY()); SetNZ(X); break;
            case 0xAE: X = Read(Abs()); SetNZ(X); break;
            case 0xBE: X = Read(AbsY()); SetNZ(X); break;

            case 0xA0: Y = Fetch(); SetNZ(Y); break;                     // LDY #imm
            case 0xA4: Y = Read(Zp()); SetNZ(Y); break;
            case 0xB4: Y = Read(ZpX()); SetNZ(Y); break;
            case 0xAC: Y = Read(Abs()); SetNZ(Y); break;
            case 0xBC: Y = Read(AbsX()); SetNZ(Y); break;

            // ── Stores ───────────────────────────────────────────
            case 0x85: Write(Zp(), A); break;                            // STA zp
            case 0x95: Write(ZpX(), A); break;
            case 0x8D: Write(Abs(), A); break;
            case 0x9D: Write(AbsX(false), A); break;
            case 0x99: Write(AbsY(false), A); break;
            case 0x81: Write(IndX(), A); break;
            case 0x91: Write(IndY(false), A); break;

            case 0x86: Write(Zp(), X); break;                            // STX
            case 0x96: Write(ZpY(), X); break;
            case 0x8E: Write(Abs(), X); break;

            case 0x84: Write(Zp(), Y); break;                            // STY
            case 0x94: Write(ZpX(), Y); break;
            case 0x8C: Write(Abs(), Y); break;

            // ── Arithmetic ───────────────────────────────────────
            case 0x69: Adc(Fetch()); break;                              // ADC
            case 0x65: Adc(Read(Zp())); break;
            case 0x75: Adc(Read(ZpX())); break;
            case 0x6D: Adc(Read(Abs())); break;
            case 0x7D: Adc(Read(AbsX())); break;
            case 0x79: Adc(Read(AbsY())); break;
            case 0x61: Adc(Read(IndX())); break;
            case 0x71: Adc(Read(IndY())); break;

            case 0xE9: Sbc(Fetch()); break;                              // SBC
            case 0xEB: Sbc(Fetch()); break;                              // SBC #imm (unofficial)
            case 0xE5: Sbc(Read(Zp())); break;
            case 0xF5: Sbc(Read(ZpX())); break;
            case 0xED: Sbc(Read(Abs())); break;
            case 0xFD: Sbc(Read(AbsX())); break;
            case 0xF9: Sbc(Read(AbsY())); break;
            case 0xE1: Sbc(Read(IndX())); break;
            case 0xF1: Sbc(Read(IndY())); break;

            case 0x29: A &= Fetch(); SetNZ(A); break;                    // AND
            case 0x25: A &= Read(Zp()); SetNZ(A); break;
            case 0x35: A &= Read(ZpX()); SetNZ(A); break;
            case 0x2D: A &= Read(Abs()); SetNZ(A); break;
            case 0x3D: A &= Read(AbsX()); SetNZ(A); break;
            case 0x39: A &= Read(AbsY()); SetNZ(A); break;
            case 0x21: A &= Read(IndX()); SetNZ(A); break;
            case 0x31: A &= Read(IndY()); SetNZ(A); break;

            case 0x09: A |= Fetch(); SetNZ(A); break;                    // ORA
            case 0x05: A |= Read(Zp()); SetNZ(A); break;
            case 0x15: A |= Read(ZpX()); SetNZ(A); break;
            case 0x0D: A |= Read(Abs()); SetNZ(A); break;
            case 0x1D: A |= Read(AbsX()); SetNZ(A); break;
            case 0x19: A |= Read(AbsY()); SetNZ(A); break;
            case 0x01: A |= Read(IndX()); SetNZ(A); break;
            case 0x11: A |= Read(IndY()); SetNZ(A); break;

            case 0x49: A ^= Fetch(); SetNZ(A); break;                    // EOR
            case 0x45: A ^= Read(Zp()); SetNZ(A); break;
            case 0x55: A ^= Read(ZpX()); SetNZ(A); break;
            case 0x4D: A ^= Read(Abs()); SetNZ(A); break;
            case 0x5D: A ^= Read(AbsX()); SetNZ(A); break;
            case 0x59: A ^= Read(AbsY()); SetNZ(A); break;
            case 0x41: A ^= Read(IndX()); SetNZ(A); break;
            case 0x51: A ^= Read(IndY()); SetNZ(A); break;

            // ── Comparisons ──────────────────────────────────────
            case 0xC9: CmpRegister(A, Fetch()); break;                   // CMP
            case 0xC5: CmpRegister(A, Read(Zp())); break;
            case 0xD5: CmpRegister(A, Read(ZpX())); break;
            case 0xCD: CmpRegister(A, Read(Abs())); break;
            case 0xDD: CmpRegister(A, Read(AbsX())); break;
            case 0xD9: CmpRegister(A, Read(AbsY())); break;
            case 0xC1: CmpRegister(A, Read(IndX())); break;
            case 0xD1: CmpRegister(A, Read(IndY())); break;

            case 0xE0: CmpRegister(X, Fetch()); break;                   // CPX
            case 0xE4: CmpRegister(X, Read(Zp())); break;
            case 0xEC: CmpRegister(X, Read(Abs())); break;

            case 0xC0: CmpRegister(Y, Fetch()); break;                   // CPY
            case 0xC4: CmpRegister(Y, Read(Zp())); break;
            case 0xCC: CmpRegister(Y, Read(Abs())); break;

            case 0x24:                                                    // BIT zp
            {
                byte v = Read(Zp());
                SetFlag(Z, (A & v) == 0);
                SetFlag(N, (v & 0x80) != 0);
                SetFlag(V, (v & 0x40) != 0);
                break;
            }
            case 0x2C:                                                    // BIT abs
            {
                byte v = Read(Abs());
                SetFlag(Z, (A & v) == 0);
                SetFlag(N, (v & 0x80) != 0);
                SetFlag(V, (v & 0x40) != 0);
                break;
            }

            // ── Inc / Dec ────────────────────────────────────────
            case 0xE6: Rmw(Zp(), b => (byte)(b + 1)); break;             // INC
            case 0xF6: Rmw(ZpX(), b => (byte)(b + 1)); break;
            case 0xEE: Rmw(Abs(), b => (byte)(b + 1)); break;
            case 0xFE: Rmw(AbsX(false), b => (byte)(b + 1)); break;

            case 0xC6: Rmw(Zp(), b => (byte)(b - 1)); break;             // DEC
            case 0xD6: Rmw(ZpX(), b => (byte)(b - 1)); break;
            case 0xCE: Rmw(Abs(), b => (byte)(b - 1)); break;
            case 0xDE: Rmw(AbsX(false), b => (byte)(b - 1)); break;

            case 0xE8: X++; SetNZ(X); break;                             // INX
            case 0xC8: Y++; SetNZ(Y); break;                             // INY
            case 0xCA: X--; SetNZ(X); break;                             // DEX
            case 0x88: Y--; SetNZ(Y); break;                             // DEY

            // ── Shifts / rotates ─────────────────────────────────
            case 0x0A: RmwAcc(b => { SetFlag(C, (b & 0x80) != 0); return (byte)(b << 1); }); break; // ASL A
            case 0x06: Rmw(Zp(), b => { SetFlag(C, (b & 0x80) != 0); return (byte)(b << 1); }); break;
            case 0x16: Rmw(ZpX(), b => { SetFlag(C, (b & 0x80) != 0); return (byte)(b << 1); }); break;
            case 0x0E: Rmw(Abs(), b => { SetFlag(C, (b & 0x80) != 0); return (byte)(b << 1); }); break;
            case 0x1E: Rmw(AbsX(false), b => { SetFlag(C, (b & 0x80) != 0); return (byte)(b << 1); }); break;

            case 0x4A: RmwAcc(b => { SetFlag(C, (b & 1) != 0); return (byte)(b >> 1); }); break;   // LSR A
            case 0x46: Rmw(Zp(), b => { SetFlag(C, (b & 1) != 0); return (byte)(b >> 1); }); break;
            case 0x56: Rmw(ZpX(), b => { SetFlag(C, (b & 1) != 0); return (byte)(b >> 1); }); break;
            case 0x4E: Rmw(Abs(), b => { SetFlag(C, (b & 1) != 0); return (byte)(b >> 1); }); break;
            case 0x5E: Rmw(AbsX(false), b => { SetFlag(C, (b & 1) != 0); return (byte)(b >> 1); }); break;

            case 0x2A: RmwAcc(b => { bool c = (b & 0x80) != 0; byte r = (byte)((b << 1) | (Flag(C) ? 1 : 0)); SetFlag(C, c); return r; }); break; // ROL A
            case 0x26: Rmw(Zp(), b => { bool c = (b & 0x80) != 0; byte r = (byte)((b << 1) | (Flag(C) ? 1 : 0)); SetFlag(C, c); return r; }); break;
            case 0x36: Rmw(ZpX(), b => { bool c = (b & 0x80) != 0; byte r = (byte)((b << 1) | (Flag(C) ? 1 : 0)); SetFlag(C, c); return r; }); break;
            case 0x2E: Rmw(Abs(), b => { bool c = (b & 0x80) != 0; byte r = (byte)((b << 1) | (Flag(C) ? 1 : 0)); SetFlag(C, c); return r; }); break;
            case 0x3E: Rmw(AbsX(false), b => { bool c = (b & 0x80) != 0; byte r = (byte)((b << 1) | (Flag(C) ? 1 : 0)); SetFlag(C, c); return r; }); break;

            case 0x6A: RmwAcc(b => { bool c = (b & 1) != 0; byte r = (byte)((b >> 1) | (Flag(C) ? 0x80 : 0)); SetFlag(C, c); return r; }); break; // ROR A
            case 0x66: Rmw(Zp(), b => { bool c = (b & 1) != 0; byte r = (byte)((b >> 1) | (Flag(C) ? 0x80 : 0)); SetFlag(C, c); return r; }); break;
            case 0x76: Rmw(ZpX(), b => { bool c = (b & 1) != 0; byte r = (byte)((b >> 1) | (Flag(C) ? 0x80 : 0)); SetFlag(C, c); return r; }); break;
            case 0x6E: Rmw(Abs(), b => { bool c = (b & 1) != 0; byte r = (byte)((b >> 1) | (Flag(C) ? 0x80 : 0)); SetFlag(C, c); return r; }); break;
            case 0x7E: Rmw(AbsX(false), b => { bool c = (b & 1) != 0; byte r = (byte)((b >> 1) | (Flag(C) ? 0x80 : 0)); SetFlag(C, c); return r; }); break;

            // ── Jumps / calls ────────────────────────────────────
            case 0x4C: Pc = Abs(); break;                                // JMP abs
            case 0x6C: Pc = Ind(); break;                                // JMP (ind)
            case 0x20:                                                    // JSR
            {
                ushort a = Abs();
                // The 6502 pushes the address of the LAST byte of the JSR
                // instruction (return address - 1); RTS adds 1 on return.
                ushort ret = (ushort)(Pc - 1);
                Push((byte)(ret >> 8));
                Push((byte)ret);
                Pc = a;
                break;
            }
            case 0x60:                                                    // RTS
            {
                Pc = (ushort)((Pop() | ((ushort)Pop() << 8)) + 1);
                break;
            }
            case 0x40:                                                    // RTI
            {
                P = (byte)(Pop() | 0x20);
                Pc = (ushort)(Pop() | ((ushort)Pop() << 8));
                break;
            }
            case 0x00:                                                    // BRK
            {
                Pc++;
                Push((byte)(Pc >> 8));
                Push((byte)Pc);
                Push((byte)(P | 0x30));
                SetFlag(I, true);
                Pc = Read16(0xFFFE);
                break;
            }

            // ── Branches ─────────────────────────────────────────
            case 0x10: Branch(!Flag(N)); break;                          // BPL
            case 0x30: Branch(Flag(N)); break;                           // BMI
            case 0x50: Branch(!Flag(V)); break;                          // BVC
            case 0x70: Branch(Flag(V)); break;                           // BVS
            case 0x90: Branch(!Flag(C)); break;                          // BCC
            case 0xB0: Branch(Flag(C)); break;                           // BCS
            case 0xD0: Branch(!Flag(Z)); break;                          // BNE
            case 0xF0: Branch(Flag(Z)); break;                           // BEQ

            // ── Stack ────────────────────────────────────────────
            case 0x48: Push(A); break;                                   // PHA
            case 0x68: A = Pop(); SetNZ(A); break;                       // PLA
            case 0x08: Push((byte)(P | 0x30)); break;                    // PHP
            case 0x28: P = (byte)(Pop() | 0x20); break;                  // PLP

            // ── Status flags ─────────────────────────────────────
            case 0x38: SetFlag(C, true); break;                          // SEC
            case 0x18: SetFlag(C, false); break;                         // CLC
            case 0x78: SetFlag(I, true); break;                          // SEI
            case 0x58: SetFlag(I, false); break;                         // CLI
            case 0xF8: SetFlag(D, true); break;                          // SED
            case 0xD8: SetFlag(D, false); break;                         // CLD
            case 0xB8: SetFlag(V, false); break;                         // CLV

            // ── Transfers ────────────────────────────────────────
            case 0xAA: X = A; SetNZ(X); break;                           // TAX
            case 0xA8: Y = A; SetNZ(Y); break;                           // TAY
            case 0xBA: X = S; SetNZ(X); break;                           // TSX
            case 0x8A: A = X; SetNZ(A); break;                           // TXA
            case 0x98: A = Y; SetNZ(A); break;                           // TYA
            case 0x9A: S = X; break;                                     // TXS (no flags)

            case 0xEA: break;                                            // NOP

            // ── Unofficial: NOPs ─────────────────────────────────
            case 0x04: case 0x44: case 0x64: _ = Read(Zp()); break;      // NOP zp
            case 0x0C: _ = Read(Abs()); break;                           // NOP abs
            case 0x14: case 0x34: case 0x54: case 0x74:
            case 0xD4: case 0xF4: _ = Read(ZpX()); break;                // NOP zp,X
            case 0x1C: case 0x3C: case 0x5C: case 0x7C:
            case 0xDC: case 0xFC: _ = Read(AbsX()); break;               // NOP abs,X
            case 0x80: case 0x82: case 0x89: case 0xC2: case 0xE2: _ = Fetch(); break; // NOP #imm
            case 0x1A: case 0x3A: case 0x5A: case 0x7A: case 0xDA: case 0xFA: break;   // NOP

            // ── Unofficial: LAX / SAX ────────────────────────────
            case 0xAB: A = X = Fetch(); SetNZ(A); break;                 // LAX #imm
            case 0xA7: A = X = Read(Zp()); SetNZ(A); break;              // LAX zp
            case 0xB7: A = X = Read(ZpY()); SetNZ(A); break;             // LAX zp,Y
            case 0xAF: A = X = Read(Abs()); SetNZ(A); break;             // LAX abs
            case 0xBF: A = X = Read(AbsY()); SetNZ(A); break;            // LAX abs,Y
            case 0xA3: A = X = Read(IndX()); SetNZ(A); break;            // LAX (zp,X)
            case 0xB3: A = X = Read(IndY()); SetNZ(A); break;            // LAX (zp),Y

            case 0x87: Write(Zp(), (byte)(A & X)); break;                // SAX zp
            case 0x97: Write(ZpY(), (byte)(A & X)); break;               // SAX zp,Y
            case 0x8F: Write(Abs(), (byte)(A & X)); break;               // SAX abs
            case 0x83: Write(IndX(), (byte)(A & X)); break;              // SAX (zp,X)

            // ── Unofficial: read-modify-write combos ─────────────
            case 0xC7: Rmw(Zp(), b => { byte n = (byte)(b + 1); CmpRegister(A, n); return n; }); break;          // DCP zp
            case 0xD7: Rmw(ZpX(), b => { byte n = (byte)(b + 1); CmpRegister(A, n); return n; }); break;
            case 0xCF: Rmw(Abs(), b => { byte n = (byte)(b + 1); CmpRegister(A, n); return n; }); break;
            case 0xDF: Rmw(AbsX(false), b => { byte n = (byte)(b + 1); CmpRegister(A, n); return n; }); break;
            case 0xC3: Rmw(IndX(), b => { byte n = (byte)(b + 1); CmpRegister(A, n); return n; }); break;
            case 0xD3: Rmw(IndY(false), b => { byte n = (byte)(b + 1); CmpRegister(A, n); return n; }); break;

            case 0xE7: Rmw(Zp(), b => { byte n = (byte)(b + 1); Sbc(n); return n; }); break;                     // ISB zp
            case 0xF7: Rmw(ZpX(), b => { byte n = (byte)(b + 1); Sbc(n); return n; }); break;
            case 0xEF: Rmw(Abs(), b => { byte n = (byte)(b + 1); Sbc(n); return n; }); break;
            case 0xFF: Rmw(AbsX(false), b => { byte n = (byte)(b + 1); Sbc(n); return n; }); break;
            case 0xE3: Rmw(IndX(), b => { byte n = (byte)(b + 1); Sbc(n); return n; }); break;
            case 0xF3: Rmw(IndY(false), b => { byte n = (byte)(b + 1); Sbc(n); return n; }); break;

            case 0x07: Rmw(Zp(), b => { SetFlag(C, (b & 0x80) != 0); byte n = (byte)(b << 1); A |= n; SetNZ(A); return n; }); break; // SLO zp
            case 0x17: Rmw(ZpX(), b => { SetFlag(C, (b & 0x80) != 0); byte n = (byte)(b << 1); A |= n; SetNZ(A); return n; }); break;
            case 0x0F: Rmw(Abs(), b => { SetFlag(C, (b & 0x80) != 0); byte n = (byte)(b << 1); A |= n; SetNZ(A); return n; }); break;
            case 0x1F: Rmw(AbsX(false), b => { SetFlag(C, (b & 0x80) != 0); byte n = (byte)(b << 1); A |= n; SetNZ(A); return n; }); break;
            case 0x03: Rmw(IndX(), b => { SetFlag(C, (b & 0x80) != 0); byte n = (byte)(b << 1); A |= n; SetNZ(A); return n; }); break;
            case 0x13: Rmw(IndY(false), b => { SetFlag(C, (b & 0x80) != 0); byte n = (byte)(b << 1); A |= n; SetNZ(A); return n; }); break;

            case 0x27: Rmw(Zp(), b => { bool c = (b & 0x80) != 0; byte n = (byte)((b << 1) | (Flag(C) ? 1 : 0)); SetFlag(C, c); A &= n; SetNZ(A); return n; }); break; // RLA zp
            case 0x37: Rmw(ZpX(), b => { bool c = (b & 0x80) != 0; byte n = (byte)((b << 1) | (Flag(C) ? 1 : 0)); SetFlag(C, c); A &= n; SetNZ(A); return n; }); break;
            case 0x2F: Rmw(Abs(), b => { bool c = (b & 0x80) != 0; byte n = (byte)((b << 1) | (Flag(C) ? 1 : 0)); SetFlag(C, c); A &= n; SetNZ(A); return n; }); break;
            case 0x3F: Rmw(AbsX(false), b => { bool c = (b & 0x80) != 0; byte n = (byte)((b << 1) | (Flag(C) ? 1 : 0)); SetFlag(C, c); A &= n; SetNZ(A); return n; }); break;
            case 0x23: Rmw(IndX(), b => { bool c = (b & 0x80) != 0; byte n = (byte)((b << 1) | (Flag(C) ? 1 : 0)); SetFlag(C, c); A &= n; SetNZ(A); return n; }); break;
            case 0x33: Rmw(IndY(false), b => { bool c = (b & 0x80) != 0; byte n = (byte)((b << 1) | (Flag(C) ? 1 : 0)); SetFlag(C, c); A &= n; SetNZ(A); return n; }); break;

            case 0x47: Rmw(Zp(), b => { SetFlag(C, (b & 1) != 0); byte n = (byte)(b >> 1); A ^= n; SetNZ(A); return n; }); break; // SRE zp
            case 0x57: Rmw(ZpX(), b => { SetFlag(C, (b & 1) != 0); byte n = (byte)(b >> 1); A ^= n; SetNZ(A); return n; }); break;
            case 0x4F: Rmw(Abs(), b => { SetFlag(C, (b & 1) != 0); byte n = (byte)(b >> 1); A ^= n; SetNZ(A); return n; }); break;
            case 0x5F: Rmw(AbsX(false), b => { SetFlag(C, (b & 1) != 0); byte n = (byte)(b >> 1); A ^= n; SetNZ(A); return n; }); break;
            case 0x43: Rmw(IndX(), b => { SetFlag(C, (b & 1) != 0); byte n = (byte)(b >> 1); A ^= n; SetNZ(A); return n; }); break;
            case 0x53: Rmw(IndY(false), b => { SetFlag(C, (b & 1) != 0); byte n = (byte)(b >> 1); A ^= n; SetNZ(A); return n; }); break;

            case 0x67: Rmw(Zp(), b => { bool c = (b & 1) != 0; byte n = (byte)((b >> 1) | (Flag(C) ? 0x80 : 0)); SetFlag(C, c); Adc(n); return n; }); break; // RRA zp
            case 0x77: Rmw(ZpX(), b => { bool c = (b & 1) != 0; byte n = (byte)((b >> 1) | (Flag(C) ? 0x80 : 0)); SetFlag(C, c); Adc(n); return n; }); break;
            case 0x6F: Rmw(Abs(), b => { bool c = (b & 1) != 0; byte n = (byte)((b >> 1) | (Flag(C) ? 0x80 : 0)); SetFlag(C, c); Adc(n); return n; }); break;
            case 0x7F: Rmw(AbsX(false), b => { bool c = (b & 1) != 0; byte n = (byte)((b >> 1) | (Flag(C) ? 0x80 : 0)); SetFlag(C, c); Adc(n); return n; }); break;
            case 0x63: Rmw(IndX(), b => { bool c = (b & 1) != 0; byte n = (byte)((b >> 1) | (Flag(C) ? 0x80 : 0)); SetFlag(C, c); Adc(n); return n; }); break;
            case 0x73: Rmw(IndY(false), b => { bool c = (b & 1) != 0; byte n = (byte)((b >> 1) | (Flag(C) ? 0x80 : 0)); SetFlag(C, c); Adc(n); return n; }); break;

            // ── Unofficial: misc ─────────────────────────────────
            case 0x0B: case 0x2B:                                        // ANC #imm
                A &= Fetch();
                SetNZ(A);
                SetFlag(C, (A & 0x80) != 0);
                break;
            case 0x4B:                                                    // ALR #imm
            {
                A &= Fetch();
                SetFlag(C, (A & 1) != 0);
                A >>= 1;
                SetNZ(A);
                break;
            }
            case 0x6B:                                                    // ARR #imm (approximation)
            {
                A &= Fetch();
                bool c = (A & 1) != 0;
                A = (byte)((A >> 1) | (Flag(C) ? 0x80 : 0));
                SetFlag(C, c);
                SetNZ(A);
                SetFlag(V, ((A >> 6) & 1) == (c ? 1 : 0) ? false : false);
                break;
            }
            case 0xCB:                                                    // SBX #imm (AXS)
            {
                byte t = (byte)((A & X) - Fetch());
                X = t;
                SetFlag(C, (A & X) >= X);
                SetNZ(X);
                break;
            }
            case 0xBB: A = X = S = (byte)(Read(AbsY()) & S); SetNZ(A); break; // LAS abs,Y
            case 0x9B:                                                    // TAS abs,Y
            {
                ushort a = AbsY(false);
                S = (byte)(A & X);
                Write(a, (byte)(S & ((a >> 8) + 1)));
                break;
            }
            case 0x9C:                                                    // SHY abs,X
            {
                ushort a = AbsX(false);
                Write(a, (byte)(Y & ((a >> 8) + 1)));
                break;
            }
            case 0x9E:                                                    // SHX abs,Y
            {
                ushort a = AbsY(false);
                Write(a, (byte)(X & ((a >> 8) + 1)));
                break;
            }
            case 0x9F:                                                    // AHX abs,Y
            {
                ushort a = AbsY(false);
                Write(a, (byte)((A & X) & ((a >> 8) + 1)));
                break;
            }
            case 0x93:                                                    // AHX (zp),Y
            {
                ushort a = IndY(false);
                Write(a, (byte)((A & X) & ((a >> 8) + 1)));
                break;
            }

            default:
                break; // unknown opcode — treat as NOP
        }
    }

    // ── Cycle table (base cycles; page-cross & branch penalties added in code) ──
    private static readonly byte[] CyclesTable =
    {
        0x07,0x06,0x00,0x08,0x03,0x03,0x05,0x05,0x03,0x02,0x02,0x02,0x04,0x04,0x06,0x06,
        0x02,0x05,0x00,0x08,0x04,0x04,0x06,0x06,0x02,0x04,0x02,0x07,0x04,0x04,0x07,0x07,
        0x06,0x06,0x00,0x08,0x03,0x03,0x05,0x05,0x04,0x02,0x02,0x02,0x04,0x04,0x06,0x06,
        0x02,0x05,0x00,0x08,0x04,0x04,0x06,0x06,0x02,0x04,0x02,0x07,0x04,0x04,0x07,0x07,
        0x06,0x06,0x00,0x08,0x03,0x03,0x05,0x05,0x04,0x02,0x02,0x02,0x03,0x04,0x06,0x06,
        0x02,0x05,0x00,0x08,0x04,0x04,0x06,0x06,0x02,0x04,0x02,0x07,0x04,0x04,0x07,0x07,
        0x06,0x06,0x00,0x08,0x03,0x03,0x05,0x05,0x04,0x02,0x02,0x02,0x05,0x04,0x06,0x06,
        0x02,0x05,0x00,0x08,0x04,0x04,0x06,0x06,0x02,0x04,0x02,0x07,0x04,0x04,0x07,0x07,
        0x02,0x06,0x02,0x06,0x03,0x03,0x03,0x03,0x02,0x02,0x02,0x02,0x04,0x04,0x04,0x04,
        0x02,0x06,0x00,0x06,0x04,0x04,0x04,0x04,0x02,0x05,0x02,0x05,0x05,0x05,0x05,0x05,
        0x02,0x06,0x02,0x06,0x03,0x03,0x03,0x03,0x02,0x02,0x02,0x02,0x04,0x04,0x04,0x04,
        0x02,0x05,0x00,0x05,0x04,0x04,0x04,0x04,0x02,0x04,0x02,0x04,0x04,0x04,0x04,0x04,
        0x02,0x06,0x02,0x08,0x03,0x03,0x05,0x05,0x02,0x02,0x02,0x02,0x04,0x04,0x06,0x06,
        0x02,0x05,0x00,0x08,0x04,0x04,0x06,0x06,0x02,0x04,0x02,0x07,0x04,0x04,0x07,0x07,
        0x02,0x06,0x02,0x08,0x03,0x03,0x05,0x05,0x02,0x02,0x02,0x02,0x04,0x04,0x06,0x06,
        0x02,0x05,0x00,0x08,0x04,0x04,0x06,0x06,0x02,0x04,0x02,0x07,0x04,0x04,0x07,0x07,
    };
}
