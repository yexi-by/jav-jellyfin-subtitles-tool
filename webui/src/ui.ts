export interface ApiClient { getUrl(path: string): string; accessToken(): string; getCurrentUserId(): string }
declare global { interface Window { ApiClient?: ApiClient; __subtitlesToolLoaded?: boolean } }
export class RequestError extends Error { constructor(message: string, readonly status: number) { super(message); } }

export function node<K extends keyof HTMLElementTagNameMap>(tag: K, className = '', text = ''): HTMLElementTagNameMap[K] {
  const element = document.createElement(tag); element.className = className; element.textContent = text; return element;
}
export function button(text: string, action: () => void, className = ''): HTMLButtonElement {
  const element = node('button', className, text); element.type = 'button'; element.addEventListener('click', action); return element;
}
export async function request(path: string, signal: AbortSignal, body?: unknown, method?: string, deviceId?: string): Promise<Response> {
  const client = window.ApiClient;
  if (!client) throw new Error('Jellyfin 尚未完成加载，请稍后重试。');
  const response = await fetch(client.getUrl(path), {
    method: method ?? (body === undefined ? 'GET' : 'POST'), signal,
    headers: { Authorization: `MediaBrowser ${deviceId ? `Client="JAV Subtitles Tool", Device="Browser", DeviceId="${deviceId}", Version="0.4.0.0", ` : ''}Token="${client.accessToken()}"`, ...(body === undefined ? {} : { 'Content-Type': 'application/json' }) },
    body: body === undefined ? undefined : JSON.stringify(body)
  });
  if (!response.ok) {
    const data = await response.json().catch(() => null) as { message?: string } | null;
    throw new RequestError(data?.message ?? (response.status === 403 ? '当前账号没有操作权限。' : `请求失败（${response.status}），请重试。`), response.status);
  }
  return response;
}
export function message(error: unknown): string { return error instanceof Error ? error.message : '操作失败，请重试。'; }
export function timeLabel(milliseconds: number): string {
  const value = Math.max(0, Math.round(milliseconds));
  return `${Math.floor(value / 3600000).toString().padStart(2, '0')}:${Math.floor(value / 60000 % 60).toString().padStart(2, '0')}:${Math.floor(value / 1000 % 60).toString().padStart(2, '0')}.${(value % 1000).toString().padStart(3, '0')}`;
}
