using System;

namespace KotH.Client.Localization
{
    /// <summary>Pluralization category used when picking count-dependent strings.</summary>
    public enum PluralKind { Germanic, Slavic, Arabic, Invariant }

    /// <summary>Metadata for one supported UI language: ISO code, native display name (autonym),
    /// layout direction, plural rule and number formatting separators.</summary>
    public sealed record LanguageInfo(
        string Code,
        string Autonym,
        bool IsRtl,
        PluralKind Plural,
        string DecimalSep,
        string GroupSep)
    {
        /// <summary>All 17 supported languages in the order shown on the Languages page.</summary>
        public static readonly LanguageInfo[] All =
        {
            new("en", "English", false, PluralKind.Germanic, ".", ","),
            new("de", "Deutsch", false, PluralKind.Germanic, ",", "."),
            new("es", "Español", false, PluralKind.Germanic, ",", "."),
            new("fr", "Français", false, PluralKind.Germanic, ",", "\u00A0"),
            new("it", "Italiano", false, PluralKind.Germanic, ",", "."),
            new("pt", "Português (Brasil)", false, PluralKind.Germanic, ",", "."),
            new("ru", "Русский", false, PluralKind.Slavic, ",", "\u00A0"),
            new("tr", "Türkçe", false, PluralKind.Invariant, ",", "."),
            new("id", "Bahasa Indonesia", false, PluralKind.Invariant, ",", "."),
            new("fil", "Filipino", false, PluralKind.Germanic, ".", ","),
            new("af", "Afrikaans", false, PluralKind.Germanic, ",", "\u00A0"),
            new("ja", "日本語", false, PluralKind.Invariant, ".", ","),
            new("ko", "한국어", false, PluralKind.Invariant, ".", ","),
            new("zh", "简体中文", false, PluralKind.Invariant, ".", ","),
            new("ar", "العربية", true, PluralKind.Arabic, ".", ","),
            new("hi", "हिन्दी", false, PluralKind.Germanic, ".", ","),
            new("th", "ไทย", false, PluralKind.Invariant, ".", ","),
        };

        public static LanguageInfo Find(string code)
        {
            if (string.IsNullOrWhiteSpace(code))
                return null;
            string norm = code.Trim().ToLowerInvariant();
            foreach (LanguageInfo lang in All)
                if (lang.Code == norm)
                    return lang;
            return null;
        }
    }
}
