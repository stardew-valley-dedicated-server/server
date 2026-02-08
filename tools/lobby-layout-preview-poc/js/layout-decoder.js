/**
 * Layout decoder for Stardew Valley lobby export strings.
 * Handles the SDVL0 format: prefix + gzip-compressed base64 JSON.
 */
const LayoutDecoder = (function() {
    'use strict';

    const FORMAT_PREFIX = 'SDVL';
    const CURRENT_VERSION = '0';

    /**
     * Clean and normalize a base64 string.
     * Handles whitespace, URL-safe encoding, and padding issues.
     * @param {string} base64 - The raw base64 string
     * @returns {string} Cleaned base64 string
     *
     * TODO: The padding fix below is a workaround. The C# exporter should ensure
     * proper base64 padding. Convert.ToBase64String() normally handles this, but
     * if the string is being truncated or modified somewhere, that should be fixed.
     * See: mod/JunimoServer/Services/Lobby/LobbyService.cs ExportLayout()
     */
    function cleanBase64(base64) {
        // Remove whitespace
        let cleaned = base64.replace(/\s/g, '');

        // Convert URL-safe base64 to standard (if needed)
        cleaned = cleaned.replace(/-/g, '+').replace(/_/g, '/');

        // Remove any non-base64 characters
        cleaned = cleaned.replace(/[^A-Za-z0-9+/=]/g, '');

        // Fix padding - workaround for improperly padded base64 from exporter
        while (cleaned.length % 4 !== 0) {
            cleaned += '=';
        }

        return cleaned;
    }

    /**
     * Decode base64 to Uint8Array.
     * @param {string} base64 - The base64 string
     * @returns {Uint8Array}
     */
    function base64ToBytes(base64) {
        const binaryString = atob(base64);
        const bytes = new Uint8Array(binaryString.length);
        for (let i = 0; i < binaryString.length; i++) {
            bytes[i] = binaryString.charCodeAt(i);
        }
        return bytes;
    }

    /**
     * Decompress gzip data using pako.
     * Falls back to raw inflate if gzip CRC check fails.
     * @param {Uint8Array} bytes - The compressed data
     * @returns {string} Decompressed string
     */
    function decompress(bytes) {
        // Verify gzip magic bytes
        if (bytes[0] !== 0x1f || bytes[1] !== 0x8b) {
            throw new Error('Invalid gzip format - missing magic bytes');
        }

        try {
            // Try standard gzip decompression
            return pako.ungzip(bytes, { to: 'string' });
        } catch (gzipError) {
            // Fall back to raw inflate (skip gzip header/footer)
            // This handles cross-platform CRC issues
            console.warn('Standard gzip failed, trying raw inflate:', gzipError);
            const rawDeflateData = bytes.slice(10, -8);
            return pako.inflateRaw(rawDeflateData, { to: 'string' });
        }
    }

    /**
     * Fix common ItemId corruption patterns.
     * Known issue: (F) sometimes gets corrupted to (F9 during export.
     * @param {string} json - The decompressed JSON string
     * @returns {string} Fixed JSON string
     *
     * TODO: This is a workaround for a bug in LobbyService.ExportLayout() in the C# mod.
     * The root cause should be fixed there - somewhere the ')' character (ASCII 41) is
     * being corrupted to '9' (ASCII 57) in ItemIds during serialization or compression.
     * Once fixed in the exporter, this workaround can be removed.
     * See: mod/JunimoServer/Services/Lobby/LobbyService.cs around line 1846
     */
    function fixItemIdCorruption(json) {
        // Fix (F9n) -> (F) and (F9 -> (F) patterns
        // The corruption replaces ) with 9 and sometimes adds 'n'
        return json
            .replace(/\(F9n\)/g, '(F)')      // (F9n) -> (F)
            .replace(/"\(F9n/g, '"(F)')      // "(F9n at start of string IDs
            .replace(/"\(F9([A-Za-z])/g, '"(F)$1'); // "(F9Letter -> "(F)Letter for string IDs
    }

    /**
     * Decode a layout export string.
     * @param {string} exportString - The full export string (SDVL0...)
     * @returns {Object} The decoded layout object
     * @throws {Error} If decoding fails
     */
    function decode(exportString) {
        if (!exportString || typeof exportString !== 'string') {
            throw new Error('Please enter a layout export string');
        }

        const trimmed = exportString.trim();

        // Check prefix
        if (!trimmed.startsWith(FORMAT_PREFIX)) {
            throw new Error(`Invalid format - string must start with "${FORMAT_PREFIX}"`);
        }

        // Check version
        const version = trimmed[FORMAT_PREFIX.length];
        if (version !== CURRENT_VERSION) {
            throw new Error(`Unsupported layout version: ${version}`);
        }

        // Extract and clean base64 data
        const base64Raw = trimmed.substring(FORMAT_PREFIX.length + 1);
        const base64Clean = cleanBase64(base64Raw);

        try {
            // Decode base64
            const bytes = base64ToBytes(base64Clean);

            // Decompress
            let json = decompress(bytes);

            // Fix known corruption patterns
            json = fixItemIdCorruption(json);

            // Parse JSON
            return JSON.parse(json);
        } catch (e) {
            if (e.message?.includes('atob') || e.name === 'InvalidCharacterError') {
                throw new Error('Invalid base64 encoding in layout string');
            }
            if (e.message?.includes('JSON')) {
                throw new Error('Invalid JSON in layout data');
            }
            throw new Error(`Failed to decode layout: ${e.message}`);
        }
    }

    /**
     * Encode a layout object to an export string.
     * @param {Object} layout - The layout object
     * @returns {string} The export string
     */
    function encode(layout) {
        const json = JSON.stringify(layout);
        const compressed = pako.gzip(json);
        const base64 = btoa(String.fromCharCode.apply(null, compressed));
        return FORMAT_PREFIX + CURRENT_VERSION + base64;
    }

    return {
        decode,
        encode,
        FORMAT_PREFIX,
        CURRENT_VERSION
    };
})();
