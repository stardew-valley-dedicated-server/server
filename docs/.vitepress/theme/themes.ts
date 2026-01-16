export interface ThemeColors {
    brand1: string;
    brand2: string;
    brand3: string;
}

export interface Theme {
    id: string;
    name: string;
    colors: ThemeColors;
}

export const themes: Theme[] = [
    {
        id: "aqua-gold",
        name: "Aqua Gold",
        colors: {
            brand1: "#63dbe4ff",
            brand2: "#25ac8aff",
            brand3: "#dda122ff",
        },
    },
    {
        id: "blue-green",
        name: "Blue Green",
        colors: {
            brand1: "#0571d7ff",
            brand2: "#2b7eb8ff",
            brand3: "#25ac8aff",
        },
    },
    {
        id: "blue-deep",
        name: "Blue Deep",
        colors: {
            brand1: "#0571d7ff",
            brand2: "#0b4373ff",
            brand3: "#0a2969ff",
        },
    },
    {
        id: "green",
        name: "Green",
        colors: {
            brand1: "#066636ff",
            brand2: "#25ac8aff",
            brand3: "#39c63cff",
        },
    },
    {
        id: "night-market",
        name: "Night Market",
        colors: {
            brand1: "#281075ff",
            brand2: "#41b824ff",
            brand3: "#420375ff",
        },
    },
    {
        id: "purple-1",
        name: "Purple",
        colors: {
            brand1: "#9370db",
            brand2: "#6e2bff",
            brand3: "#34327a",
        },
    },
    {
        id: "purple-2",
        name: "Purple Alt",
        colors: {
            brand1: "#9370db",
            brand2: "#A014DC",
            brand3: "#34327a",
        },
    },
];

export const DEFAULT_THEME_ID = "purple-1";
