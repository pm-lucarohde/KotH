using System;
using System.Collections.Generic;
using System.IO;
using KotH.MapFormat;
using KotH.Probes.Harness;
using KotH.Shared.Bots;
using KotH.Shared.Combat;
using KotH.Shared.Hosting;
using KotH.Shared.Match;
using KotH.Shared.Net;
using KotH.Shared.Physics;
using KotH.Shared.Simulation;
using Stride.Core.Mathematics;
using Stride.Engine;

namespace KotH.Probes.Probes
{
    /// <summary>A custom map package (KotH.Probes/Resources/MapPackages/Sample, Tools/maps/SampleMap.cs):
    /// found by its id's hash, built as a prefab map would be (zone, twelve slots facing the zone,
    /// solid ground and invisible wall), climbed by three bots to the zone; the same id built
    /// twice, also after an unload, plays bit-identically, and a rollback through it as well.</summary>
    public sealed class CustomMapProbe : Probe
    {
        protected override string Tag => "custommap";

        readonly string folder;
        int failures;

        public CustomMapProbe(string repo) => folder = Path.Combine(repo, "KotH.Probes", "Resources", "MapPackages", "Sample");

        /// <summary>Zone shapes: a polygon reads back from map.toml, and each shape's inside test.</summary>
        void Shapes()
        {
            var m = new MapManifest();
            m.Spawns.Add(new MapSpawn());
            m.Zone.Shape = "polygon";
            m.Zone.Corners = new[] { -2f, -2f, 2f, -2f, 0f, 2f };
            MapZone back = MapManifest.Parse(m.ToString()).Zone;
            Check(back.Shape == "polygon" && back.Corners.Length == 6 && back.Corners[5] == 2f, "a polygon zone reads back");

            HillMatch Match(ZoneShape shape)
            {
                var zone = new CaptureZone { Size = new Vector3(4f, 2f, 4f), Shape = shape };
                zone.Corners.AddRange(new[] { new Vector2(-2f, -2f), new Vector2(2f, -2f), new Vector2(0f, 2f) });
                _ = new Entity { zone };
                return new HillMatch(zone);
            }
            Vector3 corner = new Vector3(1.8f, 0f, 1.8f), edge = new Vector3(0f, 0f, 1.9f), high = new Vector3(0f, 1.1f, 0f);
            HillMatch box = Match(ZoneShape.Box), round = Match(ZoneShape.Cylinder), drawn = Match(ZoneShape.Polygon);
            Check(box.Contains(corner) && !round.Contains(corner) && round.Contains(edge) && !box.Contains(high), "box and cylinder");
            Check(drawn.Contains(new Vector3(0f, 0f, -1.9f)) && !drawn.Contains(corner) && drawn.Contains(new Vector3(0f, 0f, 1.9f)) &&
                  !drawn.Contains(new Vector3(1.2f, 0f, 1f)) && !drawn.Contains(high), "polygon");
            var ringZone = new CaptureZone { Size = new Vector3(8f, 2f, 8f), Shape = ZoneShape.Cylinder, Hole = 2f };
            _ = new Entity { ringZone };
            var ring = new HillMatch(ringZone);
            Check(!ring.Contains(new Vector3(1f, 0f, 1f)) && ring.Contains(new Vector3(0f, 0f, 3f)) && !ring.Contains(new Vector3(3f, 0f, 3f)) &&
                  ring.Hole == 2f, "ring (cylinder with a hole)");
        }

        /// <summary>Magma over the whole ground (MagmaPool on the zone): a hit of 10 % each second
        /// (shake and recoil by LastHitTick), and the first wave over 100 % respawns, percent kept.</summary>
        void Magma(HeadlessWorld world)
        {
            var pool = new MagmaPool { Radius = 60f, Surface = 0.5f };
            world.Zone.Entity.Add(pool);
            world.StartMatch();
            var body = new ScriptedBody { NetId = NetWorld.FirstPlayerId + 5 };
            var damage = new DamagePercent();
            Entity marker = NetWorld.SlotMarker(world.Builder, 6);
            world.SpawnBody(HeadlessWorld.PlayerBodyUrl, marker.Transform.Position, marker.Transform.Rotation, body, damage);
            world.Simulation.Tick();
            int moved = body.MovedTick;
            for (int t = 1; t < 40; t++)
                world.Simulation.Tick();
            Check(damage.Percent == 10f && damage.LastHitTick >= 0 && damage.LastHitTick % 40 == 0,
                  $"magma: one wave of 10 % in a second, as a hit ({damage.Percent:F2}, tick {damage.LastHitTick})");
            float at = -1f;
            for (int t = 0; t < 40 * 14 && at < 0f; t++)
            {
                float before = damage.Percent;
                world.Simulation.Tick();
                if (body.MovedTick != moved)
                    at = before;
            }
            Check(at == 110f && damage.Percent == 120f, $"magma: respawn at the first wave over 100 %, percent kept ({at:F2})");
            world.Zone.Entity.Remove(pool);
            world.Simulation.Unregister(body);
            world.Scene.Entities.Remove(body.Entity);
            world.Simulation.ResetTickCounter();
        }

        public override int Run(HeadlessWorld world, int maxTicks)
        {
            MapPackage package = world.Builder.Library.AddFolder(folder);
            string id = package.Id;
            Log($"[custommap] {id}");
            Check(id.StartsWith("local:Sample@") && id == "local:Sample@" + MapPackage.HashOf(folder), "id names the package and its hash");
            Check(world.Builder.HasMap(id), "the id is found");
            Check(!world.Builder.HasMap("local:Sample@0000000000000000"), "another hash is not");
            Check(!world.Builder.HasMap("local:Nowhere@" + package.Hash.Replace(package.Hash[0], package.Hash[0] == 'a' ? 'b' : 'a')), "nor a changed one");

            world.LoadArena(id);
            CaptureZone zone = world.Zone;
            Check(zone != null && zone.Entity.Transform.Position == new Vector3(0f, 6.75f, 0f) && zone.Size == new Vector3(3f, 3.5f, 3f),
                  "zone where map.toml puts it");
            var seen = new HashSet<Vector3>();
            for (int slot = 0; slot < NetWorld.MaxPlayers; slot++)
            {
                Entity marker = NetWorld.SlotMarker(world.Builder, slot);
                Vector3 at = marker.Transform.Position;
                Vector3 ahead = Vector3.Transform(new Vector3(0f, 0f, -1f), marker.Transform.Rotation);
                Vector3 toZone = Vector3.Normalize(new Vector3(-at.X, 0f, -at.Z));
                Check(seen.Add(at) && MathF.Abs(at.Y - 0.95f) < 1e-4f, $"slot {slot} at its own place, on the ground ({at})");
                Check(Vector3.Dot(ahead, toZone) > 0.97f, $"slot {slot} faces the zone ({Vector3.Dot(ahead, toZone):F3})");
            }
            Check(world.Find("PlayerSpawn") != null && world.Find("DummySpawn") != null, "offline and dummy markers");
            Shapes();

            WorldQueries q = world.Queries;
            Check(q.Raycast(new Vector3(0f, 10f, 0f), new Vector3(0f, -1f, 0f), 20f, WorldQueries.WorldMask, out var top)
                  && MathF.Abs(top.Distance - 5f) < 1e-3f, $"the tower's top at 5 m ({top.Distance:F3} below 10)");
            Check(q.Raycast(new Vector3(0f, 1f, -20f), new Vector3(0f, 0f, -1f), 10f, WorldQueries.WorldMask, out var wall)
                  && MathF.Abs(wall.Distance - 4f) < 1e-3f, $"the invisible wall at z -24 ({wall.Distance:F3})");
            Check(q.Raycast(new Vector3(-14f, 10f, 13f), new Vector3(0f, -1f, 0f), 20f, WorldQueries.WorldMask, out var pillar)
                  && MathF.Abs(pillar.Distance - 4f) < 1e-3f, $"the live-shadowed pillar is solid ({pillar.Distance:F3})");

            // Three bots from the slots to the zone, twice in fresh worlds and once after an unload.
            ulong first = Climb(world, id, 1, out float[] times);
            Log($"[custommap] bots in the zone after {string.Join(", ", Array.ConvertAll(times, t => t < 0 ? "never" : $"{t:F1} s"))}");
            Check(Array.TrueForAll(times, t => t >= 0f && t <= 60f), "every bot reaches the zone within 60 s");
            using (var second = new HeadlessWorld())
            {
                second.Builder.Library.AddFolder(folder);
                second.LoadArena(id);
                Check(Climb(second, id, 1, out _) == first, "a second world plays it bit-identically");
            }
            world.Builder.UnloadArena();
            world.LoadArena(id);
            Check(Climb(world, id, 1, out _) == first, "unloaded and loaded again: bit-identical");
            Check(Rollback(world, id), "a rollback through 200 ticks is bit-identical");
            Magma(world);

            Log(failures == 0 ? "[custommap] passed" : $"[custommap] {failures} failed");
            return failures == 0 ? 0 : 1;
        }

        void Check(bool ok, string what)
        {
            if (!ok)
                failures++;
            Log($"[custommap] {(ok ? "ok  " : "FAIL")} {what}");
        }

        /// <summary>Three bots for 60 s on the loaded map; a hash of their positions every tick, and
        /// the second each first stood in the zone (-1 never). The bodies go again afterwards.</summary>
        static ulong Climb(HeadlessWorld world, string id, int seed, out float[] times)
        {
            world.StartMatch().Seed = (uint)seed;
            var states = new LocalFinalStates(world.Simulation);
            BotDirector director = BotDirector.Create(world.Builder, world.Simulation, states, seed);
            var bots = new List<BotBody>();
            for (int i = 0; i < 3; i++)
            {
                Entity marker = NetWorld.SlotMarker(world.Builder, NetWorld.BotSlots[i]);
                var bot = new BotBody(director, states, NetWorld.FirstPlayerId + 1 + i);
                world.SpawnBody(HeadlessWorld.PlayerBodyUrl, marker.Transform.Position, marker.Transform.Rotation, bot, new DamagePercent());
                bots.Add(bot);
            }
            times = new float[] { -1f, -1f, -1f };
            ulong h = 14695981039346656037UL;
            HillMatch match = world.Simulation.Match;
            for (int tick = 0; tick < 40 * 60; tick++)
            {
                world.Simulation.Tick();
                for (int i = 0; i < bots.Count; i++)
                {
                    Vector3 p = bots[i].Motor.Position;
                    h = Mix(Mix(Mix(h, BitConverter.SingleToInt32Bits(p.X)), BitConverter.SingleToInt32Bits(p.Y)), BitConverter.SingleToInt32Bits(p.Z));
                    if (times[i] < 0f && match.Contains(p))
                        times[i] = (tick + 1) / 40f;
                }
            }
            foreach (BotBody bot in bots)
            {
                world.Simulation.Unregister(bot);
                world.Scene.Entities.Remove(bot.Entity);
            }
            world.Simulation.ResetTickCounter();
            return h;
        }

        /// <summary>One bot walks for 200 ticks from a snapshot; restored and run again it ends the same.</summary>
        static bool Rollback(HeadlessWorld world, string id)
        {
            var body = new ScriptedBody { NetId = NetWorld.FirstPlayerId };
            Entity marker = NetWorld.SlotMarker(world.Builder, 0);
            world.SpawnBody(HeadlessWorld.PlayerBodyUrl, marker.Transform.Position, marker.Transform.Rotation, body);
            body.Input.Move = new Vector2(0f, 1f);
            for (int t = 0; t < 20; t++)
                world.Simulation.Tick();
            var snap = new WorldSnapshot();
            world.Simulation.Capture(snap);
            for (int t = 0; t < 200; t++)
            {
                body.Input.JumpPresses = (byte)(t % 30 == 0 ? 1 : 0);
                body.Input.JumpHeld = t % 30 < 8;
                world.Simulation.Tick();
            }
            Vector3 end = body.Motor.Position;
            world.Simulation.Restore(snap);
            for (int t = 0; t < 200; t++)
            {
                body.Input.JumpPresses = (byte)(t % 30 == 0 ? 1 : 0);
                body.Input.JumpHeld = t % 30 < 8;
                world.Simulation.Tick();
            }
            return body.Motor.Position == end;
        }

        static ulong Mix(ulong h, int v)
        {
            h ^= (uint)v;
            return h * 1099511628211UL;
        }
    }
}
