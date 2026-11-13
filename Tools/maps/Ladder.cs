#!/usr/bin/env -S dotnet run
// Map "The Ladder" after "The Ultimate King of the Ladder" by KRMisha and Invensations (with permission): its
// tower measured from .work/ladder/blocks.pkl (1 m across, 0.8 m high), rebuilt as painted marble in its colours.
// Writes TheLadder.glb, TheLadderTerrain.bin and the collision prefab's Ladder/Pad entities.
// Usage: dotnet run Tools/maps/Ladder.cs, then Tools/maps/MapUV --map TheLadder.
#:project ../../KotH.MapFormat/KotH.MapFormat.csproj
#:include ArenaShapes.cs
#:include MountainTerrain.cs
#:include ../Common/Py.cs
#:include ../Common/Libm.cs
#:include ../Common/Glb.cs
#:include ../Common/Png.cs
#:property ServerGarbageCollection=true
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using KotH.MapFormat;
using KotH.Tools;
using KotH.Tools.Maps;

string root = Path.GetFullPath(Path.Combine(Here(), "../.."));
var arena = new Arena();
var chunky = Arena.Chamfered;
double Rad(double degrees) => degrees * Math.PI / 180;
V3 Polar(double r, double degrees, double y) => new(r * Math.Cos(Rad(degrees)), y, r * Math.Sin(Rad(degrees)));
double[] arms = [0, 90, 180, 270];
double[] Union(params double[][] sets) => [.. sets.SelectMany(a => a).Distinct().Order()];
// A point by an arm m: along it from the middle, across it (to the left looking out), at height y.
V3 Arm(double m, double along, double across, double y)
    => new(along * Math.Cos(Rad(m)) - across * Math.Sin(Rad(m)), y, along * Math.Sin(Rad(m)) + across * Math.Cos(Rad(m)));

// ---- the grid: block (x, y, z) of the source spans x +- 0.5, z +- 0.5 (Minecraft's x and z, both right-handed
// with y up), heights from (y - 1) * 0.8 to y * 0.8; the base's top (block y 0) is height 0 ----
const double Rise = 0.8;
double H(double y) => (y - 1) * Rise;

// ---- the colours: each side of the tower has its terracotta, as marble (TintedTriplanar's COLOR0) ----
static (double, double, double, double) Tone(double r, double g, double b) => (r, g, b, 1);
// From east (+x, 0 degrees) round to +z: red, yellow, light blue, blue, lime, green, magenta, orange.
(double, double, double, double)[] sides = [Tone(0.66, 0.29, 0.21), Tone(0.84, 0.62, 0.2), Tone(0.52, 0.52, 0.66), Tone(0.36, 0.29, 0.46),
                                            Tone(0.47, 0.56, 0.25), Tone(0.33, 0.4, 0.22), Tone(0.68, 0.4, 0.5), Tone(0.76, 0.4, 0.18)];
(double, double, double, double) Side(double degrees) => sides[(((int)Math.Round(degrees / 45)) % 8 + 8) % 8];
(double, double, double, double) SideOf(double x, double z) => Side(Math.Atan2(z, x) * 180 / Math.PI);
var gem = Tone(0.6, 0.92, 0.9);

// A square shaft at (x, z) from bottom to top (metres), w x d with corners cut by c, its sides along facing.
void Shaft(string material, double x, double z, double bottom, double top, double w, double c, (double, double, double, double)? colour = null,
           double shade = 1.0, bool solid = true, double d = 0, double facing = 0)
{
    d = d > 0 ? d : w;
    arena.Bar(material, new V3(x, bottom, z), new V3(x, top, z), [.. chunky(w, d, c).Select(p => (p.U, p.V - d / 2))], solid,
              up: Polar(1, facing, 0), colour: colour, shade: shade);
}
// The same at (x, z) turned by 0, 90, 180 and 270 degrees; act gets each copy's (x, z).
void Four(double x, double z, Action<double, double> act)
{
    foreach (var (a, b) in new[] { (x, z), (-z, x), (-x, -z), (z, -x) })
        act(a, b);
}
// A cone hanging under (x, z) from y down by depth, r wide at the top: what carries every stone in the air.
void Pendant(double x, double z, double y, double r, double depth, double shade = 0.85)
    => arena.Lathe("Trim", [(0, y - depth), (r * 0.25, y - depth * 0.8), (r * 0.6, y - depth * 0.4), (r, y - depth * 0.08), (r, y), (0, y)],
                   true, x, z, chord: 0.12, least: 12, shade: shade);
// A slab hung in the air (bottom to top): a round marble disc with a rounded lip on a cone.
void Plate(double x, double z, double bottom, double top, double r = 0.6)
{
    arena.Lathe("Marble", [(0, bottom + 0.1), (r - 0.08, bottom + 0.1), (r, bottom + 0.2), (r, top - 0.06), (r - 0.05, top), (0, top)], true, x, z,
                chord: 0.12, least: 16);
    Pendant(x, z, bottom + 0.1, r * 0.7, 0.45);
}
// A block hung in the air: an octagonal stone, its corners cut wide, on a cone.
void Floater(string material, double x, double z, double bottom, double top, (double, double, double, double)? colour = null)
{
    Shaft(material, x, z, bottom, top, 1.0, 0.2, colour);
    Pendant(x, z, bottom, 0.42, 0.55, 0.8);
}
// A trampoline (the source's slime blocks): a round mat in a coloured stone rim on wooden legs, on an
// octagonal stone (a cone under it unless it stands on a plate). Its collision is the block as it was.
void Pad(double x, double z, double bottom, double top, (double, double, double, double) colour, bool hangs)
{
    Shaft("Trim", x, z, bottom, bottom + 0.3, 1.0, 0.2, solid: false);
    if (hangs)
        Pendant(x, z, bottom, 0.42, 0.5, 0.8);
    double rim = top - 0.12;
    arena.Lathe("Tinted", [(0.36, rim), (0.48, rim), (0.5, rim + 0.04), (0.5, top - 0.03), (0.47, top), (0.36, top), (0.34, top - 0.04), (0.36, rim)],
                false, x, z, chord: 0.1, least: 20, colour: colour);
    arena.Lathe("Mat", [(0, top - 0.07), (0.36, top - 0.07), (0.36, top - 0.04), (0, top - 0.04)], false, x, z, chord: 0.1, least: 20);
    for (int k = 0; k < 6; k++)
    {
        V3 f = Polar(0.4, 30 + 60 * k, 0);
        arena.Bar("Planks", new V3(x + f.X, bottom + 0.3, z + f.Z), new V3(x + f.X, rim, z + f.Z),
                  [.. chunky(0.07, 0.07, 0.02).Select(p => (p.U, p.V - 0.035))], false);
    }
    arena.Bar("_solid", new V3(x, bottom, z), new V3(x, top, z), [.. chunky(1.0, 1.0, 0).Select(p => (p.U, p.V - 0.5))], true, up: new V3(1, 0, 0));
}
// A round column at (x, z): marble plinth and capital, the shaft coloured.
void Column(double x, double z, double bottom, double top, double r, (double, double, double, double) colour)
{
    double w = r + 0.12;
    arena.Lathe("Marble", [(w, bottom), (w, bottom + 0.2), (r + 0.06, bottom + 0.3), (r, bottom + 0.38), (0, bottom + 0.38)], true, x, z,
                chord: 0.15, least: 16);
    arena.Lathe("Tinted", [(r, bottom + 0.38), (r, top - 0.38)], true, x, z, chord: 0.15, least: 16, colour: colour);
    arena.Lathe("Marble", [(0, top - 0.38), (r, top - 0.38), (r + 0.06, top - 0.3), (w, top - 0.2), (w, top), (0, top)], true, x, z,
                chord: 0.15, least: 16);
}
// A closed ring profile (inner, outer radius, bottom, top), whole or in the eight coloured sectors.
(double R, double Y)[] Box2(double rIn, double rOut, double bottom, double top)
    => [(rIn, bottom), (rOut, bottom), (rOut, top), (rIn, top), (rIn, bottom)];
void Sectors((double R, double Y)[] profile)
{
    for (int k = 0; k < 8; k++)
        arena.Lathe("Tinted", profile, true, a0: Rad(-22.5 + 45 * k), a1: Rad(22.5 + 45 * k), colour: sides[k]);
}

// ---- the base: a marble disc r 16.6 with coloured rings let in, on a stepped cone hanging into the void ----
const double Disc = 16.6, Ring = 10.0, Wall = 16.1, Layer = 0.3;
(double In, double Out, bool Coloured)[] floor = [(0, 1.3, false), (1.3, 2.0, true), (2.0, 7.4, false), (7.4, 7.8, true), (7.8, 9.3, false),
                                                  (9.3, 10.7, true), (10.7, 14.8, false), (14.8, 15.2, true), (15.2, Disc, false)];
foreach (var (rIn, rOut, coloured) in floor)
    if (coloured)
        Sectors(Box2(rIn, rOut, -Layer, 0));
    else
        arena.Lathe("Paving", Box2(rIn, rOut, -Layer, 0), true);
arena.Lathe("Marble", [(14.6, -1.2), (15.6, -1.05), (16.4, -0.95), (16.8, -0.75), (16.8, -0.5), (Disc, -Layer), (14.6, -Layer), (14.6, -1.2)], true);
arena.Lathe("Trim", [(0, -10.5), (0.5, -10.0), (0.9, -8.8), (2.2, -7.6), (2.2, -7.2), (5.5, -5.2), (5.5, -4.8), (10, -2.6), (10, -2.2),
                       (14.6, -1.2), (14.6, -Layer), (0, -Layer)], true, shade: 0.85);

// ---- the glass ring r 10: four gates 7 m wide on the axes between square piers, glass on a sill between
// the piers and coloured round columns on the diagonals, a coloured frieze and a cornice over it all ----
const double Top = 3.2;
double pier = Math.Asin(4.0 / Ring) * 180 / Math.PI, slender = (pier + 45) / 2;
foreach (double m in arms)
    foreach (int s in new[] { -1, 1 })
    {
        V3 p = Polar(Ring, m + s * pier, 0);
        Shaft("Trim", p.X, p.Z, 0, 0.4, 1.25, 0.06, facing: m + s * pier);
        Shaft("Marble", p.X, p.Z, 0.4, 2.85, 1.0, 0.06, facing: m + s * pier);
        Shaft("Trim", p.X, p.Z, 2.85, Top + 0.05, 1.2, 0.06, facing: m + s * pier);
        // Glass from the pier to the diagonal's column, a slender column halfway.
        double a0 = Math.Min(m + s * pier, m + s * 45), a1 = Math.Max(m + s * pier, m + s * 45);
        arena.Lathe("Marble", Box2(9.75, 10.25, 0.01, 0.3), true, a0: Rad(a0), a1: Rad(a1), least: 2);
        arena.Lathe("Glass", Box2(9.97, 10.03, 0.25, 2.98), true, a0: Rad(a0 + 0.5), a1: Rad(a1 - 0.5), least: 2);
        arena.Lathe("Marble", Box2(9.8, 10.2, 2.95, Top), true, a0: Rad(a0), a1: Rad(a1), least: 2);
        V3 c = Polar(Ring, m + s * slender, 0);
        arena.Lathe("Marble", [(0.22, 0.3), (0.16, 0.42), (0.16, 2.8), (0.22, 2.95), (0, 2.95)], true, c.X, c.Z, chord: 0.1, least: 12);
    }
for (int k = 0; k < 4; k++)
{
    V3 c = Polar(9.9, 45 + 90 * k, 0);
    Column(c.X, c.Z, 0, Top + 0.05, 0.38, Side(45 + 90 * k));
}
arena.Lathe("Trim", Box2(9.5, 10.5, Top, 3.36), true);
Sectors(Box2(9.55, 10.45, 3.34, 3.7));
arena.Lathe("Marble", [(9.5, 3.6), (10.5, 3.6), (11.0, 3.66), (11.35, 3.76), (11.5, 3.84), (11.5, 4.0), (8.5, 4.0), (8.5, 3.84), (8.65, 3.76),
                       (9.0, 3.66), (9.5, 3.6)], true);

// ---- the outer wall r 16: a moulded plinth, glass between marble piers every 30 degrees and coloured posts
// on the axes, a cornice; beams on corbels from it to the ring's cornice, two by each gate ----
arena.Lathe("Marble", [(15.6, 0), (Disc, 0), (Disc, 0.7), (16.5, 0.8), (15.7, 0.8), (15.6, 0.7), (15.6, 0)], true);
arena.Lathe("Glass", Box2(16.05, 16.15, 0.75, Top + 0.02), true);
for (int k = 0; k < 12; k++)
{
    V3 p = Polar(Wall, 15 + 30 * k, 0);
    Shaft("Marble", p.X, p.Z, 0.05, Top + 0.05, 0.8, 0.06, facing: 15 + 30 * k);
}
foreach (double m in arms)
{
    V3 p = Polar(Wall, m, 0);
    // In the plinth a little narrower: at full width its faces lay on the plinth's (r 15.6 and 16.6) and
    // showed through as a line where a facet's edge meets the axis.
    Shaft("Tinted", p.X, p.Z, 0.05, 0.72, 0.98, 0.08, Side(m), facing: m);
    Shaft("Tinted", p.X, p.Z, 0.72, Top + 0.05, 1.0, 0.08, Side(m), facing: m);
}
arena.Lathe("Marble", [(15.7, Top), (16.5, Top), (16.7, 3.35), (16.8, 3.5), (16.8, 4.0), (15.4, 4.0), (15.4, 3.5), (15.5, 3.35), (15.7, Top)], true);
foreach (double m in arms)
    foreach (int s in new[] { -1, 1 })
    {
        arena.Bar("Trim", Arm(m, 10.3, s * 4, 3.3), Arm(m, 16.3, s * 4, 3.3), chunky(0.7, 0.6, 0.08), true);
        arena.Bar("Trim", Arm(m, 15.0, s * 4, 2.9), Arm(m, 16.05, s * 4, 2.9), chunky(0.5, 0.42, 0.05), true, shade: 0.85);
    }

// ---- the centre pillar: a moulded foot, marble to block 24, the collar at 12; again from the cap at 42
// to 76 and from 80 to the diamond at 96, a hole between (the way goes out round it) ----
arena.Lathe("Trim", [(0, 0.005), (1.0, 0.005), (1.0, 0.14), (0.9, 0.22), (0.78, 0.26), (0.72, 0.34), (0, 0.34)], true, chord: 0.12, least: 24);
Shaft("Marble", 0, 0, 0.34, H(24), 1.0, 0.2);
Shaft("Trim", 0, 0, H(24), H(25), 1.0, 0.2);
Shaft("Trim", 0, 0, H(42), H(43), 1.0, 0.2);
Pendant(0, 0, H(42), 0.45, 0.6, 0.8);
Shaft("Marble", 0, 0, H(43), H(76), 1.0, 0.2);
Shaft("Trim", 0, 0, H(76), H(77), 1.0, 0.2);
Shaft("Trim", 0, 0, H(80), H(81), 1.0, 0.2);
Pendant(0, 0, H(80), 0.45, 0.5, 0.8);
Shaft("Marble", 0, 0, H(81), H(96), 1.0, 0.2);
Shaft("Tinted", 0, 0, H(96), H(97), 1.0, 0.3, gem);

// The collar at block 12: a plate round the pillar with slots for its four ladders, its lower step in the
// sides' colours, arms out to the inner satellites. The slot's corners: 18.4 and 45 degrees from an axis.
double Fold(double a) { double f = ((a % (Math.PI / 2)) + Math.PI / 2) % (Math.PI / 2); return Math.Min(f, Math.PI / 2 - f); }
Func<double, double> slots = a => { double f = Fold(a); return (f <= Math.Atan(1.0 / 3) ? 1.5 / Math.Cos(f) : 0.5 / Math.Sin(f)) - 0.02; };
double[] Corners(double a0, double a1) => [.. Enumerable.Range(-1, 6).SelectMany(q => new[] { q * 90 - 18.435, q * 90 + 18.435, q * 90 + 45.0 })
                                           .Select(Rad).Where(a => a > a0 + 1e-9 && a < a1 - 1e-9)];
double[] collar = Union(Arena.Angles(0, 2 * Math.PI, 2.5, 2.83), Corners(0, 2 * Math.PI));
arena.Band("Marble", slots, Arena.Octagon(2.5, 2.83), H(12) + 0.4, H(13), collar, true);
for (int k = 0; k < 8; k++)
{
    double a0 = Rad(-22.5 + 45 * k), a1 = Rad(22.5 + 45 * k);
    arena.Band("Tinted", slots, Arena.Octagon(2.2, 2.53), H(12) + 0.05, H(12) + 0.4,
               Union(Arena.Angles(a0, a1, 2.2, 2.53), Corners(a0, a1)), true, sides[k]);
}
foreach (double m in arms)
    arena.Bar("Marble", Arm(m, 2.3, 0, H(12)), Arm(m, 4.5, 0, H(12)), chunky(1.0, 0.4, 0.05), true);

// ---- the inner satellites on the axes at 4 (blocks 1-11): banded marble, carved and coloured stones in
// the source's order, on a stepped plinth ----
Four(4, 0, (x, z) =>
{
    arena.Lathe("Trim", [(0, 0.005), (1.1, 0.005), (1.1, 0.16), (0.95, 0.26), (0.8, 0.32), (0.75, 0.4), (0, 0.4)], true, x, z, chord: 0.12, least: 24);
    for (int y = 1; y <= 11; y++)
        switch (y % 4)
        {
            case 1: Shaft("Trim", x, z, Math.Max(H(y), 0.4), H(y + 1), 1.0, 0.2); break;
            case 3: Shaft("Tinted", x, z, H(y), H(y + 1), 1.0, 0.2, SideOf(x, z)); break;
            default: Shaft("Marble", x, z, H(y), H(y + 1), 1.0, 0.2); break;
        }
});

// ---- the columns over them (blocks 17-40): coloured, marble at both ends, a pendant under the foot ----
Four(4, 0, (x, z) =>
{
    Pendant(x, z, H(17), 0.45, 0.6, 0.8);
    Shaft("Trim", x, z, H(17), H(18), 1.0, 0.2);
    Shaft("Tinted", x, z, H(18), H(40), 1.0, 0.2, SideOf(x, z));
    Shaft("Trim", x, z, H(40), H(41), 1.0, 0.2);
});

// ---- what hangs round the gap and above: plates (top slabs), carved blocks, the platforms at 56 and 59 ----
foreach (var (x, z) in new[] { (2, 1), (2, -1) })
    Four(x, z, (a, b) => Plate(a, b, H(24) + 0.4, H(25)));
Four(5, 5, (a, b) => Plate(a, b, H(26) + 0.4, H(27)));
Four(2, 0, (a, b) => Floater("Marble", a, b, H(37), H(38)));
foreach (var (x, z) in new[] { (4, 3), (3, 4) })
    Four(x, z, (a, b) => Floater("Tinted", a, b, H(41), H(42), SideOf(a, b)));
Four(2, 2, (a, b) => Floater("Tinted", a, b, H(42), H(43), SideOf(a, b)));
Four(1, 0, (a, b) => Plate(a, b, H(42) + 0.4, H(43), 0.5));
Four(3, 0, (a, b) => Plate(a, b, H(49) + 0.4, H(50)));
foreach (var (x, z) in new[] { (4, 4), (4, 3), (3, 4) })
    Four(x, z, (a, b) => Floater("Tinted", a, b, H(50), H(51), SideOf(a, b)));
// At 56: a 3 x 3 marble plate on a cross of glass round a coloured stone; at 59 a round one with a pad.
Four(5, 0, (x, z) =>
{
    arena.Lathe("Marble", [(0, H(56) + 0.4), (1.45, H(56) + 0.4), (1.6, H(56) + 0.5), (1.6, H(57) - 0.06), (1.55, H(57)), (0, H(57))], true, x, z,
                chord: 0.12, least: 24);
    Shaft("Tinted", x, z, H(56), H(56) + 0.4, 1.0, 0.2, SideOf(x, z));
    Pendant(x, z, H(56), 0.42, 0.7, 0.8);
    arena.Lathe("Marble", [(1.35, H(59) + 0.4), (1.5, H(59) + 0.55), (1.5, H(60)), (0, H(60)), (0, H(59) + 0.4), (1.35, H(59) + 0.4)], true, x, z,
                chord: 0.15, least: 24);
    Pendant(x, z, H(59) + 0.4, 0.9, 0.8);
});
foreach (var (x, z) in new[] { (4, 0), (6, 0), (5, 1), (5, -1) })
    Four(x, z, (a, b) => Shaft("Glass", a, b, H(56) + 0.01, H(56) + 0.39, 0.9, 0.2));
Four(4, 4, (a, b) => Plate(a, b, H(61) + 0.4, H(62)));
Four(4, 0, (a, b) => Floater("Marble", a, b, H(77), H(78)));
Four(4, 4, (a, b) => Floater("Marble", a, b, H(78), H(79)));
Four(2, 2, (a, b) => Floater("Marble", a, b, H(79), H(80)));

// ---- trampolines (the source's slime blocks, bounce): (x, z, block y), each in the four quarters ----
(int X, int Z, int Y)[] pads = [(4, 4, 13), (5, 5, 27), (3, 0, 50), (5, 0, 60), (4, 4, 62), (2, 2, 63)];
foreach (var (x, z, y) in pads)
    Four(x, z, (a, b) => Pad(a, b, H(y), H(y + 1), SideOf(a, b), y is 13 or 63));

// ---- ladders: (block x, z it hangs on, outward face (dx, dz), first and last block y), in the four quarters
// unless marked one; drawn as two rails with rungs 0.3 m apart, 5 cm off the face, not solid ----
var strips = new List<(int X, int Z, int Dx, int Dz, int Y0, int Y1)>();
void Strip(int x, int z, int dx, int dz, int y0, int y1, bool four = true)
{
    foreach (var (a, b, da, db) in four ? new[] { (x, z, dx, dz), (-z, x, -dz, dx), (-x, -z, -dx, -dz), (z, -x, dz, -dx) }
                                        : new[] { (x, z, dx, dz) })
        strips.Add((a, b, da, db, y0, y1));
}
Strip(0, 0, 1, 0, 1, 24);
// The upper pillar: each face has its own gaps, so a climber goes round to another face; at 77-79 the
// pillar itself has a hole, and the way leads out over the blocks at 77, 78 and 79 and back.
Strip(0, 0, 1, 0, 43, 50, false); Strip(0, 0, 1, 0, 54, 70, false); Strip(0, 0, 1, 0, 75, 76, false);
Strip(0, 0, -1, 0, 43, 55, false); Strip(0, 0, -1, 0, 59, 76, false);
Strip(0, 0, 0, -1, 43, 62, false); Strip(0, 0, 0, -1, 66, 76, false);
Strip(0, 0, 0, 1, 43, 64, false); Strip(0, 0, 0, 1, 69, 76, false);
Strip(0, 0, 1, 0, 80, 96);
Strip(4, 0, 1, 0, 2, 11);
foreach (var (dx, dz) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
    Strip(4, 0, dx, dz, 17, 40);
Strip(3, 0, 0, 1, 50, 50); Strip(3, 0, 0, -1, 50, 50);
Strip(4, 0, -1, 0, 77, 77);
Strip(4, 4, -1, 0, 78, 78); Strip(4, 4, 0, -1, 78, 78);
foreach (var (x, z, dx, dz, y0, y1) in strips)
{
    // Rails along the face's edges, round rungs between them.
    V3 Face(double across, double y) => new(x + dx * 0.55 - dz * across, y, z + dz * 0.55 + dx * across);
    var up = new V3(dx, 0, dz);
    foreach (int s in new[] { -1, 1 })
        arena.Bar("Planks", Face(s * 0.3, H(y0) + 0.01), Face(s * 0.3, H(y1 + 1)), [.. chunky(0.06, 0.06, 0.015).Select(p => (p.U, p.V - 0.03))], false, up);
    for (double y = H(y0) + 0.15; y < H(y1 + 1) - 0.05; y += 0.3)
        arena.Bar("Planks", Face(-0.28, y), Face(0.28, y), [.. chunky(0.045, 0.045, 0.013).Select(p => (p.U, p.V - 0.0225))], false);
}

// ---- the collision prefab's ladders and pads: one entity each (Ladder_k at its foot on the face, looking
// out of the wall; Pad_k in the middle of its top), rewritten on every run with ids from their names ----
{
    string prefab = Path.Combine(root, "KotH.Shared/Assets/Maps/TheLadder/TheLadderCollision.sdprefab");
    string Id(string name, string part) => new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes($"TheLadder/{name}/{part}")))
        .ToString().ToUpperInvariant();
    string Key(string name, string part) => Id(name, part).Replace("-", "").ToLowerInvariant();
    string F(double v) => Math.Round(v, 4).ToString("0.0###", System.Globalization.CultureInfo.InvariantCulture);
    var made = new List<(string Name, string Text)>();
    void Entity(string name, double x, double y, double z, double yaw, string component)
    {
        double half = Rad(yaw) / 2;
        string R(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        string pos = "{X: " + F(x) + ", Y: " + F(y) + ", Z: " + F(z) + "}";
        string rot = "{X: 0.0, Y: " + R(Math.Sin(half)) + ", Z: 0.0, W: " + R(Math.Cos(half)) + "}";
        made.Add((name, $$"""
                    -   Entity:
                            Id: {{Id(name, "entity")}}
                            Name: {{name}}
                            Components:
                                {{Key(name, "transform")}}: !TransformComponent
                                    Id: {{Id(name, "transform")}}
                                    Position: {{pos}}
                                    Rotation: {{rot}}
                                    Scale: {X: 1.0, Y: 1.0, Z: 1.0}
                                    Children: {}
                                {{Key(name, "component")}}: {{component.Replace("@ID", Id(name, "component"))}}
            """));
    }
    // Forward (-Z) turned by yaw is (-sin, -cos): out of the wall along (dx, dz).
    for (int k = 0; k < strips.Count; k++)
    {
        var (x, z, dx, dz, y0, y1) = strips[k];
        Entity($"Ladder_{k}", x + dx * 0.5, H(y0), z + dz * 0.5, Math.Atan2(-dx, -dz) * 180 / Math.PI,
               $"!KotH.Ladder\n                        Id: @ID\n                        Width: 1.0\n                        Height: {F(H(y1 + 1) - H(y0))}");
    }
    int n = 0;
    foreach (var (x, z, y) in pads)
        Four(x, z, (a, b) => Entity($"Pad_{n++}", a, H(y + 1), b, 0,
                                    "!KotH.BouncePad\n                        Id: @ID\n                        Size: {X: 1.0, Y: 1.0}\n                        Bounce: 0.9"));
    // Keep every other entity as it is (Game Studio's), drop the generated ones of the last run.
    string text = File.ReadAllText(prefab);
    int parts = text.IndexOf("    Parts:\n");
    var blocks = text[(parts + "    Parts:\n".Length)..].Split("        -   Entity:\n").Skip(1)
        .Select(b => "        -   Entity:\n" + b).Where(b => !b.Contains("Name: Ladder_") && !b.Contains("Name: Pad_")).ToList();
    var kept = blocks.Select(b => b.Split('\n')[1].Trim()[4..]).ToHashSet();
    var refs = text[..parts].Split('\n').Where(l => !l.StartsWith("        - ref!! ") || kept.Contains(l[16..].Trim()));
    string head = string.Join("\n", refs).TrimEnd('\n') + "\n" + string.Concat(made.Select(m => $"        - ref!! {Id(m.Name, "entity")}\n"));
    File.WriteAllText(prefab, head + "    Parts:\n" + string.Concat(blocks) + string.Concat(made.Select(m => m.Text + "\n")));
    Console.WriteLine($"TheLadderCollision.sdprefab: {strips.Count} ladders, {n} pads");
}

// The trampolines' blocks are collision only.
arena.Pieces.Remove("_solid");

// ---- checks: faces lying on each other the same way round flicker (z-fighting) ----
var faces = new Dictionary<(long, long, long, long), List<(string Name, V3 A, V3 B, V3 C)>>();
foreach (Piece piece in arena.Pieces.Values)
    for (int t = 0; t < piece.Idx.Count; t += 3)
    {
        V3 a = piece.Pos[piece.Idx[t]], b = piece.Pos[piece.Idx[t + 1]], c = piece.Pos[piece.Idx[t + 2]];
        V3 n = V3.Cross(V3.Sub(b, a), V3.Sub(c, a));
        double l = Math.Sqrt(n.X * n.X + n.Y * n.Y + n.Z * n.Z);
        if (l < 1e-8)
            continue;
        n = new V3(n.X / l, n.Y / l, n.Z / l);
        var key = ((long)Math.Round(n.X * 500), (long)Math.Round(n.Y * 500), (long)Math.Round(n.Z * 500),
                   (long)Math.Round((n.X * a.X + n.Y * a.Y + n.Z * a.Z) * 500));
        if (!faces.TryGetValue(key, out var list))
            faces[key] = list = new();
        list.Add((piece.Name, a, b, c));
    }
int overlaps = 0;
var where = new Dictionary<string, int>();
foreach (var (key, list) in faces)
{
    if (list.Count < 2)
        continue;
    // In the plane's two widest axes, each triangle shrunk by 2 mm so neighbours sharing an edge pass.
    double nx = Math.Abs(key.Item1), ny = Math.Abs(key.Item2), nz = Math.Abs(key.Item3);
    (double, double) P(V3 v) => nx >= ny && nx >= nz ? (v.Y, v.Z) : ny >= nz ? (v.X, v.Z) : (v.X, v.Y);
    var tris = list.Select(t =>
    {
        var pts = new[] { P(t.A), P(t.B), P(t.C) };
        double cx = pts.Average(p => p.Item1), cy = pts.Average(p => p.Item2);
        return (t.Name, Mid: t.A, Pts: pts.Select(p =>
        {
            double dx = p.Item1 - cx, dy = p.Item2 - cy, d = Math.Sqrt(dx * dx + dy * dy);
            return d < 0.004 ? (cx, cy) : (p.Item1 - 0.002 * dx / d, p.Item2 - 0.002 * dy / d);
        }).ToArray());
    }).ToList();
    static bool Apart((double, double)[] p, (double, double)[] q)
    {
        foreach (var s in new[] { p, q })
            for (int i = 0; i < 3; i++)
            {
                double ex = s[(i + 1) % 3].Item1 - s[i].Item1, ey = s[(i + 1) % 3].Item2 - s[i].Item2;
                double Proj((double, double) v) => -ey * v.Item1 + ex * v.Item2;
                double p0 = p.Min(Proj), p1 = p.Max(Proj), q0 = q.Min(Proj), q1 = q.Max(Proj);
                if (p1 <= q0 + 1e-9 || q1 <= p0 + 1e-9)
                    return true;
            }
        return false;
    }
    for (int i = 0; i < tris.Count; i++)
        for (int j = i + 1; j < tris.Count; j++)
            if (!Apart(tris[i].Pts, tris[j].Pts))
            {
                overlaps++;
                string at = $"{tris[i].Name}/{tris[j].Name} at ({Math.Round(tris[i].Mid.X)}, {Py.F(tris[i].Mid.Y, 2)}, {Math.Round(tris[i].Mid.Z)})";
                where[at] = where.GetValueOrDefault(at) + 1;
            }
}
foreach (var (at, count) in where.OrderBy(w => w.Key).Take(60))
    Console.WriteLine($"overlap: {at} x{count}");
Console.WriteLine($"{overlaps} overlapping face pairs in {where.Count} places");

// ---- output ----
string glb = Path.Combine(root, "KotH.Game/Resources/Models/TheLadder.glb");
Output.WriteGlb(glb, arena.Pieces.Values.Select(p => p.ToPart()));
foreach (Piece p in arena.Pieces.Values)
    Console.WriteLine($"  {p.Name,-10} {p.Idx.Count / 3,7} triangles");
// Stride gives the glb's materials the model asset's slots by position, not by name.
string sdm3d = Path.Combine(root, "KotH.Game/Assets/Models/TheLadder.sdm3d");
string[] slotNames = File.Exists(sdm3d) ? [.. File.ReadAllLines(sdm3d).Where(l => l.TrimStart().StartsWith("Name: ")).Select(l => l.Trim()[6..])] : [];
if (!slotNames.SequenceEqual(arena.Pieces.Keys))
    Console.WriteLine($"warning: TheLadder.sdm3d's slots ({string.Join(", ", slotNames)}) are not the glb's materials in order " +
                      $"({string.Join(", ", arena.Pieces.Keys)}): fix the slots, else materials land on the wrong parts");
Console.WriteLine($"TheLadder.glb: {arena.Pieces.Values.Sum(p => p.Idx.Count / 3)} triangles, {Py.F(new FileInfo(glb).Length / 1e6, 1)} MB");
Console.WriteLine($"{strips.Count} ladder strips, {strips.Sum(s => s.Y1 - s.Y0 + 1)} blocks; {pads.Length * 4} pads");

string data = Path.Combine(root, "KotH.Shared/Resources/Maps/TheLadderTerrain.bin");
// 77 m tall: in 4 m cubes, else every query near the tower tests the whole column's triangles.
MapCollision collision = MapCollision.FromTriangles(arena.SolidPos, arena.SolidIdx, MapCollision.ChunkSize);
using (var file = File.Create(data))
    collision.Write(file);
Console.WriteLine($"TheLadderTerrain.bin: {collision.Chunks.Count} chunks, {collision.TriangleCount} triangles, " +
                  $"at most {collision.Chunks.Max(c => c.Triangles.Length / 3)} per chunk, {Py.F(new FileInfo(data).Length / 1e6, 2)} MB");
return 0;

static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path);
