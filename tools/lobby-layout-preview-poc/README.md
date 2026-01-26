# Cabin Layout Gallery (POC)

A web-based gallery and preview tool for Stardew Valley Dedicated Server cabin layouts.

## Usage

1. Serve the files via a local web server (required for loading furniture data):
   ```bash
   # From the repository root
   npx serve .
   # Then open http://localhost:3000/tools/lobby-layout-preview-poc/
   ```

2. Browse the gallery of shared layouts
3. Click a card to open the detailed preview
4. Use "Copy Code" to get the import code for in-game use

## Project Structure

```
lobby-layout-preview-poc/
├── index.html           # Main HTML file
├── styles.css           # Stylesheet
├── data/
│   └── layouts.json     # Layout gallery data
├── js/
│   ├── furniture-parser.js  # Parses game's Furniture.json format
│   ├── sprite-loader.js     # Loads and caches sprite textures
│   ├── layout-decoder.js    # Decodes SDVL0 export format
│   ├── cabin-renderer.js    # Renders cabin layouts to canvas
│   └── app.js               # Main application logic (gallery + detail views)
└── README.md
```

## Features

- **Gallery view** with 4-column grid of layout cards with thumbnails
- **Detail view** with full-size preview, sidebar metadata, and export code
- Decodes SDVL0 format (gzip-compressed base64 JSON)
- Loads actual furniture data from the game's `Data/Furniture.json`
- **Sprite rendering** using actual game textures
- Floor and wall texture rendering from `walls_and_floors.png`
- Infinite void grid background around cabin
- Renders a 2D tile-based preview of the cabin
- Shows furniture with correct bounding box sizes and rotation
- Displays detailed item lists with names and positions
- Zoom controls (50% - 300%)
- Sprite toggle (switch between sprites and colored boxes)
- Hover tooltips with item details
- Search/filter layouts
- Upload modal (placeholder)
- Download layout (placeholder)

## Data Sources

The tool loads data from the unpacked game content:
- `../../decompiled/content-1.6.15-24356/Data/Furniture.json` - Furniture definitions
- `../../decompiled/content-1.6.15-24356/TileSheets/furniture.png` - Main furniture sprites
- `../../decompiled/content-1.6.15-24356/TileSheets/furniture_2.png` - Additional furniture
- `../../decompiled/content-1.6.15-24356/TileSheets/furniture_3.png` - More furniture
- `../../decompiled/content-1.6.15-24356/Maps/walls_and_floors.png` - Wall/floor textures
- Additional texture sheets for Joja, Wizard, Junimo, and Retro furniture

Layout data is stored in `data/layouts.json`.

## Future Improvements

- [x] Sprite rendering using actual game textures
- [x] Wallpaper/flooring texture rendering
- [x] Gallery view with layout browsing
- [ ] Backend API for upload/download
- [ ] Support for all cabin upgrade levels with accurate floor plans
- [ ] Export to image
- [ ] Drag-and-drop layout editing
- [ ] Object sprite rendering (BigCraftables)
