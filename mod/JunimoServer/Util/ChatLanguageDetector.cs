using StardewValley;

namespace JunimoServer.Util;

/// <summary>
/// Infers the chat <see cref="LocalizedContentManager.LanguageCode"/> a message should be
/// tagged with from the script of its text, so the receiving client loads a font that can
/// render the glyphs. A SpriteFont renders only its baked glyphs; tagging a Cyrillic/CJK/Thai
/// message with the wrong language loads a font without those glyphs and draws boxes.
///
/// Only languages whose <c>SmallFont</c> ships a distinct (non-Latin) glyph set need detection.
/// The Latin-script locales in vanilla's font set (pt, es, de, fr, it, tr, hu) share the same
/// glyph coverage as en, so Latin text resolves to en — identical to the prior behavior.
/// Mirrors vanilla's available font set (LocalizedContentManager.LanguageCodeString).
/// </summary>
public static class ChatLanguageDetector
{
    /// <summary>
    /// Returns the LanguageCode whose baked font covers the message's script. Scans the whole
    /// string so the Han/Kana ambiguity resolves correctly (kana proves Japanese even when Han
    /// appears first). Latin text, punctuation, and empty/null input resolve to en.
    /// </summary>
    public static LocalizedContentManager.LanguageCode DetectLanguage(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return LocalizedContentManager.LanguageCode.en;
        }

        var hasCyrillic = false;
        var hasHangul = false;
        var hasKana = false;
        var hasHan = false;
        var hasThai = false;

        foreach (var c in text)
        {
            if (c >= 0x0400 && c <= 0x04FF) // Cyrillic
            {
                hasCyrillic = true;
            }
            else if ((c >= 0xAC00 && c <= 0xD7A3) || (c >= 0x1100 && c <= 0x11FF)) // Hangul syllables / jamo
            {
                hasHangul = true;
            }
            else if ((c >= 0x3040 && c <= 0x309F) || (c >= 0x30A0 && c <= 0x30FF)) // Hiragana / Katakana
            {
                hasKana = true;
            }
            else if (c >= 0x4E00 && c <= 0x9FFF) // CJK Unified Ideographs (Han)
            {
                hasHan = true;
            }
            else if (c >= 0x0E00 && c <= 0x0E7F) // Thai
            {
                hasThai = true;
            }
        }

        // Precedence: unambiguous scripts first. Kana outranks Han because Han is shared between
        // Japanese and Chinese — kana disambiguates a Han-bearing string to Japanese.
        if (hasKana)
        {
            return LocalizedContentManager.LanguageCode.ja;
        }
        if (hasHangul)
        {
            return LocalizedContentManager.LanguageCode.ko;
        }
        if (hasCyrillic)
        {
            return LocalizedContentManager.LanguageCode.ru;
        }
        if (hasThai)
        {
            return LocalizedContentManager.LanguageCode.th;
        }
        if (hasHan)
        {
            return LocalizedContentManager.LanguageCode.zh;
        }

        return LocalizedContentManager.LanguageCode.en;
    }
}
