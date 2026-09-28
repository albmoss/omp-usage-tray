/**
 * Feeds the Windows taskbar widget in %LOCALAPPDATA%\omp-usage-tray with every provider's
 * usage limits. Reads omp's own cached usage reports (the ones behind `omp usage` and the
 * status line), so it adds no network requests and no processes besides the widget.
 *
 * - Registers this omp process in sessions/<pid>; the widget exits when all of them are gone.
 * - Launches omp-usage-tray.exe once per process; the exe itself is single-instance.
 * - Checks every minute and after turns (throttled); rewrites usage.tsv only when this process
 *   has different data that is not older than what another omp process already wrote.
 */
import type { UsageLimit, UsageReport } from "@oh-my-pi/pi-ai";
import type { ExtensionAPI, ExtensionContext } from "@oh-my-pi/pi-coding-agent";
import { spawn } from "node:child_process";
import * as fs from "node:fs";
import * as path from "node:path";

const ROOT = path.join(process.env.LOCALAPPDATA ?? "", "omp-usage-tray");
const EXE = path.join(ROOT, "omp-usage-tray.exe");
const DATA_FILE = path.join(ROOT, "usage.tsv");
const SESSIONS_DIR = path.join(ROOT, "sessions");
const REFRESH_INTERVAL_MS = 60_000;
const MIN_TURN_REFRESH_GAP_MS = 15_000;
const LAUNCHED = Symbol.for("omp-usage-tray.launched");
const HOUR_MS = 3_600_000;
const DAY_MS = 86_400_000;

/** Display name and brand color for the widget's gauges; "mono" follows the theme (white on dark, black on light). */
const PROVIDERS: Record<string, { name: string; color: string }> = {
	anthropic: { name: "Claude", color: "#D97757" },
	"openai-codex": { name: "Codex", color: "mono" },
	"google-antigravity": { name: "Antigravity", color: "#4285F4" },
	"google-gemini-cli": { name: "Gemini CLI", color: "#8E75F2" },
	"github-copilot": { name: "Copilot", color: "#A371F7" },
};
/** Unknown providers get a stable color from this palette, keyed by provider id. */
const FALLBACK_PALETTE = ["#E3B341", "#DB61A2", "#56D4DD", "#7EE787", "#F0883E", "#A5D6FF"];

function providerStyle(provider: string): { name: string; color: string } {
	const known = PROVIDERS[provider];
	if (known) return known;
	let hash = 0;
	for (const ch of provider) hash = (hash * 31 + ch.charCodeAt(0)) >>> 0;
	const name = provider.replace(/[-_]+/g, " ").replace(/\b\w/g, c => c.toUpperCase());
	return { name, color: FALLBACK_PALETTE[hash % FALLBACK_PALETTE.length] };
}

function clean(value: string): string {
	return value.replace(/[\t\r\n]+/g, " ").trim();
}

function usedPercent(limit: UsageLimit): number | undefined {
	if (typeof limit.amount.usedFraction === "number") return limit.amount.usedFraction * 100;
	if (typeof limit.amount.used === "number" && typeof limit.amount.limit === "number" && limit.amount.limit > 0) {
		return (limit.amount.used / limit.amount.limit) * 100;
	}
	return undefined;
}

function windowLabel(limit: UsageLimit): string {
	const duration = limit.window?.durationMs;
	if (duration && duration % DAY_MS === 0) return `${duration / DAY_MS}d`;
	if (duration && duration >= HOUR_MS) return `${Math.round(duration / HOUR_MS)}h`;
	return limit.window?.id ?? limit.scope.windowId ?? "";
}

/** Quota pool a limit belongs to: "Gemini", "Claude+GPT", "gpt-reserve"; "" when the label only restates the window. */
function poolName(limit: UsageLimit): string {
	const label = limit.label.replace(/\(shared\)/i, "").replace(/\s*&\s*/g, "+").trim();
	return clean(limit.scope.modelId ?? (/\b(hours?|days?|week(ly)?)\b/i.test(label) ? "" : label));
}

function serialize(reports: UsageReport[]): string {
	const now = Date.now();
	const times = reports.map(r => Math.round(r.fetchedAt ?? now));
	// "fetched" is the oldest report (shown as the data age); "newest" orders writes from several omp processes.
	const lines = [`fetched\t${Math.min(now, ...times)}`, `newest\t${Math.max(0, ...times)}`];
	const named = reports
		.filter(r => r.limits.length > 0)
		.map(r => ({ report: r, ...providerStyle(r.provider) }))
		.sort((a, b) => a.name.localeCompare(b.name));
	const seenNames = new Map<string, number>();
	for (const { report, name, color } of named) {
		const rows: { pool: string; window: string; pct: number; resetsAt: number; duration: number }[] = [];
		const seen = new Set<string>();
		for (const limit of report.limits) {
			const pct = usedPercent(limit);
			if (pct === undefined) continue;
			// Antigravity reports one shared quota once per backend (Anthropic, OpenAI).
			const key = `${limit.scope.sharedGroup ?? limit.id}\0${limit.scope.windowId ?? ""}`;
			if (seen.has(key)) continue;
			seen.add(key);
			rows.push({
				pool: poolName(limit),
				window: clean(windowLabel(limit)),
				pct,
				resetsAt: limit.window?.resetsAt ?? 0,
				duration: limit.window?.durationMs ?? 0,
			});
		}
		if (rows.length === 0) continue;
		// Keep each pool's windows together (Gemini 5h, Gemini 7d, Claude+GPT 5h, ...); the provider's
		// own unnamed pool first, then pools in report order, shortest window first within a pool.
		const poolOrder = [...new Set(rows.map(r => r.pool))].sort((a, b) => Number(a !== "") - Number(b !== ""));
		rows.sort((a, b) => poolOrder.indexOf(a.pool) - poolOrder.indexOf(b.pool) || a.duration - b.duration);
		// Several accounts of one provider: "Claude", "Claude 2", ...
		const index = (seenNames.get(name) ?? 0) + 1;
		seenNames.set(name, index);
		const displayName = index === 1 ? name : `${name} ${index}`;
		const account = typeof report.metadata?.email === "string" ? report.metadata.email : "";
		lines.push(`provider\t${clean(displayName)}\t${clean(account)}\t${color}`);
		for (const row of rows) {
			const label = row.pool ? `${row.pool} ${row.window}` : row.window;
			lines.push(`limit\t${label}\t${row.pct.toFixed(2)}\t${Math.round(row.resetsAt)}\t${row.window}\t${row.duration}`);
		}
	}
	return `${lines.join("\n")}\n`;
}

function newestOf(content: string): number {
	const match = /^newest\t(\d+)$/m.exec(content);
	return match ? Number(match[1]) : 0;
}

/** Several omp processes share one file: skip identical content and never replace newer data with older. */
function writeIfNewer(file: string, content: string): void {
	let existing = "";
	try {
		existing = fs.readFileSync(file, "utf8");
	} catch {
		// Missing or mid-replace: write.
	}
	if (existing === content || newestOf(existing) > newestOf(content)) return;
	const tmp = `${file}.${process.pid}.tmp`;
	fs.writeFileSync(tmp, content);
	try {
		fs.renameSync(tmp, file);
	} catch {
		// The widget may hold the file open for a moment; the next refresh overwrites it.
		fs.rmSync(tmp, { force: true });
	}
}

/** Once per omp process: register it for the widget's lifetime tracking and start the widget. */
function launchTray(): void {
	const state = globalThis as Record<symbol, unknown>;
	if (state[LAUNCHED]) return;
	state[LAUNCHED] = true;
	fs.mkdirSync(SESSIONS_DIR, { recursive: true });
	const startedAt = Math.round(Date.now() - process.uptime() * 1000);
	fs.writeFileSync(path.join(SESSIONS_DIR, String(process.pid)), String(startedAt));
	if (!fs.existsSync(EXE)) return;
	const child = spawn(EXE, [], { detached: true, stdio: "ignore", windowsHide: true });
	child.on("error", () => {});
	child.unref();
}

export default function usageTray(pi: ExtensionAPI): void {
	if (process.platform !== "win32" || !process.env.LOCALAPPDATA) return;
	let lastRefreshAt = 0;
	let stopTicker: (() => void) | undefined;

	const refresh = async (ctx: ExtensionContext): Promise<void> => {
		lastRefreshAt = Date.now();
		try {
			const reports = await ctx.modelRegistry.authStorage.usage.reports();
			if (reports?.length) writeIfNewer(DATA_FILE, serialize(reports));
		} catch {
			// Usage fetch failures keep the last snapshot; the widget shows its age.
		}
	};

	pi.on("session_start", async (_event, ctx) => {
		if (!ctx.hasUI) return;
		try {
			launchTray();
		} catch {
			return;
		}
		stopTicker?.();
		const handle = ctx.setInterval(() => refresh(ctx), REFRESH_INTERVAL_MS);
		stopTicker = () => ctx.clearTimer(handle);
		await refresh(ctx);
	});
	pi.on("turn_end", async (_event, ctx) => {
		if (!ctx.hasUI || !stopTicker) return;
		if (Date.now() - lastRefreshAt >= MIN_TURN_REFRESH_GAP_MS) await refresh(ctx);
	});
}
