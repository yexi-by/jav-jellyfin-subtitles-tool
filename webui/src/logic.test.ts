import { describe, expect, it } from 'vitest';
import { applyEvent, mergeCandidates, readEvents, targetStatus, type Candidate, type TargetState } from './logic';
const candidate = (id: string, source: 'xunlei' | 'subtitlecat' = 'xunlei'): Candidate => ({ id, source, name: id, format: 'srt', language: source === 'xunlei' ? null : 'zh-CN', match: '待确认', partNumber: null, canDownload: true, unavailableReason: null });
function state(): TargetState {
  return { info: { id: 'part2', versionId: 'version', versionName: '版本', fileName: 'ABC-123-cd2.mp4', partNumber: 2, partCount: 3, runTimeTicks: 1000, query: 'ABC-123 CD2', hasRecord: false, subtitles: [] }, query: 'ABC-123 CD2', candidates: [], sources: {}, status: 'idle', progress: '', notice: '' };
}
describe('分段候选与流式反馈', () => {
  it('迅雷候选全部保留，跨查询的相同候选合并', () => {
    const items = [candidate('未标语言'), candidate('English'), candidate('中文')];
    expect(mergeCandidates(items, [items[0], candidate('站点字幕', 'subtitlecat')])).toEqual([...items, candidate('站点字幕', 'subtitlecat')]);
  });
  it('分段分别累计结果，来源失败不变成无字幕', () => {
    const first = state(); const second = state();
    applyEvent(second, { type: 'candidates', candidates: [candidate('B')] });
    applyEvent(second, { type: 'source', source: 'subtitlecat', state: 'error', message: '超时' });
    applyEvent(second, { type: 'done' });
    expect(first.candidates).toEqual([]); expect(targetStatus(second)).toBe('找到 1 条');
    second.candidates = []; expect(targetStatus(second)).toBe('查询失败或未完成');
    second.sources = {}; expect(targetStatus(second)).toBe('未找到');
  });
  it('跨网络分块和 UTF-8 字节边界接收多个来源和结束事件', async () => {
    const expected = [{ type: 'source', source: 'xunlei', state: 'searching' }, { type: 'error', message: '网络失败' }, { type: 'done' }];
    const data = new TextEncoder().encode(expected.map(value => JSON.stringify(value)).join('\n'));
    const stream = new ReadableStream<Uint8Array>({ start(controller) { for (const byte of data) controller.enqueue(Uint8Array.of(byte)); controller.close(); } });
    const events: Record<string, unknown>[] = [];
    await readEvents(stream, event => events.push(event)); expect(events).toEqual(expected);
  });
});
