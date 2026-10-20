using System;
using System.Globalization;
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
    /// </summary>
    public sealed class LaunchOptions
    {
        public bool Host;
        public string JoinAddress;
        public int Port = LiteNetTransport.DefaultPort;
        public string Name;
        public int MinPlayers = HillMatch.DefaultMinPlayers;
        public int? Display;

        public bool Networked => Host || JoinAddress != null;

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
