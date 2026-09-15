(() => {
  const telegram = window.Telegram?.WebApp;
  const explicitPreview = new URLSearchParams(window.location.search).get("preview") === "1";
  const state = { data: null, preview: explicitPreview, stale: false, detail: null, detailThreadId: null };
  let lastLiveData = null;
  let loadController = null;
  let detailController = null;
  let detailHistoryActive = false;
  let detailPreviousFocus = null;
  let browserPairingToken = sessionStorage.getItem("codexTelegramBrowserPairing");
  let browserSessionToken = sessionStorage.getItem("codexTelegramBrowserSession");
  let pairingPollTimer = null;
  let pairingPollInFlight = false;
  const byId = (id) => document.getElementById(id);
  const root = document.documentElement;

  const previewData = {
    user: { id: 0, firstName: "Preview", username: "local" },
    activeSessionId: "preview-session",
    projects: [{ id: "project-preview", displayName: "codex-telegram", isActive: true, addedAt: new Date().toISOString() }],
    threads: [
      { id: "preview-session", name: "Mini App spike", preview: "Read-only dashboard surface", status: "idle", lifecycleState: "completed", updatedAt: new Date().toISOString(), workingDirectory: "codex-telegram" },
      { id: "preview-second", name: "UIKit integration", preview: "Published web component runtime", status: "idle", lifecycleState: "completed", updatedAt: new Date(Date.now() - 86400000).toISOString(), workingDirectory: "codex-telegram" }
    ],
    recentActivity: [],
    needsAttention: [],
    runtime: { initialized: true, serverName: "Preview Codex", serverVersion: "local" },
    usage: null,
    runtimeError: null,
    threadsError: null,
    usageError: null,
    serverTimeUtc: new Date().toISOString()
  };
  previewData.recentActivity = previewData.threads;

  function applyTelegramTheme() {
    if (!telegram) return;
    const colors = telegram.themeParams || {};
    if (colors.bg_color) document.documentElement.style.setProperty("--inc-app-bg", colors.bg_color);
    if (colors.text_color) document.documentElement.style.setProperty("--inc-ink", colors.text_color);
    if (colors.hint_color) document.documentElement.style.setProperty("--inc-muted", colors.hint_color);
    if (colors.button_color) document.documentElement.style.setProperty("--inc-primary", colors.button_color);
    if (colors.secondary_bg_color) document.documentElement.style.setProperty("--inc-surface", colors.secondary_bg_color);
  }

  function setTelegramInset(name, value) {
    if (Number.isFinite(Number(value)) && Number(value) >= 0) root.style.setProperty(name, `${Number(value)}px`);
  }

  function syncViewport() {
    if (!telegram) {
      root.dataset.telegramDisplayMode = "browser";
      byId("viewport-badge").hidden = true;
      byId("fullscreen-button").hidden = true;
      return;
    }

    const viewportHeight = Number(telegram.viewportHeight);
    const stableHeight = Number(telegram.viewportStableHeight);
    if (Number.isFinite(viewportHeight) && viewportHeight > 0) root.style.setProperty("--app-viewport-height", `${viewportHeight}px`);
    if (Number.isFinite(stableHeight) && stableHeight > 0) root.style.setProperty("--app-viewport-stable-height", `${stableHeight}px`);
    const safeArea = telegram.safeAreaInset || {};
    const contentSafeArea = telegram.contentSafeAreaInset || {};
    setTelegramInset("--app-safe-top", safeArea.top);
    setTelegramInset("--app-safe-right", safeArea.right);
    setTelegramInset("--app-safe-bottom", safeArea.bottom);
    setTelegramInset("--app-safe-left", safeArea.left);
    setTelegramInset("--app-content-safe-top", contentSafeArea.top);
    setTelegramInset("--app-content-safe-right", contentSafeArea.right);
    setTelegramInset("--app-content-safe-bottom", contentSafeArea.bottom);
    setTelegramInset("--app-content-safe-left", contentSafeArea.left);

    const mode = telegram.isFullscreen === true ? "Full screen" : telegram.isExpanded === false ? "Compact" : "Full height";
    root.dataset.telegramDisplayMode = mode.toLowerCase().replace(" ", "-");
    const viewportBadge = byId("viewport-badge");
    viewportBadge.hidden = false;
    viewportBadge.textContent = mode;
    viewportBadge.setAttribute("variant", mode === "Compact" ? "warning" : mode === "Full screen" ? "success" : "info");
    const fullscreenButton = byId("fullscreen-button");
    const supportsFullscreen = telegram.isFullscreen === true ? typeof telegram.exitFullscreen === "function" : typeof telegram.requestFullscreen === "function";
    fullscreenButton.hidden = !supportsFullscreen;
    fullscreenButton.textContent = telegram.isFullscreen === true ? "Exit fullscreen" : "Fullscreen";
    fullscreenButton.setAttribute("aria-label", fullscreenButton.textContent);
    fullscreenButton.title = fullscreenButton.textContent;
  }

  function requestMaximumHeight() {
    if (!telegram || typeof telegram.expand !== "function" || telegram.isExpanded === true) return;
    try { telegram.expand(); }
    catch (error) { showNotice(`Telegram could not expand this Mini App: ${error.message || "unsupported operation"}.`, true); }
  }

  function initializeTelegram() {
    if (!telegram) { root.dataset.telegramDisplayMode = "browser"; syncViewport(); return; }
    if (typeof telegram.ready === "function") telegram.ready();
    applyTelegramTheme();
    syncViewport();
    requestMaximumHeight();
    syncViewport();
    if (typeof telegram.onEvent === "function") {
      telegram.onEvent("themeChanged", applyTelegramTheme);
      telegram.onEvent("viewportChanged", syncViewport);
      telegram.onEvent("safeAreaChanged", syncViewport);
      telegram.onEvent("contentSafeAreaChanged", syncViewport);
      telegram.onEvent("fullscreenChanged", syncViewport);
      telegram.onEvent("fullscreenFailed", (event) => { syncViewport(); showNotice(`Telegram could not enter fullscreen mode${event?.error ? ` (${event.error})` : ""}.`, true); });
      telegram.onEvent("activated", () => { syncViewport(); load(); });
      telegram.onEvent("deactivated", syncViewport);
    }
    if (telegram.BackButton?.onClick) telegram.BackButton.onClick(() => closeThreadDetail());
  }

  function showNotice(message, error = false) {
    const notice = byId("notice");
    notice.hidden = !message;
    notice.textContent = message || "";
    notice.classList.toggle("error", error);
  }

  function setBrowserPairingVisible(visible) {
    byId("browser-pairing-card").hidden = !visible;
  }

  function clearPairingPoll() {
    if (pairingPollTimer) window.clearTimeout(pairingPollTimer);
    pairingPollTimer = null;
  }

  async function pollBrowserPairing() {
    if (!browserPairingToken || pairingPollInFlight) return;
    pairingPollInFlight = true;
    try {
      const response = await fetch("/api/mini-app/pairing/status", {
        headers: { "X-Codex-Browser-Pairing": browserPairingToken },
        credentials: "same-origin"
      });
      if (response.status === 401) throw new Error("This pairing code is no longer valid.");
      if (!response.ok) throw new Error(`Pairing status is unavailable (${response.status}).`);
      const status = await response.json();
      if (status.state === "approved" && status.sessionToken) {
        browserSessionToken = status.sessionToken;
        sessionStorage.setItem("codexTelegramBrowserSession", browserSessionToken);
        sessionStorage.removeItem("codexTelegramBrowserPairing");
        browserPairingToken = null;
        clearPairingPoll();
        setBrowserPairingVisible(false);
        await load();
        return;
      }
      if (status.state === "expired") {
        browserPairingToken = null;
        sessionStorage.removeItem("codexTelegramBrowserPairing");
        clearPairingPoll();
        showNotice("That pairing code expired. Select Get a new code to try again.", true);
        setBadge("Pairing expired", "warning");
        return;
      }
      pairingPollTimer = window.setTimeout(pollBrowserPairing, 2000);
    } catch (error) {
      if (browserPairingToken) {
        showNotice(error.message || "Browser pairing status is unavailable.", true);
        pairingPollTimer = window.setTimeout(pollBrowserPairing, 5000);
      }
    } finally {
      pairingPollInFlight = false;
    }
  }

  async function beginBrowserPairing() {
    clearPairingPoll();
    setBrowserPairingVisible(false);
    setBadge("Pairing", "warning");
    try {
      const response = await fetch("/api/mini-app/pairing/start", { credentials: "same-origin" });
      if (response.status === 404) throw new Error("Open this Mini App from Telegram, or enable browser pairing on this host.");
      if (!response.ok) throw new Error(`Browser pairing is unavailable (${response.status}).`);
      const start = await response.json();
      browserPairingToken = start.pairingToken;
      sessionStorage.setItem("codexTelegramBrowserPairing", browserPairingToken);
      byId("browser-pairing-code").textContent = start.pairingCode || "—";
      byId("browser-pairing-instructions").textContent = `Send /pair ${start.pairingCode || "<code>"} to your bot in its private Telegram chat. This code expires at ${formatDate(start.expiresAtUtc)}.`;
      setBrowserPairingVisible(true);
      showNotice("Approve the browser pairing code in Telegram to connect this page.");
      pollBrowserPairing();
    } catch (error) {
      setBadge("Unavailable", "danger");
      showNotice(error.message || "Browser pairing is unavailable.", true);
    }
  }

  function setBadge(label, variant) {
    const badge = byId("connection-badge");
    badge.setAttribute("variant", variant);
    badge.textContent = label;
  }

  function formatDate(value) {
    if (!value) return "—";
    const date = new Date(value);
    return Number.isNaN(date.valueOf()) ? "—" : new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" }).format(date);
  }

  function shortPath(value) {
    if (!value) return "No working directory";
    const parts = value.split(/[\\/]/).filter(Boolean);
    return parts.slice(-2).join("/") || value;
  }

  function runtimeLabel(runtime) {
    if (!runtime) return ["Offline", "Codex status unavailable"];
    if (runtime.initialized) return ["Ready", runtime.serverVersion ? `${runtime.serverName || "Codex"} ${runtime.serverVersion}` : "Codex initialized"];
    return ["Starting", runtime.message || "Codex is initializing"];
  }

  function usageLabel(usage) {
    const window = usage?.rateLimits?.[0]?.primary;
    return window ? [`${window.usedPercent}%`, "Primary rate-limit window"] : ["—", "Usage unavailable"];
  }

  function statusVariant(thread) {
    switch (thread.lifecycleState) {
      case "waiting": return "warning";
      case "failed": return "danger";
      case "interrupted": return "danger";
      case "unavailable": return "warning";
      case "running": return "success";
      default: return "neutral";
    }
  }

  function renderSummary(data) {
    const [runtime, runtimeDetail] = runtimeLabel(data.runtime);
    byId("runtime-value").textContent = runtime;
    byId("runtime-detail").textContent = data.runtimeError || runtimeDetail;
    byId("session-value").textContent = data.activeSessionId ? "Selected" : "None";
    const threads = data.recentActivity || data.threads || [];
    const active = threads.find((thread) => thread.id === data.activeSessionId);
    byId("session-detail").textContent = active?.name || (data.activeSessionId ? data.activeSessionId : "No session selected");
    byId("project-value").textContent = String(data.projects?.length || 0);
    const [usage, usageDetail] = usageLabel(data.usage);
    byId("usage-value").textContent = usage;
    byId("usage-detail").textContent = data.usageError || usageDetail;
  }

  function addAttentionRow(list, item) {
    const row = document.createElement("div");
    row.className = "attention-row";
    row.tabIndex = item.threadId ? 0 : -1;
    row.setAttribute("role", item.threadId ? "button" : "status");
    const copy = document.createElement("div");
    copy.className = "attention-copy";
    const title = document.createElement("div");
    title.className = "attention-title";
    title.textContent = item.title || "Needs attention";
    const detail = document.createElement("div");
    detail.className = "attention-detail";
    detail.textContent = item.detail || "Review this item in Telegram.";
    copy.append(title, detail);
    const meta = document.createElement("div");
    meta.className = "attention-meta";
    meta.textContent = formatDate(item.updatedAt);
    row.append(copy, meta);
    if (item.threadId) {
      const open = () => openThread(item.threadId);
      row.addEventListener("click", open);
      row.addEventListener("keydown", (event) => { if (event.key === "Enter" || event.key === " ") { event.preventDefault(); open(); } });
    }
    list.append(row);
  }

  function renderAttention(data) {
    const list = byId("attention-list");
    const items = data.needsAttention || [];
    list.replaceChildren();
    byId("attention-badge").textContent = items.length ? `${items.length} item${items.length === 1 ? "" : "s"}` : "Clear";
    byId("attention-badge").setAttribute("variant", items.length ? "warning" : "success");
    if (!items.length) {
      const empty = document.createElement("div");
      empty.className = "empty-row";
      empty.textContent = "Nothing needs your attention right now.";
      list.append(empty);
      return;
    }
    items.forEach((item) => addAttentionRow(list, item));
  }

  function renderSessions(data) {
    const list = byId("sessions-list");
    const threads = data.recentActivity || data.threads || [];
    list.replaceChildren();
    if (!threads.length) {
      const empty = document.createElement("div");
      empty.className = "empty-row";
      empty.textContent = "No Codex activity is available yet.";
      list.append(empty);
      return;
    }
    threads.forEach((thread) => {
      const row = document.createElement("div");
      row.className = `session-row${thread.id === data.activeSessionId ? " active-row" : ""}`;
      row.tabIndex = 0;
      row.setAttribute("role", "button");
      row.setAttribute("aria-label", `Open ${thread.name || "unnamed task"}`);
      const copy = document.createElement("div");
      copy.className = "session-copy";
      const name = document.createElement("div");
      name.className = "session-name";
      name.textContent = thread.name || "Unnamed task";
      const preview = document.createElement("div");
      preview.className = "session-preview";
      preview.textContent = thread.preview || shortPath(thread.workingDirectory);
      copy.append(name, preview);
      const meta = document.createElement("div");
      meta.className = "session-meta";
      const status = document.createElement("inc-badge");
      status.setAttribute("variant", statusVariant(thread));
      const dot = document.createElement("span");
      dot.className = `status-dot ${thread.lifecycleState || "completed"}`;
      status.append(dot, document.createTextNode(thread.lifecycleState || thread.status || "completed"));
      const date = document.createElement("span");
      date.className = "session-date";
      date.textContent = formatDate(thread.updatedAt);
      meta.append(status, date);
      row.append(copy, meta);
      const open = () => openThread(thread.id);
      row.addEventListener("click", open);
      row.addEventListener("keydown", (event) => { if (event.key === "Enter" || event.key === " ") { event.preventDefault(); open(); } });
      list.append(row);
    });
  }

  function renderWorkspace(data) {
    const list = byId("workspace-list");
    list.replaceChildren();
    const projects = data.projects || [];
    if (data.projectsError) {
      const unavailable = document.createElement("div");
      unavailable.className = "empty-row";
      unavailable.textContent = data.projectsError;
      list.append(unavailable);
      return;
    }
    if (!projects.length) {
      const empty = document.createElement("div");
      empty.className = "empty-row";
      empty.textContent = "No saved projects yet. Add one through the Telegram chat.";
      list.append(empty);
      return;
    }
    projects.forEach((project) => {
      const row = document.createElement("div");
      row.className = "workspace-row";
      const copy = document.createElement("div");
      copy.className = "workspace-copy";
      const name = document.createElement("div");
      name.className = "workspace-name";
      name.textContent = project.displayName || "Project";
      const path = document.createElement("div");
      path.className = "workspace-path";
      path.textContent = project.displayName || "Workspace";
      copy.append(name, path);
      const badge = document.createElement("inc-badge");
      badge.setAttribute("variant", project.isActive ? "success" : "neutral");
      badge.textContent = project.isActive ? "Active" : "Saved";
      row.append(copy, badge);
      list.append(row);
    });
  }

  function supervisionVariant(state) {
    switch ((state || "").toLowerCase()) {
      case "running": return "success";
      case "waitingforinput":
      case "waiting_for_input":
      case "readyforreview":
      case "ready_for_review": return "warning";
      case "failed":
      case "interrupted":
      case "unknown": return "danger";
      default: return "neutral";
    }
  }

  function renderSupervision(data) {
    const list = byId("supervision-list");
    list.replaceChildren();
    if (data.supervisionError) {
      const unavailable = document.createElement("div");
      unavailable.className = "empty-row";
      unavailable.textContent = data.supervisionError;
      list.append(unavailable);
      return;
    }
    const tasks = data.supervisionTasks || [];
    if (!tasks.length) {
      const empty = document.createElement("div");
      empty.className = "empty-row";
      empty.textContent = "No durable task runs have been recorded yet.";
      list.append(empty);
      return;
    }
    tasks.slice(0, 12).forEach((task) => {
      const row = document.createElement("div");
      row.className = "supervision-row";
      row.tabIndex = task.codexThreadId ? 0 : -1;
      row.setAttribute("role", task.codexThreadId ? "button" : "status");
      const copy = document.createElement("div");
      copy.className = "supervision-copy";
      const name = document.createElement("div");
      name.className = "supervision-name";
      name.textContent = task.sessionName || "Codex task";
      const ids = document.createElement("div");
      ids.className = "supervision-ids";
      ids.textContent = `${task.state || "not_started"} · ${task.workerId || "unassigned"}${task.runId ? ` · ${task.runId.slice(-12)}` : ""}`;
      copy.append(name, ids);
      const badge = document.createElement("inc-badge");
      badge.setAttribute("variant", supervisionVariant(task.state));
      badge.textContent = task.state || "not started";
      row.append(copy, badge);
      if (task.codexThreadId) {
        const open = () => openThread(task.codexThreadId);
        row.addEventListener("click", open);
        row.addEventListener("keydown", (event) => { if (event.key === "Enter" || event.key === " ") { event.preventDefault(); open(); } });
      }
      list.append(row);
    });
  }

  function workerVariant(worker) {
    if (worker.readiness === "ready" && worker.state === "online") return "success";
    if (worker.state === "draining" || worker.readiness === "starting") return "warning";
    return "danger";
  }

  function renderWorkers(data) {
    const list = byId("workers-list");
    list.replaceChildren();
    if (data.workersError) {
      const unavailable = document.createElement("div");
      unavailable.className = "empty-row";
      unavailable.textContent = data.workersError;
      list.append(unavailable);
      return;
    }
    const workers = data.workers || [];
    if (!workers.length) {
      const empty = document.createElement("div");
      empty.className = "empty-row";
      empty.textContent = "No worker registration is available.";
      list.append(empty);
      return;
    }
    workers.forEach((worker) => {
      const row = document.createElement("div");
      row.className = "worker-row";
      const copy = document.createElement("div");
      copy.className = "worker-copy";
      const name = document.createElement("div");
      name.className = "worker-name";
      name.textContent = worker.displayName || worker.workerId || "Worker";
      const meta = document.createElement("div");
      meta.className = "worker-meta";
      meta.textContent = `${worker.isRemote ? "remote" : "local"} · ${worker.readiness || "unknown"} · ${worker.activeLeaseCount || 0}/${worker.maximumConcurrentTasks || 0} leases · ${worker.version || "unknown"}`;
      copy.append(name, meta);
      const badge = document.createElement("inc-badge");
      badge.setAttribute("variant", workerVariant(worker));
      badge.textContent = worker.state || "unknown";
      const actions = document.createElement("div");
      actions.className = "worker-actions";
      if (worker.state === "online" || worker.state === "draining") {
        const action = document.createElement("button");
        action.type = "button";
        action.className = "secondary-button compact-button";
        action.textContent = worker.state === "draining" ? "Resume" : "Drain";
        action.disabled = worker.readiness === "unavailable";
        action.addEventListener("click", (event) => {
          event.stopPropagation();
          postWorkerAction(worker, worker.state === "draining" ? "resume" : "drain");
        });
        actions.append(action);
      }
      const status = document.createElement("div");
      status.className = "worker-status";
      status.append(badge, actions);
      row.append(copy, status);
      list.append(row);
    });
  }

  function setDetailLoading(message) {
    byId("detail-card").hidden = false;
    byId("detail-title").textContent = "Task details";
    byId("detail-subtitle").textContent = message;
    byId("detail-status").textContent = "Loading";
    byId("detail-status").setAttribute("variant", "neutral");
    byId("detail-summary").replaceChildren();
    byId("detail-review-packet").replaceChildren();
    byId("handoff-result").hidden = true;
    byId("handoff-result").replaceChildren();
    byId("review-packet-status").textContent = "Loading";
    byId("review-packet-status").setAttribute("variant", "neutral");
    byId("detail-timeline").replaceChildren();
    byId("detail-changes").replaceChildren();
    byId("detail-artifacts").replaceChildren();
    byId("detail-turns").replaceChildren();
    const loading = document.createElement("div");
    loading.className = "detail-empty";
    loading.textContent = message;
    byId("detail-timeline").append(loading);
  }

  function setTelegramBackButton(visible) {
    if (!telegram?.BackButton) return;
    if (visible && telegram.BackButton.show) telegram.BackButton.show();
    if (!visible && telegram.BackButton.hide) telegram.BackButton.hide();
  }

  function appendSummaryItem(list, label, value) {
    const item = document.createElement("div");
    item.className = "detail-summary-item";
    const title = document.createElement("span");
    title.className = "detail-summary-label";
    title.textContent = label;
    const content = document.createElement("span");
    content.className = "detail-summary-value";
    content.textContent = value || "—";
    item.append(title, content);
    list.append(item);
  }

  function renderReviewPacket(packet) {
    const list = byId("detail-review-packet");
    const handoff = byId("handoff-result");
    handoff.hidden = true;
    handoff.replaceChildren();
    const badge = byId("review-packet-status");
    list.replaceChildren();
    if (!packet) {
      badge.textContent = "Unavailable";
      badge.setAttribute("variant", "warning");
      const empty = document.createElement("div");
      empty.className = "detail-empty";
      empty.textContent = "No review packet is available for this task.";
      list.append(empty);
      return;
    }

    const status = packet.reviewStatus || "unknown";
    badge.textContent = packet.acknowledged ? "Acknowledged" : status === "ready" ? "Ready" : status === "partial" ? "Partial" : "No evidence";
    badge.setAttribute("variant", packet.acknowledged ? "success" : status === "ready" ? "success" : status === "partial" ? "warning" : "neutral");
    const summary = document.createElement("div");
    summary.className = "review-packet-summary";
    const packetId = document.createElement("code");
    packetId.textContent = packet.packetId || "review:unknown";
    const counts = document.createElement("span");
    counts.textContent = `${packet.changes?.length || 0} changes · ${packet.artifacts?.length || 0} artifacts`;
    summary.append(packetId, counts);
    list.append(summary);

    const provenance = document.createElement("div");
    provenance.className = "review-packet-provenance";
    provenance.textContent = `Codex thread ${packet.codexThreadId || "unknown"}${packet.codexTurnId ? ` · turn ${packet.codexTurnId}` : ""}`;
    list.append(provenance);

    const boundary = document.createElement("div");
    boundary.className = "review-packet-boundary";
    boundary.textContent = packet.acknowledged
      ? `Acknowledged ${formatDate(packet.acknowledgedAtUtc)}. New runs will appear again.`
      : packet.requiresTelegramApproval === false
        ? "This packet is informational."
        : "Review evidence here. Send decisions and follow-up instructions in Telegram.";
    list.append(boundary);

    const actions = document.createElement("div");
    actions.className = "miniapp-action-row";
    if (packet.taskId && packet.runId && !packet.acknowledged) {
      const acknowledge = document.createElement("button");
      acknowledge.type = "button";
      acknowledge.className = "secondary-button";
      acknowledge.textContent = "Acknowledge review";
      acknowledge.addEventListener("click", () => postTaskAction("acknowledge", packet));
      actions.append(acknowledge);
    }
    if (packet.taskId) {
      const handoff = document.createElement("button");
      handoff.type = "button";
      handoff.className = "secondary-button";
      handoff.textContent = "Prepare Telegram handoff";
      handoff.addEventListener("click", () => postTaskAction("handoff", packet));
      actions.append(handoff);
    }
    if (actions.childElementCount) list.append(actions);
  }

  function renderHandoffResult(result, command) {
    const box = byId("handoff-result");
    box.replaceChildren();
    box.hidden = false;
    const heading = document.createElement("strong");
    heading.textContent = result?.evidenceAvailable ? "Handoff evidence ready" : "Handoff command ready; evidence unavailable";
    box.append(heading);
    const summary = document.createElement("div");
    summary.className = "handoff-summary";
    summary.textContent = result?.evidenceAvailable
      ? `${result.changeCount || 0} changed-file preview(s) · ${result.artifactCount || 0} artifact record(s) · ${result.reviewStatus || "unknown"}`
      : "The current Codex runtime did not return a review packet.";
    box.append(summary);
    const text = document.createElement("pre");
    text.className = "handoff-text";
    text.textContent = result?.text || command;
    box.append(text);
  }

  function renderThreadDetail(detail) {
    state.detail = detail;
    state.detailThreadId = detail.thread?.id || null;
    const thread = detail.thread;
    byId("detail-card").hidden = false;
    byId("detail-title").textContent = thread.name || "Unnamed task";
    byId("detail-subtitle").textContent = `${shortPath(detail.threadWorkingDirectory || thread.workingDirectory)} · refreshed ${formatDate(detail.retrievedAtUtc)}`;
    byId("detail-status").textContent = thread.lifecycleState || thread.status || "completed";
    byId("detail-status").setAttribute("variant", statusVariant(thread));
    const summary = byId("detail-summary");
    summary.replaceChildren();
    appendSummaryItem(summary, "Thread", thread.id);
    appendSummaryItem(summary, "Provider", thread.modelProvider);
    appendSummaryItem(summary, "Last updated", formatDate(thread.updatedAt));
    appendSummaryItem(summary, "Working directory", shortPath(detail.threadWorkingDirectory || thread.workingDirectory));
    appendSummaryItem(summary, "Active turn", detail.activeTurnId || "None");
    appendSummaryItem(summary, "Freshness", state.stale ? "Stale snapshot" : "Live snapshot");
    if (detail.supervision) {
      appendSummaryItem(summary, "Task", detail.supervision.taskId);
      appendSummaryItem(summary, "Run state", detail.supervision.state);
      appendSummaryItem(summary, "Run", detail.supervision.runId || "None");
      appendSummaryItem(summary, "Worker", detail.supervision.workerId || "Unassigned");
    }
    renderReviewPacket(detail.reviewPacket);

    const timeline = byId("detail-timeline");
    timeline.replaceChildren();
    if (!detail.timeline?.length) {
      const empty = document.createElement("div");
      empty.className = "detail-empty";
      empty.textContent = "No timeline events are available for this task.";
      timeline.append(empty);
    } else {
      detail.timeline.forEach((entry) => {
        const row = document.createElement("article");
        row.className = "timeline-entry";
        row.dataset.severity = entry.severity || "neutral";
        const heading = document.createElement("div");
        heading.className = "timeline-heading";
        const title = document.createElement("span");
        title.className = "timeline-title";
        title.textContent = entry.title || entry.type || "Codex event";
        const time = document.createElement("time");
        time.className = "timeline-time";
        time.textContent = formatDate(entry.timestamp);
        heading.append(title, time);
        row.append(heading);
        if (entry.subtitle) {
          const subtitle = document.createElement("p");
          subtitle.className = "timeline-subtitle";
          subtitle.textContent = entry.subtitle;
          row.append(subtitle);
        }
        if (entry.body) {
          const body = document.createElement("p");
          body.className = "timeline-body";
          body.textContent = entry.body;
          row.append(body);
        }
        timeline.append(row);
      });
    }

    renderChanges(detail.changes || []);
    renderArtifacts(detail.artifacts || []);

    const turns = byId("detail-turns");
    turns.replaceChildren();
    if (!detail.turns?.length) {
      const empty = document.createElement("div");
      empty.className = "detail-empty";
      empty.textContent = "No turn records are available for this task.";
      turns.append(empty);
    } else {
      detail.turns.slice().reverse().forEach((turn) => {
        const row = document.createElement("article");
        row.className = "turn-entry";
        const heading = document.createElement("div");
        heading.className = "turn-heading";
        const title = document.createElement("span");
        title.className = "turn-title";
        title.textContent = `Turn ${turn.id}`;
        const status = document.createElement("span");
        status.className = "turn-status";
        status.textContent = turn.status || "unknown";
        heading.append(title, status);
        row.append(heading);
        if (turn.errorMessage) {
          const error = document.createElement("p");
          error.className = "turn-error";
          error.textContent = turn.errorMessage;
          row.append(error);
        }
        if (turn.finalResponse) {
          const response = document.createElement("pre");
          response.className = "turn-response";
          response.textContent = turn.finalResponse;
          row.append(response);
        }
        if (turn.usage) {
          const usage = document.createElement("div");
          usage.className = "turn-usage";
          usage.textContent = `${turn.usage.totalTokens ?? 0} total tokens`;
          row.append(usage);
        }
        turns.append(row);
      });
    }
    byId("detail-card").scrollIntoView({ behavior: "smooth", block: "nearest" });
  }

  async function postTaskAction(action, packet) {
    if (!packet?.taskId) return;
    const body = { action, runId: packet.runId || null, packetId: packet.packetId || null };
    try {
      const response = await fetch(`/api/mini-app/tasks/${encodeURIComponent(packet.taskId)}/actions`, {
        method: "POST",
        headers: { ...authHeaders(), "Content-Type": "application/json" },
        credentials: "same-origin",
        body: JSON.stringify(body)
      });
      if (response.status === 401) throw new Error("Open this Mini App from Telegram to connect your account.");
      if (response.status === 409) throw new Error("This task changed. Refresh the task before trying that action again.");
      if (!response.ok) throw new Error("The Mini App task action could not be completed.");
      const result = await response.json();
      if (action === "handoff") {
        const command = result.telegramCommand || `/handoff ${result.codexThreadId}`;
        try { await navigator.clipboard?.writeText(command); } catch { /* Clipboard permission is optional. */ }
        renderHandoffResult(result.handoff, command);
        const evidence = result.handoff?.evidenceAvailable
          ? ` ${result.handoff.changeCount || 0} change(s) and ${result.handoff.artifactCount || 0} artifact(s) are attached as bounded evidence.`
          : " Review evidence is currently unavailable.";
        showNotice(`Telegram handoff prepared: ${command}.${evidence} ${navigator.clipboard ? "The command was copied when permitted." : "Copy it and send it in Telegram."}`);
        return;
      }

      showNotice("Review packet acknowledged. New runs will still appear in Needs attention.");
      await load();
      if (state.detailThreadId) await openThread(state.detailThreadId);
    } catch (error) {
      showNotice(error.message || "The Mini App task action could not be completed.", true);
    }
  }

  async function postWorkerAction(worker, action) {
    if (!worker?.workerId) return;
    const verb = action === "drain" ? "drain" : "resume";
    if (!window.confirm(`${verb === "drain" ? "Drain" : "Resume"} ${worker.displayName || worker.workerId}? ${verb === "drain" ? "Existing sessions will continue; new task claims will stop." : "New task claims will be allowed again."}`)) return;
    try {
      const response = await fetch(`/api/mini-app/workers/${encodeURIComponent(worker.workerId)}/actions`, {
        method: "POST",
        headers: { ...authHeaders(), "Content-Type": "application/json" },
        credentials: "same-origin",
        body: JSON.stringify({ action, confirm: true })
      });
      if (response.status === 401) throw new Error("Open this Mini App from Telegram to connect your account.");
      if (response.status === 409) throw new Error("That worker is unavailable or changed state. Refresh the dashboard.");
      if (!response.ok) throw new Error("The worker action could not be completed.");
      await response.json();
      showNotice(`${worker.displayName || worker.workerId} is now ${action === "drain" ? "draining" : "accepting new work"}.`);
      await load();
    } catch (error) {
      showNotice(error.message || "The worker action could not be completed.", true);
    }
  }

  function renderChanges(changes) {
    const list = byId("detail-changes");
    list.replaceChildren();
    if (!changes.length) {
      const empty = document.createElement("div");
      empty.className = "detail-empty";
      empty.textContent = "No Codex-reported file changes are available for review.";
      list.append(empty);
      return;
    }
    changes.forEach((change) => {
      const row = document.createElement("article");
      row.className = "change-entry";
      const heading = document.createElement("div");
      heading.className = "change-heading";
      const path = document.createElement("span");
      path.className = "change-path";
      path.textContent = change.path || "Unnamed file";
      const kind = document.createElement("span");
      kind.className = "change-kind";
      kind.textContent = change.kind || "change";
      heading.append(path, kind);
      row.append(heading);
      const diff = document.createElement("pre");
      diff.className = "change-diff";
      diff.textContent = change.diff || "Diff unavailable.";
      row.append(diff);
      list.append(row);
    });
  }

  function renderArtifacts(artifacts) {
    const list = byId("detail-artifacts");
    list.replaceChildren();
    if (!artifacts.length) {
      const empty = document.createElement("div");
      empty.className = "detail-empty";
      empty.textContent = "No safe artifact metadata is available for this task.";
      list.append(empty);
      return;
    }
    artifacts.forEach((artifact) => {
      const row = document.createElement("article");
      row.className = "artifact-entry";
      const heading = document.createElement("div");
      heading.className = "artifact-heading";
      const title = document.createElement("span");
      title.className = "artifact-title";
      title.textContent = artifact.title || "Artifact";
      const meta = document.createElement("span");
      meta.className = "artifact-meta";
      meta.textContent = artifact.kind || "artifact";
      heading.append(title, meta);
      row.append(heading);
      const detail = document.createElement("div");
      detail.className = "artifact-detail";
      detail.textContent = `${artifact.status || "Reported"} · ${formatDate(artifact.timestamp)}`;
      row.append(detail);
      list.append(row);
    });
  }

  function renderUnavailable() {
    renderSummary({ projects: [], threads: [], recentActivity: [], needsAttention: [], runtime: null, usage: null });
    renderAttention({ needsAttention: [] });
    renderSessions({ threads: [] });
    renderWorkspace({ projects: [] });
    renderSupervision({ supervisionTasks: [] });
    renderWorkers({ workers: [] });
  }

  function render(data) {
    state.data = data;
    renderAttention(data);
    renderSummary(data);
    renderSessions(data);
    renderWorkspace(data);
    renderSupervision(data);
    renderWorkers(data);
  }

  function authHeaders() {
    const headers = {};
    if (telegram?.initData) headers["X-Telegram-Init-Data"] = telegram.initData;
    if (browserSessionToken) headers["X-Codex-Browser-Session"] = browserSessionToken;
    return headers;
  }

  async function load() {
    loadController?.abort();
    loadController = new AbortController();
    const { signal } = loadController;
    setBadge("Connecting", "neutral");
    if (!state.stale) showNotice("");
    if (!telegram && !browserSessionToken && !explicitPreview) {
      await beginBrowserPairing();
      return;
    }
    try {
      const response = await fetch("/api/mini-app/bootstrap", { headers: authHeaders(), credentials: "same-origin", signal });
      if (response.status === 401 && !telegram && browserSessionToken) {
        browserSessionToken = null;
        sessionStorage.removeItem("codexTelegramBrowserSession");
        await beginBrowserPairing();
        return;
      }
      if (response.status === 401) throw new Error("Open this Mini App from Telegram to connect your account.");
      if (!response.ok) throw new Error(`Mini App is not enabled on this host (${response.status}).`);
      const data = await response.json();
      if (signal.aborted) return;
      state.preview = false;
      state.stale = false;
      lastLiveData = data;
      setBadge("Live", "success");
      render(data);
      showNotice("");
    } catch (error) {
      if (signal.aborted) return;
      if (explicitPreview) {
        state.preview = true;
        state.stale = false;
        setBadge("Preview", "warning");
        render(previewData);
        showNotice(`${error.message || "The Mini App could not load live data."} This is explicit local preview data.`, true);
      } else if (lastLiveData) {
        state.preview = false;
        state.stale = true;
        setBadge("Stale", "warning");
        render(lastLiveData);
        showNotice(`${error.message || "The Mini App could not refresh live data."} Showing the last confirmed workspace snapshot.`, true);
      } else {
        state.preview = false;
        state.stale = false;
        setBadge("Unavailable", "danger");
        renderUnavailable();
        showNotice(error.message || "The Mini App could not load live data.", true);
      }
    }
  }

  async function openThread(threadId) {
    if (!threadId) return;
    detailPreviousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    if (!detailHistoryActive) {
      history.pushState({ miniAppDetail: threadId }, "");
      detailHistoryActive = true;
      setTelegramBackButton(true);
    }
    detailController?.abort();
    detailController = new AbortController();
    setDetailLoading("Loading task detail…");
    try {
      const response = await fetch(`/api/mini-app/threads/${encodeURIComponent(threadId)}`, { headers: authHeaders(), credentials: "same-origin", signal: detailController.signal });
      if (response.status === 401) throw new Error("Open this Mini App from Telegram to connect your account.");
      if (response.status === 404) throw new Error("That task is no longer available.");
      if (!response.ok) throw new Error("Task detail is currently unavailable.");
      const detail = await response.json();
      if (!detailController.signal.aborted) renderThreadDetail(detail);
    } catch (error) {
      if (detailController.signal.aborted) return;
      setDetailLoading(error.message || "Task detail is currently unavailable.");
      showNotice(error.message || "Task detail is currently unavailable.", true);
    }
  }

  function closeThreadDetail(fromHistory = false) {
    detailController?.abort();
    if (!fromHistory && detailHistoryActive) {
      history.back();
      return;
    }
    detailHistoryActive = false;
    setTelegramBackButton(false);
    byId("detail-card").hidden = true;
    state.detail = null;
    state.detailThreadId = null;
    detailPreviousFocus?.focus?.();
    detailPreviousFocus = null;
  }

  byId("fullscreen-button").addEventListener("click", () => {
    if (!telegram) return;
    try {
      if (telegram.isFullscreen === true && typeof telegram.exitFullscreen === "function") telegram.exitFullscreen();
      else if (typeof telegram.requestFullscreen === "function") telegram.requestFullscreen();
      else showNotice("Fullscreen mode is not supported by this Telegram client.", true);
    } catch (error) { showNotice(`Fullscreen mode could not be changed: ${error.message || "unsupported operation"}.`, true); }
  });
  byId("refresh-button").addEventListener("click", load);
  byId("browser-pairing-retry").addEventListener("click", beginBrowserPairing);
  byId("close-detail-button").addEventListener("click", () => closeThreadDetail());
  byId("open-chat-button").addEventListener("click", () => { if (telegram) telegram.close(); else showNotice("Return to the Telegram chat to send prompts and change context."); });
  window.addEventListener("resize", syncViewport);
  window.addEventListener("popstate", () => { if (detailHistoryActive) closeThreadDetail(true); });
  initializeTelegram();
  load();
})();
