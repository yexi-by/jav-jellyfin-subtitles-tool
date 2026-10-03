export interface Candidate { id: string; source: 'xunlei' | 'subtitlecat'; name: string; format: string; language: string | null; match: string; partNumber: number | null; canDownload: boolean; unavailableReason: string | null }
export interface SavedSubtitle { id: string; fileName: string; format: string; canCalibrate: boolean }
export interface MediaTarget { id: string; versionId: string; versionName: string; fileName: string; partNumber: number; partCount: number; runTimeTicks: number | null; query: string; hasRecord: boolean; subtitles: SavedSubtitle[] }
export interface MediaInfo { rootId: string; selectedTargetId: string; targets: MediaTarget[] }
export interface SourceState { state: string; count?: number; message?: string }
export interface TargetState { info: MediaTarget; query: string; candidates: Candidate[]; sources: Record<string, SourceState>; status: 'idle' | 'searching' | 'done' | 'error'; progress: string; notice: string }

export function mergeCandidates(existing: Candidate[], incoming: Candidate[]): Candidate[] {
  return [...new Map([...existing, ...incoming].map(candidate => [candidate.id, candidate])).values()];
}
export function targetStatus(state: TargetState): string {
  if (state.status === 'searching') return '搜索中';
  if (state.info.subtitles.length) return '已保存';
  if (state.candidates.length) return `找到 ${state.candidates.length} 条`;
  if (state.status === 'error' || Object.values(state.sources).some(source => source.state === 'error' || source.state === 'partial')) return '查询失败或未完成';
  return state.status === 'done' ? '未找到' : '未查询';
}
export function applyEvent(state: TargetState, event: Record<string, unknown>): void {
  if (event.type === 'candidates') state.candidates = mergeCandidates(state.candidates, event.candidates as Candidate[]);
  else if (event.type === 'source') state.sources[String(event.source)] = { state: String(event.state), count: event.count as number | undefined, message: event.message as string | undefined };
  else if (event.type === 'progress') {
    if (event.phase === 'hashing') {
      const total = Number(event.total); const percent = total > 0 ? Math.floor(Number(event.read) * 100 / total) : 100;
      state.progress = `正在读取当前分段，生成视频指纹 · ${percent}%`;
    } else state.progress = event.phase === 'waiting' ? '正在等待指纹计算资源…' : '';
  } else if (event.type === 'done') { state.status = 'done'; state.progress = ''; }
  else if (event.type === 'error') { state.status = 'error'; state.progress = ''; state.notice = String(event.message); }
}
export async function readEvents(body: ReadableStream<Uint8Array>, receive: (event: Record<string, unknown>) => void): Promise<void> {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';
  try {
    while (true) {
      const { done, value } = await reader.read();
      buffer += done ? decoder.decode() : decoder.decode(value, { stream: true });
      let end: number;
      while ((end = buffer.indexOf('\n')) !== -1) {
        const line = buffer.slice(0, end).trim(); buffer = buffer.slice(end + 1);
        if (line) receive(JSON.parse(line));
      }
      if (done) break;
    }
    if (buffer.trim()) receive(JSON.parse(buffer));
  } finally { reader.releaseLock(); }
}
