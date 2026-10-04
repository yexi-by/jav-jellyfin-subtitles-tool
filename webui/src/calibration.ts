import Hls from 'hls.js';
import css from './style.css?inline';
import type { MediaTarget } from './logic';
import { button, message, node, request, RequestError, timeLabel } from './ui';

interface Cue { index: number; startMilliseconds: number; endMilliseconds: number; text: string }
interface AlignmentOptions { available: boolean; message: string; audioTracks: { index: number; label: string; isDefault: boolean }[]; defaultAudioIndex: number | null; mediaStartMilliseconds: number }
interface Calibration { fileName: string; format: string; currentHash: string; mediaStamp: string; cues: Cue[]; alignment: AlignmentOptions }
interface AlignmentAnchor { cueStartId: number; speechQuote: string; subtitleQuote: string; videoMilliseconds: number }
interface AlignmentJob { id: string; state: string; stage: string; completed: number; total: number; offsetMilliseconds: number | null; currentHash: string; mediaStamp: string; audioIndex: number; anchors: AlignmentAnchor[]; message: string }
interface PlaybackInfo { playSessionId?: string; PlaySessionId?: string; MediaSources: { Id: string; SupportsDirectPlay: boolean; SupportsDirectStream: boolean; TranscodingUrl?: string; Container?: string }[]; ErrorCode?: string }

export function openCalibration(root: string, target: MediaTarget, subtitleId: string, parentSignal: AbortSignal, onSaved: () => Promise<void>): Promise<void> {
  const subtitle = target.subtitles.find(value => value.canCalibrate && value.id === subtitleId);
  if (!subtitle) return Promise.reject(new Error('所选字幕已不存在，请刷新字幕面板后重试。'));
  return new Promise(resolve => {
    const abort = new AbortController();
    const host = node('div'); const shadow = host.attachShadow({ mode: 'open' }); const style = node('style'); style.textContent = css;
    const dialog = node('dialog', 'calibration'); const layout = node('div', 'panel');
    const header = node('header'); const heading = node('div'); const title = node('h1', '', '字幕校准'); title.id = 'calibration-title';
    heading.append(node('p', 'eyebrow', target.fileName), title); dialog.setAttribute('aria-labelledby', title.id);
    const video = node('video'); video.controls = true; video.playsInline = true; video.preload = 'metadata';
    const overlay = node('div', 'caption-overlay'); overlay.setAttribute('aria-hidden', 'true');
    const videoBox = node('div', 'video-box'); videoBox.append(video, overlay);
    const cueList = node('select', 'cue-list'); cueList.size = 6; cueList.setAttribute('aria-label', '选择要对齐的字幕');
    const cueText = node('p', 'cue-text');
    const subtitleLabel = node('p', 'muted subtitle-file', subtitle.fileName); subtitleLabel.title = subtitle.fileName;
    const offset = node('input'); offset.type = 'number'; offset.step = '0.001'; offset.min = '-86400'; offset.max = '86400'; offset.value = '0';
    const offsetLabel = node('label', 'field-label', '本次调整（秒，正数延后，负数提前）'); offsetLabel.append(offset);
    const status = node('p', 'status-text'); status.setAttribute('role', 'status'); status.setAttribute('aria-live', 'polite');
    const errorActions = node('div', 'confirm-actions');
    let calibration: Calibration | undefined; let hls: Hls | undefined; let sessionId = ''; let closed = false; let saving = false;
    let alignmentJob: AlignmentJob | undefined; let analyzing = false; let audioIndex: number | null = null;
    const audioTrack = node('select', 'alignment-audio'); audioTrack.setAttribute('aria-label', '预览和自动对齐使用的音轨'); audioTrack.hidden = true;
    const alignmentActions = node('div', 'adjust alignment-actions');
    const anchorText = node('p', 'anchor-text'); anchorText.hidden = true;
    const anchorChoice = node('select'); anchorChoice.setAttribute('aria-label', '选择核对的对白'); anchorChoice.hidden = true;
    const deviceId = 'jav-calibration-' + (crypto.randomUUID?.() ?? Date.now().toString(36) + Math.random().toString(36).slice(2));
    const previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;

    function close(): void {
      if (closed) return; closed = true;
      parentSignal.removeEventListener('abort', close); abort.abort(); void stopPreview();
      dialog.close(); host.remove();
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
      save.disabled = saving || analyzing || !calibration || !Number.isFinite(shift) || !offset.validity.valid;
      anchor.disabled = saving || analyzing || !calibration || video.readyState < 1;
      offset.disabled = saving || analyzing;
      earlier.disabled = later.disabled = offset.disabled;
      automatic.disabled = saving || analyzing || !calibration?.alignment.available;
      afterPosition.disabled = automatic.disabled || video.readyState < 1;
      cancelAlignment.hidden = !analyzing;
      audioTrack.disabled = saving || analyzing;
      previewAnchor.disabled = saving || analyzing || video.readyState < 1;
      anchorChoice.disabled = saving || analyzing;
    }
    function nudge(seconds: number): void { if (saving || analyzing) return; offset.value = String(Math.round((Number(offset.value) + seconds) * 1000) / 1000); paint(); }
    async function load(restart = false): Promise<void> {
      calibration = undefined;
      errorActions.replaceChildren(); status.classList.remove('error-text'); status.textContent = '正在读取字幕时间轴…';
      try {
        calibration = await (await request(root + '/calibration/open', abort.signal, { targetId: target.id, subtitleId, restart })).json() as Calibration;
        if (abort.signal.aborted) return;
        offset.value = '0'; cueList.replaceChildren();
        anchorText.hidden = anchorChoice.hidden = previewAnchor.hidden = true;
        audioTrack.replaceChildren();
        for (const track of calibration.alignment.audioTracks) { const option = node('option', '', track.label); option.value = String(track.index); audioTrack.append(option); }
        if (!calibration.alignment.audioTracks.some(track => track.index === audioIndex)) audioIndex = calibration.alignment.defaultAudioIndex;
        if (audioIndex !== null) audioTrack.value = String(audioIndex);
        audioTrack.hidden = calibration.alignment.audioTracks.length < 2;
        for (const cue of calibration.cues) { const option = node('option', '', `${timeLabel(cue.startMilliseconds)}  ${cue.text.replace(/\s+/g, ' ').slice(0, 90)}`); option.value = String(cue.index); cueList.append(option); }
        cueList.value = '0'; status.textContent = calibration.alignment.available ? '可自动匹配实际台词，或选择一句字幕手动对齐。' : calibration.alignment.message; paint();
      } catch (error) {
        fail(error);
        if (error instanceof RequestError && error.status === 409) errorActions.append(button('以当前字幕继续校准', () => void load(true)));
      }
    }
    async function saveOffset(): Promise<void> {
      if (!calibration || saving || analyzing || !offset.validity.valid) return;
      saving = true; paint(); status.classList.remove('error-text'); status.textContent = '正在保存校准结果…';
      try {
        const result = await (await request(root + '/calibration/save', abort.signal, { targetId: target.id, subtitleId, offsetMilliseconds: currentOffset(), currentHash: calibration.currentHash, mediaStamp: calibration.mediaStamp })).json() as { message: string };
        alignmentJob = undefined; await load(); await onSaved(); status.textContent = result.message;
      } catch (error) { fail(error); }
      finally { saving = false; paint(); }
    }
    const anchor = button('对齐到这里', () => {
      const cue = selectedCue(); if (!cue) return;
      video.pause(); offset.value = String((Math.round(video.currentTime * 1000) - cue.startMilliseconds) / 1000); paint();
      status.textContent = '已更新预览。播放几秒核对，再跳到后面检查另一处对白。'; status.classList.remove('error-text');
    }, 'primary'); anchor.disabled = true;
    const save = button('保存校准', () => void saveOffset(), 'primary'); save.disabled = true;
    const automatic = button('自动对齐', () => void startAlignment(null));
    const afterPosition = button('从当前播放位置之后匹配', () => { video.pause(); void startAlignment(Math.round(video.currentTime * 1000)); });
    const cancelAlignment = button('取消分析', () => void cancelAnalysis()); cancelAlignment.hidden = true;
    function chosenAnchor(): AlignmentAnchor | undefined { return alignmentJob?.anchors[Number(anchorChoice.value)]; }
    function showAnchor(): void { const found = chosenAnchor(); anchorText.textContent = found ? `实际台词：${found.speechQuote}\n对应字幕：${found.subtitleQuote}` : ''; }
    const previewAnchor = button('预览对应对白', () => { const found = chosenAnchor(); if (!found) return; cueList.value = String(found.cueStartId); video.currentTime = Math.max(0, found.videoMilliseconds / 1000 - 1); paint(); void video.play().catch(fail); }); previewAnchor.hidden = true;
    anchorChoice.addEventListener('change', showAnchor);
    alignmentActions.append(automatic, afterPosition, cancelAlignment);
    const seek = button('跳到这句字幕', () => { const cue = selectedCue(); if (cue && video.readyState >= 1) video.currentTime = Math.max(0, (cue.startMilliseconds + currentOffset()) / 1000); });
    const closeButton = button('×', close, 'close'); closeButton.setAttribute('aria-label', '关闭校准面板'); header.append(heading, closeButton);
    const controls = node('div', 'calibration-controls'); const adjust = node('div', 'adjust');
    const earlier = button('提前 0.5 秒', () => nudge(-0.5)); const later = button('延后 0.5 秒', () => nudge(0.5));
    adjust.append(earlier, later);
    const actions = node('div', 'adjust'); actions.append(seek, anchor);
    adjust.append(save);
    const timeline = node('section', 'subtitle-timeline'); timeline.append(node('h2', 'section-title', '字幕时间轴'), subtitleLabel, audioTrack);
    timeline.append(cueList);
    const subtitleControls = node('section', 'calibration-controls subtitle-adjustments'); subtitleControls.append(node('h2', 'section-title', '字幕校准'), alignmentActions, anchorChoice, anchorText, previewAnchor, cueText, actions, offsetLabel, adjust,
      node('p', 'muted', '保存直接调整字幕；零点前被裁去的条目无法恢复。'));
    controls.append(subtitleControls, status, errorActions);
    const media = node('div', 'calibration-media'); media.append(videoBox, timeline);
    const body = node('div', 'calibration-body'); body.append(media, controls); layout.append(header, body); dialog.append(layout); shadow.append(style, dialog); document.body.append(host); dialog.showModal();
    dialog.addEventListener('cancel', event => { event.preventDefault(); close(); }); parentSignal.addEventListener('abort', close, { once: true });
    offset.addEventListener('input', paint); cueList.addEventListener('change', paint); video.addEventListener('timeupdate', paint); video.addEventListener('loadedmetadata', paint);
    audioTrack.addEventListener('change', () => { audioIndex = Number(audioTrack.value); anchorText.hidden = anchorChoice.hidden = previewAnchor.hidden = true; void (async () => { await stopPreview(); await preview(); })().catch(fail); });
    video.addEventListener('error', () => fail(new Error('视频预览加载失败，请检查 Jellyfin 的播放权限与转码日志。')));

    async function stopPreview(): Promise<void> {
      video.pause(); hls?.destroy(); hls = undefined; video.removeAttribute('src'); video.load();
      const oldSession = sessionId; sessionId = '';
      if (oldSession) {
        const cleanup = new AbortController(); const timeout = setTimeout(() => cleanup.abort(), 10000);
        try { await request('Videos/ActiveEncodings?deviceId=' + encodeURIComponent(deviceId) + '&playSessionId=' + encodeURIComponent(oldSession), cleanup.signal, undefined, 'DELETE'); }
        catch { /* 服务端结束的预览会话无需重复清理。 */ }
        finally { clearTimeout(timeout); }
      }
    }
    async function startAlignment(startMilliseconds: number | null): Promise<void> {
      if (!calibration || saving || analyzing) return;
      analyzing = true; anchorText.hidden = anchorChoice.hidden = previewAnchor.hidden = true; paint();
      try {
        const job = await (await request(root + '/calibration/auto/start', abort.signal, { targetId: target.id, subtitleId, currentHash: calibration.currentHash, mediaStamp: calibration.mediaStamp, audioIndex, startMilliseconds })).json() as AlignmentJob;
        await observeAlignment(job);
      } catch (error) { fail(error); }
      finally { analyzing = false; paint(); }
    }
    async function observeAlignment(initial: AlignmentJob): Promise<void> {
      alignmentJob = initial; analyzing = initial.state === 'running'; paint();
      while (!abort.signal.aborted && alignmentJob.state === 'running') {
        status.textContent = `${alignmentJob.message} 已读取 ${alignmentJob.completed}/${alignmentJob.total} 段`; status.classList.remove('error-text');
        await new Promise<void>(done => { const timer = setTimeout(finish, 1000); function finish(): void { clearTimeout(timer); abort.signal.removeEventListener('abort', finish); done(); } abort.signal.addEventListener('abort', finish, { once: true }); });
        if (abort.signal.aborted) return;
        const result = await (await request(root + '/calibration/auto?targetId=' + encodeURIComponent(target.id), abort.signal)).json() as { job: AlignmentJob | null };
        if (!result.job) throw new Error('分析任务已清除，请重新读取字幕并分析。');
        alignmentJob = result.job;
      }
      if (abort.signal.aborted) return;
      analyzing = false;
      if (alignmentJob.state === 'ready' && alignmentJob.anchors.length >= 2 && calibration && alignmentJob.currentHash === calibration.currentHash && alignmentJob.mediaStamp === calibration.mediaStamp && alignmentJob.audioIndex === audioIndex && alignmentJob.offsetMilliseconds !== null) {
        offset.value = String(alignmentJob.offsetMilliseconds / 1000);
        anchorChoice.replaceChildren();
        alignmentJob.anchors.forEach((found, index) => { const option = node('option', '', `核对对白 ${index + 1} · ${timeLabel(found.videoMilliseconds)}`); option.value = String(index); anchorChoice.append(option); });
        anchorChoice.value = '0'; showAnchor();
        anchorText.hidden = anchorChoice.hidden = previewAnchor.hidden = false;
      }
      status.textContent = alignmentJob.message; status.classList.toggle('error-text', ['error', 'invalid'].includes(alignmentJob.state)); paint();
    }
    async function cancelAnalysis(): Promise<void> {
      if (!alignmentJob || !analyzing) return;
      cancelAlignment.disabled = true;
      try { await request(root + '/calibration/auto/cancel', abort.signal, { targetId: target.id, jobId: alignmentJob.id }); }
      catch (error) { fail(error); }
      finally { cancelAlignment.disabled = false; }
    }
    async function preview(): Promise<void> {
      const client = window.ApiClient!;
      const direct = (calibration?.alignment.audioTracks.length ?? 1) < 2 && Math.abs(calibration?.alignment.mediaStartMilliseconds ?? 0) < 100;
      const result = await (await request(`Items/${target.id}/PlaybackInfo?UserId=${encodeURIComponent(client.getCurrentUserId())}`, abort.signal, {
        AudioStreamIndex: audioIndex ?? undefined,
        UserId: client.getCurrentUserId(), MediaSourceId: target.id, DeviceId: deviceId, StartTimeTicks: 0, IsPlayback: true,
        EnableDirectPlay: direct, EnableDirectStream: true, EnableTranscoding: true, SubtitleStreamIndex: -1,
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
      if (media.SupportsDirectPlay && direct) {
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
    void (async () => {
      await load();
      const result = calibration ? await (await request(root + '/calibration/auto?targetId=' + encodeURIComponent(target.id), abort.signal)).json() as { job: AlignmentJob | null } : { job: null };
      if (result.job && calibration?.alignment.audioTracks.some(track => track.index === result.job?.audioIndex)) { audioIndex = result.job.audioIndex; audioTrack.value = String(audioIndex); }
      await preview(); paint(); if (result.job) await observeAlignment(result.job);
    })().catch(fail);
  });
}
