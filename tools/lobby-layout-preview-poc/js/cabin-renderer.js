/**
 * Cabin renderer for Stardew Valley lobby layouts.
 * Renders a 2D tile-based preview of cabin layouts with furniture.
 */
const CabinRenderer = (function() {
    'use strict';

    const TILE_SIZE = 32;

    // Tile types for the floor plan
    const TILE = {
        VOID: 0,       // Outside/void area (not part of room)
        SOLID_WALL: 1, // Solid wall (boundaries - top, left, right, bottom)
        WALL_DECOR: 2, // Wall decoration area (can place wall furniture like paintings)
        FLOOR: 3,      // Walkable floor
        DOOR: 4        // Door tile (walkable)
    };

    // Cabin dimensions and layout info for each upgrade level
    // Based on FarmHouse.tmx map and FarmHouse.cs getEntryLocation()
    // Map structure (upgrade 0, 12x12):
    //   y=0: top solid wall
    //   y=1-2: wall decoration area (for paintings etc)
    //   y=3: wall trim/baseboard (visual, part of wall decor area for game)
    //   y=4-10: walkable floor
    //   y=11: bottom solid wall with door opening
    //   x=0: left solid wall
    //   x=11: right solid wall
    const CABIN_DIMENSIONS = {
        0: {
            width: 12,
            height: 12,
            name: 'Starter Cabin',
            wallDecorArea: { x: 1, y: 1, width: 10, height: 3 }, // Wall decoration (y=1-3, where paintings go)
            floorArea: { x: 1, y: 4, width: 10, height: 7 },     // Walkable floor (y=4-10)
            doorTile: { x: 3, y: 11 }                             // Entry point
        },
        1: {
            width: 30,
            height: 12,
            name: 'Upgraded Cabin',
            wallDecorArea: { x: 1, y: 1, width: 28, height: 3 },
            floorArea: { x: 1, y: 4, width: 28, height: 7 },
            doorTile: { x: 9, y: 11 }
        },
        2: {
            width: 40,
            height: 32,
            name: 'Full House',
            wallDecorArea: { x: 1, y: 1, width: 38, height: 3 },
            floorArea: { x: 1, y: 4, width: 38, height: 27 },
            doorTile: { x: 27, y: 30 }
        }
    };

    /**
     * Generate a floor plan based on cabin dimensions.
     * @param {Object} config - Cabin dimension config
     * @returns {number[][]} 2D array of tile types
     */
    function generateFloorPlan(config) {
        const plan = [];

        for (let y = 0; y < config.height; y++) {
            const row = [];
            for (let x = 0; x < config.width; x++) {
                // Check if this is the door tile
                if (x === config.doorTile.x && y === config.doorTile.y) {
                    row.push(TILE.DOOR);
                }
                // Check if within floor area (walkable)
                else if (x >= config.floorArea.x &&
                         x < config.floorArea.x + config.floorArea.width &&
                         y >= config.floorArea.y &&
                         y < config.floorArea.y + config.floorArea.height) {
                    row.push(TILE.FLOOR);
                }
                // Check if within wall decoration area (for paintings etc.)
                else if (x >= config.wallDecorArea.x &&
                         x < config.wallDecorArea.x + config.wallDecorArea.width &&
                         y >= config.wallDecorArea.y &&
                         y < config.wallDecorArea.y + config.wallDecorArea.height) {
                    row.push(TILE.WALL_DECOR);
                }
                // Check if on boundary (solid wall)
                else if (x === 0 || x === config.width - 1 || y === 0 || y === config.height - 1) {
                    row.push(TILE.SOLID_WALL);
                }
                // Everything else is void (outside the room)
                else {
                    row.push(TILE.VOID);
                }
            }
            plan.push(row);
        }

        return plan;
    }

    // Grid padding around the cabin (in tiles)
    const GRID_PADDING = 3;

    // Rendering colors
    const COLORS = {
        void: '#1a1a1a',
        voidGrid: '#2a2a2a',       // Slightly lighter for grid lines
        solidWall: '#5D4037',      // Dark brown for solid walls
        floor: '#DEB887',          // Tan for floor
        door: '#A0522D',           // Door color
        furniture: '#4CAF50',
        furnitureBorder: '#388E3C',
        tableWithItem: '#FF9800',
        tableWithItemBorder: '#F57C00',
        object: '#2196F3',
        objectBorder: '#1565C0',
        spawnPoint: '#E91E63',
        spawnPointBorder: '#AD1457'
    };

    // Cache for generated floor plans (keyed by upgrade level)
    const layoutCache = {};

    /**
     * Get the floor plan for a given upgrade level.
     * @param {number} upgradeLevel - The cabin upgrade level (0-2)
     * @returns {Object} Floor plan with dimensions
     */
    function getCabinLayout(upgradeLevel) {
        if (layoutCache[upgradeLevel]) return layoutCache[upgradeLevel];
        const config = CABIN_DIMENSIONS[upgradeLevel] || CABIN_DIMENSIONS[0];
        const plan = generateFloorPlan(config);
        const result = { plan, ...config };
        layoutCache[upgradeLevel] = result;
        return result;
    }

    /**
     * Draw the infinite void grid background.
     * @param {CanvasRenderingContext2D} ctx - Canvas context
     * @param {number} canvasWidth - Canvas width in pixels
     * @param {number} canvasHeight - Canvas height in pixels
     * @param {number} tileSize - Size of each tile in pixels
     */
    function drawVoidGrid(ctx, canvasWidth, canvasHeight, tileSize, offsetX = 0, offsetY = 0) {
        // Fill background
        ctx.fillStyle = COLORS.void;
        ctx.fillRect(0, 0, canvasWidth, canvasHeight);

        // Draw grid lines aligned to pan offset
        ctx.strokeStyle = COLORS.voidGrid;
        ctx.lineWidth = 1;

        // Vertical lines (offset so grid moves with pan)
        const startX = ((offsetX % tileSize) + tileSize) % tileSize;
        for (let x = startX; x <= canvasWidth; x += tileSize) {
            ctx.beginPath();
            ctx.moveTo(x, 0);
            ctx.lineTo(x, canvasHeight);
            ctx.stroke();
        }

        // Horizontal lines
        const startY = ((offsetY % tileSize) + tileSize) % tileSize;
        for (let y = startY; y <= canvasHeight; y += tileSize) {
            ctx.beginPath();
            ctx.moveTo(0, y);
            ctx.lineTo(canvasWidth, y);
            ctx.stroke();
        }
    }

    // Texture constants for walls_and_floors.png
    const WALLS_AND_FLOORS_TEXTURE = 'Maps/walls_and_floors';
    const WALLPAPER_TILE_WIDTH = 16;
    const WALLPAPER_TILE_HEIGHT = 48;  // Full wall height (3 rows)
    const WALLPAPER_COLS = 16;
    const FLOOR_TILE_SIZE = 32;
    const FLOOR_COLS = 8;
    const FLOOR_START_Y = 336;

    /**
     * Get source rectangle for a wallpaper index.
     * @param {number} index - Wallpaper index
     * @returns {Object} { x, y, width, height }
     */
    function getWallpaperSourceRect(index) {
        const col = index % WALLPAPER_COLS;
        const row = Math.floor(index / WALLPAPER_COLS);
        return {
            x: col * WALLPAPER_TILE_WIDTH,
            y: row * WALLPAPER_TILE_HEIGHT,
            width: WALLPAPER_TILE_WIDTH,
            height: WALLPAPER_TILE_HEIGHT
        };
    }

    /**
     * Get source rectangle for a floor index.
     * @param {number} index - Floor index
     * @returns {Object} { x, y, width, height }
     */
    function getFloorSourceRect(index) {
        const col = index % FLOOR_COLS;
        const row = Math.floor(index / FLOOR_COLS);
        return {
            x: col * FLOOR_TILE_SIZE,
            y: FLOOR_START_Y + row * FLOOR_TILE_SIZE,
            width: FLOOR_TILE_SIZE,
            height: FLOOR_TILE_SIZE
        };
    }

    /**
     * Draw the cabin floor plan with textures.
     * @param {CanvasRenderingContext2D} ctx - Canvas context
     * @param {number[][]} plan - Floor plan array
     * @param {number} tileSize - Size of each tile in pixels
     * @param {number} offsetX - X offset for cabin position
     * @param {number} offsetY - Y offset for cabin position
     * @param {Object} layout - Layout object with wallpaper/floor IDs
     */
    function drawFloorPlan(ctx, plan, tileSize, offsetX = 0, offsetY = 0, layout = {}, snap = null) {
        const texture = SpriteLoader.getTexture(WALLS_AND_FLOORS_TEXTURE);

        // Get wallpaper and floor IDs from layout (default to 0)
        const wallpapers = layout.Wallpapers || layout.Wallpaper || {};
        const floors = layout.Floors || layout.Flooring || {};
        const wallpaperId = parseInt(wallpapers.Main) || 0;
        const floorId = parseInt(floors.Main) || 0;

        const cabinWidth = plan[0]?.length || 12;

        // Tile bounds helper — uses device-pixel snapping if available, else identity
        function tileBounds(tx, ty, tw, th) {
            if (snap) return snap(tx, ty, tw, th);
            return {
                x: offsetX + tx * tileSize,
                y: offsetY + ty * tileSize,
                w: tw * tileSize,
                h: th * tileSize
            };
        }

        // Draw wallpaper on wall decoration area (y=1 to y=3, which is 3 tiles high)
        // The wallpaper texture is 16x48 (16 wide, 48 tall for 3 rows at 16px each)
        if (texture) {
            const wallSource = getWallpaperSourceRect(wallpaperId);

            for (let x = 1; x < cabinWidth - 1; x++) {
                const s = tileBounds(x, 1, 1, 3);
                ctx.drawImage(
                    texture,
                    wallSource.x, wallSource.y,
                    wallSource.width, wallSource.height,
                    s.x, s.y,
                    s.w, s.h
                );
            }
        }

        // Draw floor tiles and remaining structure
        for (let y = 0; y < plan.length; y++) {
            for (let x = 0; x < plan[y].length; x++) {
                const tile = plan[y][x];

                // Skip void tiles (already drawn by grid)
                if (tile === TILE.VOID) continue;

                // Skip wall decor area (already drawn with wallpaper)
                if (tile === TILE.WALL_DECOR) continue;

                const s = tileBounds(x, y, 1, 1);

                if (tile === TILE.FLOOR || tile === TILE.DOOR) {
                    // Draw floor texture
                    if (texture) {
                        const floorSource = getFloorSourceRect(floorId);
                        // Floor tiles are 32x32 in source, we tile them at 16x16 game scale
                        const srcSubX = (x % 2) * 16;
                        const srcSubY = (y % 2) * 16;
                        ctx.drawImage(
                            texture,
                            floorSource.x + srcSubX, floorSource.y + srcSubY,
                            16, 16,
                            s.x, s.y,
                            s.w, s.h
                        );
                    } else {
                        // Fallback color
                        ctx.fillStyle = tile === TILE.DOOR ? COLORS.door : COLORS.floor;
                        ctx.fillRect(s.x, s.y, s.w, s.h);
                    }
                } else if (tile === TILE.SOLID_WALL) {
                    // Solid walls (boundaries) - draw with dark color
                    ctx.fillStyle = COLORS.solidWall;
                    ctx.fillRect(s.x, s.y, s.w, s.h);
                }
            }
        }
    }

    /**
     * Draw a tile grid overlay on the cabin interior.
     * @param {CanvasRenderingContext2D} ctx - Canvas context
     * @param {Object} cabin - Cabin layout from getCabinLayout
     * @param {number} tileSize - Tile size in pixels
     * @param {number} offsetX - Cabin X offset
     * @param {number} offsetY - Cabin Y offset
     */
    function drawTileGrid(ctx, cabin, tileSize, offsetX, offsetY) {
        ctx.strokeStyle = 'rgba(255, 255, 255, 0.15)';
        ctx.lineWidth = 1;

        // Draw grid lines within the cabin bounds
        for (let x = 0; x <= cabin.width; x++) {
            const px = offsetX + x * tileSize;
            ctx.beginPath();
            ctx.moveTo(px, offsetY);
            ctx.lineTo(px, offsetY + cabin.height * tileSize);
            ctx.stroke();
        }
        for (let y = 0; y <= cabin.height; y++) {
            const py = offsetY + y * tileSize;
            ctx.beginPath();
            ctx.moveTo(offsetX, py);
            ctx.lineTo(offsetX + cabin.width * tileSize, py);
            ctx.stroke();
        }
    }

    /**
     * Draw a rectangle with fill and stroke.
     * @param {CanvasRenderingContext2D} ctx - Canvas context
     * @param {number} x - X position
     * @param {number} y - Y position
     * @param {number} width - Width
     * @param {number} height - Height
     * @param {string} fillColor - Fill color
     * @param {string} strokeColor - Stroke color
     * @param {number} padding - Inner padding
     */
    function drawRect(ctx, x, y, width, height, fillColor, strokeColor, padding = 2) {
        ctx.fillStyle = fillColor;
        ctx.fillRect(x + padding, y + padding, width - padding * 2, height - padding * 2);
        ctx.strokeStyle = strokeColor;
        ctx.lineWidth = 2;
        ctx.strokeRect(x + padding, y + padding, width - padding * 2, height - padding * 2);
    }

    /**
     * Draw a star shape (for spawn point).
     * @param {CanvasRenderingContext2D} ctx - Canvas context
     * @param {number} centerX - Center X
     * @param {number} centerY - Center Y
     * @param {number} outerRadius - Outer radius
     * @param {number} innerRadius - Inner radius
     * @param {string} fillColor - Fill color
     * @param {string} strokeColor - Stroke color
     */
    function drawStar(ctx, centerX, centerY, outerRadius, innerRadius, fillColor, strokeColor) {
        ctx.fillStyle = fillColor;
        ctx.beginPath();
        for (let i = 0; i < 10; i++) {
            const radius = i % 2 === 0 ? outerRadius : innerRadius;
            const angle = (i * Math.PI / 5) - Math.PI / 2;
            const x = centerX + Math.cos(angle) * radius;
            const y = centerY + Math.sin(angle) * radius;
            if (i === 0) {
                ctx.moveTo(x, y);
            } else {
                ctx.lineTo(x, y);
            }
        }
        ctx.closePath();
        ctx.fill();
        ctx.strokeStyle = strokeColor;
        ctx.lineWidth = 2;
        ctx.stroke();
    }


    /**
     * Draw a furniture sprite.
     * @param {CanvasRenderingContext2D} ctx - Canvas context
     * @param {Object} furn - Furniture data { ItemId, TileX, TileY, Rotation }
     * @param {number} tileSize - Size of a tile in pixels
     * @param {number} offsetX - X offset for drawing position
     * @param {number} offsetY - Y offset for drawing position
     * @returns {boolean} True if sprite was drawn
     */
    function drawFurnitureSprite(ctx, furn, tileSize, offsetX = 0, offsetY = 0) {
        const data = FurnitureParser.get(furn.ItemId);
        if (!data) {
            console.warn(`[CabinRenderer] No furniture data for: ${furn.ItemId}`);
            return false;
        }

        const rotation = furn.Rotation || 0;

        // Get rotated bounding box dimensions
        const [boxWidth, boxHeight] = FurnitureParser.getBoundingBox(furn.ItemId, rotation);

        // Get sprite dimensions - these also change with rotation for non-square furniture
        let spriteWidth = data.spriteWidth;
        let spriteHeight = data.spriteHeight;

        // For non-square bounding boxes at rotation 1 or 3, sprite dimensions are swapped
        // Based on Furniture.cs updateRotation() - sourceRect width/height swap
        if (data.boxWidth !== data.boxHeight && (rotation === 1 || rotation === 3)) {
            [spriteWidth, spriteHeight] = [spriteHeight, spriteWidth];
        }

        // Calculate destination position
        // Sprite is anchored at bottom of bounding box
        const destX = offsetX + furn.TileX * tileSize;
        const destY = offsetY + furn.TileY * tileSize - (spriteHeight - boxHeight) * tileSize;

        // Scale factor: tileSize / 16 (game uses 16px tiles)
        const scale = tileSize / 16;

        const isTextureLoaded = SpriteLoader.isLoaded(data.texture);
        if (isTextureLoaded) {
            // Get source rectangle with rotation applied
            const textureWidth = SpriteLoader.getTextureWidth(data.texture);
            const sourceRect = FurnitureParser.getSourceRect(furn.ItemId, textureWidth, rotation);

            // Rotation 3 uses same sprite as rotation 1 but horizontally flipped
            const flipped = (data.rotations === 4 && rotation === 3);

            // Draw sprite
            return SpriteLoader.drawSprite(
                ctx,
                data.texture,
                sourceRect,
                destX,
                destY,
                scale,
                flipped
            );
        }

        console.warn(`[CabinRenderer] Texture not loaded for ${furn.ItemId}: "${data.texture}" (spriteIndex: ${data.spriteIndex})`);
        return false;
    }

    /**
     * Draw an error sprite (red circle with diagonal line - "no entry" sign).
     * Used for invalid/unknown item IDs, matching Stardew Valley's error sprite.
     * @param {CanvasRenderingContext2D} ctx - Canvas context
     * @param {number} x - X position
     * @param {number} y - Y position
     * @param {number} size - Size of the sprite
     */
    function drawErrorSprite(ctx, x, y, size) {
        const centerX = x + size / 2;
        const centerY = y + size / 2;
        const radius = size * 0.4;
        const lineWidth = size * 0.12;

        // Red circle
        ctx.beginPath();
        ctx.arc(centerX, centerY, radius, 0, Math.PI * 2);
        ctx.fillStyle = '#CC0000';
        ctx.fill();
        ctx.strokeStyle = '#880000';
        ctx.lineWidth = lineWidth * 0.5;
        ctx.stroke();

        // White diagonal line (from top-right to bottom-left)
        ctx.beginPath();
        const offset = radius * 0.65;
        ctx.moveTo(centerX + offset, centerY - offset);
        ctx.lineTo(centerX - offset, centerY + offset);
        ctx.strokeStyle = '#FFFFFF';
        ctx.lineWidth = lineWidth;
        ctx.lineCap = 'round';
        ctx.stroke();
    }

    /**
     * Check if an item ID is invalid/unknown (should show error sprite).
     * @param {string} itemId - The item ID to check
     * @returns {boolean} True if the item ID is invalid
     */
    function isInvalidItemId(itemId) {
        // Check for JunimoServer namespace (our custom invalid IDs)
        if (itemId && itemId.includes('JunimoServer.')) return true;
        return false;
    }

    /**
     * Draw a BigCraftable object sprite.
     * @param {CanvasRenderingContext2D} ctx - Canvas context
     * @param {Object} obj - Object data { ItemId, TileX, TileY }
     * @param {number} tileSize - Size of a tile in pixels
     * @param {number} offsetX - X offset for drawing position
     * @param {number} offsetY - Y offset for drawing position
     * @returns {boolean} True if sprite was drawn
     */
    function drawObjectSprite(ctx, obj, tileSize, offsetX = 0, offsetY = 0) {
        // Check for invalid item IDs first - draw error sprite
        if (isInvalidItemId(obj.ItemId)) {
            const destX = offsetX + obj.TileX * tileSize;
            const destY = offsetY + obj.TileY * tileSize;
            drawErrorSprite(ctx, destX, destY, tileSize);
            return true;
        }

        if (typeof BigCraftablesParser === 'undefined' || !BigCraftablesParser.isLoaded()) {
            return false;
        }

        const data = BigCraftablesParser.get(obj.ItemId);
        if (!data) return false;

        const textureWidth = SpriteLoader.getTextureWidth(data.texture);
        const sourceRect = BigCraftablesParser.getSourceRect(obj.ItemId, textureWidth);
        if (!sourceRect) return false;

        if (!SpriteLoader.isLoaded(data.texture)) return false;

        const scale = tileSize / 16;
        // BigCraftables are 1 tile wide, 2 tiles tall (16x32)
        // Bounding box is 1x1 tile, sprite extends 1 tile above
        const destX = offsetX + obj.TileX * tileSize;
        const destY = offsetY + (obj.TileY - 1) * tileSize;

        return SpriteLoader.drawSprite(ctx, data.texture, sourceRect, destX, destY, scale);
    }

    /**
     * Get information about what's at a specific pixel position.
     * @param {Object} layout - The layout object
     * @param {number} pixelX - X position in pixels
     * @param {number} pixelY - Y position in pixels
     * @param {number} zoomScale - Current zoom scale
     * @returns {Object|null} Information about the item at position, or null
     */
    function getItemAtPosition(layout, pixelX, pixelY, zoomScale = 1) {
        const tileSize = TILE_SIZE * zoomScale;
        const paddingPx = GRID_PADDING * tileSize;

        // Adjust for grid padding offset
        const adjustedX = pixelX - paddingPx;
        const adjustedY = pixelY - paddingPx;

        const tileX = Math.floor(adjustedX / tileSize);
        const tileY = Math.floor(adjustedY / tileSize);
        const cabin = getCabinLayout(layout.UpgradeLevel || 0);

        // Check spawn point first
        const spawnX = layout.SpawnX ?? 3;
        const spawnY = layout.SpawnY ?? 11;
        if (tileX === spawnX && tileY === spawnY) {
            return {
                type: 'spawn',
                name: 'Spawn Point',
                description: `Player spawn location (${spawnX}, ${spawnY})`,
                tileX: spawnX,
                tileY: spawnY
            };
        }

        // Check furniture (reverse order to get topmost first)
        // Hit-test against full sprite area, not just bounding box
        if (layout.Furniture) {
            for (let i = layout.Furniture.length - 1; i >= 0; i--) {
                const furn = layout.Furniture[i];
                const rotation = furn.Rotation || 0;
                const [boxWidth, boxHeight] = FurnitureParser.getBoundingBox(furn.ItemId, rotation);
                const data = FurnitureParser.get(furn.ItemId);

                // Calculate sprite dimensions (may be taller than bounding box)
                let spriteWidth = data?.spriteWidth || boxWidth;
                let spriteHeight = data?.spriteHeight || boxHeight;
                if (data && data.boxWidth !== data.boxHeight && (rotation === 1 || rotation === 3)) {
                    [spriteWidth, spriteHeight] = [spriteHeight, spriteWidth];
                }

                // Sprite top-left in tile coords (sprite anchored at bottom of bounding box)
                const spriteTileX = furn.TileX;
                const spriteTileY = furn.TileY - (spriteHeight - boxHeight);

                // Hit-test against the full sprite area
                const inSprite = tileX >= spriteTileX && tileX < spriteTileX + spriteWidth &&
                                 tileY >= spriteTileY && tileY < spriteTileY + spriteHeight;
                // Also hit-test bounding box (in case sprite is narrower somehow)
                const inBox = tileX >= furn.TileX && tileX < furn.TileX + boxWidth &&
                              tileY >= furn.TileY && tileY < furn.TileY + boxHeight;

                if (inSprite || inBox) {
                    const name = data?.name || 'Unknown Furniture';
                    const type = data?.type || 'unknown';

                    let description = `${name} (${furn.ItemId})`;
                    description += `\nType: ${type}`;
                    description += `\nPosition: (${furn.TileX}, ${furn.TileY})`;
                    description += `\nSize: ${boxWidth}x${boxHeight}`;
                    if (furn.Rotation) {
                        description += `\nRotation: ${furn.Rotation}`;
                    }
                    if (furn.HeldObjectId) {
                        description += `\nHolding: ${furn.HeldObjectId}`;
                    }

                    return {
                        type: 'furniture',
                        itemId: furn.ItemId,
                        name,
                        furnitureType: type,
                        description,
                        tileX: furn.TileX,
                        tileY: furn.TileY,
                        width: boxWidth,
                        height: boxHeight,
                        spriteTileX,
                        spriteTileY,
                        spriteWidth,
                        spriteHeight,
                        data: furn
                    };
                }
            }
        }

        // Check objects (also hit-test the sprite area 1 tile above)
        if (layout.Objects) {
            for (const obj of layout.Objects) {
                const inBox = tileX === obj.TileX && tileY === obj.TileY;
                const inSprite = tileX === obj.TileX && tileY === obj.TileY - 1;
                if (inBox || inSprite) {
                    const bcData = (typeof BigCraftablesParser !== 'undefined') ? BigCraftablesParser.get(obj.ItemId) : null;
                    const name = bcData?.name || obj.ItemId;
                    return {
                        type: 'object',
                        itemId: obj.ItemId,
                        name,
                        description: `${name} (${obj.ItemId})\nPosition: (${obj.TileX}, ${obj.TileY})`,
                        tileX: obj.TileX,
                        tileY: obj.TileY,
                        data: obj
                    };
                }
            }
        }

        // Check floor plan tile
        if (tileX >= 0 && tileX < cabin.width && tileY >= 0 && tileY < cabin.height) {
            const tileType = cabin.plan[tileY]?.[tileX];
            const tileNames = {
                [TILE.VOID]: 'Void',
                [TILE.SOLID_WALL]: 'Wall',
                [TILE.WALL_DECOR]: 'Wall (decor)',
                [TILE.FLOOR]: 'Floor',
                [TILE.DOOR]: 'Door'
            };
            const tileName = tileNames[tileType] || 'Unknown';

            return {
                type: 'tile',
                name: tileName,
                description: `${tileName}\nPosition: (${tileX}, ${tileY})`,
                tileX,
                tileY,
                tileType
            };
        }

        return null;
    }

    /**
     * Render a cabin layout to the canvas.
     * @param {HTMLCanvasElement} canvas
     * @param {Object} layout - Decoded layout object
     * @param {number} zoomScale - Zoom multiplier
     * @param {Object} [viewport] - Optional viewport config for pan/zoom mode
     * @param {number} viewport.width - CSS pixel width of the container
     * @param {number} viewport.height - CSS pixel height of the container
     * @param {number} viewport.panX - Pan offset X in CSS pixels
     * @param {number} viewport.panY - Pan offset Y in CSS pixels
     */
    function render(canvas, layout, zoomScale = 1, viewport = null) {
        const ctx = canvas.getContext('2d');
        const cabin = getCabinLayout(layout.UpgradeLevel || 0);
        const tileSize = TILE_SIZE * zoomScale;

        // Calculate scene size with padding for void grid
        const paddingPx = GRID_PADDING * tileSize;
        const sceneWidth = cabin.width * tileSize + paddingPx * 2;
        const sceneHeight = cabin.height * tileSize + paddingPx * 2;

        if (viewport) {
            // Viewport mode: canvas matches container, HiDPI aware
            const dpr = window.devicePixelRatio || 1;
            canvas.width = viewport.width * dpr;
            canvas.height = viewport.height * dpr;
            ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
            ctx.imageSmoothingEnabled = false;

            // Clear the full viewport with void background
            drawVoidGrid(ctx, viewport.width, viewport.height, tileSize, viewport.panX, viewport.panY);

            // Translate to pan offset for all scene drawing
            ctx.save();
            ctx.translate(viewport.panX, viewport.panY);
        } else {
            // Legacy mode: canvas sized to scene (for thumbnails)
            canvas.width = sceneWidth;
            canvas.height = sceneHeight;
            ctx.imageSmoothingEnabled = false;
            drawVoidGrid(ctx, sceneWidth, sceneHeight, tileSize);
        }

        // Cabin offset (centered in the void grid)
        const offsetX = paddingPx;
        const offsetY = paddingPx;

        // Build a snap function that rounds tile bounds to device pixels.
        // In viewport mode the canvas transform is dpr * (panX + logical_coord),
        // so we must snap in that space then convert back to logical coords.
        const dpr = viewport ? (window.devicePixelRatio || 1) : 1;
        const originX = viewport ? viewport.panX + offsetX : offsetX;
        const originY = viewport ? viewport.panY + offsetY : offsetY;
        function snapTile(tx, ty, tw, th) {
            // Compute device-pixel positions for tile start and end
            const dx0 = Math.round((originX + tx * tileSize) * dpr) / dpr;
            const dy0 = Math.round((originY + ty * tileSize) * dpr) / dpr;
            const dx1 = Math.round((originX + (tx + tw) * tileSize) * dpr) / dpr;
            const dy1 = Math.round((originY + (ty + th) * tileSize) * dpr) / dpr;
            // Return in local coords (relative to the current ctx translate)
            return {
                x: dx0 - (viewport ? viewport.panX : 0),
                y: dy0 - (viewport ? viewport.panY : 0),
                w: dx1 - dx0,
                h: dy1 - dy0
            };
        }

        // Draw floor plan with textures (offset by padding)
        drawFloorPlan(ctx, cabin.plan, tileSize, offsetX, offsetY, layout, snapTile);

        // Draw objects (BigCraftables)
        if (layout.Objects) {
            for (const obj of layout.Objects) {
                const spriteDrawn = drawObjectSprite(ctx, obj, tileSize, offsetX, offsetY);
                if (!spriteDrawn) {
                    drawRect(
                        ctx,
                        offsetX + obj.TileX * tileSize,
                        offsetY + obj.TileY * tileSize,
                        tileSize,
                        tileSize,
                        COLORS.object,
                        COLORS.objectBorder,
                        4
                    );
                }
            }
        }

        // Draw furniture
        if (layout.Furniture) {
            for (const furn of layout.Furniture) {
                const rotation = furn.Rotation || 0;
                const [boxWidth, boxHeight] = FurnitureParser.getBoundingBox(furn.ItemId, rotation);
                const hasHeldItem = furn.HeldObjectId != null;

                // Try to draw sprite first
                const spriteDrawn = drawFurnitureSprite(ctx, furn, tileSize, offsetX, offsetY);

                // Fall back to colored rectangle if sprite not available
                if (!spriteDrawn) {
                    let fillColor, strokeColor;
                    if (hasHeldItem) {
                        fillColor = COLORS.tableWithItem;
                        strokeColor = COLORS.tableWithItemBorder;
                    } else {
                        fillColor = COLORS.furniture;
                        strokeColor = COLORS.furnitureBorder;
                    }

                    drawRect(
                        ctx,
                        offsetX + furn.TileX * tileSize,
                        offsetY + furn.TileY * tileSize,
                        boxWidth * tileSize,
                        boxHeight * tileSize,
                        fillColor,
                        strokeColor
                    );

                    // Draw rotation indicator for fallback
                    if (rotation !== 0) {
                        ctx.fillStyle = 'rgba(255, 255, 255, 0.6)';
                        ctx.font = `bold ${10 * zoomScale}px Arial`;
                        ctx.fillText(
                            `R${rotation}`,
                            offsetX + furn.TileX * tileSize + 4,
                            offsetY + furn.TileY * tileSize + 12 * zoomScale
                        );
                    }
                }

            }
        }

        // Draw spawn point
        const spawnX = layout.SpawnX ?? 3;
        const spawnY = layout.SpawnY ?? 11;
        drawStar(
            ctx,
            offsetX + (spawnX + 0.5) * tileSize,
            offsetY + (spawnY + 0.5) * tileSize,
            tileSize * 0.4,
            tileSize * 0.2,
            COLORS.spawnPoint,
            COLORS.spawnPointBorder
        );

        // Draw tile grid overlay on cabin floor
        if (viewport?.showGrid) {
            drawTileGrid(ctx, cabin, tileSize, offsetX, offsetY);
        }

        if (viewport) {
            ctx.restore();
        }
    }

    /**
     * Draw a highlight on an item: colored overlay on the sprite, border on the bounding box.
     * Call after render() to overlay without a full re-render.
     * @param {HTMLCanvasElement} canvas - The canvas element
     * @param {Object} item - Item info from getItemAtPosition
     * @param {number} zoomScale - Current zoom scale
     * @param {number} alpha - Highlight opacity (0-1), for transition animation
     */
    function drawHighlight(canvas, item, zoomScale = 1, alpha = 1, viewport = null) {
        if (!item || item.type === 'tile' || alpha <= 0) return;

        const ctx = canvas.getContext('2d');
        const tileSize = TILE_SIZE * zoomScale;
        const paddingPx = GRID_PADDING * tileSize;

        if (viewport) {
            const dpr = window.devicePixelRatio || 1;
            ctx.save();
            ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
            ctx.translate(viewport.panX, viewport.panY);
        }

        const overlayAlpha = 0.15 * alpha;
        const borderAlpha = alpha;

        if (item.type === 'furniture') {
            // Colored overlay on the full sprite area
            const sx = paddingPx + item.spriteTileX * tileSize;
            const sy = paddingPx + item.spriteTileY * tileSize;
            const sw = item.spriteWidth * tileSize;
            const sh = item.spriteHeight * tileSize;

            ctx.fillStyle = `rgba(255, 255, 255, ${overlayAlpha})`;
            ctx.fillRect(sx, sy, sw, sh);

            // Border on the bounding box
            const bx = paddingPx + item.tileX * tileSize;
            const by = paddingPx + item.tileY * tileSize;
            const bw = item.width * tileSize;
            const bh = item.height * tileSize;

            ctx.strokeStyle = `rgba(255, 255, 255, ${borderAlpha})`;
            ctx.lineWidth = 2;
            ctx.strokeRect(bx + 1, by + 1, bw - 2, bh - 2);
        } else {
            // Objects and spawn: overlay + border on same area
            const x = paddingPx + item.tileX * tileSize;
            const y = paddingPx + item.tileY * tileSize;
            const w = tileSize;
            const h = tileSize;

            ctx.fillStyle = `rgba(255, 255, 255, ${overlayAlpha})`;
            ctx.fillRect(x, y, w, h);

            ctx.strokeStyle = `rgba(255, 255, 255, ${borderAlpha})`;
            ctx.lineWidth = 2;
            ctx.strokeRect(x + 1, y + 1, w - 2, h - 2);
        }

        if (viewport) {
            ctx.restore();
        }
    }

    return {
        render,
        drawHighlight,
        getCabinLayout,
        getItemAtPosition,
        CABIN_DIMENSIONS,
        TILE_SIZE,
        GRID_PADDING,
        COLORS
    };
})();
