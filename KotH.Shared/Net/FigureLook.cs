using System;

namespace KotH.Shared.Net
{
    /// <summary>A player's look: the figure (fly agaric or champignon), colours (sRGB bytes) of cap, skin,
    /// leaves, gloves, spores, glow and trail, the glow switch and the trail's shape. Only shown, never
    /// simulated; it travels with the join. Default is the fly agaric as painted: the region means
    /// Tools/figure/FigureMask.cs prints, the spores the double jump's yellow, no glow, no trail.</summary>
    public struct FigureLook : IEquatable<FigureLook>
    {
        /// <summary>The figure's own parts, each a region of its texture.</summary>
        public const int Parts = 5;
        public const int Cap = 0, Skin = 1, Leaves = 2, Gloves = 3, Spores = 4;

        /// <summary>Every colour: the parts, then the glow's and the trail's.</summary>
        public const int Colors = 7;
        public const int Glow = 5, Trail = 6;

        public const byte FlyAgaric = 0, Champignon = 1, Kinds = 2;
        public const byte NoTrail = 0, Sparks = 1, Stars = 2, SporeTrail = 3, TrailShapes = 4;

        static readonly uint[] Painted = { 0xAD1815, 0xE7CEBE, 0x396C26, 0x513222, 0xFFEB4D, 0xFFC94D, 0xFFE9A0 };

        // Seven colours, 0xRRGGBB each.
        uint c0, c1, c2, c3, c4, c5, c6;

        /// <summary>FlyAgaric or Champignon.</summary>
        public byte Kind;

        /// <summary>Whether the figure glows in the Glow colour (from level 100).</summary>
        public bool Glows;

        /// <summary>The sparkle trail's shape, NoTrail for none (from level 100).</summary>
        public byte TrailShape;

        public static FigureLook Default
        {
            get
            {
                var look = new FigureLook();
                for (int i = 0; i < Colors; i++)
                    look[i] = Painted[i];
                return look;
            }
        }

        /// <summary>Colour i as 0xRRGGBB.</summary>
        public uint this[int i]
        {
            readonly get => i switch
            {
                0 => c0, 1 => c1, 2 => c2, 3 => c3, 4 => c4, 5 => c5, 6 => c6,
                _ => throw new ArgumentOutOfRangeException(nameof(i)),
            };
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
                    case 5: c5 = value; break;
                    case 6: c6 = value; break;
                    default: throw new ArgumentOutOfRangeException(nameof(i));
                }
            }
        }

        public static uint PaintedColor(int part) => Painted[part];

        // The champignon's cap as painted (the mean of ChampignonCap.png under its mask).
        const uint ChampignonCap = 0xD3B999;

        /// <summary>Part's painted colour on the figure kind: the champignon's cap differs.</summary>
        public static uint PaintedColor(byte kind, int part) => kind == Champignon && part == Cap ? ChampignonCap : Painted[part];

        /// <summary>Changes the figure; a cap still as painted takes the new figure's painted cap.</summary>
        public void Become(byte kind)
        {
            if (this[Cap] == PaintedColor(Kind, Cap))
                this[Cap] = PaintedColor(kind, Cap);
            Kind = kind;
        }

        public void Write(ref Writer w)
        {
            for (int i = 0; i < Colors; i++)
            {
                uint c = this[i];
                w.Byte((byte)(c >> 16));
                w.Byte((byte)(c >> 8));
                w.Byte((byte)c);
            }
            w.Byte(Kind);
            w.Byte((byte)(Glows ? 1 : 0));
            w.Byte(TrailShape);
        }

        /// <summary>The look as written; values out of range read as the default's.</summary>
        public static FigureLook Read(ref Reader r)
        {
            var look = new FigureLook();
            for (int i = 0; i < Colors; i++)
                look[i] = (uint)r.Byte() << 16 | (uint)r.Byte() << 8 | r.Byte();
            byte kind = r.Byte(), glows = r.Byte(), trail = r.Byte();
            look.Kind = kind < Kinds ? kind : FlyAgaric;
            look.Glows = glows == 1;
            look.TrailShape = trail < TrailShapes ? trail : NoTrail;
            return look;
        }

        public readonly bool Equals(FigureLook o) => c0 == o.c0 && c1 == o.c1 && c2 == o.c2 && c3 == o.c3 && c4 == o.c4
                                                      && c5 == o.c5 && c6 == o.c6 && Kind == o.Kind && Glows == o.Glows
                                                      && TrailShape == o.TrailShape;
        public override readonly bool Equals(object o) => o is FigureLook l && Equals(l);
        public override readonly int GetHashCode() => HashCode.Combine(HashCode.Combine(c0, c1, c2, c3, c4, c5, c6), Kind, Glows, TrailShape);
        public static bool operator ==(FigureLook a, FigureLook b) => a.Equals(b);
        public static bool operator !=(FigureLook a, FigureLook b) => !a.Equals(b);
    }
}
