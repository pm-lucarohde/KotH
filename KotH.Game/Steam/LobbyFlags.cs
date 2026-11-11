using System;
using System.Collections.Generic;
using KotH.Shared.Match;

namespace KotH.Client.Steam
{
    /// <summary>What a host tells the lobby list about its round besides map and players: its name,
    /// private (a password guards it) or public, up to 2 regions, up to 4 languages (ISO 639-1), whether
    /// it is modded (only a label for now), and solo or teams.</summary>
    public sealed record LobbyFlags(string Name, bool Private, string[] Regions, string[] Languages, bool Modded, MatchMode Mode = MatchMode.Solo)
    {
        /// <summary>Languages a host can name, by code, with their English names (the menu's font
        /// has no CJK or Cyrillic letters).</summary>
        public static readonly (string Code, string Name)[] AllLanguages =
        {
            ("en", "English"), ("de", "German"), ("fr", "French"), ("es", "Spanish"), ("it", "Italian"),
            ("pt", "Portuguese"), ("nl", "Dutch"), ("pl", "Polish"), ("cs", "Czech"), ("sv", "Swedish"),
            ("da", "Danish"), ("no", "Norwegian"), ("fi", "Finnish"), ("hu", "Hungarian"), ("ro", "Romanian"),
            ("tr", "Turkish"), ("el", "Greek"), ("uk", "Ukrainian"), ("ru", "Russian"), ("ar", "Arabic"),
            ("hi", "Hindi"), ("id", "Indonesian"), ("vi", "Vietnamese"), ("th", "Thai"), ("ja", "Japanese"),
            ("ko", "Korean"), ("zh", "Chinese"),
        };

        /// <summary>The regions a host can name (Luca, 11.11.2026), by code, with their names.</summary>
        public static readonly (string Code, string Name)[] AllRegions =
        {
            ("eu", "Europe"), ("nae", "East America"), ("naw", "West America"), ("sa", "South America"),
            ("oc", "Oceania"), ("as", "Asia"), ("me", "Middle East"), ("af", "Africa"),
        };

        /// <summary>A host may name up to this many regions and languages.</summary>
        public const int MaxRegions = 2, MaxLanguages = 4;

        // ISO 3166-1 codes per region; North and Central America with the Caribbean ("na") go east or west
        // by the PC's standard time. Anything else (or unknown) is Europe.
        const string RegionTable =
            "me:AE BH EG IL IQ IR JO KW LB OM PS QA SA SY YE|"
            + "af:AO BF BI BJ BW CD CF CG CI CM CV DJ DZ EH ER ET GA GH GM GN GQ GW KE KM LR LS LY MA MG ML MR MU MW MZ "
            + "NA NE NG RE RW SC SD SH SL SN SO SS ST SZ TD TG TN TZ UG YT ZA ZM ZW|"
            + "sa:AR BO BR CL CO EC FK GF GY PE PY SR UY VE|"
            + "oc:AS AU CK FJ FM GU KI MH MP NC NF NR NU NZ PF PG PN PW SB TK TO TV UM VU WF WS|"
            + "as:AF BD BN BT CC CN CX HK ID IN IO JP KG KH KP KR KZ LA LK MM MN MO MV MY NP PH PK SG TH TJ TL TM TW UZ VN|"
            + "na:AG AI AW BB BL BM BQ BS BZ CA CR CU CW DM DO GD GL GP GT HN HT JM KN KY LC MF MQ MS MX NI PA PM PR SV "
            + "SX TC TT US VC VG VI|";

        /// <summary>The region of a country (ISO 3166-1); in America east or west by the standard UTC offset
        /// in hours (Pacific and Mountain time are west).</summary>
        public static string RegionOf(string country, double utcHours)
        {
            if (country is { Length: 2 })
                foreach (string part in RegionTable.Split('|', StringSplitOptions.RemoveEmptyEntries))
                    if (part.IndexOf(country.ToUpperInvariant(), 3, StringComparison.Ordinal) >= 3)
                        return part[..2] == "na" ? (utcHours <= -6.5 ? "naw" : "nae") : part[..2];
            return "eu";
        }

        public static string RegionName(string code)
        {
            foreach ((string c, string name) in AllRegions)
                if (c == code)
                    return name;
            return code;
        }

        /// <summary>Keeps the known codes of a list, each once, at most max of them.</summary>
        public static string[] Known(IEnumerable<string> codes, (string Code, string Name)[] all, int max)
        {
            var kept = new List<string>();
            foreach (string code in codes)
                if (kept.Count < max && !kept.Contains(code) && Array.Exists(all, a => a.Code == code))
                    kept.Add(code);
            return kept.ToArray();
        }

        public static string LanguageName(string code)
        {
            foreach ((string c, string name) in AllLanguages)
                if (c == code)
                    return name;
            return code;
        }

        /// <summary>Steam's language names (GetCurrentGameLanguage) as codes; unknown ones are English.</summary>
        public static string LanguageOfSteam(string steam) => steam switch
        {
            "german" => "de", "french" => "fr", "spanish" or "latam" => "es", "italian" => "it",
            "portuguese" or "brazilian" => "pt", "dutch" => "nl", "polish" => "pl", "czech" => "cs",
            "swedish" => "sv", "danish" => "da", "norwegian" => "no", "finnish" => "fi", "hungarian" => "hu",
            "romanian" => "ro", "turkish" => "tr", "greek" => "el", "ukrainian" => "uk", "russian" => "ru",
            "arabic" => "ar", "indonesian" => "id", "vietnamese" => "vi", "thai" => "th", "japanese" => "ja",
            "koreana" => "ko", "schinese" or "tchinese" => "zh",
            _ => "en",
        };
    }

    /// <summary>What Find server asks Steam for: null or Any means no filter.</summary>
    public sealed record LobbyFilter(bool? Private = null, string Region = null, string Language = null, bool? Modded = null, MatchMode? Mode = null)
    {
        public static readonly LobbyFilter Any = new LobbyFilter();
    }
}
