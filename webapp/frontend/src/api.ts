export class ApiError extends Error {
  constructor(public status: number, message: string) {
    super(message);
  }
}

async function request<T>(method: string, url: string, body?: unknown): Promise<T> {
  const init: RequestInit = { method, credentials: 'same-origin', headers: {} };
  if (body instanceof FormData) init.body = body;
  else if (body !== undefined) {
    init.body = JSON.stringify(body);
    (init.headers as Record<string, string>)['Content-Type'] = 'application/json';
  }
  const res = await fetch(url, init);
  if (res.status === 401) {
    const login = res.headers.get('X-Login-Url') ?? '/login';
    if (!window.location.pathname.startsWith('/login')) window.location.href = login;
    throw new ApiError(401, 'Please sign in');
  }
  const text = await res.text();
  const data = text ? (() => { try { return JSON.parse(text); } catch { return text; } })() : undefined;
  if (!res.ok) {
    const message = (data && typeof data === 'object' && 'error' in data ? (data as { error: string }).error : null)
      ?? (res.status === 403 ? 'You do not have access to this.' : `Request failed (${res.status})`);
    throw new ApiError(res.status, message);
  }
  return data as T;
}

export const api = {
  get: <T,>(url: string) => request<T>('GET', url),
  post: <T,>(url: string, body?: unknown) => request<T>('POST', url, body ?? {}),
  put: <T,>(url: string, body?: unknown) => request<T>('PUT', url, body ?? {}),
  del: <T,>(url: string) => request<T>('DELETE', url),
  upload: <T,>(url: string, file: File) => {
    const form = new FormData();
    form.append('file', file);
    return request<T>('POST', url, form);
  },
  /** Downloads a file response (exports, templates) through the browser. */
  download: async (url: string, fallbackName: string) => {
    const res = await fetch(url, { credentials: 'same-origin' });
    if (!res.ok) throw new ApiError(res.status, `Download failed (${res.status})`);
    const name = /filename="?([^";]+)"?/.exec(res.headers.get('Content-Disposition') ?? '')?.[1] ?? fallbackName;
    const blob = await res.blob();
    const a = document.createElement('a');
    a.href = URL.createObjectURL(blob);
    a.download = name;
    a.click();
    URL.revokeObjectURL(a.href);
  },
};
