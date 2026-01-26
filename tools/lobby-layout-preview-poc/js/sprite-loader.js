/**
 * Sprite loader and cache for Stardew Valley textures.
 * Handles loading sprite textures and provides drawing utilities.
 */
const SpriteLoader = (function() {
    'use strict';

    // Base path for textures (relative to index.html)
    const TEXTURE_BASE_PATH = '../../decompiled/content-1.6.15-24356/';

    // Texture cache: path -> HTMLImageElement
    const textureCache = {};

    // Loading promises to avoid duplicate loads
    const loadingPromises = {};

    // Texture width cache (determined after loading)
    const textureWidths = {
        'TileSheets/furniture': 512,
        'TileSheets/furniture_2': 256,
        'TileSheets/furniture_3': 256,
        'TileSheets/joja_furniture': 256,
        'TileSheets/wizard_furniture': 256,
        'TileSheets/junimo_furniture': 256,
        'TileSheets/retro_furniture': 256,
        'TileSheets/Craftables': 128
    };

    /**
     * Normalize a texture path (convert backslashes to forward slashes).
     * @param {string} texturePath - Game texture path
     * @returns {string} Normalized path
     */
    function normalizePath(texturePath) {
        return texturePath.replace(/\\/g, '/');
    }

    /**
     * Convert a game texture path to an actual file path.
     * @param {string} texturePath - Game texture path (e.g., "TileSheets/furniture")
     * @returns {string} Actual file path
     */
    function getFilePath(texturePath) {
        // Normalize path separators and add .png extension
        const normalized = texturePath.replace(/\\/g, '/');
        return TEXTURE_BASE_PATH + normalized + '.png';
    }

    /**
     * Load a texture and cache it.
     * @param {string} texturePath - Game texture path
     * @returns {Promise<HTMLImageElement>} The loaded image
     */
    async function loadTexture(texturePath) {
        const normalizedPath = normalizePath(texturePath);

        // Check cache first
        if (textureCache[normalizedPath]) {
            return textureCache[normalizedPath];
        }

        // Check if already loading
        if (loadingPromises[normalizedPath]) {
            return loadingPromises[normalizedPath];
        }

        // Start loading
        const filePath = getFilePath(normalizedPath);
        console.log(`[SpriteLoader] Loading texture: ${normalizedPath} -> ${filePath}`);
        const promise = new Promise((resolve, reject) => {
            const img = new Image();
            img.onload = () => {
                console.log(`[SpriteLoader] ✓ Loaded texture: ${normalizedPath} (${img.width}x${img.height})`);
                textureCache[normalizedPath] = img;
                // Update texture width from actual image
                textureWidths[normalizedPath] = img.width;
                delete loadingPromises[normalizedPath];
                resolve(img);
            };
            img.onerror = (e) => {
                console.error(`[SpriteLoader] ✗ Failed to load texture: ${filePath}`, e);
                delete loadingPromises[normalizedPath];
                reject(new Error(`Failed to load texture: ${filePath}`));
            };
            img.src = filePath;
        });

        loadingPromises[normalizedPath] = promise;
        return promise;
    }

    /**
     * Get a cached texture (must be loaded first).
     * @param {string} texturePath - Game texture path
     * @returns {HTMLImageElement|null} The cached image or null
     */
    function getTexture(texturePath) {
        return textureCache[normalizePath(texturePath)] || null;
    }

    /**
     * Get the width of a texture.
     * @param {string} texturePath - Game texture path
     * @returns {number} Texture width in pixels
     */
    function getTextureWidth(texturePath) {
        return textureWidths[normalizePath(texturePath)] || 512;
    }

    /**
     * Check if a texture is loaded.
     * @param {string} texturePath - Game texture path
     * @returns {boolean}
     */
    function isLoaded(texturePath) {
        return normalizePath(texturePath) in textureCache;
    }

    /**
     * Preload multiple textures.
     * @param {string[]} texturePaths - Array of texture paths to load
     * @returns {Promise<void>}
     */
    async function preloadTextures(texturePaths) {
        const promises = texturePaths.map(path => loadTexture(path).catch(e => {
            console.warn(`Failed to preload texture: ${path}`, e);
            return null;
        }));
        await Promise.all(promises);
    }

    /**
     * Draw a sprite from a texture to a canvas.
     * @param {CanvasRenderingContext2D} ctx - Canvas context
     * @param {string} texturePath - Game texture path
     * @param {Object} sourceRect - Source rectangle { x, y, width, height }
     * @param {number} destX - Destination X position
     * @param {number} destY - Destination Y position
     * @param {number} scale - Scale factor (default 1)
     * @param {boolean} flipped - Whether to flip horizontally
     * @returns {boolean} True if drawn successfully
     */
    function drawSprite(ctx, texturePath, sourceRect, destX, destY, scale = 1, flipped = false) {
        const texture = textureCache[normalizePath(texturePath)];
        if (!texture) {
            return false;
        }

        const destWidth = sourceRect.width * scale;
        const destHeight = sourceRect.height * scale;

        if (flipped) {
            ctx.save();
            ctx.translate(destX + destWidth, destY);
            ctx.scale(-1, 1);
            ctx.drawImage(
                texture,
                sourceRect.x, sourceRect.y,
                sourceRect.width, sourceRect.height,
                0, 0,
                destWidth, destHeight
            );
            ctx.restore();
        } else {
            ctx.drawImage(
                texture,
                sourceRect.x, sourceRect.y,
                sourceRect.width, sourceRect.height,
                destX, destY,
                destWidth, destHeight
            );
        }

        return true;
    }

    /**
     * Get all loaded texture paths.
     * @returns {string[]}
     */
    function getLoadedTextures() {
        return Object.keys(textureCache);
    }

    return {
        loadTexture,
        getTexture,
        getTextureWidth,
        isLoaded,
        preloadTextures,
        drawSprite,
        getLoadedTextures,
        TEXTURE_BASE_PATH
    };
})();
