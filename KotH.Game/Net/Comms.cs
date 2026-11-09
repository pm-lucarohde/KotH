using System;
using System.Collections.Generic;
using System.Globalization;
using KotH.Client.Steam;

namespace KotH.Client.Net
{
    /// <summary>Talk between the players of a Steam round, straight to each other (SteamMesh): the chat's
    /// lines and who is muted. A sender is named by the player its lobby member data ("netid") says it
    /// plays, else by its Steam name. Muted members' lines never show. Game thread only.</summary>
    public sealed class Comms
    {
        /// <summary>A chat line; Name null for a note of the game's own.</summary>
        public readonly record struct Line(string Name, string Text, double At, bool Own);

        public const int Kept = 50;

        readonly SteamLobby lobby;
        readonly SteamMesh mesh;
        readonly Func<int, string> playerName;
        readonly List<Line> lines = new List<Line>();
        readonly HashSet<ulong> muted = new HashSet<ulong>();

        /// <param name="lobby">Null: lines stay local (the chat's own notes, shots).</param>
        /// <param name="playerName">The name of the player with a NetId, null when there is none.</param>
        public Comms(SteamLobby lobby, Func<int, string> playerName)
        {
            this.lobby = lobby;
            this.playerName = playerName;
            mesh = lobby != null ? new SteamMesh(lobby) : null;
        }

        /// <summary>The newest lines, oldest first, at most Kept.</summary>
        public IReadOnlyList<Line> Lines => lines;

        /// <summary>Counts every change of Lines.</summary>
        public int Version { get; private set; }

        /// <summary>When the newest line came, NetClient's milliseconds; -inf before any.</summary>
        public double LastAt { get; private set; } = double.NegativeInfinity;

        public SteamMesh Mesh => mesh;

        /// <summary>Once per frame: takes in what the others sent.</summary>
        public void Update(double now)
        {
            polledAt = now;
            if (lobby != null && now - mappedAt >= MapEveryMs)
                Map(now);
            mesh?.Poll(now, heard ??= Heard, VoiceHeard);
        }

        /// <summary>Gets every voice packet that came (VoiceChat's); null drops them.</summary>
        public SteamMesh.VoiceHandler VoiceHeard { get; set; }

        // The handlers are made once: a lambda per frame would allocate.
        Action<ulong, string> heard;
        double polledAt;

        void Heard(ulong from, string text)
        {
            if (!muted.Contains(from))
                Add(new Line(NameOf(from), text, polledAt, false));
        }

        /// <summary>Sends a line to everyone and shows it; false when it was empty.</summary>
        public bool Say(string text, string ownName, double now)
        {
            text = SteamMesh.Clean(text);
            if (text.Length == 0 || mesh != null && !mesh.SendChat(text))
                return false;
            Add(new Line(ownName, text, now, true));
            return true;
        }

        /// <summary>Test aid: a line as if someone sent it.</summary>
        public void Pretend(string name, string text, double now) => Add(new Line(name, text, now, false));

        /// <summary>A note of the game's own in the chat.</summary>
        public void Note(string text, double now) => Add(new Line(null, text, now, false));

        void Add(in Line line)
        {
            lines.Add(line);
            if (lines.Count > Kept)
                lines.RemoveAt(0);
            Version++;
            LastAt = line.At;
        }

        /// <summary>The player a member plays, by its lobby member data; its Steam name when unknown.</summary>
        public string NameOf(ulong member)
        {
            if (int.TryParse(lobby.MemberData(member, "netid"), NumberStyles.None, CultureInfo.InvariantCulture, out int netId)
                && playerName(netId) is { } name)
                return name;
            return SteamMesh.PersonaOf(member);
        }

        /// <summary>The NetId a member plays, 0 when unknown (as of the last 2 s).</summary>
        public int NetIdOf(ulong member) => netIds.TryGetValue(member, out int id) ? id : 0;

        /// <summary>The other members as of the last 2 s; read per frame without allocating.</summary>
        public IReadOnlyList<ulong> Members => members;

        // Steam's member data is read as new strings: once in a while, not per frame.
        const double MapEveryMs = 2000.0;
        readonly Dictionary<ulong, int> netIds = new Dictionary<ulong, int>();
        readonly List<ulong> members = new List<ulong>();
        double mappedAt = double.NegativeInfinity;

        void Map(double now)
        {
            mappedAt = now;
            lobby.Members(members);
            members.Remove(SteamClient.SteamId);
            netIds.Clear();
            foreach (ulong member in members)
                if (int.TryParse(lobby.MemberData(member, "netid"), NumberStyles.None, CultureInfo.InvariantCulture, out int id))
                    netIds[member] = id;
        }

        public bool IsMuted(ulong member) => muted.Contains(member);

        public void SetMuted(ulong member, bool mute)
        {
            if (mute)
                muted.Add(member);
            else
                muted.Remove(member);
        }

        /// <summary>The other members of the lobby, for the Players page.</summary>
        public List<ulong> Others()
        {
            if (lobby == null)
                return new List<ulong>();
            List<ulong> all = lobby.Members();
            all.Remove(SteamClient.SteamId);
            return all;
        }
    }
}
