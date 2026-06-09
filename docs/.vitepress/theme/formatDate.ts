/** Format a date as "DD.MM.YY HH:MM" (24h) — same format for every locale, in the visitor's local timezone. */
export function formatDateTime(value: Date | string | number): string {
    const d = new Date(value);
    const p = (n: number) => String(n).padStart(2, "0");
    const date = `${p(d.getDate())}.${p(d.getMonth() + 1)}.${p(d.getFullYear() % 100)}`;
    return `${date} ${p(d.getHours())}:${p(d.getMinutes())}`;
}
