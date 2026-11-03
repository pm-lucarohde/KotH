using System;

namespace KotH.Shared.Net
{
    /// <summary>A player's colours (sRGB bytes): cap, skin, leaves, gloves and spores. Only shown,
    /// never simulated; it travels with the join. Default is the figure as painted: the region
    /// means Tools/figure/FigureMask.cs prints, the spores the double jump's yellow.</summary>
    public struct FigureLook : IEquatable<FigureLook>
    {
        public const int Parts = 5;
        public const int Cap = 0, Skin = 1, Leaves = 2, Gloves = 3, Spores = 4;

        static readonly uint[] Painted = { 0xAD1815, 0xE7CEBE, 0x396C26, 0x513222, 0xFFEB4D };

        // Five colours, 0xRRGGBB each.
        uint c0, c1, c2, c3, c4;

        public static FigureLook Default
        {
            get
            {
                var look = new FigureLook();
                for (int i = 0; i < Parts; i++)
                    look[i] = Painted[i];
                return look;
            }
        }

        /// <summary>Part i's colour as 0xRRGGBB.</summary>
        public uint this[int i]
        {
            readonly get => i switch { 0 => c0, 1 => c1, 2 => c2, 3 => c3, 4 => c4, _ => throw new ArgumentOutOfRangeException(nameof(i)) };
            set
            {
                value &= 0xFFFFFF;
                switch (i)
                {
                    case 0: c0 = value; break;
                    case 1: c1 = value; break;
                    case 2: c2 = value; break;
                    case 3: c3 = value; break;
                    case 4: c4 = value; break;
                    default: throw new ArgumentOutOfRangeException(nameof(i));
                }
            }
        }

        public static uint PaintedColor(int part) => Painted[part];

        public void Write(ref Writer w)
        {
            for (int i = 0; i < Parts; i++)
            {
                uint c = this[i];
                w.Byte((byte)(c >> 16));
                w.Byte((byte)(c >> 8));
                w.Byte((byte)c);
            }
        }

        public static FigureLook Read(ref Reader r)
        {
            var look = new FigureLook();
            for (int i = 0; i < Parts; i++)
                look[i] = (uint)r.Byte() << 16 | (uint)r.Byte() << 8 | r.Byte();
            return look;
        }

        public readonly bool Equals(FigureLook o) => c0 == o.c0 && c1 == o.c1 && c2 == o.c2 && c3 == o.c3 && c4 == o.c4;
        public override readonly bool Equals(object o) => o is FigureLook l && Equals(l);
        public override readonly int GetHashCode() => HashCode.Combine(c0, c1, c2, c3, c4);
        public static bool operator ==(FigureLook a, FigureLook b) => a.Equals(b);
        public static bool operator !=(FigureLook a, FigureLook b) => !a.Equals(b);
    }
}
