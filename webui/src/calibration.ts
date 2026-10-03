import Hls from 'hls.js';
import css from './style.css?inline';
import type { MediaTarget } from './logic';
import { button, message, node, request, RequestError, timeLabel } from './ui';

interface Cue { index: number; startMilliseconds: number; endMilliseconds: number; text: string }
interface Calibration { fileName: string; format: string; offsetMilliseconds: number; currentHash: string; mediaStamp: string; cues: Cue[] }
interface PlaybackInfo { playSessionId?: string; PlaySessionId?: string; MediaSources: { Id: string; SupportsDirectPlay: boolean; SupportsDirectStream: boolean; TranscodingUrl?: string; Container?: string }[]; ErrorCode?: string }

export function openCalibration(root: string, target: MediaTarget, subtitleId: string, parentSignal: AbortSignal, onSaved: () => Promise<void>): Promise<void> {
  return new Promise(resolve => {
    const abort = new AbortController();
    const host = node('div'); const shadow = host.attachShadow({ mode: 'open' }); const style = node('style'); style.textContent = css;
    const dialog = node('dialog', 'calibration'); const layout = node('div', 'panel');
    const header = node('header'); const heading = node('div'); const title = node('h1', '', '校准字幕'); title.id = 'calibration-title';
    heading.append(node('p', 'eyebrow', target.fileName), title); dialog.setAttribute('aria-labelledby', title.id);
    const video = node('video'); video.controls = true; video.playsInline = true; video.preload = 'metadata';
    const overlay = node('div', 'caption-overlay'); overlay.setAttribute('aria-hidden', 'true');
    const videoBox = node('div', 'video-box'); videoBox.append(video, overlay);
    const cueList = node('select', 'cue-list'); cueList.size = 9; cueList.setAttribute('aria-label', '选择要对齐的字幕');
    const cueText = node('p', 'cue-text');
    const offset = node('input'); offset.type = 'number'; offset.step = '0.001'; offset.min = '-86400'; offset.max = '86400'; offset.value = '0';
    const offsetLabel = node('label', 'field-label', '整体偏移（秒，正数延后，负数提前）'); offsetLabel.append(offset);
    const status = node('p', 'status-text'); status.setAttribute('role', 'status'); status.setAttribute('aria-live', 'polite');
    const errorActions = node('div', 'confirm-actions');
    let calibration: Calibration | undefined; let hls: Hls | undefined; let sessionId = ''; let closed = false; let saving = false;
    const deviceId = 'jav-calibration-' + (crypto.randomUUID?.() ?? Date.now().toString(36) + Math.random().toString(36).slice(2));
    const previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;

    function close(): void {
      if (closed) return; closed = true;
      parentSignal.removeEventListener('abort', close); abort.abort(); video.pause(); hls?.destroy(); video.removeAttribute('src'); video.load();
      dialog.close(); host.remove();
      if (sessionId) {
        const cleanup = new AbortController(); const timeout = setTimeout(() => cleanup.abort(), 5000);
        void request('Videos/ActiveEncodings?deviceId=' + encodeURIComponent(deviceId) + '&playSessionId=' + encodeURIComponent(sessionId), cleanup.signal, undefined, 'DELETE').catch(() => undefined).finally(() => clearTimeout(timeout));
      }
      previousFocus?.isConnected && previousFocus.focus(); resolve();
    }
    function fail(error: unknown): void { if (!abort.signal.aborted) { status.textContent = message(error); status.classList.add('error-text'); } }
    function currentOffset(): number { return Math.round(Number(offset.value) * 1000); }
    function selectedCue(): Cue | undefined { return calibration?.cues[Number(cueList.value)]; }
    function paint(): void {
      const shift = currentOffset();
      const time = video.currentTime * 1000;
      overlay.textContent = calibration?.cues.filter(cue => Math.max(0, cue.startMilliseconds + shift) <= time && cue.endMilliseconds + shift > time).map(cue => cue.text).join('\n') ?? '';
      const cue = selectedCue(); cueText.textContent = cue ? `${timeLabel(cue.startMilliseconds + shift)} → ${timeLabel(cue.endMilliseconds + shift)}\n${cue.text}` : '';
      save.disabled = saving || !calibration || !Number.isFinite(shift) || !offset.validity.valid;
      anchor.disabled = saving || !calibration || video.readyState < 1;
    }
    function nudge(seconds: number): void { offset.value = String(Math.round((Number(offset.value) + seconds) * 1000) / 1000); paint(); }
    async function load(restart = false): Promise<void> {
      errorActions.replaceChildren(); status.classList.remove('error-text'); status.textContent = '正在读取字幕时间轴…';
      try {
        calibration = await (await request(root + '/calibration/open', abort.signal, { targetId: target.id, subtitleId, restart })).json() as Calibration;
        if (abort.signal.aborted) return;
        title.textContent = calibration.fileName; offset.value = String(calibration.offsetMilliseconds / 1000); cueList.replaceChildren();
        for (const cue of calibration.cues) { const option = node('option', '', `${timeLabel(cue.startMilliseconds)}  ${cue.text.replace(/\s+/g, ' ').slice(0, 90)}`); option.value = String(cue.index); cueList.append(option); }
        cueList.value = '0'; status.textContent = '选择一句字幕，把视频暂停在对应对白开始处，再点击“对齐到这里”。'; paint();
      } catch (error) {
        fail(error);
        if (error instanceof RequestError && error.status === 409) errorActions.append(button('确认以当前字幕重新开始', () => void load(true)));
      }
    }
    async function saveOffset(): Promise<void> {
      if (!calibration || saving || !offset.validity.valid) return;
      saving = true; paint(); status.classList.remove('error-text'); status.textContent = '正在保存校准结果…';
      try {
        const result = await (await request(root + '/calibration/save', abort.signal, { targetId: target.id, subtitleId, offsetMilliseconds: currentOffset(), currentHash: calibration.currentHash, mediaStamp: calibration.mediaStamp })).json() as { message: string };
        await load(); await onSaved(); status.textContent = result.message;
      } catch (error) { fail(error); }
      finally { saving = false; paint(); }
    }
    const anchor = button('对齐到这里', () => {
      const cue = selectedCue(); if (!cue) return;
      video.pause(); offset.value = String((Math.round(video.currentTime * 1000) - cue.startMilliseconds) / 1000); paint();
      status.textContent = '已更新预览。播放几秒核对，再跳到后面检查另一处对白。'; status.classList.remove('error-text');
    }, 'primary'); anchor.disabled = true;
    const save = button('保存校准', () => void saveOffset(), 'primary'); save.disabled = true;
    const restore = button('恢复原始时间', () => { offset.value = '0'; paint(); void saveOffset(); });
    const seek = button('跳到这句字幕', () => { const cue = selectedCue(); if (cue && video.readyState >= 1) video.currentTime = Math.max(0, (cue.startMilliseconds + currentOffset()) / 1000); });
    const closeButton = button('×', close, 'close'); closeButton.setAttribute('aria-label', '关闭校准面板'); header.append(heading, closeButton);
    const controls = node('div', 'calibration-controls'); const adjust = node('div', 'adjust');
    adjust.append(button('提前 0.5 秒', () => nudge(-0.5)), button('延后 0.5 秒', () => nudge(0.5)));
    const actions = node('div', 'adjust'); actions.append(seek, anchor);
    const persistence = node('div', 'adjust'); persistence.append(restore, save);
    controls.append(node('h2', 'section-title', '字幕时间轴'), cueList, cueText, actions, offsetLabel, adjust, persistence, status, errorActions,
      node('p', 'muted', '保存会更新当前外挂字幕，原稿留在插件数据目录。下次播放这份字幕会沿用校准结果。'));
    const body = node('div', 'calibration-body'); body.append(videoBox, controls); layout.append(header, body); dialog.append(layout); shadow.append(style, dialog); document.body.append(host); dialog.showModal();
    dialog.addEventListener('cancel', event => { event.preventDefault(); close(); }); parentSignal.addEventListener('abort', close, { once: true });
    offset.addEventListener('input', paint); cueList.addEventListener('change', paint); video.addEventListener('timeupdate', paint); video.addEventListener('loadedmetadata', paint);
    video.addEventListener('error', () => fail(new Error('视频预览加载失败，请检查 Jellyfin 的播放权限与转码日志。')));

    async function preview(): Promise<void> {
      const client = window.ApiClient!;
      const result = await (await request(`Items/${target.id}/PlaybackInfo?UserId=${encodeURIComponent(client.getCurrentUserId())}`, abort.signal, {
        UserId: client.getCurrentUserId(), MediaSourceId: target.id, DeviceId: deviceId, StartTimeTicks: 0, IsPlayback: true,
        EnableDirectPlay: true, EnableDirectStream: true, EnableTranscoding: true, SubtitleStreamIndex: -1,
        DeviceProfile: {
          Name: 'JAV subtitle calibration', MaxStreamingBitrate: 12000000, MaxStaticBitrate: 200000000,
          DirectPlayProfiles: [
            { Container: 'mp4,m4v,mov', Type: 'Video', VideoCodec: 'h264', AudioCodec: 'aac,mp3' },
            { Container: 'webm', Type: 'Video', VideoCodec: 'vp8,vp9', AudioCodec: 'vorbis,opus' }
          ],
          TranscodingProfiles: [{ Container: 'ts', Type: 'Video', Protocol: 'hls', Context: 'Streaming', VideoCodec: 'h264', AudioCodec: 'aac', MaxAudioChannels: '2', MinSegments: 1, SegmentLength: 4, BreakOnNonKeyFrames: false }],
          CodecProfiles: [], SubtitleProfiles: []
        }
      }, undefined, deviceId)).json() as PlaybackInfo;
      if (abort.signal.aborted) return;
      const media = result.MediaSources?.find(source => source.Id === target.id) ?? result.MediaSources?.[0];
      if (!media || result.ErrorCode) throw new Error('Jellyfin 无法为这一段视频提供预览。');
      sessionId = result.PlaySessionId ?? result.playSessionId ?? '';
      if (media.SupportsDirectPlay) {
        video.src = client.getUrl(`Videos/${target.id}/stream?Static=true&MediaSourceId=${encodeURIComponent(media.Id)}&api_key=${encodeURIComponent(client.accessToken())}`);
      } else {
        if (!media.TranscodingUrl) throw new Error('该视频需要转码，请检查当前账号的转码权限。');
        const base = new URL('.', client.getUrl('Videos'));
        const path = media.TranscodingUrl;
        const url = path.startsWith(base.pathname.replace(/\/$/, '') + '/') && base.pathname !== '/' ? new URL(path, base.origin).href : client.getUrl(path.replace(/^\//, ''));
        if (Hls.isSupported()) {
          hls = new Hls({ maxBufferLength: 20, backBufferLength: 15 }); hls.loadSource(url); hls.attachMedia(video);
          hls.on(Hls.Events.ERROR, (_event, data) => { if (data.fatal) fail(new Error('视频预览转码或网络中断，请重新打开校准面板。')); });
        } else if (video.canPlayType('application/vnd.apple.mpegurl')) video.src = url;
        else throw new Error('当前浏览器不支持此视频的预览格式。');
      }
    }
    void load(); void preview().catch(fail);
  });
}
