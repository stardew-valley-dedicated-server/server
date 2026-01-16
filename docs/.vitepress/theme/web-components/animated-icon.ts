/**
 * Copied from https://animatedicons.co/scripts/embed-animated-icons.js and modified
 * to allow external access to the animation instance, used to randomly trigger play
 * in our feature overview.
 */
class AnimatedIcons extends HTMLElement {
    constructor() {
        super();
        this.attachShadow({ mode: "open" });

        // Create the container for the Lottie animation
        this.container = document.createElement("div");
        this.shadowRoot.appendChild(this.container);

        const style = document.createElement("style");
        style.textContent = `
        div {
          display: flex;
          align-items: center;
          justify-content: center;
        }
      `;
        this.shadowRoot.appendChild(style);
    }

    connectedCallback() {
        // Access the attributes after the element is connected to the DOM
        const customWidth = this.getAttribute("width") || "100px";
        const customHeight = this.getAttribute("height") || "100px";
        // const trigger = this.getAttribute("trigger") || "hover";  // MODIFIED
        this.trigger = this.getAttribute("trigger") || "hover";

        // If no unit is provided, append 'px' to width and height
        this.container.style.width = this.addUnit(customWidth);
        this.container.style.height = this.addUnit(customHeight);

        if (!window.lottie) {
            const lottieScript = document.createElement("script");
            lottieScript.src = "https://cdnjs.cloudflare.com/ajax/libs/bodymovin/5.12.2/lottie.min.js";
            lottieScript.onload = async () => {
                // await this.loadAnimation(this.container, trigger); // MODIFIED
                await this.loadAnimation(this.container);
            };
            lottieScript.onerror = () => {
                console.error("Failed to load the Lottie library.");
            };
            document.head.appendChild(lottieScript);
        } else {
            // this.loadAnimation(this.container, trigger); // MODIFIED
            this.loadAnimation(this.container);
        }
    }

    addUnit(value) {
        if (!value) {
            return "100px";
        }

        const hasUnit = value.includes("px") || value.includes("%") || value.includes("em");
        return hasUnit ? value : `${value}px`;
    }

    async loadAnimation(container) {
        const src = this.getAttribute("src");
        const attributes = this.getAttribute("attributes");

        if (!src) {
            console.error('AnimatedIcons: Missing "src" attribute for Lottie JSON.');
            return;
        }

        try {
            const response = await fetch(src);
            if (!response.ok) {
                throw new Error(`Failed to fetch Lottie JSON: ${response.statusText}`);
            }
            const animationData = await response.json();

            if (attributes) {
                try {
                    const parsedAttributes = JSON.parse(attributes);
                    this.applyAttributes(animationData, parsedAttributes);
                } catch (error) {
                    console.error("Invalid JSON in attributes:", error);
                }
            }

            const animation = lottie.loadAnimation({
                container: container,
                renderer: "svg",
                loop: this.trigger === "loop",
                autoplay: this.trigger === "loop",
                animationData: animationData,
            });

            this.animation = animation;

            // Always bind events
            container.addEventListener("mouseenter", () => {
                if (this.trigger === "hover") {
                    animation.loop = false;
                    animation.play();
                } else if (this.trigger === "loop-on-hover") {
                    animation.loop = true;
                    animation.play();
                }
            });

            container.addEventListener("mouseleave", () => {
                if (this.trigger === "loop-on-hover") {
                    animation.loop = false;
                    animation.stop();
                }
            });

            container.addEventListener("click", () => {
                if (this.trigger === "click") {
                    animation.loop = false;
                    animation.play();
                }
            });

            // Stop animation on completion for non-loop triggers
            animation.addEventListener("complete", () => {
                if (this.trigger !== "loop" && this.trigger !== "loop-on-hover") {
                    animation.stop();
                }
            });
        } catch (error) {
            console.error("Error loading or parsing Lottie JSON:", error);
        }
    }

    applyAttributes(animationData, attributes) {
        const layers = animationData.layers || [];
        const { defaultColours, numberOfGroups, variationNumber, strokeWidth } = attributes;

        this.strokeWidth = strokeWidth;

        this.applyGroupColors(defaultColours, numberOfGroups, variationNumber, layers);

        this.applyBackgroundColor(defaultColours, layers);
        this.applySecondaryBackgroundColor(defaultColours, layers);
    }

    applyGroupColors(defaultColours, numberOfGroups, variationNumber, layers) {
        // Ensure that defaultColors, numberOfGroups, and variationNumber are provided
        if (!defaultColours || !numberOfGroups || !variationNumber) return;

        // Loop through each group and apply the corresponding color
        for (let groupIndex = 1; groupIndex <= numberOfGroups; groupIndex++) {
            // Generate the group ID part using variation and group index
            const groupIdPart = `s${variationNumber}g${groupIndex}`;
            const colorKey = `group-${groupIndex}`;
            const groupColor = defaultColours[colorKey];

            // If the color is defined for the group, apply it
            if (groupColor) {
                const rgbColor = this.hexToRgb(groupColor);
                // Update the colors in the layers for this group
                this.updateColorsInLayers(layers, groupIdPart, rgbColor, "fill");
            }
        }
    }

    applyBackgroundColor(defaultColours, layers) {
        const backgroundColor = defaultColours.background;

        // If a background color is provided, apply it to the layers
        if (backgroundColor) {
            const rgb = this.hexToRgb(backgroundColor);
            this.updateBackgroundColorInLayers(layers, rgb);
        }
    }

    applySecondaryBackgroundColor(defaultColours, layers) {
        const bgSecondary = defaultColours.background2;

        // If a secondary background color is provided, apply it to the layers
        if (bgSecondary) {
            const rgb = this.hexToRgb(bgSecondary);
            this.updateBackgroundSecondayColorInLayers(layers, rgb);
        }
    }

    updateColorsInLayers(layers, groupIdPart, rgb) {
        layers.forEach((layer) => {
            if (layer.nm.includes(groupIdPart) && layer.shapes) {
                this.updateColorsRecursively(layer.shapes, rgb, "stroke");
            }
        });
    }

    // Code to update the background layers
    updateBackgroundColorInLayers(layers, rgb) {
        layers.forEach((layer) => {
            if (layer.nm.includes("background") && layer.shapes) {
                this.updateColorsRecursively(layer.shapes, rgb, "fill");
            }
        });
    }

    updateBackgroundSecondayColorInLayers(layers, rgb) {
        layers.forEach((layer) => {
            if (!layer.nm.includes("background") && layer.shapes) {
                this.updateColorsRecursively(layer.shapes, rgb, "fill");
            }
        });
    }

    updateColorsRecursively(items, rgb, updateType) {
        if (rgb.a === undefined) rgb.a = 1;
        items.forEach((item) => {
            if (updateType === "fill" && item.ty === "fl") {
                item.c.k = [rgb.r / 255, rgb.g / 255, rgb.b / 255]; // Set RGB
                if (item.o) {
                    item.o.k = typeof item.o.k === "number" ? rgb.a * 100 : rgb.a; // Adjust the opacity
                }
            } else if (updateType === "stroke" && item.ty === "st") {
                item.c.k = [rgb.r / 255, rgb.g / 255, rgb.b / 255]; // Set RGB
                if (item.o) {
                    item.o.k = typeof item.o.k === "number" ? rgb.a * 100 : rgb.a; // Adjust the opacity
                }
                item.w.k = this.strokeWidth * 1; // Modify the stroke width
            } else if (item.ty === "gr" && item.it) {
                this.updateColorsRecursively(item.it, rgb, updateType);
            }
        });
    }

    hexToRgb(hex) {
        if (typeof hex !== "string") {
            console.error("Provided hex value is not a string:", hex);
            return null; // Return a default or null value if hex is not a string
        }

        const bigint = Number.parseInt(hex.slice(1, 7), 16);
        const r = (bigint >> 16) & 255;
        const g = (bigint >> 8) & 255;
        const b = bigint & 255;

        if (hex.length === 9) {
            const a = Number.parseInt(hex.slice(7), 16) / 255;
            return { r, g, b, a };
        } else {
            return { r, g, b };
        }
    }
}

/**
 * Thin wrapper for AnimatedIcons to keep markup in feature definitions simple
 */
export default class AnimatedIcon extends AnimatedIcons {
    constructor() {
        super();

        // Store which color group this icon uses
        this.colorGroup = null;

        // Small trick to have dynamic colors without recreating each AnimatedIcon.
        // Targets the SVG path with known stroke color, and just overrides it.
        const sheet = new CSSStyleSheet();
            sheet.replaceSync(`
            g path[stroke="rgb(255,0,0)"] {
                stroke: var(--vp-c-brand-1) !important;
                transition: stroke 0.3s ease !important;
            }
            g path[stroke="rgb(0,255,0)"] {
                stroke: var(--vp-c-brand-2) !important;
                transition: stroke 0.3s ease !important;
            }
        `);

        this.shadowRoot.adoptedStyleSheets = [sheet];
    }

    connectedCallback() {
        // Set default attributes for AnimatedIcon
        const name = this.getAttribute("name") || "";
        const width = this.getAttribute("width") || "64";
        const height = this.getAttribute("height") || "64";
        const trigger = this.getAttribute("trigger") || "loop-on-hover";

        // Construct the Lottie JSON URL
        const src = `https://animatedicons.co/get-icon?name=${name}&style=minimalistic&token=7c322611-169d-413f-8483-caa466995de0`;

        const root = document.documentElement;

        // Get the computed style of the element
        const rootStyles = getComputedStyle(root);

        // Get the value of the CSS variable
        const c1 = rootStyles.getPropertyValue('--vp-c-brand-2').trim();
        const c2 = rootStyles.getPropertyValue('--vp-c-brand-3').trim();

        // Randomly pick a color and remember which one
        this.colorGroup = Math.random() >= 0.5 ? 'c1' : 'c2';

        // Default attributes
        const defaultAttributes = {
            variationNumber: 2, // "Two Tone"
            numberOfGroups: 2,
            backgroundIsGroup: false,
            strokeWidth: 1,
            defaultColours: {
                // "group-1": "#32363f",
                // "group-2": "#34D058",
                // "group-1": "#FFFFFF",
                // "group-2": this.colorGroup === 'c1' ? c1 : c2,
                "group-1": "#ff0000",
                "group-2": "#00ff00",
                background: "#FFFFFF00",
            },
        };

        // Set attributes if not already provided
        if (!this.hasAttribute("src")) this.setAttribute("src", src);
        if (!this.hasAttribute("width")) this.setAttribute("width", width);
        if (!this.hasAttribute("height")) this.setAttribute("height", height);
        if (!this.hasAttribute("trigger")) this.setAttribute("trigger", trigger);
        if (!this.hasAttribute("attributes")) this.setAttribute("attributes", JSON.stringify(defaultAttributes));

        // Call the parent connectedCallback to handle Lottie initialization
        super.connectedCallback();


        // Uncomment in case we remove all the customization for external play,
        // and load "https://animatedicons.co/scripts/embed-animated-icons.js"
        //  this.shadowRoot.innerHTML = `
        //       <animated-icons
        //           src="${src}"
        //           width="${width}"
        //           height="${height}"
        //           trigger="${trigger}"
        //           attributes='${JSON.stringify(defaultAttributes)}'>
        //       </animated-icons>
        // `;
    }

    disconnectedCallback() {
        // Clean up the observer when the element is removed
        if (this._themeObserver) {
            this._themeObserver.disconnect();
        }
    }
}
