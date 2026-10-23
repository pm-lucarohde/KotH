using System;
using System.Globalization;
using KotH.Shared.Hosting;
using KotH.Shared.Match;
using KotH.Shared.Net;

namespace KotH.Client.Net
{
    /// <summary>
    /// What the client was started with. Without --host or --join it plays
    /// alone, as it always did.
    ///
    ///   --host [--port N] [--min-players N]   hosts a game and plays in it
    ///   --join ADDRESS[:PORT]                 joins one
    ///   --name NAME                           the name the others see
    ///   --display N                           fullscreen on that monitor, 0 first
    ///   --bots N, --bot-seed N                bots alone or as host (3 by default), their seed
    ///   --map NAME                            the map alone or as host (Maps.Default); joiners take the host's
    ///   --fps N                               at most N frames per second (unlimited without)
    /// </summary>
    public sealed class LaunchOptions
    {
        public bool Host;
        public string JoinAddress;
        public int Port = LiteNetTransport.DefaultPort;
        public string Name;
        public int MinPlayers = HillMatch.DefaultMinPlayers;
        public int? Display;
        public int Bots = 3;
        public int BotSeed = 1;
        public string Map = Maps.Default;
        public int? Fps;

        public bool Networked => Host || JoinAddress != null;

        /// <summary>Joins someone else's game, so the map comes from that host.</summary>
        public bool Joining => !Host && JoinAddress != null;

        static LaunchOptions current;

        /// <summary>The options of this process, read once from its command line.</summary>
        public static LaunchOptions Current => current ??= Parse(Environment.GetCommandLineArgs());

        public static LaunchOptions Parse(string[] args)
        {
            var o = new LaunchOptions { Host = Array.IndexOf(args, "--host") >= 0 };

            if (Text(args, "--join") is { } join)
            {
                // "host:port", but an IPv6 address has colons of its own.
                int colon = join.LastIndexOf(':');
                if (colon > 0 && join.IndexOf(':') == colon && int.TryParse(join[(colon + 1)..], out int port))
                {
                    o.JoinAddress = join[..colon];
                    o.Port = port;
                }
                else
                    o.JoinAddress = join;
            }

            if (Number(args, "--port") is { } p)
                o.Port = p;
            if (Number(args, "--min-players") is { } min)
                o.MinPlayers = Math.Clamp(min, 1, NetWorld.MaxPlayers);
            o.Display = Number(args, "--display");
            if (Number(args, "--bots") is { } bots)
                o.Bots = Math.Clamp(bots, 0, NetWorld.MaxPlayers - 1);
            if (Number(args, "--bot-seed") is { } seed)
                o.BotSeed = seed;
            o.Map = Text(args, "--map") ?? o.Map;
            o.Fps = Number(args, "--fps");
            o.Name = Text(args, "--name") ?? Environment.UserName;
            return o;
        }

        static string Text(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        static int? Number(string[] args, string name)
            => int.TryParse(Text(args, name), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;
    }
}
