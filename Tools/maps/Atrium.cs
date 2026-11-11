#!/usr/bin/env -S dotnet run
// Map "Atrium" after Shotbow's Smash map cp_atrium_2 (used with Shotbow's permission), built anew as a
// round arena: its plan and measures (heights x 0.8) as lathed rings, columns and rock (ArenaShapes.cs),
// MountDestiny's trees. Writes Atrium.glb and KotH.Shared/Resources/Maps/AtriumTerrain.bin.
//   dotnet run Tools/maps/Atrium.cs   then Tools/maps/MapUV --map Atrium
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
// A chamfered box w x d (d along facing) standing at (x, z) from bottom to top.
void Box(string material, V3 at, double bottom, double top, double w, double d, bool solid, double facing = 0)
    => arena.Bar(material, new V3(at.X, bottom, at.Z), new V3(at.X, top, at.Z),
                 [.. chunky(w, d, Math.Min(0.05, Math.Min(w, d) / 6)).Select(p => (p.U, p.V - d / 2))], solid, up: Polar(1, facing, 0));

// ---- the plan (metres; the shaft's middle is the origin, the upper floor's top is 0; the map's heights x 0.8) ----
const double Shaft = 9.0, InnerOut = 20.5, OuterIn = 23.0, OuterOut = 32.0, ArmEnd = 36.0, ArmWidth = 5.0, Slab = 0.8;
const double Hall = -5.6, HallEdge = 20.0, ShaftFoot = -22.4;
const double GardenIn = 10.5, GardenOut = 20.1, GardenTop = 6.4, GardenUnder = 4.8, GardenRim = 7.2;
const double Facade = 24.2, FacadeTop = 4.8, Eave = 25.7, LogRing = 20.0, LogTop = 4.0;
const double CanalIn = 24.8, CanalOut = 25.6;
// The half angle of an arm's 1 m channel where it leaves the ring channel, and where its straight edge crosses.
double mouth = Math.Asin(0.5 / CanalOut), Mouth = CanalOut * Math.Cos(mouth);
// The way up from the hall: stones H, the lower platform, stones M, the upper floor, stones A, the zone, stones B, the garden.
const double ZoneTop = 3.2, LowerTop = -3.2, PlatformRadius = 3.5;

// ---- floors: the upper ones thin slabs, the outer ring on rock; the hall under the inner one ----
// The inner floor: a glass octagon round the shaft lights the hall below; cobble on both sides of it.
const double GlassSide = 10.0, GlassDiagonal = 11.31, Beyond = 12.5;
Func<double, double> glassIn = Arena.Octagon(GlassSide - 0.5, GlassDiagonal - 0.5), glassOut = Arena.Octagon(GlassSide + 0.5, GlassDiagonal + 0.5);
double[] glassAngles = Union(Arena.Angles(0, 2 * Math.PI, GlassSide - 0.5, GlassDiagonal - 0.5),
                             Arena.Angles(0, 2 * Math.PI, GlassSide + 0.5, GlassDiagonal + 0.5));
arena.Band("Cobble", _ => Shaft, glassIn, -Slab, 0, glassAngles, true);
arena.Band("Glass", glassIn, glassOut, -0.08, 0, glassAngles, true);
arena.Band("Cobble", glassOut, _ => Beyond, -Slab, 0, glassAngles, true);
// The skylight's frame: a light stone lip round both edges, bars under the glass across it and at its corners.
arena.Band("Smooth", a => glassIn(a) - 0.15, a => glassIn(a) + 0.03, 0, 0.03, glassAngles, true);
arena.Band("Smooth", a => glassOut(a) - 0.03, a => glassOut(a) + 0.15, 0, 0.03, glassAngles, true);
void SkylightBar(V3 a, V3 b) => arena.Bar("Smooth", a with { Y = -0.14 }, b with { Y = -0.14 }, chunky(0.09, 0.12, 0.02), false);
foreach (var (dist, reach) in new[] { (GlassSide, GlassDiagonal * Math.Sqrt(2) - GlassSide), (GlassDiagonal, (2 * GlassSide - GlassDiagonal * Math.Sqrt(2)) / Math.Sqrt(2)) })
    for (int q = 0; q < 4; q++)
    {
        double phi = (dist == GlassSide ? 0 : 45) + 90 * q;
        int bars = (int)Math.Round(2 * Math.Abs(reach) / 1.5);
        for (int i = 1; i < bars; i++)
        {
            double along = -Math.Abs(reach) + 2 * Math.Abs(reach) * i / bars;
            SkylightBar(Arm(phi, dist - 0.48, along, 0), Arm(phi, dist + 0.48, along, 0));
        }
    }
foreach (double m in arms)
    foreach (int side in new[] { -1, 1 })
    {
        // The corners where a side meets a diagonal, inner to outer.
        V3 Corner(double sd, double dg) => Arm(m, sd, side * (dg * Math.Sqrt(2) - sd), 0);
        SkylightBar(Corner(GlassSide - 0.5, GlassDiagonal - 0.5), Corner(GlassSide + 0.5, GlassDiagonal + 0.5));
    }
arena.Lathe("Cobble", [(Beyond, -Slab), (InnerOut, -Slab), (InnerOut, -0.13), (InnerOut - 0.03, -0.04), (InnerOut - 0.12, 0), (Beyond, 0)], true);
arena.Lathe("Cobble", [(24.8, 0), (OuterIn + 0.12, 0), (OuterIn + 0.03, -0.04), (OuterIn, -0.13), (OuterIn, -Slab)], true);
arena.Lathe("Bricks", [(HallEdge, Hall - 0.6), (HallEdge, Hall - 0.08), (HallEdge - 0.08, Hall), (Shaft + 0.08, Hall), (Shaft, Hall - 0.08),
                       (Shaft, ShaftFoot), (Shaft + 0.5, ShaftFoot)], true);

// ---- rock: an earth cone under the hall round the shaft's lining, a flat stone ring under the outer floor ----
((double, double, double, double), double) RockPaint(V3 p, double low, double high)
    => (Arena.Plain, 0.72 + 0.28 * Math.Clamp((p.Y - low) / (high - low), 0, 1));
arena.Rock([(Shaft + 0.5, ShaftFoot), (11.5, -21), (14, -19.5), (16.8, -17), (18.7, -14), (19.9, -11), (20.3, -8.5), (HallEdge, Hall - 0.6)],
           11, 0.8, 4.0, true, colour: (p, n) => (Arena.Grass, 0.65 + 0.35 * Math.Clamp((p.Y + 22) / 16.0, 0, 1)));
arena.Rock([(OuterIn, -Slab), (23.0, -3), (23.3, -6.5), (25, -8.2), (28, -7.2), (30.6, -4.6), (32.1, -2.2), (OuterOut, -Slab)],
           12, 0.5, 4.0, true, colour: (p, n) => RockPaint(p, -8, -1));

// ---- the bridges over the open ring: low rails on ledges, a keel under the middle ----
var tubs = new Mesh(null!);
int seedTubs = 500;
foreach (double m in arms)
{
    // The deck: straight sides, its ends on the floors' circles where their bevelled rims begin, so no face lies on another.
    double from = InnerOut - 0.12, to = OuterIn + 0.12, w = ArmWidth / 2, a0 = Rad(m), side = Math.Asin(w / from);
    arena.Band("Cobble", _ => from, a => Math.Min(to, w / Math.Max(Math.Abs(Math.Sin(a - a0)), 1e-9)), -1.65, 0,
               Union([a0 - side, a0 - Math.Asin(w / to), a0 + Math.Asin(w / to), a0 + side], Arena.Angles(a0 - side, a0 + side)), true);
    arena.Bar("Cobble", Arm(m, InnerOut, 0, -2.4), Arm(m, OuterIn + 0.2, 0, -2.4), chunky(1.0, 0.8, 0.06), true);
    foreach (int s in new[] { -1, 1 })
    {
        arena.Bar("Bricks", Arm(m, InnerOut, s * 3, -0.6), Arm(m, 22.85, s * 3, -0.6), chunky(1.0, 0.6, 0.05), true);
        arena.Bar("Smooth", Arm(m, InnerOut + 0.1, s * 3, 0), Arm(m, OuterIn - 0.1, s * 3, 0), chunky(0.9, 0.4, 0.05), true);
    }
}

// ---- the outer floor: cobble with a glass channel out along each arm, a crenellated kerb, a grass strip, a low rim ----
const double Kerb = 30.0, GrassIn = 30.4, GrassOut = 31.4, ArmFloor = 35.5;
foreach (double m in arms)
{
    double a0 = Rad(m), a1 = Rad(m + 90), tip = Math.Asin(0.5 / GrassIn), full = Math.Asin(0.5 / 25.6);
    Func<double, double> slotted = a => Math.Min(GrassIn, Math.Max(25.6, Math.Max(0.5 / Math.Sin(a - a0), 0.5 / Math.Sin(a1 - a))));
    arena.Band("Cobble", slotted, _ => GrassIn, -Slab, 0, Union([a0 + tip, a1 - tip], Arena.Angles(a0 + full, a1 - full)), true);
    // Between the arms' parapets: a low wall with a coping, piers with caps every 3 m; behind it the grass and the rim.
    double k = Math.Asin(2.5 / Kerb);
    arena.Lathe("Bricks", [(Kerb + 0.3, 0), (Kerb + 0.3, 0.45), (Kerb - 0.3, 0.45), (Kerb - 0.3, 0)], true, a0: a0 + k, a1: a1 - k, chord: 0.5);
    arena.Lathe("Smooth", [(Kerb + 0.38, 0.45), (Kerb + 0.38, 0.52), (Kerb + 0.33, 0.57), (Kerb - 0.33, 0.57), (Kerb - 0.38, 0.52), (Kerb - 0.38, 0.45),
                           (Kerb + 0.38, 0.45)], true, a0: a0 + k, a1: a1 - k, chord: 0.5);
    int piers = (int)Math.Round((a1 - a0 - 2 * k) * Kerb / 3.0);
    for (int i = 0; i <= piers; i++)
    {
        double a = m + (k + (a1 - a0 - 2 * k) * i / piers) * 180 / Math.PI;
        if (i == 0 || i == piers)
            continue;
        V3 c = Polar(Kerb, a, 0);
        Box("Bricks", c, 0, 0.85, 0.7, 0.7, true, a);
        Box("Smooth", c, 0.85, 0.97, 0.84, 0.84, true, a);
        Box("Smooth", c, 0.97, 1.04, 0.6, 0.6, true, a);
    }
    double g = Math.Asin(2.5 / GrassIn);
    arena.Lathe("Terrain", [(GrassOut, 0), (GrassIn, 0)], true, a0: a0 + g, a1: a1 - g, colour: Arena.Grass);
    double e = Math.Asin(2.5 / OuterOut);
    arena.Lathe("Smooth", [(OuterOut, -Slab), (OuterOut, 0.36), (OuterOut - 0.04, 0.4), (GrassOut + 0.04, 0.4), (GrassOut, 0.36), (GrassOut, 0)],
                true, a0: a0 + e, a1: a1 - e);
}

// ---- the arms: a balcony out from the rim on stepped corbels, parapets with a fire in a niche, the channel to its end ----
foreach (double m in arms)
{
    foreach (int s in new[] { -1, 1 })
    {
        arena.Bar("Cobble", Arm(m, GrassIn, s * 1.5, -Slab), Arm(m, ArmFloor, s * 1.5, -Slab), chunky(2.0, Slab, 0.04), true);
        foreach (var (from, to) in new[] { (Kerb, 33.6), (34.4, ArmFloor + 1.0) })
            arena.Bar("Bricks", Arm(m, from, s * 2, 0), Arm(m, to, s * 2, 0), chunky(1.0, 0.8, 0.05), true);
        arena.Bar("Smooth", Arm(m, Kerb - 0.05, s * 2, 0.8), Arm(m, ArmFloor + 1.05, s * 2, 0.8), chunky(1.1, 0.4, 0.06), true);
        V3 fire = Arm(m, 34, s * 2, 0);
        arena.Lathe("Dark", [(0.3, 0), (0.36, 0.22), (0.3, 0.24), (0.26, 0.12), (0, 0.12)], true, fire.X, fire.Z, chord: 0.05, least: 12);
        arena.Lathe("Glow", [(0, 0.1), (0.22, 0.24), (0.16, 0.48), (0.05, 0.7), (0, 0.74)], false, fire.X, fire.Z, chord: 0.05, least: 10);
    }
    arena.Bar("Smooth", Arm(m, 34.5, 0, -Slab), Arm(m, ArmFloor + 0.1, 0, -Slab), chunky(1.0, Slab, 0.04), true);
    // The channel: glass over water in a trough, from the facade's channel to the balcony's end.
    arena.Bar("Smooth", Arm(m, Mouth, 0, -Slab), Arm(m, 34.5, 0, -Slab), chunky(1.0, 0.35, 0.0), false);
    // From just inside the ring channel's edge (its chords lie up to 5 mm in), so the two waters meet without a gap.
    arena.Bar("Water", Arm(m, Mouth, 0, -0.2), Arm(m, 34.5, 0, -0.2), chunky(1.0, 0.0, 0.0), false);
    arena.Bar("Glass", Arm(m, Mouth, 0, -0.01), Arm(m, 34.5, 0, -0.01), chunky(1.0, 0.0, 0.0), true);
    for (int i = 0; i < 4; i++)
    {
        double top = -Slab * (i + 1), nose = ArmFloor - 0.5 - i;
        arena.Bar("Bricks", Arm(m, 0, -2.5, top - Slab), Arm(m, 0, 2.5, top - Slab), [(30.6, 0), (nose, 0), (nose, Slab), (30.6, Slab)], true);
        arena.Bar("Smooth", Arm(m, 0, -2.5, top - 0.4), Arm(m, 0, 2.5, top - 0.4), [(nose, 0), (nose + 0.5, 0), (nose + 0.5, 0.4), (nose, 0.4)], true);
    }
    // Leaves hang over the balcony's end.
    foreach (var (across, y, r) in new[] { (-1.7, -0.6, 0.5), (-0.6, -1.0, 0.45), (0.5, -0.7, 0.5), (1.5, -1.3, 0.45), (-1.4, -1.6, 0.35), (1.0, -2.0, 0.35) })
        KotH.Tools.Maps.Trees.Leaves(tubs, new PyRandom(seedTubs++), [(Arm(m, ArmFloor + 0.25, across, y), r)]);
}

// A column on the floor at (x, z): plinth and capital of light stone, the shaft of material.
void Column(double x, double z, double bottom, double top, double r, string shaft)
{
    double w = r + 0.12;
    arena.Lathe("Quartz", [(w, bottom), (w, bottom + 0.2), (r + 0.06, bottom + 0.3), (r, bottom + 0.38)], true, x, z, chord: 0.2, least: 14);
    arena.Lathe(shaft, [(r, bottom + 0.38), (r, top - 0.38)], true, x, z, chord: 0.2, least: 14);
    arena.Lathe("Quartz", [(r, top - 0.38), (r + 0.06, top - 0.3), (w, top - 0.2), (w, top), (0, top)], true, x, z, chord: 0.2, least: 14);
}

// ---- the hall round the garden: 24 logs inside, the facade outside, the shingled roof from the garden's rim ----
// A square block at radius r and angle a (degrees) from bottom to top, its sides along and across the ring.
void Block(string material, double r, double a, double bottom, double top, double width, bool solid = true)
    => arena.Bar(material, Polar(r, a, bottom), Polar(r, a, top), chunky(width, width, 0.05), solid, up: Polar(1, a, 0));
// A carved panel on a face at radius r looking outward (out = 1) or inward (-1): a light frame, a darker middle.
void Panel(double r, double a, double y, double size, int out_)
{
    Plate("Smooth", r, a, y - size / 2, y + size / 2, size, 0.04, out_);
    Plate("Bricks", r, a, y - size * 0.3, y + size * 0.3, size * 0.6, 0.08, out_);
}
// A thin plate on a face at radius r, standing out by depth towards out_ (1 outward, -1 inward).
void Plate(string material, double r, double a, double bottom, double top, double width, double depth, int out_)
    => arena.Bar(material, Polar(r - out_ * 0.02, a, bottom), Polar(r - out_ * 0.02, a, top), chunky(width, depth + 0.02, 0.012), false,
                 up: Polar(out_, a, 0));
foreach (double a in new[] { 8.5, 24.0, 36.9, 53.1, 66.0, 81.5 }.SelectMany(a => arms.Select(m => m + a)))
{
    Block("Bricks", LogRing, a, 0, 0.8, 0.8);
    Block("Bricks", LogRing, a, LogTop, GardenUnder, 0.8);
    foreach (double y in new[] { 0.4, 4.4 })
        foreach (int s in new[] { 1, -1 })
            Panel(LogRing + s * 0.4, a, y, 0.56, s);
    arena.Lathe("Bark", [(0.3, 0.8), (0.3, LogTop)], true, Polar(LogRing, a, 0).X, Polar(LogRing, a, 0).Z, chord: 0.15, least: 10);
}
// The facade per quarter, from an arm's opening to the quarter's middle and mirrored: piers, barred windows,
// high and wide windows, slender posts; spans (bottom, top) of wall in each, the openings between.
(char Kind, double Bays)[] half = [('P', 1), ('W', 1), ('O', 1), ('S', 1), ('O', 1), ('W', 1), ('P', 1), ('W', 1), ('V', 2), ('W', 1),
                                   ('P', 1), ('W', 1), ('O', 1), ('S', 0.5)];
double opening = Math.Asin((ArmWidth / 2) / Facade) * 180 / Math.PI;
double bay = (45 - opening) / half.Sum(e => e.Bays);
void Wall(string material, double a0, double a1, double bottom, double top, double thick, double r = Facade)
    => arena.Lathe(material, [(r + thick / 2, bottom), (r + thick / 2, top), (r - thick / 2, top), (r - thick / 2, bottom),
                              (r + thick / 2, bottom)], true, a0: Rad(a0), a1: Rad(a1), least: 2);
// Dark bars across a window's opening: a post in the middle, rails to the jambs, as a ladder.
void Bars(double lo, double hi, double bottom, double top)
{
    V3 mid = Polar(Facade, (lo + hi) / 2, 0);
    arena.Lathe("Dark", [(0.06, bottom), (0.06, top)], true, mid.X, mid.Z, chord: 0.05, least: 8);
    for (double y = bottom + 0.3; y < top - 0.1; y += 0.4)
        arena.Bar("Dark", Polar(Facade, lo, y), Polar(Facade, hi, y), chunky(0.07, 0.07, 0.015), false, up: Polar(1, (lo + hi) / 2, 0));
}
foreach (double m in arms)
    foreach (int dir in new[] { 1, -1 })
    {
        double at = opening;
        foreach (var (kind, bays) in half)
        {
            double from = m + dir * at, to = m + dir * (at + bay * bays);
            (double lo, double hi) = (Math.Min(from, to), Math.Max(from, to));
            at += bay * bays;
            switch (kind)
            {
                case 'P':
                    // A pier: carved foot and head, a white band above the foot, stone between.
                    Wall("Bricks", lo, hi, 0, FacadeTop, 1.2);
                    Wall("Quartz", lo - 0.05, hi + 0.05, 0.8, 1.15, 1.32);
                    foreach (double y in new[] { 0.4, 4.4 })
                        foreach (int s in new[] { 1, -1 })
                            Panel(Facade + s * 0.6, (lo + hi) / 2, y, 0.6, s);
                    break;
                case 'W':
                    Wall("Smooth", lo, hi, 0, 0.35, 1.1);
                    Wall("Bricks", lo, hi, 0.35, 1.2, 1.0);
                    Bars(lo, hi, 1.2, 4.0);
                    Wall("Cobble", lo, hi, 4.0, FacadeTop, 1.0);
                    break;
                case 'O':
                    Wall("Smooth", lo, hi, 0, 0.35, 1.1);
                    Wall("Cobble", lo, hi, 0.35, 1.8, 1.0);
                    Wall("Bricks", lo, hi, 4.0, FacadeTop, 1.0);
                    break;
                case 'V':
                    Wall("Smooth", lo, hi, 0, 0.35, 1.1);
                    Wall("Cobble", lo, hi, 0.35, 1.0, 1.0);
                    Wall("Smooth", lo, hi, 4.0, 4.4, 1.0);
                    Wall("Planks", lo, hi, 4.4, FacadeTop, 1.0);
                    break;
                default:
                    // A slender post: carved blocks at foot and head, a thin shaft between.
                    double c = dir > 0 ? hi : lo, w = bay * 0.2;
                    Wall("Smooth", lo, hi, 0, 0.35, 1.1);
                    Wall("Bricks", lo, hi, 0.35, 1.6, 1.0);
                    Wall("Cobble", c - w, c + w, 1.6, 4.0, 0.5);
                    Wall("Bricks", lo, hi, 4.0, FacadeTop, 1.0);
                    foreach (int s in new[] { 1, -1 })
                        Panel(Facade + s * 0.5, c, 1.2, 0.5, s);
                    break;
            }
        }
    }
arena.Lathe("Planks", [(Facade + 0.55, FacadeTop), (Facade + 0.55, 5.5), (Facade - 0.55, 5.5), (Facade - 0.55, FacadeTop),
                      (Facade + 0.55, FacadeTop)], true);
// The roof: shingle rows from the eave up to the garden's rim, each with a small lip.
{
    const double Inner = GardenOut - 0.3, Rows = 10;
    double Top(double r) => 5.2 + (GardenRim - 5.2) * (Eave - r) / (Eave - Inner);
    var roof = new List<(double R, double Y)> { (Inner, GardenRim - 0.35), (Eave, 4.85), (Eave, 5.2) };
    for (int k = 1; k <= Rows; k++)
    {
        double r = Eave - (Eave - Inner) * k / Rows;
        roof.Add((r + 0.02, Top(r) - 0.06));
        roof.Add((r, Top(r)));
    }
    arena.Lathe("Planks", [.. roof], true);
}
// Over each arm's entrance a gable, its ridge running out from the roof; lanterns hang under its eave.
foreach (double m in arms)
{
    arena.Bar("Planks", Polar(21.6, m, 5.1), Polar(26.3, m, 5.1), [(-3.2, 0), (3.2, 0), (0.2, 1.35), (-0.2, 1.35)], true);
    foreach (int s in new[] { -1, 1 })
    {
        V3 p = Polar(25.7, m + s * 5.4, 4.3);
        Lantern(p, 5.1);
    }
}
// A lantern: a glowing box in a dark frame at p, hung from the height hold.
void Lantern(V3 p, double hold)
{
    arena.Bar("Glow", new V3(p.X, p.Y - 0.2, p.Z), new V3(p.X, p.Y + 0.2, p.Z), chunky(0.3, 0.3, 0.04), false, up: new V3(1, 0, 0));
    foreach (double y in new[] { p.Y - 0.25, p.Y + 0.2 })
        arena.Bar("Dark", new V3(p.X, y, p.Z), new V3(p.X, y + 0.06, p.Z), chunky(0.38, 0.38, 0.03), false, up: new V3(1, 0, 0));
    arena.Lathe("Dark", [(0.03, p.Y + 0.26), (0.03, hold)], false, p.X, p.Z, chord: 0.05, least: 6);
}

// ---- the garden ring on the logs: grass in a carved rim, a log ring beam inside, a boarded ceiling with ribs ----
arena.Lathe("Planks", [(GardenIn, GardenUnder), (GardenOut, GardenUnder)], true);
arena.Lathe("Cobble", [(GardenOut, GardenUnder), (GardenOut, GardenRim), (GardenOut - 0.6, GardenRim), (GardenOut - 0.6, GardenTop)], true);
arena.Lathe("Terrain", [(GardenOut - 0.6, GardenTop), (GardenIn + 0.4, GardenTop)], true, colour: Arena.Grass);
arena.Lathe("Bricks", [(GardenIn + 0.4, GardenTop), (GardenIn + 0.04, GardenTop), (GardenIn, GardenTop - 0.04), (GardenIn, 5.6)], true);
arena.Lathe("Bark", [(GardenIn, 5.6), (GardenIn - 0.06, 5.55), (GardenIn - 0.06, GardenUnder + 0.05), (GardenIn, GardenUnder)], true);
foreach (double a in new[] { 8.5, 24.0, 36.9, 53.1, 66.0, 81.5 }.SelectMany(a => arms.Select(m => m + a)))
    Panel(GardenOut - 0.6, a, 6.8, 0.5, -1);
for (int k = 0; k < 24; k++)
{
    double a = 7.5 + 15 * k;
    arena.Bar("Smooth", Polar(GardenIn + 0.3, a, GardenUnder - 0.4), Polar(GardenOut - 0.5, a, GardenUnder - 0.4), chunky(0.4, 0.4, 0.05), true);
}
for (int k = 0; k < 8; k++)
{
    double a = 26.6 + 90 * (k / 2) + (k % 2) * 36.8;
    Block("Dark", 11.2, a, GardenUnder - 0.4, GardenUnder, 0.8, false);
}

// ---- the lit glass channel round the facade's foot: water near the arms, light between; tubs at the entrances ----
arena.Lathe("Smooth", [(CanalOut, -0.45), (CanalIn, -0.45), (CanalIn, 0)], true);
// Glass and water leave out each arm's mouth, filled by one straight-edged piece that meets the arm's channel exactly.
void MouthPiece(string material, double m, double y, bool solid)
    => arena.Band(material, _ => CanalIn, _ => CanalOut, y, y, [Rad(m) - mouth, Rad(m) + mouth], solid);
foreach (double m in arms)
{
    arena.Lathe("Glass", [(CanalOut, -0.01), (CanalIn, -0.01)], true, a0: Rad(m) + mouth, a1: Rad(m + 90) - mouth);
    MouthPiece("Glass", m, -0.01, true);
}foreach (double m in arms)
{
    foreach (var (lo, hi) in new[] { (Rad(m - 20), Rad(m) - mouth), (Rad(m) + mouth, Rad(m + 20)) })
        arena.Lathe("Water", [(CanalOut, -0.2), (CanalIn, -0.2)], false, a0: lo, a1: hi);
    MouthPiece("Water", m, -0.2, false);
    // Walls close the water's basin; between the basins lamp tiles lie under the glass.
    foreach (int s in new[] { -1, 1 })
        arena.Bar("Smooth", Polar(CanalIn, m + s * 20, -0.45), Polar(CanalOut, m + s * 20, -0.45), chunky(0.1, 0.43, 0.01), false);
    const int Tiles = 18;
    for (int i = 0; i < Tiles; i++)
    {
        double c = m + 20 + 50 * (i + 0.5) / Tiles, span = 0.35 / ((CanalIn + CanalOut) / 2) * 180 / Math.PI;
        arena.Bar("Glow", Polar(25.2, c - span, -0.45), Polar(25.2, c + span, -0.45), chunky(0.6, 0.12, 0.03), false);
    }
    foreach (int s in new[] { -1, 1 })
    {
        V3 p = Polar(26.5, m + s * 7.4, 0);
        arena.Lathe("Smooth", [(0.5, 0), (0.55, 0.6), (0.48, 0.62), (0.45, 0.5), (0, 0.5)], true, p.X, p.Z, chord: 0.15, least: 14);
        KotH.Tools.Maps.Trees.Leaves(tubs, new PyRandom(seedTubs++), [(new V3(p.X, 1.0, p.Z), 0.6), (new V3(p.X, 1.6, p.Z), 0.5)]);
    }
}

// ---- the hall under the inner floor (octagonal like the glass): benches and a ceiling beam round its edge,
// doorways under the arms, eight small trees, lights in the floor and over the doorways, pots on pedestals ----
const double BenchSide = 18.0, BenchDiagonal = 17.68;
Func<double, double> Bench(double off) => Arena.Octagon(BenchSide + off, BenchDiagonal + off);
double[] BenchAngles(double a0, double a1) => Union(Arena.Angles(a0, a1, BenchSide - 1.5, BenchDiagonal - 1.5),
                                                    Arena.Angles(a0, a1, BenchSide + 0.5, BenchDiagonal + 0.5));
foreach (double m in arms)
{
    double[] between = BenchAngles(Rad(m + 7), Rad(m + 83));
    arena.Band("Smooth", Bench(-1.5), Bench(-0.5), Hall, Hall + 0.4, between, true);
    arena.Band("Sandstone", Bench(-0.5), Bench(0.5), Hall, Hall + 0.8, between, true);
    arena.Band("Cobble", Bench(0.5), _ => HallEdge, Hall, Hall + 0.8, between, true);
    // The doorway out under the arm, a light hung over its lintel.
    foreach (int s in new[] { -1, 1 })
        Box("Smooth", Arm(m, 19, s * 2.2, 0), Hall, -3.2, 0.7, 0.7, true, m);
    arena.Bar("Smooth", Arm(m, 19, -2.55, -3.2), Arm(m, 19, 2.55, -3.2), chunky(0.7, 0.8, 0.05), true);
    Box("Glow", Arm(m, 19, 0, 0), -2.4, -1.6, 0.6, 0.6, false, m);
    Box("Glow", Polar(15.6, m + 45, 0), Hall - 0.1, Hall + 0.015, 0.9, 0.9, false, m + 45);
    foreach (double a in new[] { m + 26.6, m + 63.4 })
    {
        V3 t = Polar(13.4, a, 0);
        Box("Smooth", t, Hall - 0.05, Hall + 0.02, 1.2, 1.2, false, a);
        Box("Bricks", t, Hall - 0.05, Hall + 0.04, 0.7, 0.7, false, a);
        arena.Lathe("Bark", [(0.12, Hall), (0.08, Hall + 1.8), (0, Hall + 1.9)], true, t.X, t.Z, chord: 0.05, least: 8);
        KotH.Tools.Maps.Trees.Leaves(tubs, new PyRandom(seedTubs++), [(new V3(t.X, Hall + 1.3, t.Z), 0.6), (new V3(t.X, Hall + 1.9, t.Z), 0.5)]);
    }
    foreach (double a in new[] { m + 34.5, m + 55.5 })
    {
        V3 p = Polar(19.4, a, 0);
        Box("Sandstone", p, Hall, Hall + 1.6, 0.9, 0.9, true, a);
        arena.Lathe("Dark", [(0.17, Hall + 1.6), (0.24, Hall + 1.92), (0.21, Hall + 1.95), (0.18, Hall + 1.85), (0, Hall + 1.85)], true, p.X, p.Z,
                    chord: 0.05, least: 10);
        KotH.Tools.Maps.Trees.Leaves(tubs, new PyRandom(seedTubs++), [(new V3(p.X, Hall + 2.15, p.Z), 0.3)]);
    }
}
double[] round = BenchAngles(0, 2 * Math.PI);
arena.Band("Smooth", Bench(-1.5), Bench(-0.5), -1.2, -Slab, round, true);
arena.Band("Sandstone", Bench(-0.5), Bench(0.5), -1.6, -Slab, round, true);
arena.Band("Cobble", Bench(0.5), _ => InnerOut, -1.6, -Slab, round, true);
// Small columns by the skylight's corners.
foreach (double m in arms)
    foreach (int s in new[] { -1, 1 })
    {
        V3 c = Arm(m, GlassSide + 1.0, s * 5.3, 0);
        Column(c.X, c.Z, 0, 0.9, 0.14, "Smooth");
    }

// ---- the zone and the lower platform in the shaft: stone discs on a smaller drum, four glowing rods under it ----
void Platform(double top)
{
    arena.Lathe("Smooth", [(0, top - 1.3), (2.6, top - 1.3), (2.6, top - 0.5), (PlatformRadius, top - 0.5), (PlatformRadius, top - 0.06),
                           (PlatformRadius - 0.06, top), (PlatformRadius - 0.6, top)], true, chord: 0.3, least: 28);
    arena.Lathe("Bricks", [(PlatformRadius - 0.6, top), (0, top)], true, chord: 0.3, least: 28);
    arena.Lathe("Dark", [(0, top - 1.42), (0.6, top - 1.42), (0.62, top - 1.3)], true, chord: 0.1, least: 12);
    for (int k = 0; k < 4; k++)
    {
        V3 p = Polar(1.41, 45 + 90 * k, 0);
        arena.Lathe("Glow", [(0, top - 2.05), (0.07, top - 2.0), (0.07, top - 1.3)], false, p.X, p.Z, chord: 0.05, least: 8);
    }
}
Platform(ZoneTop);
Platform(LowerTop);
// The stepping stones: floating stone discs over a rock cone.
void Stone(double r, double a, double radius, double top, int seed)
{
    const double T = 0.4, Depth = 1.2;
    V3 c = Polar(r, a, 0);
    arena.Lathe("Smooth", [(radius - 0.15, top - T), (radius, top - T + 0.12), (radius, top - 0.08), (radius - 0.08, top), (0, top)], true, c.X, c.Z,
                chord: 0.3, least: 20);
    arena.Rock([(0, top - T - Depth), (radius * 0.45, top - T - Depth * 0.6), (radius * 0.85, top - T - Depth * 0.2), (radius - 0.15, top - T)],
               seed, 0.18 * radius, radius, true, c.X, c.Z, chord: 0.35, colour: (p, n) => RockPaint(p, top - T - Depth, top - T));
}
// Nine stones: two per step, three to the zone (of the first sixteen, which wind round the shaft).
foreach (int k in new[] { 0, 2 })
{
    Stone(6.6, 22.5 + 90 * k, 1.2, -4.0, 30 + k);   // H: from the hall
    Stone(6.6, 67.5 + 90 * k, 1.2, -1.6, 34 + k);   // M: up to the upper floor
    Stone(7.0, 90 + 90 * k, 1.3, 4.8, 26 + k);      // B: to the garden
}
foreach (int k in new[] { 0, 1, 3 })
    Stone(6.2, 45 + 90 * k, 1.2, 1.6, 22 + k);      // A: to the zone


// ---- trees in the garden, where the map's eight stood ----
(int X, int Z)[] gardenTrees = [(-16, 1), (-12, 10), (-11, -10), (0, -15), (0, 16), (10, -12), (13, 9), (16, 0)];
var rng = new PyRandom(11);
for (int i = 0; i < gardenTrees.Length; i++)
    Tree(gardenTrees[i].X, gardenTrees[i].Z, GardenTop, rng.RandInt(4, 5), 1100 + i);

// A MountDestiny tree standing at (x, ground, z).
void Tree(double x, double z, double ground, int trunk, int seed)
{
    var mesh = new Mesh(null!);
    KotH.Tools.Maps.Trees.Grow(mesh, new PyRandom(seed), 0, 0, 0, trunk)();
    arena.Take(mesh, new V3(x - 0.5, ground, z - 0.5));
}

// ---- the five floating islands round the arena: rock with a grass top, a well at the waterfall's foot, a two-tier
// tower (slender posts, a band with a white stripe, a waist, short posts, a corbel) under a grassy deck with a tree ----
(double X, double Y, double Z)[] islands = [(0, 56, 2.5), (18, 65, 66.5), (-41, 97, 53.5), (46, 97, 24.5), (-62, 72, -49.5)];
for (int i = 0; i < islands.Length; i++)
{
    var (x, by, z) = islands[i];
    double y = by * 0.8, top = y + 11.2, deck = top + 15.2;
    arena.Rock([(0, y), (3.5, y + 2.5), (6.5, y + 6.5), (7.6, y + 9.6), (7.3, y + 10.9), (6.6, top - 0.05), (0, top)], 50 + i, 0.9, 4.0, true, x, z,
               colour: (p, n) => (n.Y > 0.7 && p.Y > top - 0.9 ? Arena.Grass : Arena.Plain, 0.75 + 0.25 * Math.Clamp((p.Y - y) / 11.0, 0, 1)));
    void Ring(string material, double inner, double outer, double bottom, double height)
        => arena.Lathe(material, [(outer, bottom), (outer, bottom + height), (inner, bottom + height), (inner, bottom), (outer, bottom)], true, x, z,
                       chord: 0.4, least: 24);
    V3 At(double r, double a) => new(x + r * Math.Cos(Rad(a)), 0, z + r * Math.Sin(Rad(a)));
    arena.Lathe("Smooth", [(1.3, top - 0.1), (1.3, top + 0.4), (0.75, top + 0.4), (0.75, top + 0.05), (0, top + 0.05)], true, x, z, chord: 0.2, least: 14);
    Ring("Cobble", 5.6, 6.9, top - 0.1, 0.5);
    foreach (double a in new[] { 25.0, 45, 65 })
        for (int k = 0; k < 4; k++)
            Column(At(6.2, a + 90 * k).X, At(6.2, a + 90 * k).Z, top + 0.4, top + 4.8, 0.3, "Smooth");
    Ring("Bricks", 5.6, 6.5, top + 4.8, 2.4);
    Ring("Quartz", 6.0, 6.6, top + 5.6, 0.8);
    Ring("Smooth", 4.0, 5.0, top + 7.2, 0.8);
    Ring("Bricks", 4.0, 4.9, top + 8.0, 0.8);
    for (int k = 0; k < 8; k++)
        Column(At(4.45, 45 * k).X, At(4.45, 45 * k).Z, top + 8.8, top + 11.2, 0.28, "Bricks");
    Ring("Bricks", 4.0, 5.2, top + 11.2, 0.8);
    Ring("Smooth", 4.0, 5.7, top + 12.0, 0.8);
    Ring("Cobble", 4.0, 6.2, top + 12.8, 0.8);
    arena.Lathe("Quartz", [(0, deck - 1.6), (6.4, deck - 1.6), (6.4, deck - 0.8)], true, x, z, chord: 0.4, least: 24);
    arena.Lathe("Smooth", [(6.4, deck - 0.8), (6.4, deck + 0.4), (5.9, deck + 0.4), (5.9, deck)], true, x, z, chord: 0.4, least: 24);
    arena.Lathe("Terrain", [(5.9, deck), (0, deck)], true, x, z, colour: Arena.Grass);
    for (int k = 0; k < 4; k++)
        Box("Glow", At(2.9, 45 + 90 * k), deck - 1.66, deck - 1.58, 0.8, 0.8, false, 45 + 90 * k);
    arena.Lathe("Water", [(0.45, top + 0.05), (0.45, deck - 1.6)], false, x, z, chord: 0.2, least: 10);
    Tree(x, z, deck, 5, 1200 + i);
    var hang = new PyRandom(1300 + i);
    for (int k = 0; k < 7; k++)
    {
        double a = hang.Uniform(0, 360), h = hang.Choice(new[] { top + 4.6, top + 7.0, top + 12.6 });
        KotH.Tools.Maps.Trees.Leaves(tubs, new PyRandom(seedTubs++), [(At(h > top + 12 ? 6.3 : 6.6, a) with { Y = h }, hang.Uniform(0.35, 0.55))]);
    }
}

arena.Take(tubs, new V3(0, 0, 0));

// ---- output ----
string glb = Path.Combine(root, "KotH.Game/Resources/Models/Atrium.glb");
Output.WriteGlb(glb, arena.Pieces.Values.Select(p => p.ToPart()));
foreach (Piece p in arena.Pieces.Values)
    Console.WriteLine($"  {p.Name,-10} {p.Idx.Count / 3,7} triangles");
// Stride gives the glb's materials the model asset's slots by position, not by name.
string[] slots = [.. File.ReadAllLines(Path.Combine(root, "KotH.Game/Assets/Models/Atrium.sdm3d"))
    .Where(l => l.TrimStart().StartsWith("Name: ")).Select(l => l.Trim()[6..])];
if (!slots.SequenceEqual(arena.Pieces.Keys))
    Console.WriteLine($"warning: Atrium.sdm3d's slots ({string.Join(", ", slots)}) are not the glb's materials in order " +
                      $"({string.Join(", ", arena.Pieces.Keys)}): fix the slots, else materials land on the wrong parts");
Console.WriteLine($"Atrium.glb: {arena.Pieces.Values.Sum(p => p.Idx.Count / 3)} triangles, {Py.F(new FileInfo(glb).Length / 1e6, 1)} MB");

string data = Path.Combine(root, "KotH.Shared/Resources/Maps/AtriumTerrain.bin");
MapCollision collision = MapCollision.FromTriangles(arena.SolidPos, arena.SolidIdx);
using (var file = File.Create(data))
    collision.Write(file);
Console.WriteLine($"AtriumTerrain.bin: {collision.Chunks.Count} chunks, {collision.TriangleCount} triangles, " +
                  $"at most {collision.Chunks.Max(c => c.Triangles.Length / 3)} per chunk, {Py.F(new FileInfo(data).Length / 1e6, 2)} MB");
return 0;

static string Here([CallerFilePath] string path = "") => Path.GetDirectoryName(path);
