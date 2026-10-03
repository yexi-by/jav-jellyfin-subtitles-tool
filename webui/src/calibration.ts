import Hls from 'hls.js';
import css from './style.css?inline';
import type { MediaTarget } from './logic';
import { button, message, node, request, RequestError, timeLabel } from './ui';

interface Cue { index: number; startMilliseconds: number; endMilliseconds: number; text: string }
interface Calibration { fileName: string; format: string; currentHash: string; mediaStamp: string; cues: Cue[] }
interface TrimPlan { id: string; requestedMilliseconds: number; cutMilliseconds: number; durationMilliseconds: number }
interface TrimJob { id: string; state: string; progress: number; cutMilliseconds: number; message: string }
interface PlaybackInfo { playSessionId?: string; PlaySessionId?: string; MediaSources: { Id: string; SupportsDirectPlay: boolean; SupportsDirectStream: boolean; TranscodingUrl?: string; Container?: string }[]; ErrorCode?: string }

export function openCalibration(root: string, target: MediaTarget, subtitleId: string | null, parentSignal: AbortSignal, onSaved: (videoChanged?: boolean) => Promise<void>, canTrim: boolean): Promise<void> {
  return new Promise(resolve => {
    const abort = new AbortController();
    const host = node('div'); const shadow = host.attachShadow({ mode: 'open' }); const style = node('style'); style.textContent = css;
    const dialog = node('dialog', 'calibration'); const layout = node('div', 'panel');
    const header = node('header'); const heading = node('div'); const title = node('h1', '', canTrim ? '裁切片头与校准' : '校准字幕'); title.id = 'calibration-title';
    heading.append(node('p', 'eyebrow', target.fileName), title); dialog.setAttribute('aria-labelledby', title.id);
    const video = node('video'); video.controls = true; video.playsInline = true; video.preload = 'metadata';
    const overlay = node('div', 'caption-overlay'); overlay.setAttribute('aria-hidden', 'true');
    const videoBox = node('div', 'video-box'); videoBox.append(video, overlay);
    const cueList = node('select', 'cue-list'); cueList.size = 9; cueList.setAttribute('aria-label', '选择要对齐的字幕');
    const cueText = node('p', 'cue-text');
    const subtitleChoice = node('select'); subtitleChoice.setAttribute('aria-label', '校准的字幕');
    const subtitleLabel = node('label', 'field-label', '校准的字幕'); subtitleLabel.append(subtitleChoice);
    for (const subtitle of target.subtitles.filter(value => value.canCalibrate)) { const option = node('option', '', subtitle.fileName); option.value = subtitle.id; subtitleChoice.append(option); }
    subtitleChoice.value = subtitleId ?? subtitleChoice.options[0]?.value ?? ''; subtitleId = subtitleChoice.value;
    const offset = node('input'); offset.type = 'number'; offset.step = '0.001'; offset.min = '-86400'; offset.max = '86400'; offset.value = '0';
    const offsetLabel = node('label', 'field-label', '本次调整（秒，正数延后，负数提前）'); offsetLabel.append(offset);
    const status = node('p', 'status-text'); status.setAttribute('role', 'status'); status.setAttribute('aria-live', 'polite');
    const errorActions = node('div', 'confirm-actions');
    let calibration: Calibration | undefined; let hls: Hls | undefined; let sessionId = ''; let closed = false; let saving = false;
    let trimPlan: TrimPlan | undefined; let trimJob: TrimJob | undefined; let locating = false; let starting = false; let planGeneration = 0;
    const cutPoint = node('input'); cutPoint.type = 'number'; cutPoint.step = '0.001'; cutPoint.min = '0.001'; cutPoint.value = '0';
    const cutLabel = node('label', 'field-label', '正片起点（秒）'); cutLabel.append(cutPoint);
    const cutSummary = node('p', 'muted', '选择正片起点，定位之后的首个关键帧。');
    const trimProgress = node('progress'); trimProgress.max = 100; trimProgress.value = 0; trimProgress.hidden = true; trimProgress.setAttribute('aria-label', '裁切进度');
    const deviceId = 'jav-calibration-' + (crypto.randomUUID?.() ?? Date.now().toString(36) + Math.random().toString(36).slice(2));
    const previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;

    function close(): void {
      if (closed) return; closed = true;
      confirmation.close();
      parentSignal.removeEventListener('abort', close); abort.abort(); void stopPreview();
      dialog.close(); host.remove();
      previousFocus?.isConnected && previousFocus.focus(); resolve();
    }
    function fail(error: unknown): void { if (!abort.signal.aborted) { status.textContent = message(error); status.classList.add('error-text'); } }
    function currentOffset(): number { return Math.round(Number(offset.value) * 1000); }
    function selectedCue(): Cue | undefined { return calibration?.cues[Number(cueList.value)]; }
    function trimming(): boolean { return starting || !!trimJob && ['copying', 'verifying', 'publishing'].includes(trimJob.state); }
    function paint(): void {
      const shift = currentOffset();
      const time = video.currentTime * 1000;
      overlay.textContent = calibration?.cues.filter(cue => Math.max(0, cue.startMilliseconds + shift) <= time && cue.endMilliseconds + shift > time).map(cue => cue.text).join('\n') ?? '';
      const cue = selectedCue(); cueText.textContent = cue ? `${timeLabel(cue.startMilliseconds + shift)} → ${timeLabel(cue.endMilliseconds + shift)}\n${cue.text}` : '';
      save.disabled = saving || trimming() || !calibration || !Number.isFinite(shift) || !offset.validity.valid;
      anchor.disabled = saving || trimming() || !calibration || video.readyState < 1;
      subtitleChoice.disabled = saving || trimming(); offset.disabled = saving || trimming();
      locate.disabled = locating || saving || trimming() || !cutPoint.validity.valid;
      permanent.disabled = !trimPlan || locating || saving || trimming();
      usePosition.disabled = trimming() || video.readyState < 1;
      viewKeyframe.disabled = !trimPlan || trimming() || video.readyState < 1;
      cutPoint.disabled = trimming() || saving;
      cancelTrim.hidden = !trimJob || !['copying', 'verifying'].includes(trimJob.state);
    }
    function nudge(seconds: number): void { offset.value = String(Math.round((Number(offset.value) + seconds) * 1000) / 1000); paint(); }
    async function load(restart = false): Promise<void> {
      calibration = undefined;
      if (!subtitleId) { subtitleControls.hidden = true; paint(); return; }
      subtitleControls.hidden = false;
      errorActions.replaceChildren(); status.classList.remove('error-text'); status.textContent = '正在读取字幕时间轴…';
      try {
        calibration = await (await request(root + '/calibration/open', abort.signal, { targetId: target.id, subtitleId, restart })).json() as Calibration;
        if (abort.signal.aborted) return;
        offset.value = '0'; cueList.replaceChildren();
        for (const cue of calibration.cues) { const option = node('option', '', `${timeLabel(cue.startMilliseconds)}  ${cue.text.replace(/\s+/g, ' ').slice(0, 90)}`); option.value = String(cue.index); cueList.append(option); }
        cueList.value = '0'; status.textContent = '选择一句字幕，把视频暂停在对应对白开始处，再点击“对齐到这里”。'; paint();
      } catch (error) {
        fail(error);
        if (error instanceof RequestError && error.status === 409) errorActions.append(button('以当前字幕继续校准', () => void load(true)));
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
    const seek = button('跳到这句字幕', () => { const cue = selectedCue(); if (cue && video.readyState >= 1) video.currentTime = Math.max(0, (cue.startMilliseconds + currentOffset()) / 1000); });
    const closeButton = button('×', close, 'close'); closeButton.setAttribute('aria-label', '关闭校准面板'); header.append(heading, closeButton);
    const controls = node('div', 'calibration-controls'); const adjust = node('div', 'adjust');
    adjust.append(button('提前 0.5 秒', () => nudge(-0.5)), button('延后 0.5 秒', () => nudge(0.5)));
    const actions = node('div', 'adjust'); actions.append(seek, anchor);
    const persistence = node('div', 'adjust'); persistence.append(save);
    const subtitleControls = node('div', 'calibration-controls'); subtitleControls.append(node('h2', 'section-title', '字幕时间轴'), subtitleLabel, cueList, cueText, actions, offsetLabel, adjust, persistence,
      node('p', 'muted', '每次保存直接调整当前字幕，不保留原稿。提前到 0 秒之前的条目会裁去，之后无法恢复。'));
    function clearPlan(): void { planGeneration++; trimPlan = undefined; cutSummary.textContent = '选择正片起点，定位之后的首个关键帧。'; paint(); }
    const usePosition = button('用当前播放位置', () => { video.pause(); cutPoint.value = (Math.round(video.currentTime * 1000) / 1000).toString(); clearPlan(); });
    const locate = button('定位关键帧', () => void locateCut());
    const viewKeyframe = button('预览实际起点', () => { if (trimPlan && video.readyState >= 1) { video.pause(); video.currentTime = trimPlan.cutMilliseconds / 1000; } });
    const permanent = button('确认裁切范围', reviewTrim, 'danger'); permanent.disabled = true;
    const confirmation = node('dialog', 'trim-confirm'); confirmation.setAttribute('aria-labelledby', 'trim-confirm-title');
    const confirmationTitle = node('h2', '', '确认永久裁切'); confirmationTitle.id = 'trim-confirm-title';
    const review = node('p', 'trim-review');
    const confirmCommit = button('确认永久裁切', () => { confirmation.close(); void startTrim(); }, 'danger');
    const confirmCancel = button('取消', () => { confirmation.close(); permanent.focus(); });
    const confirmActions = node('div', 'adjust'); confirmActions.append(confirmCancel, confirmCommit);
    confirmation.append(confirmationTitle, review, node('p', 'muted', '将直接替换视频，原视频和裁去的片头无法恢复。请核对上面的文件与时间。'), confirmActions);
    confirmation.addEventListener('cancel', event => { event.preventDefault(); confirmation.close(); permanent.focus(); });
    function reviewTrim(): void {
      if (!trimPlan || trimming()) return;
      review.textContent = `文件：${target.fileName}\n版本：${target.versionName}${target.partCount > 1 ? ' · 第 ' + target.partNumber + ' 段' : ''}\n删除：00:00:00.000 → ${timeLabel(trimPlan.cutMilliseconds)}\n正片从 ${timeLabel(trimPlan.cutMilliseconds)} 开始，保留约 ${timeLabel(trimPlan.durationMilliseconds - trimPlan.cutMilliseconds)}。`;
      confirmation.showModal(); confirmCancel.focus();
    }
    const cancelTrim = button('取消裁切', () => void cancelJob()); cancelTrim.hidden = true;
    const trimActions = node('div', 'adjust'); trimActions.append(usePosition, locate, viewKeyframe);
    const commitActions = node('div', 'adjust'); commitActions.append(permanent, cancelTrim);
    const trimControls = node('div', 'trim-controls'); trimControls.hidden = !canTrim;
    trimControls.append(node('h2', 'section-title', '永久裁切片头'), cutLabel, trimActions, cutSummary, commitActions, trimProgress,
      node('p', 'muted', '直接替换当前版本的这一段视频，不保留原视频或广告片段。音画保持原画质，字幕时间保持当前设置。关闭窗口后任务继续，可重新打开查看。'));
    controls.append(trimControls, subtitleControls, status, errorActions);
    if (!subtitleId) controls.append(node('p', 'muted', '当前没有可校准的外挂字幕，下载后可在这里对齐对白。'));
    const body = node('div', 'calibration-body'); body.append(videoBox, controls); layout.append(header, body); dialog.append(layout); shadow.append(style, dialog, confirmation); document.body.append(host); dialog.showModal();
    dialog.addEventListener('cancel', event => { event.preventDefault(); close(); }); parentSignal.addEventListener('abort', close, { once: true });
    offset.addEventListener('input', paint); cueList.addEventListener('change', paint); video.addEventListener('timeupdate', paint); video.addEventListener('loadedmetadata', paint);
    cutPoint.addEventListener('input', clearPlan);
    subtitleChoice.addEventListener('change', () => { subtitleId = subtitleChoice.value; void load(); });
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
    async function locateCut(): Promise<void> {
      if (locating || trimming() || !cutPoint.validity.valid) return;
      video.pause(); locating = true; trimPlan = undefined; const generation = ++planGeneration; paint();
      status.classList.remove('error-text'); status.textContent = '正在定位所选位置之后的关键帧…';
      try {
        const result = await (await request(root + '/trim/plan', abort.signal, { targetId: target.id, requestedMilliseconds: Number(cutPoint.value) * 1000 })).json() as TrimPlan;
        if (generation !== planGeneration || abort.signal.aborted) return;
        trimPlan = result;
        cutSummary.textContent = `所选 ${timeLabel(result.requestedMilliseconds)} → 实际起点 ${timeLabel(result.cutMilliseconds)}；保留约 ${timeLabel(result.durationMilliseconds - result.cutMilliseconds)}。`;
        status.textContent = '可先预览实际起点，再永久裁切并替换。';
      } catch (error) { fail(error); }
      finally { locating = false; paint(); }
    }
    async function observeJob(initial: TrimJob): Promise<void> {
      trimJob = initial; paint();
      while (!abort.signal.aborted && trimming()) {
        status.classList.remove('error-text'); status.textContent = `${trimJob.message} ${Math.floor(trimJob.progress)}%`;
        trimProgress.hidden = false; trimProgress.value = trimJob.progress; paint();
        await new Promise<void>(resolveWait => { const timer = setTimeout(done, 1000); function done(): void { clearTimeout(timer); abort.signal.removeEventListener('abort', done); resolveWait(); } abort.signal.addEventListener('abort', done, { once: true }); });
        if (abort.signal.aborted) return;
        const result = await (await request(root + '/trim?targetId=' + encodeURIComponent(target.id), abort.signal)).json() as { job: TrimJob | null };
        if (!result.job) throw new Error('裁切任务已结束或服务器已重启，请刷新媒体信息。');
        trimJob = result.job;
      }
      if (abort.signal.aborted) return;
      trimProgress.hidden = true; clearPlan();
      if (trimJob?.state === 'done') await onSaved(true);
      await preview(); if (subtitleId) await load();
      status.textContent = trimJob?.message ?? ''; status.classList.toggle('error-text', trimJob?.state === 'error'); paint();
    }
    async function startTrim(): Promise<void> {
      if (!trimPlan || trimming()) return;
      starting = true; paint();
      try {
        await stopPreview();
        const job = await (await request(root + '/trim/start', abort.signal, { targetId: target.id, planId: trimPlan.id, confirmed: true })).json() as TrimJob;
        starting = false; await observeJob(job);
      } catch (error) {
        starting = false;
        if (error instanceof RequestError && error.status === 409) clearPlan();
        if (!trimming() && !abort.signal.aborted) void preview().catch(fail);
        fail(error);
      }
      finally { starting = false; paint(); }
    }
    async function cancelJob(): Promise<void> {
      if (!trimJob) return;
      cancelTrim.disabled = true;
      try { await request(root + '/trim/cancel', abort.signal, { targetId: target.id, jobId: trimJob.id }); status.textContent = '正在取消裁切…'; }
      catch (error) { fail(error); }
      finally { cancelTrim.disabled = false; }
    }
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
    async function initialize(): Promise<void> {
      if (canTrim) {
        const result = await (await request(root + '/trim?targetId=' + encodeURIComponent(target.id), abort.signal)).json() as { job: TrimJob | null };
        if (result.job && ['copying', 'verifying', 'publishing'].includes(result.job.state)) { await observeJob(result.job); return; }
      }
      await load(); await preview(); paint();
    }
    void initialize().catch(fail);
  });
}
