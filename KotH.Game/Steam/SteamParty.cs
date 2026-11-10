using System;
using System.Collections.Generic;
using System.Globalization;

namespace KotH.Client.Steam
{
    /// <summary>A party of two over Steam (Luca: friends play duo together), a friends-only lobby of its
    /// own that lasts across rounds until Leave. Its owner leads: the leader names the round it plays in
    /// (lobby data "round"), the mate follows from the main menu, and duo pairs them (SteamLobby.PartnerNetId).</summary>
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

        /// <summary>The other member, 0 while alone.</summary>
        public static ulong Mate
        {
            get
            {
                if (lobby is not { Id: not 0 })
                    return 0;
                lobby.Members(members);
                foreach (ulong m in members)
                    if (m != SteamClient.SteamId)
                        return m;
                return 0;
            }
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
                return "No party: invite a friend to play together, paired in duo.";
            if (lobby.Id == 0)
                return lobby.Failure ?? "Making the party ...";
            ulong mate = Mate;
            if (mate == 0)
                return "Your party: just you. Invite a friend.";
            string name = SteamClient.NameOf(mate);
            return Leads ? $"Your party: you and {name}. {name} follows you into rounds."
                         : $"Your party: you and {name}. You follow {name} into rounds.";
        }
    }
}
