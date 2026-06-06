---
paths:
  - "mod/**/*.cs"
---

# Stardew chat font is chosen by the per-message LanguageCode tag — no glyph fallback

The font a chat message renders in is decided **solely** by the `LocalizedContentManager.LanguageCode` tag attached to that message when it's sent, not by the receiver's language. `ChatMessage.draw` → `ChatBox.messageFont(language)` → `Game1.content.Load<SpriteFont>("Fonts\\SmallFont", language)` loads `SmallFont.<lang>.xnb` for the message's tag. A `SpriteFont` renders only its baked glyphs; a missing glyph draws a box. There is no per-glyph fallback and no broad multi-script font (`SmallFont_international` exists for some tilesheets but **not** for fonts). So a message tagged `en` shows Cyrillic/CJK as boxes on every client, regardless of which fonts are installed.

**Why:** Issue #259 — Cyrillic from the Discord relay rendered as boxes. The server's send primitives (`SendPublicMessage`/`SendPrivateMessage` in `mod/JunimoServer/Util/ModHelperExtensions.cs`) hardcoded `LocalizedContentManager.CurrentLanguageCode`, which is always `en` on the headless server. In-game player chat works because the *sender's* client tags each message with that player's language — the tag is the entire mechanism, there is no script auto-detection in vanilla. Two separate things are required for non-Latin server chat to render: (1) the message must be tagged with a LanguageCode whose font covers the script, and (2) that font file must be present in the download (the operator's `STEAM_KEEP_LANGUAGES`, added by #336). #336 supplied (2) only; it did not touch the tag, so #259 stayed broken even with the Russian font present.

**How to apply:** When the mod sends chat to clients (relay, festival/system announcements, command replies, auth prompts), do not pass the server's `CurrentLanguageCode` — it's `en` and will box any non-Latin text. A relayed/system message has no inherent sender language, so infer the LanguageCode from the message text's script (Cyrillic→`ru`, Kana→`ja`, Hangul→`ko`, Han→`zh`, Thai→`th`, else→`en`) and tag with that. Do this at the central send primitive so every caller benefits. Keep the change paired with the font-availability requirement — tagging `ru` only helps if `SmallFont.ru-RU.xnb` was kept in the download.
