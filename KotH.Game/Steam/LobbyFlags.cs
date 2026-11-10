using System;
using System.Collections.Generic;
using KotH.Shared.Match;

namespace KotH.Client.Steam
{
    /// <summary>What a host tells the lobby list about its round besides map and players: its name,
    /// private (a password guards it) or public, country, languages and whether it is modded (only a
    /// label for now), and solo or teams. Countries are ISO 3166 codes, languages ISO 639-1.</summary>
    public sealed record LobbyFlags(string Name, bool Private, string Country, string[] Languages, bool Modded, MatchMode Mode = MatchMode.Solo)
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

        // ISO 3166-1 by English name: code, space, name, bar.
        const string CountryTable =
            "AF Afghanistan|AL Albania|DZ Algeria|AS American Samoa|AD Andorra|AO Angola|AI Anguilla|AQ Antarctica|"
            + "AG Antigua and Barbuda|AR Argentina|AM Armenia|AW Aruba|AU Australia|AT Austria|AZ Azerbaijan|"
            + "BS Bahamas|BH Bahrain|BD Bangladesh|BB Barbados|BY Belarus|BE Belgium|BZ Belize|BJ Benin|BM Bermuda|"
            + "BT Bhutan|BO Bolivia|BQ Bonaire, Sint Eustatius and Saba|BA Bosnia and Herzegovina|BW Botswana|"
            + "BV Bouvet Island|BR Brazil|IO British Indian Ocean Territory|BN Brunei Darussalam|BG Bulgaria|"
            + "BF Burkina Faso|BI Burundi|CV Cabo Verde|KH Cambodia|CM Cameroon|CA Canada|KY Cayman Islands|"
            + "CF Central African Republic|TD Chad|CL Chile|CN China|CX Christmas Island|CC Cocos (Keeling) Islands|"
            + "CO Colombia|KM Comoros|CG Congo|CD Congo, The Democratic Republic of the|CK Cook Islands|CR Costa Rica|"
            + "HR Croatia|CU Cuba|CW Curaçao|CY Cyprus|CZ Czechia|CI Côte d'Ivoire|DK Denmark|DJ Djibouti|DM Dominica|"
            + "DO Dominican Republic|EC Ecuador|EG Egypt|SV El Salvador|GQ Equatorial Guinea|ER Eritrea|EE Estonia|"
            + "SZ Eswatini|ET Ethiopia|FK Falkland Islands (Malvinas)|FO Faroe Islands|FJ Fiji|FI Finland|FR France|"
            + "GF French Guiana|PF French Polynesia|TF French Southern Territories|GA Gabon|GM Gambia|GE Georgia|"
            + "DE Germany|GH Ghana|GI Gibraltar|GR Greece|GL Greenland|GD Grenada|GP Guadeloupe|GU Guam|GT Guatemala|"
            + "GG Guernsey|GN Guinea|GW Guinea-Bissau|GY Guyana|HT Haiti|HM Heard Island and McDonald Islands|"
            + "VA Holy See (Vatican City State)|HN Honduras|HK Hong Kong|HU Hungary|IS Iceland|IN India|ID Indonesia|"
            + "IR Iran|IQ Iraq|IE Ireland|IM Isle of Man|IL Israel|IT Italy|JM Jamaica|JP Japan|JE Jersey|JO Jordan|"
            + "KZ Kazakhstan|KE Kenya|KI Kiribati|KW Kuwait|KG Kyrgyzstan|LA Laos|LV Latvia|LB Lebanon|LS Lesotho|"
            + "LR Liberia|LY Libya|LI Liechtenstein|LT Lithuania|LU Luxembourg|MO Macao|MG Madagascar|MW Malawi|"
            + "MY Malaysia|MV Maldives|ML Mali|MT Malta|MH Marshall Islands|MQ Martinique|MR Mauritania|MU Mauritius|"
            + "YT Mayotte|MX Mexico|FM Micronesia, Federated States of|MD Moldova|MC Monaco|MN Mongolia|ME Montenegro|"
            + "MS Montserrat|MA Morocco|MZ Mozambique|MM Myanmar|NA Namibia|NR Nauru|NP Nepal|NL Netherlands|"
            + "NC New Caledonia|NZ New Zealand|NI Nicaragua|NE Niger|NG Nigeria|NU Niue|NF Norfolk Island|"
            + "KP North Korea|MK North Macedonia|MP Northern Mariana Islands|NO Norway|OM Oman|PK Pakistan|PW Palau|"
            + "PS Palestine, State of|PA Panama|PG Papua New Guinea|PY Paraguay|PE Peru|PH Philippines|PN Pitcairn|"
            + "PL Poland|PT Portugal|PR Puerto Rico|QA Qatar|RO Romania|RU Russian Federation|RW Rwanda|RE Réunion|"
            + "BL Saint Barthélemy|SH Saint Helena, Ascension and Tristan da Cunha|KN Saint Kitts and Nevis|"
            + "LC Saint Lucia|MF Saint Martin (French part)|PM Saint Pierre and Miquelon|"
            + "VC Saint Vincent and the Grenadines|WS Samoa|SM San Marino|ST Sao Tome and Principe|SA Saudi Arabia|"
            + "SN Senegal|RS Serbia|SC Seychelles|SL Sierra Leone|SG Singapore|SX Sint Maarten (Dutch part)|"
            + "SK Slovakia|SI Slovenia|SB Solomon Islands|SO Somalia|ZA South Africa|"
            + "GS South Georgia and the South Sandwich Islands|KR South Korea|SS South Sudan|ES Spain|LK Sri Lanka|"
            + "SD Sudan|SR Suriname|SJ Svalbard and Jan Mayen|SE Sweden|CH Switzerland|SY Syria|TW Taiwan|"
            + "TJ Tajikistan|TZ Tanzania|TH Thailand|TL Timor-Leste|TG Togo|TK Tokelau|TO Tonga|TT Trinidad and Tobago|"
            + "TN Tunisia|TM Turkmenistan|TC Turks and Caicos Islands|TV Tuvalu|TR Türkiye|UG Uganda|UA Ukraine|"
            + "AE United Arab Emirates|GB United Kingdom|US United States|UM United States Minor Outlying Islands|"
            + "UY Uruguay|UZ Uzbekistan|VU Vanuatu|VE Venezuela|VN Vietnam|VG Virgin Islands, British|"
            + "VI Virgin Islands, U.S.|WF Wallis and Futuna|EH Western Sahara|YE Yemen|ZM Zambia|ZW Zimbabwe|"
            + "AX Åland Islands|";

        static (string Code, string Name)[] countries;

        /// <summary>Every country by English name.</summary>
        public static IReadOnlyList<(string Code, string Name)> AllCountries
        {
            get
            {
                if (countries != null)
                    return countries;
                string[] parts = CountryTable.Split('|', StringSplitOptions.RemoveEmptyEntries);
                countries = new (string, string)[parts.Length];
                for (int i = 0; i < parts.Length; i++)
                    countries[i] = (parts[i][..2], parts[i][3..]);
                return countries;
            }
        }

        public static string CountryName(string code)
        {
            foreach ((string c, string name) in AllCountries)
                if (c == code)
                    return name;
            return string.IsNullOrEmpty(code) ? "Unknown" : code;
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
    public sealed record LobbyFilter(bool? Private = null, string Country = null, string Language = null, bool? Modded = null, MatchMode? Mode = null)
    {
        public static readonly LobbyFilter Any = new LobbyFilter();
    }
}
