const PREFIX = "[Discord Bot]";

/**
 * Creates a logger whose lines carry the bot prefix, plus an optional scope for subsystems.
 */
export function createLogger(scope?: string) {
    const prefix = scope ? `${PREFIX} [${scope}]` : PREFIX;
    return {
        info: (message: string): void => console.log(`${prefix} ${message}`),
        warn: (message: string): void => console.warn(`${prefix} ${message}`),
        error: (message: string): void => console.error(`${prefix} ${message}`),
    };
}

export const log = createLogger();
