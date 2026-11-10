using System;
using System.Globalization;
using KotH.Client.Menu;
using KotH.Client.Steam;
using KotH.Shared.Hosting;
using KotH.Shared.Match;
using KotH.Shared.Net;

namespace KotH.Client.Net
{
    /// <summary>What the client was started with; without --host, --join or --offline the main menu. Options:
    /// --host [--port N] [--min-players N], --join ADDRESS[:PORT] [--key FP] or "koth://join?…", --offline, --name NAME, --display N
    /// (monitor, 0 first), --bots N, --bot-seed N, --map NAME (to play on; joiners take the host's), --fps N (cap; these two
    /// over settings.toml), --password TEXT (a private round, hosting or joining), --dlss off|dlaa|quality|balanced|performance|ultra (or KOTH_DLSS), +connect_lobby ID (Steam's invite).</summary>
    public sealed class LaunchOptions
    {
        public bool Host;
        public string JoinAddress;

        /// <summary>Joining by direct IP: the host key's fingerprint the host must have (an invite code's, --key).</summary>
        public string JoinKey;
        public bool Offline;
        public int Port = LiteNetTransport.DefaultPort;
        public string Name;
        public int MinPlayers = HillMatch.DefaultMinPlayers;

        /// <summary>Solo or teams (--mode duo|trio|squad, the Host server page); a guest plays the host's.</summary>
        public MatchMode Mode;
        public int? Display;
        public int Bots = 3;
        public int BotSeed = 1;
        public string Map = Maps.Default;
        public int? Fps;
        /// <summary>--dlss or KOTH_DLSS, over the settings; null when neither is given.</summary>
        public KotH.Client.Rendering.Dlss.DlssMode? Dlss;

        /// <summary>The own figure's colours: look.toml (LookFile), for shots KOTH_SHOT_LOOK
        /// ("cap,skin,leaves,gloves,spores" as hex, empty keeps one).</summary>
        public FigureLook Look;

        /// <summary>Hosting or joining through a Steam lobby (the main menu), not direct IP.</summary>
        public bool Steam;

        /// <summary>Hosting: the password of a private round (Host server page, --password), null for none.
        /// Joining: the password to answer the host's challenge with.</summary>
        public string Password;

        public bool Networked => Host || JoinAddress != null || Steam;

        /// <summary>Joins someone else's game, so the map comes from that host.</summary>
        public bool Joining => !Host && JoinAddress != null;

        /// <summary>Starts in the main menu: not told to host, join or play alone. Shots
        /// and headless runs (KOTH_SHOT, KOTH_HEADLESS) play alone as before, unless
        /// KOTH_SHOT_MENU asks for the menu.</summary>
        public bool ShowsMenu;

        static LaunchOptions current;

        /// <summary>The options of this process, read once from its command line.</summary>
        public static LaunchOptions Current => current ??= Parse(Environment.GetCommandLineArgs());

        public static LaunchOptions Parse(string[] args)
        {
            var o = new LaunchOptions { Host = Array.IndexOf(args, "--host") >= 0, Offline = Array.IndexOf(args, "--offline") >= 0 };

            if (Text(args, "--join") is { } invite && Invites.TryParse(invite, out string at, out int atPort, out string key))
            {
                o.JoinAddress = at;
                o.Port = atPort;
                o.JoinKey = key;
            }
            else if (Text(args, "--join") is { } join)
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
            o.JoinKey = Text(args, "--key") ?? o.JoinKey;
            if (Number(args, "--min-players") is { } min)
                o.MinPlayers = Math.Clamp(min, 1, NetWorld.MaxPlayers);
            if (Text(args, "--mode") is { } mode)
                o.Mode = HillMatch.ParseMode(mode) ?? MatchMode.Solo;
            o.Display = Number(args, "--display");
            if (Number(args, "--bots") is { } bots)
                o.Bots = Math.Clamp(bots, 0, NetWorld.MaxPlayers - 1);
            if (Number(args, "--bot-seed") is { } seed)
                o.BotSeed = seed;
            o.Map = Text(args, "--map") ?? o.Map;
            o.Password = Text(args, "--password");
            o.Fps = Number(args, "--fps");
            if ((Text(args, "--dlss") ?? Environment.GetEnvironmentVariable("KOTH_DLSS")) is { } dlss)
                o.Dlss = KotH.Client.Rendering.Dlss.Dlss.ParseMode(dlss);
            o.Name = Text(args, "--name") ?? SteamClient.PersonaName ?? Environment.UserName;
            if (ulong.TryParse(Text(args, "+connect_lobby"), NumberStyles.Integer, CultureInfo.InvariantCulture, out ulong lobby))
                SteamClient.Invite(lobby);
            o.Look = LookFile.Load();
            if (Environment.GetEnvironmentVariable("KOTH_SHOT_LOOK") is { } shot)
            {
                string[] parts = shot.Split(',');
                for (int i = 0; i < Math.Min(parts.Length, FigureLook.Parts); i++)
                    if (LookFile.ParseHex(parts[i]) is { } rgb)
                        o.Look[i] = rgb;
            }
            bool test = Environment.GetEnvironmentVariable("KOTH_SHOT") != null || Environment.GetEnvironmentVariable("KOTH_HEADLESS") == "1";
            o.ShowsMenu = !o.Networked && !o.Offline && (!test || Environment.GetEnvironmentVariable("KOTH_SHOT_MENU") == "1");
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
