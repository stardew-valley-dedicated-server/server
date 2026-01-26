/**
 * Main application for the Lobby Layout Gallery.
 * Manages gallery view, detail view, and layout previewing.
 */
const App = (function() {
    'use strict';

    // Path to game data (relative to the HTML file)
    const FURNITURE_DATA_PATH = '../../decompiled/content-1.6.15-24356/Data/Furniture.json';
    const BIG_CRAFTABLES_DATA_PATH = '../../decompiled/content-1.6.15-24356/Data/BigCraftables.json';
    const LAYOUTS_DATA_PATH = 'data/layouts.json';

    // Textures to preload
    const TEXTURES_TO_PRELOAD = [
        'TileSheets/furniture',
        'TileSheets/furniture_2',
        'TileSheets/furniture_3',
        'TileSheets/joja_furniture',
        'TileSheets/wizard_furniture',
        'TileSheets/junimo_furniture',
        'TileSheets/retro_furniture',
        'TileSheets/Craftables',
        'Maps/walls_and_floors'
    ];

    // State
    let layoutsData = { layouts: [] };
    let currentLayout = null;
    let currentLayoutMeta = null;
    let zoomScale = 1;

    // Pan/viewport state
    let panX = 0;
    let panY = 0;
    let isPanning = false;
    let panStartX = 0;
    let panStartY = 0;
    let panStartPanX = 0;
    let panStartPanY = 0;
    let showGrid = false;

    // DOM element references
    const elements = {};

    // Tooltip element
    let tooltip = null;

    // Example layout (built-in fallback)
    const EXAMPLE_LAYOUT = {
        Name: 'example-lobby',
        CabinSkin: 'Log Cabin',
        UpgradeLevel: 0,
        Furniture: [
            { ItemId: '(F)512', TileX: 1, TileY: 5, Rotation: 0 },
            { ItemId: '(F)1134', TileX: 5, TileY: 5, Rotation: 0, HeldObjectId: '(O)395' },
            { ItemId: '(F)1376', TileX: 9, TileY: 5, Rotation: 0 },
            { ItemId: '(F)1451', TileX: 4, TileY: 7, Rotation: 0 },
            { ItemId: '(F)1614', TileX: 5, TileY: 1, Rotation: 0 }
        ],
        Objects: [
            { ItemId: '(BC)FishSmoker', TileX: 8, TileY: 7 }
        ],
        Wallpapers: { Main: '11' },
        Floors: { Main: '3' },
        SpawnX: 5,
        SpawnY: 8
    };

    /**
     * Initialize DOM element references.
     */
    function initElements() {
        elements.loadingIndicator = document.getElementById('loadingIndicator');
        elements.galleryView = document.getElementById('galleryView');
        elements.detailView = document.getElementById('detailView');
        elements.galleryGrid = document.getElementById('galleryGrid');
        elements.searchInput = document.getElementById('searchInput');

        // Detail view elements
        elements.previewCanvas = document.getElementById('previewCanvas');
        elements.zoomLevel = document.getElementById('zoomLevel');
        elements.layoutName = document.getElementById('layoutName');
        elements.cabinSkin = document.getElementById('cabinSkin');
        elements.upgradeLevel = document.getElementById('upgradeLevel');
        elements.spawnPoint = document.getElementById('spawnPoint');
        // Breadcrumb elements
        elements.breadcrumbCabin = document.getElementById('breadcrumbCabin');
        elements.breadcrumbName = document.getElementById('breadcrumbName');
        // Sidebar elements
        elements.detailLayoutName = document.getElementById('detailLayoutName');
        elements.detailAuthor = document.getElementById('detailAuthor');
        elements.detailDate = document.getElementById('detailDate');
        elements.detailDescription = document.getElementById('detailDescription');
        elements.detailDownloads = document.getElementById('detailDownloads');
        elements.exportCodeDisplay = document.getElementById('exportCodeDisplay');

        // Modal elements
        elements.uploadModal = document.getElementById('uploadModal');
    }

    /**
     * Set loading state.
     */
    function setLoading(show, message = 'Loading...') {
        elements.loadingIndicator.textContent = message;
        elements.loadingIndicator.classList.toggle('hidden', !show);
        elements.galleryView.classList.toggle('hidden', show);
        elements.detailView.classList.add('hidden');
    }

    /**
     * Show a dismissable warning banner below the header.
     */
    function showLoadingWarning(message) {
        // Remove any existing warning
        const existing = document.querySelector('.loading-warning');
        if (existing) existing.remove();

        const banner = document.createElement('div');
        banner.className = 'loading-warning';
        banner.innerHTML = `<span>${message}</span><button onclick="this.parentElement.remove()">&times;</button>`;
        const header = document.querySelector('.site-header');
        header.insertAdjacentElement('afterend', banner);
    }

    /**
     * Escape HTML special characters.
     */
    function escapeHtml(text) {
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    /**
     * Load layouts data from JSON file.
     */
    async function loadLayoutsData() {
        try {
            const response = await fetch(LAYOUTS_DATA_PATH);
            if (response.ok) {
                layoutsData = await response.json();
            }
        } catch (e) {
            console.warn('Failed to load layouts data:', e);
        }
    }

    /**
     * Render a thumbnail for a layout, sized to fill its container sharply.
     */
    function renderThumbnail(layout, canvas) {
        if (!layout) return;
        const container = canvas.parentElement;
        if (!container) {
            CabinRenderer.render(canvas, layout, 0.5);
            return;
        }
        // Measure container and compute a zoom scale that fits the scene
        const cw = container.clientWidth || 300;
        const ch = container.clientHeight || 225;
        const cabin = CabinRenderer.getCabinLayout(layout.UpgradeLevel || 0);
        const pad = CabinRenderer.GRID_PADDING * 2;
        const sceneW = cabin.width + pad;
        const sceneH = cabin.height + pad;
        const scale = Math.min(cw / (sceneW * CabinRenderer.TILE_SIZE), ch / (sceneH * CabinRenderer.TILE_SIZE));
        CabinRenderer.render(canvas, layout, scale);
    }

    /**
     * Decode a layout from its metadata entry.
     * @returns {Object|null} Decoded layout or null
     */
    function decodeLayoutMeta(layoutMeta) {
        if (layoutMeta.exportCode) {
            return LayoutDecoder.decode(layoutMeta.exportCode);
        }
        if (layoutMeta.id === 'example-lobby') {
            return EXAMPLE_LAYOUT;
        }
        return null;
    }

    /**
     * Get item count string for a layout.
     */
    function getItemCountLabel(layout) {
        if (!layout) return '';
        const f = layout.Furniture?.length || 0;
        const o = layout.Objects?.length || 0;
        const total = f + o;
        return `${total} item${total !== 1 ? 's' : ''}`;
    }

    /**
     * Create a gallery card element.
     */
    function createGalleryCard(layoutMeta, index) {
        const card = document.createElement('div');
        card.className = 'gallery-card';
        card.dataset.index = index;

        // Thumbnail container
        const thumbnail = document.createElement('div');
        thumbnail.className = 'card-thumbnail';

        // Create thumbnail canvas
        const canvas = document.createElement('canvas');
        thumbnail.appendChild(canvas);

        // Info section (item count filled in after decode)
        const info = document.createElement('div');
        info.className = 'card-info';
        info.innerHTML = `
            <h3 class="card-title">${escapeHtml(layoutMeta.name)}</h3>
            <div class="card-meta">
                <span class="card-author">by ${escapeHtml(layoutMeta.author)}</span>
                <span class="card-downloads">&#x2B07; ${layoutMeta.downloads}</span>
            </div>
            <div class="card-item-count"></div>
        `;

        card.appendChild(thumbnail);
        card.appendChild(info);

        // Click handler
        card.addEventListener('click', () => openLayout(index));

        // Render thumbnail and get item count async
        setTimeout(() => {
            try {
                const layout = decodeLayoutMeta(layoutMeta);
                if (layout) {
                    renderThumbnail(layout, canvas);
                    const countEl = info.querySelector('.card-item-count');
                    if (countEl) countEl.textContent = getItemCountLabel(layout);
                }
            } catch (e) {
                console.warn('Failed to render thumbnail for', layoutMeta.name, e);
            }
        }, 50 * index);

        return card;
    }

    /**
     * Create a placeholder gallery card.
     */
    function createPlaceholderCard() {
        const card = document.createElement('div');
        card.className = 'gallery-card placeholder';

        const thumbnail = document.createElement('div');
        thumbnail.className = 'card-thumbnail';
        thumbnail.innerHTML = '<span class="placeholder-icon">?</span>';

        const info = document.createElement('div');
        info.className = 'card-info';
        info.innerHTML = `
            <h3 class="card-title">Coming Soon</h3>
            <div class="card-meta">
                <span class="card-author">Upload your layout!</span>
            </div>
        `;

        card.appendChild(thumbnail);
        card.appendChild(info);

        return card;
    }

    /**
     * Render the gallery grid.
     */
    function renderGallery() {
        elements.galleryGrid.innerHTML = '';

        // Add actual layouts
        layoutsData.layouts.forEach((layoutMeta, index) => {
            const card = createGalleryCard(layoutMeta, index);
            elements.galleryGrid.appendChild(card);
        });

        // Add placeholder cards to fill the row
        const totalCards = layoutsData.layouts.length;
        const placeholderCount = Math.max(0, 4 - totalCards);
        for (let i = 0; i < placeholderCount; i++) {
            elements.galleryGrid.appendChild(createPlaceholderCard());
        }
    }

    /**
     * Filter gallery based on search input.
     */
    function filterGallery() {
        const query = elements.searchInput.value.toLowerCase();
        const cards = elements.galleryGrid.querySelectorAll('.gallery-card');

        cards.forEach((card, index) => {
            if (card.classList.contains('placeholder')) {
                card.style.display = query ? 'none' : '';
                return;
            }

            if (index >= layoutsData.layouts.length) return;

            const layoutMeta = layoutsData.layouts[index];
            const matches = layoutMeta.name.toLowerCase().includes(query) ||
                           layoutMeta.author.toLowerCase().includes(query) ||
                           layoutMeta.description.toLowerCase().includes(query);

            card.style.display = matches ? '' : 'none';
        });
    }

    /**
     * Show the gallery view.
     */
    function showGallery() {
        elements.galleryView.classList.remove('hidden');
        elements.detailView.classList.add('hidden');
        currentLayout = null;
        currentLayoutMeta = null;
    }

    /**
     * Open a layout for detailed view.
     * @param {number} index - Layout index
     * @param {boolean} pushState - Whether to push a history state (false when restoring from popstate)
     */
    function openLayout(index, pushState = true) {
        const layoutMeta = layoutsData.layouts[index];
        if (!layoutMeta) return;

        currentLayoutMeta = layoutMeta;

        try {
            // Decode the layout
            currentLayout = decodeLayoutMeta(layoutMeta);
            if (!currentLayout) {
                console.error('No export code for layout:', layoutMeta.name);
                return;
            }

            // Push browser history state
            if (pushState) {
                history.pushState({ view: 'detail', index }, '', `#layout/${layoutMeta.id}`);
            }

            // Show detail view
            elements.galleryView.classList.add('hidden');
            elements.detailView.classList.remove('hidden');

            // Reset zoom, pan and grid
            zoomScale = 1;
            elements.zoomLevel.textContent = '100%';
            showGrid = false;
            document.getElementById('gridToggle').classList.remove('active');

            // Center and render in viewport
            // Use requestAnimationFrame to ensure container is laid out first
            requestAnimationFrame(() => {
                centerScene();
                renderViewport();
            });

            // Update info
            updateLayoutInfo(currentLayout);
            updateSidebar(layoutMeta);

        } catch (e) {
            console.error('Failed to open layout:', e);
            alert('Failed to load layout: ' + e.message);
        }
    }

    /**
     * Update the layout info header.
     */
    function updateLayoutInfo(layout) {
        elements.layoutName.textContent = layout.Name || 'Unnamed';
        elements.cabinSkin.textContent = layout.CabinSkin || 'Log Cabin';
        elements.upgradeLevel.textContent = `Level ${layout.UpgradeLevel || 0}`;
        elements.spawnPoint.textContent = (layout.SpawnX != null && layout.SpawnY != null)
            ? `(${layout.SpawnX}, ${layout.SpawnY})`
            : 'Default';
    }

    /**
     * Update the sidebar with layout metadata.
     */
    function updateSidebar(layoutMeta) {
        elements.detailLayoutName.textContent = layoutMeta.name;
        elements.detailAuthor.textContent = layoutMeta.author;
        elements.detailDate.textContent = layoutMeta.createdAt
            ? new Date(layoutMeta.createdAt).toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' })
            : '-';
        elements.detailDescription.textContent = layoutMeta.description;
        elements.detailDownloads.textContent = layoutMeta.downloads || 0;
        elements.exportCodeDisplay.value = layoutMeta.exportCode || '(No export code available)';

        // Update breadcrumb
        const cabinSkin = currentLayout?.CabinSkin || 'Log Cabin';
        elements.breadcrumbCabin.textContent = cabinSkin + 's';
        elements.breadcrumbName.textContent = layoutMeta.name;
    }

    /**
     * Get the current viewport config for the canvas container.
     */
    function getViewport() {
        const container = elements.previewCanvas.parentElement;
        return {
            width: container.clientWidth,
            height: container.clientHeight,
            panX: panX,
            panY: panY,
            showGrid: showGrid
        };
    }

    /**
     * Get the scene size in pixels for the current layout and zoom.
     */
    function getSceneSize() {
        if (!currentLayout) return { width: 0, height: 0 };
        const cabin = CabinRenderer.getCabinLayout(currentLayout.UpgradeLevel || 0);
        const tileSize = CabinRenderer.TILE_SIZE * zoomScale;
        const padding = CabinRenderer.GRID_PADDING * tileSize;
        return {
            width: cabin.width * tileSize + padding * 2,
            height: cabin.height * tileSize + padding * 2
        };
    }

    /**
     * Center the scene in the viewport.
     */
    function centerScene() {
        const vp = getViewport();
        const scene = getSceneSize();
        panX = (vp.width - scene.width) / 2;
        panY = (vp.height - scene.height) / 2;
    }

    /**
     * Render the current layout to the viewport.
     */
    function renderViewport() {
        if (!currentLayout) return;
        const vp = getViewport();
        vp.panX = panX;
        vp.panY = panY;
        CabinRenderer.render(elements.previewCanvas, currentLayout, zoomScale, vp);
    }

    /**
     * Zoom in on the preview.
     */
    function zoomIn() {
        if (zoomScale < 3) {
            // Zoom toward center of viewport
            const vp = getViewport();
            const cx = vp.width / 2;
            const cy = vp.height / 2;
            const oldScale = zoomScale;
            zoomScale += 0.25;
            const ratio = zoomScale / oldScale;
            panX = cx - (cx - panX) * ratio;
            panY = cy - (cy - panY) * ratio;
            elements.zoomLevel.textContent = Math.round(zoomScale * 100) + '%';
            renderViewport();
        }
    }

    /**
     * Zoom out on the preview.
     */
    function zoomOut() {
        if (zoomScale > 0.5) {
            const vp = getViewport();
            const cx = vp.width / 2;
            const cy = vp.height / 2;
            const oldScale = zoomScale;
            zoomScale -= 0.25;
            const ratio = zoomScale / oldScale;
            panX = cx - (cx - panX) * ratio;
            panY = cy - (cy - panY) * ratio;
            elements.zoomLevel.textContent = Math.round(zoomScale * 100) + '%';
            renderViewport();
        }
    }

    /**
     * Reset zoom to 100% and re-center the scene.
     */
    function resetZoom() {
        zoomScale = 1;
        elements.zoomLevel.textContent = '100%';
        centerScene();
        renderViewport();
    }

    /**
     * Toggle the tile grid overlay.
     */
    function toggleGrid() {
        showGrid = !showGrid;
        document.getElementById('gridToggle').classList.toggle('active', showGrid);
        renderViewport();
    }

    /**
     * Show a brief toast message near a button.
     * @param {HTMLElement} anchor - The element to position near
     * @param {string} message - The message text
     */
    function showToast(anchor, message) {
        const toast = document.createElement('div');
        toast.className = 'toast';
        toast.textContent = message;
        document.body.appendChild(toast);

        // Position above the anchor element
        const rect = anchor.getBoundingClientRect();
        toast.style.left = `${rect.left + rect.width / 2}px`;
        toast.style.top = `${rect.top - 8}px`;

        // Trigger fade-in
        requestAnimationFrame(() => toast.classList.add('visible'));

        // Fade out and remove
        setTimeout(() => {
            toast.classList.remove('visible');
            toast.addEventListener('transitionend', () => toast.remove());
            // Fallback: remove after transition should have finished
            setTimeout(() => { if (toast.parentNode) toast.remove(); }, 300);
        }, 1500);
    }

    /**
     * Copy the export code to clipboard.
     */
    async function copyExportCode(e) {
        const code = elements.exportCodeDisplay.value;
        const btn = e?.target || e?.currentTarget;
        if (!code || code.startsWith('(')) return;

        try {
            await navigator.clipboard.writeText(code);
        } catch (_) {
            elements.exportCodeDisplay.select();
            document.execCommand('copy');
        }
        if (btn) showToast(btn, 'Copied!');
    }

    /**
     * Save the current preview as a PNG image.
     */
    function saveImage(e) {
        const btn = e?.target || e?.currentTarget;
        if (!currentLayout) return;

        // Render to an offscreen canvas at full resolution (no viewport/pan)
        const offscreen = document.createElement('canvas');
        CabinRenderer.render(offscreen, currentLayout, 1);
        offscreen.toBlob((blob) => {
            const url = URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = `${currentLayoutMeta?.id || 'layout'}.png`;
            a.click();
            URL.revokeObjectURL(url);
            if (btn) showToast(btn, 'Saved!');
        }, 'image/png');
    }

    /**
     * Copy the current page URL (with hash) to clipboard.
     */
    async function copyShareLink(e) {
        const btn = e?.target || e?.currentTarget;
        try {
            await navigator.clipboard.writeText(location.href);
        } catch (_) {
            // fallback: temporary input
            const input = document.createElement('input');
            input.value = location.href;
            document.body.appendChild(input);
            input.select();
            document.execCommand('copy');
            input.remove();
        }
        if (btn) showToast(btn, 'Link copied!');
    }

    /**
     * Download the layout (placeholder).
     */
    function downloadLayout(e) {
        const btn = e?.target || e?.currentTarget;
        if (btn) showToast(btn, 'Coming soon');
    }

    /**
     * Show upload modal.
     */
    function showUploadModal() {
        elements.uploadModal.classList.remove('hidden');
        // Reset state
        const previewContainer = document.getElementById('uploadPreviewContainer');
        const errorEl = document.getElementById('uploadError');
        if (previewContainer) previewContainer.classList.add('hidden');
        if (errorEl) errorEl.classList.add('hidden');
    }

    /**
     * Hide upload modal.
     */
    function hideUploadModal() {
        elements.uploadModal.classList.add('hidden');
    }

    /**
     * Handle paste/input in upload code field — render preview.
     */
    function handleUploadCodeInput() {
        const code = document.getElementById('uploadCode').value.trim();
        const previewContainer = document.getElementById('uploadPreviewContainer');
        const previewCanvas = document.getElementById('uploadPreviewCanvas');
        const previewInfo = document.getElementById('uploadPreviewInfo');
        const errorEl = document.getElementById('uploadError');

        if (!code) {
            previewContainer.classList.add('hidden');
            errorEl.classList.add('hidden');
            return;
        }

        try {
            const layout = LayoutDecoder.decode(code);
            CabinRenderer.render(previewCanvas, layout, 0.5);

            const itemCount = (layout.Furniture?.length || 0) + (layout.Objects?.length || 0);
            previewInfo.textContent = `${layout.Name || 'Unnamed'} — Level ${layout.UpgradeLevel || 0} — ${itemCount} items`;

            previewContainer.classList.remove('hidden');
            errorEl.classList.add('hidden');

            // Auto-fill name if empty
            const nameInput = document.getElementById('uploadName');
            if (!nameInput.value && layout.Name) {
                nameInput.value = layout.Name;
            }
        } catch (e) {
            previewContainer.classList.add('hidden');
            errorEl.textContent = 'Invalid export code: ' + e.message;
            errorEl.classList.remove('hidden');
        }
    }

    /**
     * Submit upload (placeholder).
     */
    function submitUpload(e) {
        const btn = e?.target || e?.currentTarget;
        if (btn) showToast(btn, 'Coming soon');
        hideUploadModal();
    }

    /**
     * Create and initialize the tooltip element.
     */
    function createTooltip() {
        tooltip = document.createElement('div');
        tooltip.className = 'tooltip hidden';
        document.body.appendChild(tooltip);
    }

    /**
     * Show tooltip at the specified position.
     */
    function showTooltip(item, x, y) {
        if (!item || !tooltip) return;

        const typeColors = {
            furniture: '#4CAF50',
            object: '#2196F3',
            spawn: '#E91E63',
            tile: '#9E9E9E'
        };

        const typeLabels = {
            furniture: item.furnitureType || 'Furniture',
            object: 'Object',
            spawn: 'Spawn Point',
            tile: item.name
        };

        tooltip.innerHTML = `
            <div class="tooltip-type" style="color: ${typeColors[item.type] || '#9E9E9E'}">
                ${typeLabels[item.type] || item.type}
            </div>
            <div class="tooltip-title">${escapeHtml(item.name)}</div>
            <div class="tooltip-details">${escapeHtml(item.description)}</div>
        `;

        tooltip.classList.remove('hidden');

        const rect = tooltip.getBoundingClientRect();
        const padding = 10;

        let posX = x + padding;
        let posY = y + padding;

        if (posX + rect.width > window.innerWidth) {
            posX = x - rect.width - padding;
        }
        if (posY + rect.height > window.innerHeight) {
            posY = y - rect.height - padding;
        }

        tooltip.style.left = `${posX}px`;
        tooltip.style.top = `${posY}px`;
    }

    /**
     * Hide the tooltip.
     */
    function hideTooltip() {
        if (tooltip) {
            tooltip.classList.add('hidden');
        }
    }

    // Highlight animation state
    let highlightedItemKey = null;
    let highlightedItem = null;
    let highlightAlpha = 0;
    let highlightTarget = 0;
    let highlightAnimFrame = null;
    const HIGHLIGHT_DURATION = 100; // ms

    /**
     * Animate the highlight overlay alpha toward its target.
     */
    function animateHighlight(startTime) {
        const elapsed = performance.now() - startTime;
        const progress = Math.min(elapsed / HIGHLIGHT_DURATION, 1);

        // Ease: linear is fine for 100ms
        highlightAlpha = highlightTarget === 1
            ? progress
            : 1 - progress;

        // Redraw viewport
        const canvas = elements.previewCanvas;
        const vp = getViewport();
        vp.panX = panX;
        vp.panY = panY;
        CabinRenderer.render(canvas, currentLayout, zoomScale, vp);
        if (highlightAlpha > 0 && highlightedItem) {
            CabinRenderer.drawHighlight(canvas, highlightedItem, zoomScale, highlightAlpha, vp);
        }

        if (progress < 1) {
            highlightAnimFrame = requestAnimationFrame(() => animateHighlight(startTime));
        } else {
            highlightAnimFrame = null;
            // If fully faded out, clear the item reference
            if (highlightTarget === 0) {
                highlightedItem = null;
            }
        }
    }

    /**
     * Start a highlight transition.
     */
    function transitionHighlight(item) {
        if (highlightAnimFrame) {
            cancelAnimationFrame(highlightAnimFrame);
            highlightAnimFrame = null;
        }

        if (item && item.type !== 'tile') {
            highlightedItem = item;
            highlightTarget = 1;
            highlightAlpha = 0;
        } else {
            // Fade out, keep old highlightedItem for drawing
            highlightTarget = 0;
        }

        highlightAnimFrame = requestAnimationFrame(() => animateHighlight(performance.now()));
    }

    /**
     * Handle mouse move over canvas.
     */
    function handleCanvasMouseMove(e) {
        if (!currentLayout || isPanning) return;

        const canvas = elements.previewCanvas;
        const rect = canvas.getBoundingClientRect();
        // Screen pixel position within canvas element
        const screenX = e.clientX - rect.left;
        const screenY = e.clientY - rect.top;
        // Map to scene coords by removing pan offset
        const sceneX = screenX - panX;
        const sceneY = screenY - panY;

        const item = CabinRenderer.getItemAtPosition(currentLayout, sceneX, sceneY, zoomScale);
        const itemKey = item ? `${item.type}:${item.tileX},${item.tileY}` : null;

        if (itemKey !== highlightedItemKey) {
            highlightedItemKey = itemKey;
            transitionHighlight(item);
        }

        if (item) {
            showTooltip(item, e.clientX, e.clientY);
        } else {
            hideTooltip();
        }
    }

    /**
     * Handle mouse leave from canvas.
     */
    function handleCanvasMouseLeave() {
        hideTooltip();
        if (highlightedItemKey && currentLayout) {
            highlightedItemKey = null;
            transitionHighlight(null);
        }
    }

    /**
     * Handle mouse down on container — start panning.
     */
    function handlePanStart(e) {
        if (e.button !== 0) return; // left click only
        isPanning = true;
        panStartX = e.clientX;
        panStartY = e.clientY;
        panStartPanX = panX;
        panStartPanY = panY;
        const container = elements.previewCanvas.parentElement;
        container.classList.add('panning');
        hideTooltip();
        // Clear highlight while dragging
        if (highlightedItemKey) {
            highlightedItemKey = null;
            transitionHighlight(null);
        }
    }

    /**
     * Handle mouse move for panning.
     */
    function handlePanMove(e) {
        if (!isPanning) return;
        const dx = e.clientX - panStartX;
        const dy = e.clientY - panStartY;
        panX = panStartPanX + dx;
        panY = panStartPanY + dy;
        renderViewport();
    }

    /**
     * Handle mouse up — stop panning.
     */
    function handlePanEnd(e) {
        if (!isPanning) return;
        isPanning = false;
        const container = elements.previewCanvas.parentElement;
        container.classList.remove('panning');
        // Re-check hover at current mouse position
        if (e) handleCanvasMouseMove(e);
    }

    /**
     * Handle mouse wheel for zooming.
     */
    function handleWheel(e) {
        if (!currentLayout) return;
        e.preventDefault();

        const rect = elements.previewCanvas.parentElement.getBoundingClientRect();
        // Zoom toward mouse position
        const mx = e.clientX - rect.left;
        const my = e.clientY - rect.top;

        const oldScale = zoomScale;
        if (e.deltaY < 0) {
            zoomScale = Math.min(3, zoomScale + 0.25);
        } else {
            zoomScale = Math.max(0.5, zoomScale - 0.25);
        }

        if (zoomScale !== oldScale) {
            const ratio = zoomScale / oldScale;
            panX = mx - (mx - panX) * ratio;
            panY = my - (my - panY) * ratio;
            elements.zoomLevel.textContent = Math.round(zoomScale * 100) + '%';
            renderViewport();
        }
    }

    /**
     * Handle browser back/forward navigation.
     */
    function handlePopState(e) {
        if (e.state && e.state.view === 'detail' && e.state.index != null) {
            openLayout(e.state.index, false);
        } else {
            showGallery();
        }
    }

    /**
     * Restore view from the current URL hash (e.g. on page load).
     */
    function restoreFromHash() {
        const match = location.hash.match(/^#layout\/(.+)$/);
        if (match) {
            const id = match[1];
            const index = layoutsData.layouts.findIndex(l => l.id === id);
            if (index >= 0) {
                openLayout(index, false);
                history.replaceState({ view: 'detail', index }, '');
                return;
            }
        }
        history.replaceState({ view: 'gallery' }, '');
    }

    /**
     * Initialize event listeners.
     */
    function initEventListeners() {
        const container = elements.previewCanvas.parentElement;

        // Canvas hover events
        container.addEventListener('mousemove', handleCanvasMouseMove);
        container.addEventListener('mouseleave', handleCanvasMouseLeave);

        // Pan events
        container.addEventListener('mousedown', handlePanStart);
        window.addEventListener('mousemove', handlePanMove);
        window.addEventListener('mouseup', handlePanEnd);

        // Wheel zoom
        container.addEventListener('wheel', handleWheel, { passive: false });

        // Re-render on window resize (debounced)
        let resizeTimer = 0;
        window.addEventListener('resize', () => {
            cancelAnimationFrame(resizeTimer);
            resizeTimer = requestAnimationFrame(() => {
                if (currentLayout) renderViewport();
            });
        });

        // Browser navigation
        window.addEventListener('popstate', handlePopState);

        // Close modal on backdrop click
        elements.uploadModal.addEventListener('click', (e) => {
            if (e.target === elements.uploadModal) {
                hideUploadModal();
            }
        });

        // Keyboard shortcuts
        document.addEventListener('keydown', (e) => {
            // Ignore when typing in an input/textarea
            if (e.target.tagName === 'INPUT' || e.target.tagName === 'TEXTAREA') return;

            if (e.key === 'Escape') {
                if (!elements.uploadModal.classList.contains('hidden')) {
                    hideUploadModal();
                } else if (!elements.detailView.classList.contains('hidden')) {
                    history.back();
                }
            }

            // Zoom shortcuts (detail view only)
            if (!elements.detailView.classList.contains('hidden')) {
                if (e.key === '+' || e.key === '=') { zoomIn(); }
                else if (e.key === '-') { zoomOut(); }
                else if (e.key === '0') { resetZoom(); }
                else if (e.key === 'g') { toggleGrid(); }
            }
        });

        // Upload code field: preview on paste/input
        const uploadCode = document.getElementById('uploadCode');
        if (uploadCode) {
            uploadCode.addEventListener('paste', () => setTimeout(handleUploadCodeInput, 50));
            uploadCode.addEventListener('input', handleUploadCodeInput);
        }

    }

    /**
     * Initialize the application.
     */
    async function init() {
        initElements();

        // Check for pako
        if (typeof pako === 'undefined') {
            elements.loadingIndicator.innerHTML =
                '<div style="color:#e57373">Error: Failed to load pako library. Please check your internet connection.</div>';
            return;
        }

        const warnings = [];

        // Load furniture & BigCraftables data
        setLoading(true, 'Loading game data...');
        await Promise.all([
            FurnitureParser.load(FURNITURE_DATA_PATH).catch(e => {
                console.warn('Failed to load Furniture data:', e);
                warnings.push('Furniture data');
            }),
            BigCraftablesParser.load(BIG_CRAFTABLES_DATA_PATH).catch(e => {
                console.warn('Failed to load BigCraftables data:', e);
                warnings.push('BigCraftables data');
            })
        ]);
        if (FurnitureParser.isLoaded()) {
            console.log(`Loaded ${FurnitureParser.getAllIds().length} furniture items`);
        }

        // Load sprite textures
        setLoading(true, 'Loading sprite textures...');
        await SpriteLoader.preloadTextures(TEXTURES_TO_PRELOAD);
        const loadedTextures = SpriteLoader.getLoadedTextures();
        console.log(`Loaded ${loadedTextures.length}/${TEXTURES_TO_PRELOAD.length} textures:`, loadedTextures);

        const failedCount = TEXTURES_TO_PRELOAD.length - loadedTextures.length;
        if (failedCount > 0) {
            warnings.push(`${failedCount} texture(s)`);
        }

        // Load layouts data
        setLoading(true, 'Loading layouts...');
        await loadLayoutsData();
        console.log(`Loaded ${layoutsData.layouts.length} layouts`);

        // Show warning if anything failed
        if (warnings.length > 0) {
            showLoadingWarning(`Failed to load ${warnings.join(', ')} — some sprites may show as colored boxes.`);
        }

        // Create tooltip and render gallery
        createTooltip();
        renderGallery();
        setLoading(false);
        initEventListeners();
        restoreFromHash();
    }

    // Initialize when DOM is ready
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', init);
    } else {
        init();
    }

    // Public API
    return {
        showGallery,
        openLayout,
        filterGallery,
        zoomIn,
        zoomOut,
        resetZoom,
        toggleGrid,
        copyExportCode,
        saveImage,
        copyShareLink,
        downloadLayout,
        showUploadModal,
        hideUploadModal,
        submitUpload
    };
})();
