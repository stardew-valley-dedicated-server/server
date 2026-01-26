/**
 * Furniture data parser for Stardew Valley.
 * Parses the game's Furniture.json format and provides dimension lookups.
 */
const FurnitureParser = (function() {
    'use strict';

    // Type name to type number mapping (from game's Furniture.cs)
    const TYPE_NAME_TO_NUMBER = {
        'chair': 0,
        'bench': 1,
        'couch': 2,
        'armchair': 3,
        'dresser': 4,
        'long table': 5,
        'painting': 6,
        'lamp': 7,
        'decor': 8,
        'other': 9,
        'bookcase': 10,
        'table': 11,
        'rug': 12,
        'window': 13,
        'fireplace': 14,
        'torch': 16,
        'sconce': 17
    };

    // Default sprite sizes by furniture type [width, height] in tiles
    // From game's Furniture.cs getDefaultSourceRectForType()
    const DEFAULT_SPRITE_SIZES = {
        0:  [1, 2],  // chair
        1:  [2, 2],  // bench
        2:  [3, 2],  // couch
        3:  [2, 2],  // armchair
        4:  [2, 2],  // dresser
        5:  [5, 3],  // long table
        6:  [2, 2],  // painting
        7:  [1, 3],  // lamp
        8:  [1, 2],  // decor
        9:  [1, 2],  // other (default)
        10: [2, 3],  // bookcase
        11: [2, 3],  // table
        12: [3, 2],  // rug
        13: [1, 2],  // window
        14: [2, 5],  // fireplace
        15: [3, 4],  // bed
        16: [1, 2],  // torch
        17: [1, 2]   // sconce
    };

    // Default bounding box sizes by furniture type [width, height] in tiles
    // From game's Furniture.cs getDefaultBoundingBoxForType()
    const DEFAULT_BOUNDING_BOXES = {
        0:  [1, 1],  // chair
        1:  [2, 1],  // bench
        2:  [3, 1],  // couch
        3:  [2, 1],  // armchair
        4:  [2, 1],  // dresser
        5:  [5, 2],  // long table
        6:  [2, 2],  // painting
        7:  [1, 1],  // lamp
        8:  [1, 1],  // decor
        9:  [1, 1],  // other (default)
        10: [2, 1],  // bookcase
        11: [2, 2],  // table
        12: [3, 2],  // rug
        13: [1, 2],  // window
        14: [2, 2],  // fireplace
        15: [3, 3],  // bed
        16: [1, 1],  // torch
        17: [1, 2]   // sconce
    };

    let furnitureData = null;
    let parsedCache = {};

    /**
     * Get the type number from a type name.
     * @param {string} typeName - The furniture type name (e.g., "chair", "table")
     * @returns {number} The type number
     */
    function getTypeNumber(typeName) {
        const lower = typeName.toLowerCase();
        if (lower.startsWith('bed')) {
            return 15;
        }
        return TYPE_NAME_TO_NUMBER[lower] ?? 9;
    }

    // Default texture for furniture without explicit texture
    const DEFAULT_TEXTURE = 'TileSheets/furniture';
    const DEFAULT_TEXTURE_WIDTH = 512; // furniture.png width in pixels

    /**
     * Parse a single furniture entry from the game data.
     * Format: "Name/Type/TilesheetSize/BoundingBoxSize/Rotations/Price/PlacementRestriction/DisplayName/SpriteIndex/Texture/..."
     * @param {string} id - The furniture ID
     * @param {string} data - The raw data string
     * @returns {Object} Parsed furniture data
     */
    function parseEntry(id, data) {
        const parts = data.split('/');
        const name = parts[0];
        const typeName = parts[1];
        const tilesheetSize = parts[2];
        const boundingBoxSize = parts[3];
        const rotations = parseInt(parts[4]) || 1;

        // Extended format fields (index 7+ for newer furniture)
        // Parts[5] = price, parts[6] = placement restriction, parts[7] = display name
        // Parts[8] = sprite index (optional, empty string means use ID), parts[9] = texture (optional)
        const spriteIndexParsed = parts[8] ? parseInt(parts[8]) : NaN;
        const spriteIndex = !isNaN(spriteIndexParsed) ? spriteIndexParsed : parseInt(id);
        const texture = parts[9] || DEFAULT_TEXTURE;

        const typeNumber = getTypeNumber(typeName);

        // Parse tilesheet size (sprite dimensions)
        let spriteWidth, spriteHeight;
        if (tilesheetSize === '-1') {
            [spriteWidth, spriteHeight] = DEFAULT_SPRITE_SIZES[typeNumber] || [1, 2];
        } else {
            const [w, h] = tilesheetSize.split(' ').map(Number);
            spriteWidth = w || 1;
            spriteHeight = h || 2;
        }

        // Parse bounding box size (placement dimensions)
        let boxWidth, boxHeight;
        if (boundingBoxSize === '-1') {
            [boxWidth, boxHeight] = DEFAULT_BOUNDING_BOXES[typeNumber] || [1, 1];
        } else {
            const [w, h] = boundingBoxSize.split(' ').map(Number);
            boxWidth = w || 1;
            boxHeight = h || 1;
        }

        return {
            id,
            name,
            type: typeName,
            typeNumber,
            spriteWidth,
            spriteHeight,
            boxWidth,
            boxHeight,
            rotations,
            spriteIndex,
            texture
        };
    }

    /**
     * Load furniture data from a JSON file.
     * @param {string} url - URL to the Furniture.json file
     * @returns {Promise<void>}
     */
    async function load(url) {
        const response = await fetch(url);
        if (!response.ok) {
            throw new Error(`Failed to load furniture data: ${response.status}`);
        }
        furnitureData = await response.json();
        parsedCache = {};
    }

    /**
     * Set furniture data directly (for testing or embedded data).
     * @param {Object} data - The furniture data object
     */
    function setData(data) {
        furnitureData = data;
        parsedCache = {};
    }

    /**
     * Check if furniture data is loaded.
     * @returns {boolean}
     */
    function isLoaded() {
        return furnitureData !== null;
    }

    /**
     * Get parsed furniture data by ID.
     * @param {string} itemId - The item ID (e.g., "(F)1376" or "1376")
     * @returns {Object|null} Parsed furniture data or null if not found
     */
    function get(itemId) {
        if (!furnitureData) {
            return null;
        }

        // Normalize ID (remove "(F)" prefix if present)
        const id = itemId.replace(/^\(F\)/, '');

        // Check cache first
        if (parsedCache[id]) {
            return parsedCache[id];
        }

        // Parse and cache
        const rawData = furnitureData[id];
        if (!rawData) {
            return null;
        }

        const parsed = parseEntry(id, rawData);
        parsedCache[id] = parsed;
        return parsed;
    }

    /**
     * Get the bounding box dimensions for a furniture item.
     * @param {string} itemId - The item ID
     * @param {number} rotation - Current rotation (0-3)
     * @returns {[number, number]} [width, height] in tiles
     */
    function getBoundingBox(itemId, rotation = 0) {
        const data = get(itemId);
        if (!data) {
            return [1, 1];
        }

        let width = data.boxWidth;
        let height = data.boxHeight;

        // For furniture with non-square bounding boxes, rotation 1 and 3 swap dimensions
        // Based on Furniture.cs updateRotation() logic
        if (width !== height && (rotation === 1 || rotation === 3)) {
            // Swap width and height
            // Note: game also applies adjustments based on furniture type, but we simplify here
            [width, height] = [height, width];
        }

        return [width, height];
    }

    /**
     * Get the sprite dimensions for a furniture item.
     * @param {string} itemId - The item ID
     * @returns {[number, number]} [width, height] in tiles
     */
    function getSpriteSize(itemId) {
        const data = get(itemId);
        if (data) {
            return [data.spriteWidth, data.spriteHeight];
        }
        // Fallback for unknown items
        return [1, 2];
    }

    /**
     * Get all loaded furniture IDs.
     * @returns {string[]}
     */
    function getAllIds() {
        return furnitureData ? Object.keys(furnitureData) : [];
    }

    /**
     * Get the source rectangle for a furniture sprite.
     * @param {string} itemId - The item ID
     * @param {number} textureWidth - Width of the texture in pixels (default 512 for furniture.png)
     * @param {number} rotation - Current rotation (0-3)
     * @returns {Object} { x, y, width, height } in pixels
     */
    function getSourceRect(itemId, textureWidth = DEFAULT_TEXTURE_WIDTH, rotation = 0) {
        const data = get(itemId);
        if (!data) {
            return { x: 0, y: 0, width: 16, height: 32 };
        }

        const spriteIndex = data.spriteIndex;
        let spriteWidthPx = data.spriteWidth * 16;
        let spriteHeightPx = data.spriteHeight * 16;

        // Calculate base position in spritesheet
        // Formula: x = spriteIndex * 16 % textureWidth, y = floor(spriteIndex * 16 / textureWidth) * 16
        let x = (spriteIndex * 16) % textureWidth;
        const y = Math.floor((spriteIndex * 16) / textureWidth) * 16;

        // For non-square bounding boxes, rotation changes sprite dimensions and position
        const isNonSquare = data.boxWidth !== data.boxHeight;

        if (isNonSquare) {
            // From Furniture.cs updateRotation() for flag2 (non-square) furniture:
            // Rotation 1 & 3: sprite source is at x + defaultWidth, with swapped dimensions
            // Rotation 2: sprite source is at x + defaultWidth + rotatedWidth
            if (rotation === 1 || rotation === 3) {
                x += spriteWidthPx;
                // Swap dimensions for rotated sprite
                [spriteWidthPx, spriteHeightPx] = [spriteHeightPx, spriteWidthPx];
            } else if (rotation === 2) {
                // Rotation 2: skip past rotation 0 sprite AND rotation 1 sprite
                x += spriteWidthPx + spriteHeightPx; // original width + rotated width (which is original height)
            }
        } else {
            // Square bounding box - simpler rotation handling
            // - 2 rotations: rotation 2 uses sprite at x + width
            // - 4 rotations: rotation 0 = base, rotation 1 = +width, rotation 2 = +2*width, rotation 3 = +width (but flipped)
            if (data.rotations === 2 && rotation === 2) {
                x += spriteWidthPx;
            } else if (data.rotations === 4) {
                // Rotation 3 uses same sprite as rotation 1 but flipped (handled in renderer)
                const spriteRotation = (rotation === 3) ? 1 : rotation;
                x += spriteRotation * spriteWidthPx;
            }
        }

        return {
            x,
            y,
            width: spriteWidthPx,
            height: spriteHeightPx
        };
    }

    /**
     * Get the texture path for a furniture item.
     * @param {string} itemId - The item ID
     * @returns {string} Texture path (e.g., "TileSheets/furniture")
     */
    function getTexture(itemId) {
        const data = get(itemId);
        return data?.texture || DEFAULT_TEXTURE;
    }

    return {
        load,
        setData,
        isLoaded,
        get,
        getBoundingBox,
        getSpriteSize,
        getSourceRect,
        getTexture,
        getAllIds,
        getTypeNumber,
        DEFAULT_BOUNDING_BOXES,
        DEFAULT_SPRITE_SIZES,
        DEFAULT_TEXTURE
    };
})();
