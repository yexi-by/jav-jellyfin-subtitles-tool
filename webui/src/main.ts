import css from './style.css?inline';
import { applyEvent, readEvents, targetStatus, type Candidate, type MediaInfo, type TargetState } from './logic';
import { button, message, node, request, RequestError } from './ui';

function itemId(): string | null {
  if (!/\/details(?:\?|$)|\/item(?:\?|$)/.test(location.hash)) return null;
  return new URLSearchParams(location.hash.slice(location.hash.indexOf('?') + 1)).get('id');
}
function page(): HTMLElement | null { return [...document.querySelectorAll<HTMLElement>('.itemDetailPage')].find(element => !element.classList.contains('hide') && element.getClientRects().length > 0) ?? null; }
function sourceId(): string | undefined { return page()?.querySelector<HTMLSelectElement>('.selectSource')?.value || undefined; }
function refreshDetails(id: string, selectedSource: string, signal: AbortSignal): Promise<boolean> {
  const currentPage = page(); const select = currentPage?.querySelector<HTMLSelectElement>('.selectSource');
  if (!currentPage || !select || itemId() !== id || signal.aborted) return Promise.resolve(false);
  return new Promise(resolve => {
    const finish = (updated: boolean) => { observer.disconnect(); clearTimeout(timeout); signal.removeEventListener('abort', cancel); resolve(updated); };
    const cancel = () => finish(false);
    const observer = new MutationObserver(() => {
      if ([...select.options].some(option => option.value === selectedSource)) { select.value = selectedSource; select.dispatchEvent(new Event('change', { bubbles: true })); }
      finish(true);
    });
    const timeout = setTimeout(() => finish(false), 10000);
    signal.addEventListener('abort', cancel, { once: true }); observer.observe(select, { childList: true });
    currentPage.dispatchEvent(new CustomEvent('viewbeforehide'));
    currentPage.dispatchEvent(new CustomEvent('viewshow', { bubbles: true, detail: { isRestored: false } }));
  });
}

class Panel {
  private readonly host = node('div');
  private readonly shadow = this.host.attachShadow({ mode: 'open' });
  private readonly dialog = node('dialog');
  private readonly title = node('h1', '', '正在读取视频和分段…');
  private readonly version = node('select');
  private readonly parts = node('nav', 'parts');
  private readonly query = node('input');
  private readonly language = node('select');
  private readonly sourceFilter = node('select');
  private readonly saved = node('div', 'saved');
  private readonly status = node('div', 'status');
  private readonly list = node('div', 'candidates');
  private readonly confirm = node('div', 'confirm');
  private readonly abort = new AbortController();
  private readonly states = new Map<string, TargetState>();
  private readonly searches = new Map<string, AbortController>();
  private readonly partButtons = new Map<string, HTMLButtonElement>();
  private selected = '';
  private busyDownload = false;
  private calibrationOpen = false;
  private canTrim = false;
  private readonly trim = button('裁切片头与校准', () => { const state = this.current(); if (state) void this.calibrate(state, null); });
  private readonly retry = button('搜索这一段', () => void this.search(this.selected), 'primary');
  private readonly all = button('搜索全部分段', () => void this.searchAll());
  private readonly hash = button('按视频文件进一步搜索', () => void this.search(this.selected, true));
  private readonly focusBefore = document.activeElement instanceof HTMLElement ? document.activeElement : null;
  private readonly root: string;

  constructor(private readonly id: string, private readonly onClose: () => void) {
    this.root = `SubtitlesTool/Items/${id}`;
    const style = node('style'); style.textContent = css; this.shadow.append(style, this.dialog);
    this.dialog.setAttribute('aria-labelledby', 'jav-title'); this.title.id = 'jav-title';
    this.dialog.addEventListener('cancel', event => { event.preventDefault(); this.close(true); });
    const layout = node('div', 'panel');
    const header = node('header'); const heading = node('div'); heading.append(node('p', 'eyebrow', 'JAV 字幕'), this.title);
    const close = button('×', () => this.close(true), 'close'); close.setAttribute('aria-label', '关闭字幕面板'); header.append(heading, close);
    const targetToolbar = node('div', 'toolbar target-toolbar'); const versionLabel = node('label', 'field-label', '视频版本');
    this.version.setAttribute('aria-label', '视频版本'); this.version.addEventListener('change', () => {
      const target = [...this.states.values()].find(state => state.info.versionId === this.version.value);
      if (target) { this.buildParts(); this.select(target.info.id); }
    }); versionLabel.append(this.version); targetToolbar.append(versionLabel, this.all);
    this.parts.setAttribute('aria-label', '视频分段');
    const queryToolbar = node('div', 'toolbar search-toolbar');
    const queryLabel = node('label', 'field-label grow', '番号与分段');
    this.query.type = 'search'; this.query.maxLength = 160; this.query.placeholder = '例如 ABC-123 CD2';
    this.query.addEventListener('input', () => { const state = this.current(); if (state) state.query = this.query.value; });
    this.query.addEventListener('keydown', event => { if (event.key === 'Enter') { event.preventDefault(); void this.search(this.selected); } });
    queryLabel.append(this.query);
    const languageLabel = node('label', 'field-label', 'SubtitleCat 语言');
    for (const [value, text] of [['zh', '简体与繁体'], ['zh-CN', '简体中文'], ['zh-TW', '繁体中文'], ['en', '英语'], ['ja', '日语'], ['ko', '韩语'], ['th', '泰语'], ['vi', '越南语'], ['id', '印尼语'], ['all', '全部已有语言']]) {
      const option = node('option', '', text); option.value = value; this.language.append(option);
    }
    languageLabel.append(this.language); queryToolbar.append(queryLabel, languageLabel, this.retry);
    const tools = node('div', 'toolbar compact'); tools.append(this.hash, this.trim);
    const filterLabel = node('label', 'field-label source-filter', '显示来源');
    for (const [value, text] of [['all', '两个来源'], ['xunlei', '迅雷'], ['subtitlecat', 'SubtitleCat']]) { const option = node('option', '', text); option.value = value; this.sourceFilter.append(option); }
    this.sourceFilter.addEventListener('change', () => this.render()); filterLabel.append(this.sourceFilter); tools.append(filterLabel);
    this.status.setAttribute('role', 'status'); this.status.setAttribute('aria-live', 'polite');
    const content = node('div', 'content'); content.append(this.saved, this.list);
    this.confirm.hidden = true; this.confirm.setAttribute('role', 'alertdialog'); this.confirm.setAttribute('aria-label', '替换已有字幕');
    layout.append(header, targetToolbar, this.parts, queryToolbar, tools, this.status, content, this.confirm);
    this.dialog.append(layout); document.body.append(this.host); this.dialog.showModal();
    history.pushState({ ...history.state, subtitlesTool: true }, '', location.href); void this.initialize();
  }

  private current(): TargetState | undefined { return this.states.get(this.selected); }
  private async initialize(): Promise<void> {
    try {
      await this.loadInfo(true);
      const state = this.current();
      if (state && (state.query || state.info.hasRecord)) await this.search(state.info.id);
    } catch (error) { if (!this.abort.signal.aborted) this.status.replaceChildren(node('p', 'error-text', message(error))); }
  }
  private async loadInfo(initial = false): Promise<void> {
    const source = initial ? sourceId() : this.version.value;
    const info = await (await request(this.root + (source ? '?mediaSourceId=' + encodeURIComponent(source) : ''), this.abort.signal)).json() as MediaInfo;
    if (this.abort.signal.aborted) return;
    this.canTrim = info.canTrim;
    const remaining = new Set(info.targets.map(target => target.id));
    for (const id of this.states.keys()) if (!remaining.has(id)) this.states.delete(id);
    for (const target of info.targets) {
      const old = this.states.get(target.id);
      this.states.set(target.id, old ? Object.assign(old, { info: target }) : { info: target, query: target.query, candidates: [], sources: {}, status: 'idle', progress: '', notice: '' });
    }
    if (!this.states.has(this.selected)) this.selected = info.selectedTargetId;
    this.version.replaceChildren();
    const versions = new Map(info.targets.map(target => [target.versionId, target.versionName]));
    for (const [id, name] of versions) { const option = node('option', '', name); option.value = id; this.version.append(option); }
    this.version.value = this.current()!.info.versionId; this.buildParts(); this.select(this.selected, false);
  }
  private buildParts(): void {
    this.parts.replaceChildren(); this.partButtons.clear();
    for (const state of this.states.values()) {
      if (state.info.versionId !== this.version.value) continue;
      const choice = button('', () => this.select(state.info.id), 'part');
      this.partButtons.set(state.info.id, choice); this.parts.append(choice);
    }
  }
  private select(id: string, autoSearch = true): void {
    this.selected = id; this.confirm.hidden = true; this.list.inert = false;
    const state = this.current(); if (!state) return;
    this.query.value = state.query; this.title.textContent = state.info.fileName; this.render();
    if (autoSearch && state.status === 'idle' && (state.query || state.info.hasRecord)) void this.search(id);
  }
  private render(): void {
    const state = this.current(); if (!state) return;
    for (const [id, choice] of this.partButtons) {
      const value = this.states.get(id)!;
      choice.textContent = `${value.info.partCount > 1 ? '第 ' + value.info.partNumber + ' 段' : '当前视频'} · ${targetStatus(value)}`;
      choice.setAttribute('aria-pressed', String(id === this.selected));
    }
    const searching = state.status === 'searching';
    this.retry.disabled = searching || this.busyDownload; this.hash.disabled = searching || this.busyDownload;
    this.query.disabled = searching; this.language.disabled = searching;
    this.all.disabled = this.busyDownload || this.searches.size > 0;
    this.trim.hidden = !this.canTrim;
    this.trim.disabled = this.busyDownload || this.calibrationOpen;
    this.all.hidden = this.partButtons.size < 2;
    this.status.replaceChildren();
    if (state.notice) this.status.append(node('p', state.status === 'error' ? 'error-text' : 'status-text', state.notice));
    if (state.progress) this.status.append(node('p', 'status-text', state.progress));
    const sources = node('div', 'source-status');
    for (const [id, label] of [['xunlei', '迅雷'], ['subtitlecat', 'SubtitleCat']]) {
      const result = state.sources[id];
      if (result) sources.append(node('p', result.state === 'error' || result.state === 'partial' ? 'error-text' : 'status-text', `${label}：${result.state === 'searching' ? '正在查询…' : result.message ?? '查询完成'}`));
    }
    this.status.append(sources);
    this.saved.replaceChildren();
    if (state.info.subtitles.length) {
      this.saved.append(node('h2', 'section-title', '已保存的字幕'));
      for (const subtitle of state.info.subtitles) {
        const row = node('div', 'saved-row'); row.append(node('span', '', subtitle.fileName));
        const align = button('校准', () => void this.calibrate(state, subtitle.id)); align.disabled = !subtitle.canCalibrate || this.busyDownload || this.calibrationOpen;
        if (!subtitle.canCalibrate) align.title = '当前格式暂不支持校准';
        row.append(align); this.saved.append(row);
      }
    }
    this.list.replaceChildren();
    const visible = state.candidates.filter(candidate => this.sourceFilter.value === 'all' || candidate.source === this.sourceFilter.value);
    if (!visible.length) {
      let text = '输入番号后搜索，或按视频文件进一步搜索。';
      if (searching) text = '正在查找候选，先返回的字幕会先显示。';
      else if (state.status === 'done' || state.status === 'error') text = state.candidates.length ? '当前来源没有候选，可切换显示来源。' : targetStatus(state) === '查询失败或未完成' ? '部分查询尚未成功，请查看上方来源状态并重试。' : '本次没有找到匹配的字幕，可修改搜索词或按视频文件进一步搜索。';
      this.list.append(node('p', 'empty', text));
    }
    for (const candidate of visible) {
      const card = node('article', 'card'); const details = node('div', 'details');
      details.append(node('h2', 'name', candidate.name));
      const badges = node('div', 'badges'); badges.append(node('span', 'badge', candidate.source === 'xunlei' ? '迅雷' : 'SubtitleCat'), node('span', 'badge', candidate.format.toUpperCase() || '未知格式'));
      if (candidate.source === 'subtitlecat' && candidate.language) badges.append(node('span', 'badge', candidate.language));
      details.append(badges, node('p', 'muted', candidate.match));
      if (candidate.unavailableReason) details.append(node('p', 'muted', candidate.unavailableReason));
      const save = button('下载', () => void this.download(state, candidate, false), 'primary');
      save.disabled = this.busyDownload || !candidate.canDownload; save.setAttribute('aria-label', `下载 ${candidate.name}`);
      card.append(details, save); this.list.append(card);
    }
  }

  private async search(id: string, computeHash = false): Promise<void> {
    const state = this.states.get(id); if (!state || this.busyDownload || this.searches.has(id)) return;
    const controller = new AbortController(); this.searches.set(id, controller);
    const cancel = () => controller.abort(); this.abort.signal.addEventListener('abort', cancel, { once: true });
    state.status = 'searching'; state.sources = {}; state.notice = ''; state.progress = '';
    if (!computeHash) state.candidates = [];
    this.confirm.hidden = true; this.list.inert = false; this.render();
    let terminal = false;
    try {
      const response = await request(this.root + '/search', controller.signal, { targetId: id, query: state.query, subtitleCatLanguage: this.language.value, computeHash });
      if (!response.body) throw new Error('无法接收搜索结果，请重试。');
      await readEvents(response.body, event => {
        if (controller.signal.aborted) return;
        const target = this.states.get(String(event.targetId)); if (!target) return;
        applyEvent(target, event);
        if (event.type === 'done' || event.type === 'error') terminal = true;
        this.render();
      });
      if (!terminal && !controller.signal.aborted) throw new Error('搜索连接提前结束，请重试。');
      if (computeHash) await this.loadInfo();
    } catch (error) {
      if (!controller.signal.aborted) { state.status = 'error'; state.notice = message(error); }
    } finally {
      this.abort.signal.removeEventListener('abort', cancel); this.searches.delete(id);
      if (!this.abort.signal.aborted) this.render();
    }
  }
  private async searchAll(): Promise<void> {
    const queue = [...this.states.values()].filter(state => state.info.versionId === this.version.value).map(state => state.info.id);
    let next = 0;
    const worker = async () => { while (next < queue.length && !this.abort.signal.aborted) await this.search(queue[next++]); };
    await Promise.all([worker(), worker()]);
  }
  private async download(state: TargetState, candidate: Candidate, overwrite: boolean): Promise<void> {
    this.busyDownload = true; state.notice = '正在下载并保存到这一段视频旁…'; this.render();
    try {
      const result = await (await request(this.root + '/download', this.abort.signal, { targetId: state.info.id, candidateId: candidate.id, overwrite })).json() as { message: string; refreshed: boolean };
      await this.loadInfo(); state.notice = result.message;
      if (result.refreshed) await refreshDetails(this.id, state.info.versionId, this.abort.signal);
    } catch (error) {
      if (this.abort.signal.aborted) return;
      if (error instanceof RequestError && error.status === 409) {
        const cancel = button('保留当前字幕', () => { this.confirm.hidden = true; this.list.inert = false; state.notice = '已保留当前字幕。'; this.render(); });
        const replace = button('替换字幕', () => { this.confirm.hidden = true; this.list.inert = false; void this.download(state, candidate, true); }, 'primary');
        const actions = node('div', 'confirm-actions'); actions.append(cancel, replace);
        this.confirm.replaceChildren(node('p', '', error.message), actions); this.confirm.hidden = false; this.list.inert = true; cancel.focus();
      } else state.notice = message(error);
    } finally { this.busyDownload = false; if (!this.abort.signal.aborted) this.render(); }
  }
  private async calibrate(state: TargetState, subtitleId: string | null): Promise<void> {
    if (this.calibrationOpen) return;
    this.calibrationOpen = true; this.render();
    try {
      const { openCalibration } = await import('./calibration');
      if (this.abort.signal.aborted) return;
      await openCalibration(this.root, state.info, subtitleId, this.abort.signal, async videoChanged => {
        if (videoChanged) { state.candidates = []; state.sources = {}; state.status = 'idle'; }
        await this.loadInfo(); await refreshDetails(this.id, state.info.versionId, this.abort.signal);
      }, this.canTrim);
    } catch (error) { if (!this.abort.signal.aborted) state.notice = message(error); }
    finally { this.calibrationOpen = false; if (!this.abort.signal.aborted) this.render(); }
  }
  close(back: boolean): void {
    this.abort.abort(); for (const search of this.searches.values()) search.abort(); this.dialog.close(); this.host.remove(); this.onClose();
    if (back && history.state?.subtitlesTool) history.back();
    if (this.focusBefore?.isConnected) this.focusBefore.focus();
  }
}

if (!window.__subtitlesToolLoaded) {
  window.__subtitlesToolLoaded = true;
  let panel: Panel | undefined; let checked = ''; let allowed = false; let timer: ReturnType<typeof setTimeout>;
  async function sync(): Promise<void> {
    const id = itemId(); const currentPage = page();
    if (!id || !currentPage) { checked = ''; if (panel) panel.close(false); return; }
    const client = window.ApiClient;
    if (!client?.accessToken() || !client.getCurrentUserId()) return;
    if (checked !== id) {
      checked = id; allowed = false;
      const old = currentPage.querySelector<HTMLElement>('[data-subtitles-tool]'); if (old) old.hidden = true;
      let permitted = false;
      try { await request(`SubtitlesTool/Items/${id}`, new AbortController().signal); permitted = true; } catch { permitted = false; }
      if (itemId() !== id || checked !== id) return; allowed = permitted;
    }
    if (!allowed) return;
    const existing = currentPage.querySelector<HTMLElement>('[data-subtitles-tool]'); if (existing) { existing.hidden = false; return; }
    const actions = currentPage.querySelector('.mainDetailButtons'); if (!actions) return;
    const launch = button('JAV 字幕', () => { const current = itemId(); if (!panel && current) panel = new Panel(current, () => { panel = undefined; }); }, 'button-flat button-flat-mini detailButton emby-button');
    launch.dataset.subtitlesTool = 'true'; launch.style.minWidth = '72px'; launch.style.minHeight = '44px'; actions.append(launch);
  }
  function schedule(): void { clearTimeout(timer); timer = setTimeout(() => void sync(), 100); }
  document.addEventListener('viewshow', schedule, true);
  window.addEventListener('hashchange', () => { if (panel) panel.close(false); schedule(); });
  window.addEventListener('popstate', () => { if (panel) panel.close(false); schedule(); });
  new MutationObserver(schedule).observe(document.body, { childList: true, subtree: true }); schedule();
}
