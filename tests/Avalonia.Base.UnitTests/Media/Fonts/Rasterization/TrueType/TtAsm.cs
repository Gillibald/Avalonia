using System.Collections.Generic;

namespace Avalonia.Base.UnitTests.Media.Fonts.Rasterization.TrueType
{
    /// <summary>
    /// A minimal TrueType instruction assembler for the interpreter tests: opcode bytes plus
    /// the push encodings, nothing more. Opcode constants cover only what the tests use.
    /// </summary>
    internal sealed class TtAsm
    {
        public const byte Svtca0 = 0x00;
        public const byte Srp0 = 0x10;
        public const byte Szp0 = 0x13;
        public const byte Sloop = 0x17;
        public const byte Rtg = 0x18;
        public const byte Rthg = 0x19;
        public const byte Else = 0x1B;
        public const byte Jmpr = 0x1C;
        public const byte Scvtci = 0x1D;
        public const byte Ssw = 0x1F;
        public const byte Dup = 0x20;
        public const byte Pop = 0x21;
        public const byte Clear = 0x22;
        public const byte Swap = 0x23;
        public const byte Depth = 0x24;
        public const byte Cindex = 0x25;
        public const byte Mindex = 0x26;
        public const byte LoopCall = 0x2A;
        public const byte Call = 0x2B;
        public const byte Fdef = 0x2C;
        public const byte Endf = 0x2D;
        public const byte Mdap0 = 0x2E;
        public const byte Rtdg = 0x3D;
        public const byte Ws = 0x42;
        public const byte Rs = 0x43;
        public const byte Wcvtp = 0x44;
        public const byte Rcvt = 0x45;
        public const byte Mppem = 0x4B;
        public const byte Mps = 0x4C;
        public const byte Debug = 0x4F;
        public const byte Lt = 0x50;
        public const byte Lteq = 0x51;
        public const byte Gt = 0x52;
        public const byte Gteq = 0x53;
        public const byte Eq = 0x54;
        public const byte Neq = 0x55;
        public const byte Odd = 0x56;
        public const byte Even = 0x57;
        public const byte If = 0x58;
        public const byte Eif = 0x59;
        public const byte And = 0x5A;
        public const byte Or = 0x5B;
        public const byte Not = 0x5C;
        public const byte Sdb = 0x5E;
        public const byte Sds = 0x5F;
        public const byte Add = 0x60;
        public const byte Sub = 0x61;
        public const byte Div = 0x62;
        public const byte Mul = 0x63;
        public const byte Abs = 0x64;
        public const byte Neg = 0x65;
        public const byte Floor = 0x66;
        public const byte Ceiling = 0x67;
        public const byte Round0 = 0x68;
        public const byte Wcvtf = 0x70;
        public const byte DeltaC1 = 0x73;
        public const byte Sround = 0x76;
        public const byte S45Round = 0x77;
        public const byte Jrot = 0x78;
        public const byte Jrof = 0x79;
        public const byte Roff = 0x7A;
        public const byte Rutg = 0x7C;
        public const byte Rdtg = 0x7D;
        public const byte Scanctrl = 0x85;
        public const byte GetInfo = 0x88;
        public const byte Idef = 0x89;
        public const byte Roll = 0x8A;
        public const byte Max = 0x8B;
        public const byte Min = 0x8C;
        public const byte Scantype = 0x8D;
        public const byte Instctrl = 0x8E;

        private readonly List<byte> _bytes = new();

        public TtAsm Op(params byte[] opcodes)
        {
            _bytes.AddRange(opcodes);
            return this;
        }

        /// <summary>PUSHB[n] for up to 8 values, NPUSHB beyond.</summary>
        public TtAsm PushB(params byte[] values)
        {
            if (values.Length <= 8)
            {
                _bytes.Add((byte)(0xB0 + values.Length - 1));
            }
            else
            {
                _bytes.Add(0x40);
                _bytes.Add((byte)values.Length);
            }

            _bytes.AddRange(values);
            return this;
        }

        /// <summary>PUSHW[n] for up to 8 values, NPUSHW beyond.</summary>
        public TtAsm PushW(params short[] values)
        {
            if (values.Length <= 8)
            {
                _bytes.Add((byte)(0xB8 + values.Length - 1));
            }
            else
            {
                _bytes.Add(0x41);
                _bytes.Add((byte)values.Length);
            }

            foreach (var value in values)
            {
                _bytes.Add((byte)(value >> 8));
                _bytes.Add((byte)value);
            }

            return this;
        }

        public byte[] Build() => _bytes.ToArray();
    }
}
