/**
 * BigCraftables data parser for Stardew Valley.
 * Parses the game's BigCraftables.json and provides sprite lookups.
 * BigCraftables are 1-tile-wide, 2-tile-tall objects (16x32 sprites).
 */
const BigCraftablesParser = (function() {
    'use strict';

    const DEFAULT_TEXTURE = 'TileSheets/Craftables';
    const SPRITE_WIDTH = 16;   // pixels
    const SPRITE_HEIGHT = 32;  // pixels

    let data = null;

    /**
     * Load BigCraftables data from a JSON file.
     * @param {string} url - URL to the BigCraftables.json file
     */
    async function load(url) {
        const response = await fetch(url);
        if (!response.ok) {
            throw new Error(`Failed to load BigCraftables data: ${response.status}`);
        }
        data = await response.json();
    }

    /**
     * Check if data is loaded.
     * @returns {boolean}
     */
    function isLoaded() {
        return data !== null;
    }

    /**
     * Get a BigCraftable entry by ID.
     * @param {string} itemId - The item ID (e.g., "(BC)FishSmoker" or "FishSmoker" or "128")
     * @returns {Object|null} Entry with name, texture, spriteIndex, or null
     */
    function get(itemId) {
        if (!data) return null;
        // Strip (BC) prefix if present
        const id = itemId.replace(/^\(BC\)/, '');
        const entry = data[id];
        if (!entry) return null;
        return {
            id,
            name: entry.Name,
            texture: entry.Texture || DEFAULT_TEXTURE,
            spriteIndex: (entry.SpriteIndex ?? parseInt(id)) || 0
        };
    }

    /**
     * Get the source rectangle for a BigCraftable sprite.
     * @param {string} itemId - The item ID
     * @param {number} textureWidth - Width of the texture in pixels
     * @returns {Object|null} { x, y, width, height } in pixels, or null
     */
    function getSourceRect(itemId, textureWidth) {
        const entry = get(itemId);
        if (!entry) return null;
        const cols = Math.floor(textureWidth / SPRITE_WIDTH);
        const col = entry.spriteIndex % cols;
        const row = Math.floor(entry.spriteIndex / cols);
        return {
            x: col * SPRITE_WIDTH,
            y: row * SPRITE_HEIGHT,
            width: SPRITE_WIDTH,
            height: SPRITE_HEIGHT
        };
    }

    return {
        load,
        isLoaded,
        get,
        getSourceRect,
        DEFAULT_TEXTURE,
        SPRITE_WIDTH,
        SPRITE_HEIGHT
    };
})();
