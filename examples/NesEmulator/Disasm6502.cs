namespace NesEmulator;

/// <summary>Tiny 6502 disassembler for diagnostics.</summary>
public static class Disasm6502
{
    // (mnemonic, addressing-mode byte count: 0=implied/acc, 1=imm/zp, 2=abs, 3=ind)
    private static readonly (string, int)[] Table = Build();

    private static (string, int)[] Build()
    {
        var t = new (string, int)[256];
        void Set(int op, string m, int n) => t[op] = (m, n);
        for (int i = 0; i < 256; i++) Set(i, "??", 0);

        Set(0x00, "BRK", 1); Set(0x01, "ORA", 1); Set(0x05, "ORA", 1); Set(0x06, "ASL", 1);
        Set(0x08, "PHP", 0); Set(0x09, "ORA", 1); Set(0x0A, "ASL", 0); Set(0x0D, "ORA", 2);
        Set(0x0E, "ASL", 2); Set(0x10, "BPL", 1); Set(0x11, "ORA", 3); Set(0x15, "ORA", 1);
        Set(0x16, "ASL", 1); Set(0x18, "CLC", 0); Set(0x19, "ORA", 2); Set(0x1D, "ORA", 2);
        Set(0x1E, "ASL", 2); Set(0x20, "JSR", 2); Set(0x21, "AND", 1); Set(0x24, "BIT", 1);
        Set(0x25, "AND", 1); Set(0x26, "ROL", 1); Set(0x28, "PLP", 0); Set(0x29, "AND", 1);
        Set(0x2A, "ROL", 0); Set(0x2C, "BIT", 2); Set(0x2D, "AND", 2); Set(0x2E, "ROL", 2);
        Set(0x30, "BMI", 1); Set(0x31, "AND", 3); Set(0x35, "AND", 1); Set(0x36, "ROL", 1);
        Set(0x38, "SEC", 0); Set(0x39, "AND", 2); Set(0x3D, "AND", 2); Set(0x3E, "ROL", 2);
        Set(0x40, "RTI", 0); Set(0x41, "EOR", 1); Set(0x45, "EOR", 1); Set(0x46, "LSR", 1);
        Set(0x48, "PHA", 0); Set(0x49, "EOR", 1); Set(0x4A, "LSR", 0); Set(0x4C, "JMP", 2);
        Set(0x4D, "EOR", 2); Set(0x4E, "LSR", 2); Set(0x50, "BVC", 1); Set(0x51, "EOR", 3);
        Set(0x55, "EOR", 1); Set(0x56, "LSR", 1); Set(0x58, "CLI", 0); Set(0x59, "EOR", 2);
        Set(0x5D, "EOR", 2); Set(0x5E, "LSR", 2); Set(0x60, "RTS", 0); Set(0x61, "ADC", 1);
        Set(0x65, "ADC", 1); Set(0x66, "ROR", 1); Set(0x68, "PLA", 0); Set(0x69, "ADC", 1);
        Set(0x6A, "ROR", 0); Set(0x6C, "JMP", 2); Set(0x6D, "ADC", 2); Set(0x6E, "ROR", 2);
        Set(0x70, "BVS", 1); Set(0x71, "ADC", 3); Set(0x75, "ADC", 1); Set(0x76, "ROR", 1);
        Set(0x78, "SEI", 0); Set(0x79, "ADC", 2); Set(0x7D, "ADC", 2); Set(0x7E, "ROR", 2);
        Set(0x81, "STA", 1); Set(0x84, "STY", 1); Set(0x85, "STA", 1); Set(0x86, "STX", 1);
        Set(0x88, "DEY", 0); Set(0x8A, "TXA", 0); Set(0x8C, "STY", 2); Set(0x8D, "STA", 2);
        Set(0x8E, "STX", 2); Set(0x90, "BCC", 1); Set(0x91, "STA", 3); Set(0x94, "STY", 1);
        Set(0x95, "STA", 1); Set(0x96, "STX", 1); Set(0x98, "TYA", 0); Set(0x99, "STA", 2);
        Set(0x9A, "TXS", 0); Set(0x9D, "STA", 2); Set(0xA0, "LDY", 1); Set(0xA1, "LDA", 1);
        Set(0xA2, "LDX", 1); Set(0xA4, "LDY", 1); Set(0xA5, "LDA", 1); Set(0xA6, "LDX", 1);
        Set(0xA8, "TAY", 0); Set(0xA9, "LDA", 1); Set(0xAA, "TAX", 0); Set(0xAC, "LDY", 2);
        Set(0xAD, "LDA", 2); Set(0xAE, "LDX", 2); Set(0xB0, "BCS", 1); Set(0xB1, "LDA", 3);
        Set(0xB4, "LDY", 1); Set(0xB5, "LDA", 1); Set(0xB6, "LDX", 1); Set(0xB8, "CLV", 0);
        Set(0xB9, "LDA", 2); Set(0xBA, "TSX", 0); Set(0xBC, "LDY", 2); Set(0xBD, "LDA", 2);
        Set(0xBE, "LDX", 2); Set(0xC0, "CPY", 1); Set(0xC1, "CMP", 1); Set(0xC4, "CPY", 1);
        Set(0xC5, "CMP", 1); Set(0xC6, "DEC", 1); Set(0xC8, "INY", 0); Set(0xC9, "CMP", 1);
        Set(0xCA, "DEX", 0); Set(0xCC, "CPY", 2); Set(0xCD, "CMP", 2); Set(0xCE, "DEC", 2);
        Set(0xD0, "BNE", 1); Set(0xD1, "CMP", 3); Set(0xD5, "CMP", 1); Set(0xD6, "DEC", 1);
        Set(0xD8, "CLD", 0); Set(0xD9, "CMP", 2); Set(0xDD, "CMP", 2); Set(0xDE, "DEC", 2);
        Set(0xE0, "CPX", 1); Set(0xE1, "SBC", 1); Set(0xE4, "CPX", 1); Set(0xE5, "SBC", 1);
        Set(0xE6, "INC", 1); Set(0xE8, "INX", 0); Set(0xE9, "SBC", 1); Set(0xEA, "NOP", 0);
        Set(0xEC, "CPX", 2); Set(0xED, "SBC", 2); Set(0xEE, "INC", 2); Set(0xF0, "BEQ", 1);
        Set(0xF1, "SBC", 3); Set(0xF5, "SBC", 1); Set(0xF6, "INC", 1); Set(0xF8, "SED", 0);
        Set(0xF9, "SBC", 2); Set(0xFD, "SBC", 2); Set(0xFE, "INC", 2);

        // Common unofficial (treat as 2-byte or no-arg NOPs where unclear)
        Set(0x80, "NOP", 1); Set(0x82, "NOP", 1); Set(0x89, "NOP", 1); Set(0xC2, "NOP", 1); Set(0xE2, "NOP", 1);
        Set(0x0C, "NOP", 2); Set(0x1A, "NOP", 0); Set(0x3A, "NOP", 0); Set(0x5A, "NOP", 0);
        Set(0x7A, "NOP", 0); Set(0xDA, "NOP", 0); Set(0xFA, "NOP", 0);
        Set(0xA7, "LAX", 1); Set(0xB7, "LAX", 1); Set(0xAF, "LAX", 2); Set(0xBF, "LAX", 2);
        Set(0xAB, "LAX", 1); Set(0x87, "SAX", 1); Set(0x97, "SAX", 1); Set(0x8F, "SAX", 2);
        Set(0xC7, "DCP", 1); Set(0xCF, "DCP", 2); Set(0xE7, "ISB", 1); Set(0xEF, "ISB", 2);
        Set(0x07, "SLO", 1); Set(0x0F, "SLO", 2); Set(0x27, "RLA", 1); Set(0x2F, "RLA", 2);
        Set(0x47, "SRE", 1); Set(0x4F, "SRE", 2); Set(0x67, "RRA", 1); Set(0x6F, "RRA", 2);
        Set(0x2B, "ANC", 1); Set(0x0B, "ANC", 1); Set(0x4B, "ALR", 1); Set(0x6B, "ARR", 1);
        Set(0xCB, "SBX", 1);
        return t;
    }

    /// <summary>Disassemble from addr for count bytes; returns lines.</summary>
    public static string[] Disassemble(Func<ushort, byte> read, ushort start, int count)
    {
        var lines = new List<string>();
        ushort a = start;
        while (a < start + count)
        {
            byte op = read(a);
            var (mn, n) = Table[op];
            var sb = new System.Text.StringBuilder();
            sb.Append($"${a:X4}: {mn}");
            ushort next = (ushort)(a + 1);
            switch (n)
            {
                case 1:
                {
                    byte b = read(next); next++;
                    sb.Append($" #${b:X2}");
                    if (op is 0x10 or 0x30 or 0x50 or 0x70 or 0x90 or 0xB0 or 0xD0 or 0xF0)
                    {
                        int target = (sbyte)b + next;
                        sb.Append($" -> ${(ushort)target:X4}");
                    }
                    break;
                }
                case 2:
                {
                    byte lo = read(next); byte hi = read((ushort)(next + 1));
                    ushort addr = (ushort)(lo | (hi << 8)); next += 2;
                    if (op == 0x20 || op == 0x4C) sb.Append($" ${addr:X4}");
                    else if (op == 0x6C) sb.Append($" (${addr:X4})");
                    else sb.Append($" ${addr:X4}");
                    break;
                }
                case 3:
                {
                    byte lo = read(next); byte hi = read((ushort)(next + 1));
                    ushort addr = (ushort)(lo | (hi << 8)); next += 2;
                    if (op == 0x91 || op == 0x81) sb.Append($" (${lo:X2},X)");
                    else sb.Append($" (${lo:X2}),Y");
                    break;
                }
            }
            lines.Add(sb.ToString());
            a = next;
        }
        return lines.ToArray();
    }
}
