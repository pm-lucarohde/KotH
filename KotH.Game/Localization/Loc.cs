using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using KotH.Client.Gamepad;
using KotH.Client.Menu;
using KotH.Client.Settings;
using KotH.Client.Steam;
using KotH.Shared.Hosting;
using KotH.Shared.Match;
using KotH.Shared.Net;
using Stride.Input;

namespace KotH.Client.Localization
{
    /// <summary>AOT-safe localization store backed by embedded UTF-8 key=value tables (Localization/Strings/*.txt).
    /// Supports live switching across all 17 languages, RTL isolation for numbers/tokens, plural forms,
    /// localized maps/modes/keys and safe 32-codepoint Steam name sanitization.</summary>
    public static class Loc
    {
        static readonly Dictionary<string, string> English = LoadTable("en");
        static Dictionary<string, string> active = English;
        static LanguageInfo current = LanguageInfo.All[0];
        static NumberFormatInfo numbers = BuildNumbers(current);
        static bool initialized;

        /// <summary>The active language metadata.</summary>
        public static LanguageInfo Current
        {
            get
            {
                EnsureInitialized();
                return current;
            }
        }

        /// <summary>Whether the active language uses right-to-left layout (Arabic).</summary>
        public static bool IsRtl => Current.IsRtl;

        /// <summary>Number format for UI display (simulation keeps InvariantCulture).</summary>
        public static NumberFormatInfo Numbers
        {
            get
            {
                EnsureInitialized();
                return numbers;
            }
        }

        /// <summary>Incremented on every language switch so cached HUD/menu text refreshes.</summary>
        public static int Version { get; private set; }

        /// <summary>Raised after the active language changed.</summary>
        public static event Action Changed;

        /// <summary>Applies the saved language (or Steam's auto-detected language when unset).</summary>
        public static void EnsureInitialized()
        {
            if (initialized)
                return;
            initialized = true;
            string wanted = ClientSettings.Applied.Language;
            string code = ResolveCode(wanted);
            Apply(code, notify: false);
        }

        /// <summary>Resolves a saved or Steam language code to one of the 17 supported codes.</summary>
        public static string ResolveCode(string wanted)
        {
            if (LanguageInfo.Find(wanted) is { } direct)
                return direct.Code;
            if (LanguageInfo.Find(SteamClient.Language) is { } fromSteam)
                return fromSteam.Code;
            return "en";
        }

        /// <summary>Switches the active language immediately and notifies all open views.</summary>
        public static void Set(string code, bool save = true)
        {
            initialized = true;
            LanguageInfo target = LanguageInfo.Find(code) ?? LanguageInfo.All[0];
            if (save)
                ClientSettings.RememberLanguage(target.Code);
            if (target.Code == current.Code && active != null)
                return;
            Apply(target.Code, notify: true);
        }

        static void Apply(string code, bool notify)
        {
            LanguageInfo target = LanguageInfo.Find(code) ?? LanguageInfo.All[0];
            current = target;
            active = target.Code == "en" ? English : LoadTable(target.Code);
            numbers = BuildNumbers(target);
            Version++;
            if (notify)
            {
                MenuOverlay.RefreshDirection();
                Changed?.Invoke();
            }
        }

        /// <summary>Returns the localized string for key, falling back to English then key.</summary>
        public static string T(string key)
        {
            EnsureInitialized();
            if (active.TryGetValue(key, out string text) && text.Length > 0)
                return text;
            return English.TryGetValue(key, out string fallback) ? fallback : key;
        }

        /// <summary>Formats a localized template using the active UI number format.</summary>
        public static string F(string key, params object[] args)
        {
            string template = T(key);
            if (args == null || args.Length == 0)
                return template;
            try
            {
                return string.Format(Numbers, template, args);
            }
            catch (FormatException)
            {
                return template;
            }
        }

        /// <summary>Selects a plural variant ({baseKey}.one/.two/.few/.many/.other) for count.</summary>
        public static string Plural(string baseKey, int count) => PluralFormat(baseKey, count, Number(count));

        /// <summary>Selects a plural variant for count and formats with the supplied arguments.</summary>
        public static string PluralFormat(string baseKey, int count, params object[] args)
        {
            EnsureInitialized();
            int n = Math.Abs(count);
            string suffix = current.Plural switch
            {
                PluralKind.Germanic => n == 1 ? ".one" : ".other",
                PluralKind.Slavic => n % 10 == 1 && n % 100 != 11 ? ".one"
                                   : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? ".few"
                                   : ".many",
                PluralKind.Arabic => n == 1 ? ".one" : n == 2 ? ".two" : n % 100 is >= 3 and <= 10 ? ".few" : ".other",
                _ => ".other",
            };
            string key = HasKey(baseKey + suffix) ? baseKey + suffix : baseKey + ".other";
            return F(key, args);
        }

        static bool HasKey(string key) => active.ContainsKey(key) || English.ContainsKey(key);

        /// <summary>Wraps a left-to-right token (timer, percent, ping, ratio, link) with LRM guards when RTL.</summary>
        public static string Ltr(string token) => IsRtl ? "\u200E" + token + "\u200E" : token;

        public static string Number(int n) => Ltr(n.ToString("N0", Numbers));

        public static string Decimal(double v, string format) => Ltr(v.ToString(format, Numbers));

        public static string Percent(double v, string format = "F1") => Ltr(v.ToString(format, Numbers) + " %");

        public static string Clock(int seconds)
        {
            int s = Math.Max(0, seconds);
            return Ltr(string.Format(CultureInfo.InvariantCulture, "{0}:{1:D2}", s / 60, s % 60));
        }

        /// <summary>Joins player names with localized list separator and conjunction.</summary>
        public static string JoinNames(IReadOnlyList<string> names, bool words = false)
        {
            if (names.Count == 0)
                return "";
            if (names.Count == 1)
                return names[0];
            string sep = T("list.sep");
            string and = T(words ? "list.and_words" : "list.and");
            var sb = new StringBuilder(names[0]);
            for (int i = 1; i < names.Count - 1; i++)
                sb.Append(sep).Append(names[i]);
            sb.Append(and).Append(names[^1]);
            return sb.ToString();
        }

        /// <summary>Canonical map title (map names stay untranslated).</summary>
        public static string MapTitle(string mapId, string customTitle = null) => customTitle ?? Maps.Title(mapId);

        /// <summary>Canonical mode name (mode names stay untranslated).</summary>
        public static string ModeName(MatchMode mode) => Ltr(HillMatch.DisplayName(mode));

        public static string ModeNote(MatchMode mode) => T(mode switch
        {
            MatchMode.Duo => "mode.duo.note",
            MatchMode.Trio => "mode.trio.note",
            MatchMode.Squad => "mode.squad.note",
            MatchMode.Mighty => "mode.mighty.note",
            MatchMode.Free => "mode.free.note",
            _ => "mode.solo.note",
        });

        public static string UnlockMessage(int level) => level switch
        {
            Levels.CustomizeLevel => T("unlock.customize"),
            Levels.VolcanoLevel => T("unlock.volcano"),
            Levels.PyramidLevel => T("unlock.pyramid"),
            Levels.ChampignonLevel => T("unlock.champignon"),
            Levels.TheLadderLevel => T("unlock.ladder"),
            Levels.GlowLevel => T("unlock.glow"),
            _ => null,
        };

        public static string RegionName(string code)
        {
            string key = "region." + code;
            return HasKey(key) ? T(key) : LobbyFlags.RegionName(code);
        }

        public static string LobbyLanguageName(string code)
        {
            string key = "lang." + code;
            return HasKey(key) ? T(key) : LobbyFlags.LanguageName(code);
        }

        public static string SecureFailure(SecureLinkFailure failure) => failure switch
        {
            SecureLinkFailure.KeyMismatch => T("net.host_key_mismatch"),
            SecureLinkFailure.BadSignature => T("net.host_signature_wrong"),
            _ => T("net.host_handshake_broken"),
        };

        public static string KeyName(Keys key) => key switch
        {
            Keys.None => "—",
            Keys.Space => T("key.space"),
            Keys.LeftShift => T("key.left_shift"),
            Keys.RightShift => T("key.right_shift"),
            Keys.LeftCtrl => T("key.left_ctrl"),
            Keys.RightCtrl => T("key.right_ctrl"),
            Keys.LeftAlt => T("key.left_alt"),
            Keys.RightAlt => T("key.right_alt"),
            Keys.Tab => T("key.tab"),
            Keys.Enter or Keys.Return => T("key.enter"),
            Keys.Back => T("key.backspace"),
            Keys.CapsLock => T("key.caps_lock"),
            _ => DefaultKeyName(key),
        };

        static string DefaultKeyName(Keys key)
        {
            string name = key.ToString();
            if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1]))
                return name[1..];
            if (name.StartsWith("NumPad", StringComparison.Ordinal))
                return F("key.numpad", name[6..]);
            var spaced = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsUpper(name[i]) && !char.IsUpper(name[i - 1]))
                    spaced.Append(' ');
                spaced.Append(name[i]);
            }
            return spaced.ToString();
        }

        public static string PadName(PadButton b, PadStyle style = PadStyle.Xbox) => (style, b) switch
        {
            (_, PadButton.None) => "—",
            (PadStyle.PlayStation, PadButton.A) => T("pad.cross"),
            (PadStyle.PlayStation, PadButton.B) => T("pad.circle"),
            (PadStyle.PlayStation, PadButton.X) => T("pad.square"),
            (PadStyle.PlayStation, PadButton.Y) => T("pad.triangle"),
            (PadStyle.PlayStation, PadButton.Back) => T("pad.share"),
            (PadStyle.PlayStation, PadButton.Start) => T("pad.options"),
            (PadStyle.PlayStation, PadButton.LeftShoulder) => "L1",
            (PadStyle.PlayStation, PadButton.RightShoulder) => "R1",
            (PadStyle.PlayStation, PadButton.LeftTrigger) => "L2",
            (PadStyle.PlayStation, PadButton.RightTrigger) => "R2",
            (_, PadButton.Back) => T("pad.view"),
            (_, PadButton.Start) => T("pad.menu"),
            (_, PadButton.LeftStick) => T("pad.left_stick"),
            (_, PadButton.RightStick) => T("pad.right_stick"),
            (_, PadButton.LeftShoulder) => "LB",
            (_, PadButton.RightShoulder) => "RB",
            (_, PadButton.LeftTrigger) => "LT",
            (_, PadButton.RightTrigger) => "RT",
            (_, PadButton.DPadUp) => T("pad.dpad_up"),
            (_, PadButton.DPadDown) => T("pad.dpad_down"),
            (_, PadButton.DPadLeft) => T("pad.dpad_left"),
            (_, PadButton.DPadRight) => T("pad.dpad_right"),
            _ => b.ToString(),
        };

        /// <summary>Loads any language table directly (used by automated completeness tests).</summary>
        public static IReadOnlyDictionary<string, string> LoadTableForTest(string code) => LoadTable(code);

        static Dictionary<string, string> LoadTable(string code)
        {
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            Assembly asm = typeof(Loc).Assembly;
            using Stream stream = asm.GetManifestResourceStream($"Localization/{code}.txt");
            if (stream == null)
                return map;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            while (reader.ReadLine() is { } raw)
            {
                if (raw.Length == 0 || raw[0] == '#')
                    continue;
                int eq = raw.IndexOf('=');
                if (eq <= 0)
                    continue;
                string key = raw[..eq].Trim();
                string rhs = raw[(eq + 1)..];
                if (rhs.StartsWith(' '))
                    rhs = rhs[1..];
                string value = Unescape(rhs);
                if (key.Length > 0)
                    map[key] = value;
            }
            return map;
        }

        static string Unescape(string s)
        {
            if (s.IndexOf('\\') < 0)
                return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    char next = s[++i];
                    sb.Append(next switch { 'n' => '\n', 't' => '\t', '\\' => '\\', _ => next });
                }
                else
                    sb.Append(s[i]);
            }
            return sb.ToString();
        }

        static NumberFormatInfo BuildNumbers(LanguageInfo info)
        {
            var n = (NumberFormatInfo)CultureInfo.InvariantCulture.NumberFormat.Clone();
            n.NumberDecimalSeparator = info.DecimalSep;
            n.NumberGroupSeparator = info.GroupSep;
            return n;
        }
    }
}
