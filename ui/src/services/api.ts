let csrf = "";
const base = import.meta.env.VITE_API_BASE || "/api";
if (base != "/api")
  throw new Error(
    "Use the same-origin /api proxy. External API origins are unsupported.",
  );
export async function refreshCsrf() {
  const r = await fetch(`${base}/auth/csrf`, { credentials: "same-origin" });
  if (!r.ok) throw new Error("Cannot connect to API");
  csrf = (await r.json()).token;
}
export async function api<T = unknown>(
  path: string,
  body?: unknown,
): Promise<T> {
  if (body !== undefined && !csrf) await refreshCsrf();
  const r = await fetch(base + path, {
    credentials: "same-origin",
    method: body === undefined ? "GET" : "POST",
    headers:
      body === undefined
        ? {}
        : { "Content-Type": "application/json", "X-CSRF-TOKEN": csrf },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  if (!r.ok) {
    const data = await r.json().catch(() => ({}));
    throw new Error(
      data.error ||
        (r.status === 401
          ? "Please sign in with your local operator password."
          : `Request failed (${r.status})`),
    );
  }
  return r.json();
}
