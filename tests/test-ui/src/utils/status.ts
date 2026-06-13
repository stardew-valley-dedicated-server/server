/**
 * Centralized status-to-color mappings used by every test/step/instance UI component.
 */

/** Map a test/step status to a Tailwind text color class. */
export function statusTextClass(status: string): string {
    switch (status) {
        case "running":
        case "started":
        case "in_progress":
            return "text-info";
        case "passed":
        case "completed":
            return "text-success";
        case "failed":
            return "text-error";
        case "canceled":
        case "warning":
            return "text-warning";
        case "aborted":
            return "text-secondary";
        case "queued":
            return "text-primary";
        case "skipped":
        case "notDispatched":
            return "text-base-content/50";
        default:
            return "text-base-content/30";
    }
}

/** Map a test/step status to a Tailwind bg color class (for stripes, dots). */
export function statusBgClass(status: string): string {
    switch (status) {
        case "running":
        case "started":
        case "in_progress":
            return "bg-info";
        case "passed":
        case "completed":
            return "bg-success";
        case "failed":
            return "bg-error";
        case "canceled":
        case "warning":
            return "bg-warning";
        case "aborted":
            return "bg-secondary";
        case "queued":
            return "bg-primary";
        case "skipped":
        case "notDispatched":
            return "bg-base-content/30";
        default:
            return "bg-base-content/20";
    }
}

/** Map a test/step status to a DaisyUI badge class. */
export function statusBadgeClass(status: string): string {
    switch (status) {
        case "running":
        case "started":
        case "in_progress":
            return "badge-info";
        case "passed":
        case "completed":
            return "badge-success";
        case "failed":
            return "badge-error";
        case "canceled":
        case "warning":
            return "badge-warning";
        case "aborted":
            return "badge-secondary";
        case "queued":
            return "badge-primary";
        case "skipped":
        case "notDispatched":
            return "badge-ghost";
        default:
            return "badge-ghost";
    }
}

/** Map a test/step status to a filter pill bg+text class pair. */
export function statusFilterClass(status: string): string {
    switch (status) {
        case "failed":
            return "bg-error/20 text-error";
        case "passed":
            return "bg-success/20 text-success";
        case "running":
            return "bg-info/20 text-info";
        case "queued":
            return "bg-primary/20 text-primary";
        case "canceled":
            return "bg-warning/20 text-warning";
        case "aborted":
            return "bg-secondary/20 text-secondary";
        case "skipped":
        case "notDispatched":
            return "bg-base-content/10 text-base-content/50";
        default:
            return "bg-base-content/5 text-base-content/30";
    }
}

/** Map a Lucide icon name to a test/step status. */
export function statusIcon(status: string): string {
    switch (status) {
        case "running":
        case "started":
        case "in_progress":
            return "lucide:loader";
        case "passed":
        case "completed":
            return "lucide:check-circle";
        case "failed":
            return "lucide:x-circle";
        case "canceled":
            return "lucide:ban";
        case "aborted":
            return "lucide:octagon-x";
        case "queued":
            return "lucide:circle-dot";
        case "warning":
            return "lucide:alert-triangle";
        case "skipped":
        case "notDispatched":
            return "lucide:minus-circle";
        default:
            return "lucide:circle";
    }
}

/** Whether a status icon should spin. */
export function statusIconSpin(status: string): boolean {
    return status === "running" || status === "started" || status === "in_progress";
}

/** Whether a status icon should pulse. */
export function statusIconPulse(status: string): boolean {
    return status === "queued";
}
