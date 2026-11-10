using System;
using System.Collections.Generic;
using System.Globalization;

namespace KotH.Client.Steam
{
    /// <summary>A party of up to four over Steam (Luca: friends play a team together), a friends-only lobby
    /// of its own that lasts across rounds until Leave. Its owner leads: the leader names the round it plays
    /// in (lobby data "round"), the others follow from the main menu, and teams join them (SteamLobby.PartnerNetId).</summary>
    public static class SteamParty
    {
        static SteamLobby lobby;
        static readonly List<ulong> members = new List<ulong>();
        static ulong leading, followed;
        static double nextLook;

        /// <summary>Whether this player is in a party (or one is being made).</summary>
        public static bool Active => lobby != null;

        /// <summary>Whether this player leads it: its rounds are the party's.</summary>
        public static bool Leads => lobby is { Id: not 0 } && lobby.Owner == SteamClient.SteamId;

        /// <summary>The largest party: a squad.</summary>
        public const int MaxMembers = 4;

        /// <summary>Who this player teams up with: a member wishes the leader, the leader the first other
        /// member (the wishes join them all into one team); 0 while alone.</summary>
        public static ulong Mate
        {
            get
            {
                if (lobby is not { Id: not 0 })
                    return 0;
                if (!Leads)
                    return lobby.Owner;
                lobby.Members(members);
                foreach (ulong m in members)
                    if (m != SteamClient.SteamId)
                        return m;
                return 0;
            }
        }

        /// <summary>Whether the player is another member of this party.</summary>
        public static bool Has(ulong member)
        {
            if (member == 0 || member == SteamClient.SteamId || lobby is not { Id: not 0 })
                return false;
            lobby.Members(members);
            return members.Contains(member);
        }

        /// <summary>Makes a party with this player alone in it, if it has none; then friends are invited.</summary>
        public static void Open()
        {
            if (lobby != null || !SteamClient.Ready)
                return;
            lobby = SteamLobby.OpenParty();
        }

        /// <summary>An entered party's lobby (an accepted invite): the party now, any old one left.</summary>
        public static void Joined(SteamLobby entered)
        {
            if (lobby != entered)
                lobby?.Dispose();
            lobby = entered;
            leading = followed = 0;
        }

        /// <summary>Invites a friend into the party; Steam shows it in their chat.</summary>
        public static bool Invite(ulong friend)
        {
            Open();
            return lobby?.Invite(friend) == true;
        }

        public static void Leave()
        {
            lobby?.Dispose();
            lobby = null;
            leading = followed = 0;
        }

        /// <summary>The leader, every frame: the Steam round it plays in, 0 for none (the main menu, direct IP).</summary>
        public static void Playing(ulong round)
        {
            if (!Leads || round == leading)
                return;
            leading = round;
            lobby.SetData("round", round.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>The mate in the main menu, once a second: a round the leader went to since, to join.</summary>
        public static bool FollowTo(out ulong round, out ulong leader)
        {
            round = leader = 0;
            double now = Environment.TickCount64 / 1000.0;
            if (lobby is not { Id: not 0 } || Leads || now < nextLook)
                return false;
            nextLook = now + 1.0;
            ulong.TryParse(lobby.Data("round"), NumberStyles.None, CultureInfo.InvariantCulture, out round);
            if (round == followed)
                return false;
            followed = round;
            leader = lobby.Owner;
            return round != 0;
        }

        /// <summary>What the party page says: who is in it and who leads.</summary>
        public static string Status()
        {
            if (lobby == null)
                return "No party: invite up to three friends to play together as one team.";
            if (lobby.Id == 0)
                return lobby.Failure ?? "Making the party ...";
            lobby.Members(members);
            var others = new List<string>();
            foreach (ulong m in members)
                if (m != SteamClient.SteamId)
                    others.Add(SteamClient.NameOf(m));
            if (others.Count == 0)
                return "Your party: just you. Invite a friend.";
            string names = others.Count == 1 ? others[0] : string.Join(", ", others.GetRange(0, others.Count - 1)) + " and " + others[^1];
            return Leads ? $"Your party: you and {names}. They follow you into rounds."
                         : $"Your party: you and {names}. You follow {SteamClient.NameOf(lobby.Owner)} into rounds.";
        }
    }
}
