/**
 * Scoped logger. Silent by default, enable via localStorage.
 *
 *   localStorage.debug = '*'            // everything
 *   localStorage.debug = 'Store,WS'    // specific scopes
 *   delete localStorage.debug          // off
 */

function isEnabled(scope: string): boolean {
    try {
        const flag = localStorage.getItem("debug");
        if (!flag) {
            return false;
        }
        if (flag === "*") {
            return true;
        }
        return flag.split(",").some((s) => s.trim() === scope);
    } catch {
        return false;
    }
}

export interface Logger {
    log: (...args: unknown[]) => void;
    warn: (...args: unknown[]) => void;
    error: (...args: unknown[]) => void;
}

export function createLogger(scope: string): Logger {
    const prefix = `[${scope}]`;
    return {
        log: (...args) => {
            if (isEnabled(scope)) {
                console.log(prefix, ...args);
            }
        },
        warn: (...args) => {
            if (isEnabled(scope)) {
                console.warn(prefix, ...args);
            }
        },
        error: (...args) => {
            if (isEnabled(scope)) {
                console.error(prefix, ...args);
            }
        },
    };
}
