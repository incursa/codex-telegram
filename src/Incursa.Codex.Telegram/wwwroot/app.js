(() => {
  const telegram = window.Telegram?.WebApp;
  const explicitPreview = new URLSearchParams(window.location.search).get("preview") === "1";
  const state = { data: null, preview: explicitPreview, stale: false };
  let lastLiveData = null;
  let loadController = null;
  let browserPairingToken = sessionStorage.getItem("codexTelegramBrowserPairing");
  let browserSessionToken = sessionStorage.getItem("codexTelegramBrowserSession");
  let pairingPollTimer = null;
  let pairingPollInFlight = false;
  const byId = (id) => document.getElementById(id);
  const root = document.documentElement;

  const previewNow = Date.now();
  const previewData = {
    user: { id: 0, firstName: "Preview", username: "local" },
    activeSessionId: "preview-session",
    projects: [], threads: [
      { id: "preview-session", name: "Tighten Mini App control panel", preview: "Make the Mini App the current session's control panel", status: "idle", lifecycleState: "completed", updatedAt: new Date(previewNow).toISOString(), workingDirectory: "codex-telegram" },
      { id: "preview-second", name: "UIKit integration", preview: "Published web component runtime", status: "idle", lifecycleState: "completed", updatedAt: new Date(previewNow - 86400000).toISOString(), workingDirectory: "codex-telegram" }
    ],
    recentActivity: [], needsAttention: [], runtime: { initialized: true, serverName: "Preview Codex", serverVersion: "local" }, usage: {
      retrievedAtUtc: new Date(previewNow).toISOString(),
      rateLimits: [{ limitId: "codex", limitName: "Codex", primary: { usedPercent: 68, resetsAtUtc: new Date(previewNow + 7200000).toISOString(), windowDurationMinutes: 300 }, secondary: { usedPercent: 34, resetsAtUtc: new Date(previewNow + 604800000).toISOString(), windowDurationMinutes: 10080 } }],
      history: [
        ...[12, 18, 22, 31, 28, 42, 39, 48, 57, 61, 65, 68].map((usedPercent, index) => ({ limitId: "codex", window: "primary", usedPercent, observedAtUtc: new Date(previewNow - (11 - index) * 1800000).toISOString() })),
        ...[20, 21, 23, 24, 25, 27, 29, 28, 31, 30, 32, 34].map((usedPercent, index) => ({ limitId: "codex", window: "secondary", usedPercent, observedAtUtc: new Date(previewNow - (11 - index) * 43200000).toISOString() }))
      ]
    },
    globalInstructions: { text: "Keep responses concise. Report what changed, what you tested, and anything unresolved.", updatedAtUtc: new Date(previewNow - 86400000).toISOString(), hasSavedValue: true, applicationState: "saved_for_next_session_or_turn" },
    currentSession: {
      id: "preview-session", name: "Tighten Mini App control panel", repository: "codex-telegram", workingDirectory: "codex-telegram", lifecycleState: "running", activityStatus: "Running a command", activityDescription: "dotnet test", activityStartedAtUtc: new Date(previewNow - 42000).toISOString(), activityElapsedSeconds: 42,
      currentModel: "gpt-5.2-codex", currentThinkingEffort: "High", nextModel: "gpt-5.2-codex", nextThinkingEffort: "High",
      models: [
        { id: "gpt-5.2-codex", displayName: "gpt-5.2-codex", description: "Codex optimized", defaultThinkingEffort: "High", supportedThinkingEfforts: ["Low", "Medium", "High"], isDefault: true, hidden: false },
        { id: "gpt-5.1-codex", displayName: "gpt-5.1-codex", description: "Fast and capable", defaultThinkingEffort: "Medium", supportedThinkingEfforts: ["Minimal", "Low", "Medium", "High"], isDefault: false, hidden: false }
      ],
      goal: { threadId: "preview-session", objective: "Make the Mini App the current session's control panel", status: "Active", tokenBudget: null, tokensUsed: 0, timeUsedSeconds: 0, createdAt: new Date(previewNow - 7200000).toISOString(), updatedAt: new Date(previewNow - 7200000).toISOString() }
    }
  };
  previewData.recentActivity = previewData.threads;

  function applyTelegramTheme() {
    if (!telegram) return;
    const colors = telegram.themeParams || {};
    if (colors.bg_color) root.style.setProperty("--app-bg", colors.bg_color);
    if (colors.text_color) root.style.setProperty("--ink", colors.text_color);
    if (colors.hint_color) root.style.setProperty("--muted", colors.hint_color);
    if (colors.button_color) root.style.setProperty("--accent", colors.button_color);
    if (colors.secondary_bg_color) root.style.setProperty("--surface", colors.secondary_bg_color);
  }

  function setTelegramInset(name, value) { if (Number.isFinite(Number(value)) && Number(value) >= 0) root.style.setProperty(name, `${Number(value)}px`); }
  function syncViewport() {
    if (!telegram) { root.dataset.telegramDisplayMode = "browser"; byId("viewport-badge").hidden = true; byId("fullscreen-button").hidden = true; return; }
    const viewportHeight = Number(telegram.viewportHeight), stableHeight = Number(telegram.viewportStableHeight);
    if (viewportHeight > 0) root.style.setProperty("--app-viewport-height", `${viewportHeight}px`);
    if (stableHeight > 0) root.style.setProperty("--app-viewport-stable-height", `${stableHeight}px`);
    const safeArea = telegram.safeAreaInset || {}, contentSafeArea = telegram.contentSafeAreaInset || {};
    [["--app-safe-top", safeArea.top], ["--app-safe-right", safeArea.right], ["--app-safe-bottom", safeArea.bottom], ["--app-safe-left", safeArea.left], ["--app-content-safe-top", contentSafeArea.top], ["--app-content-safe-right", contentSafeArea.right], ["--app-content-safe-bottom", contentSafeArea.bottom], ["--app-content-safe-left", contentSafeArea.left]].forEach(([name, value]) => setTelegramInset(name, value));
    const mode = telegram.isFullscreen === true ? "Full screen" : telegram.isExpanded === false ? "Compact" : "Full height";
    root.dataset.telegramDisplayMode = mode.toLowerCase().replace(" ", "-");
    const viewportBadge = byId("viewport-badge"); viewportBadge.hidden = false; viewportBadge.textContent = mode;
    const fullscreenButton = byId("fullscreen-button");
    const supportsFullscreen = telegram.isFullscreen === true ? typeof telegram.exitFullscreen === "function" : typeof telegram.requestFullscreen === "function";
    fullscreenButton.hidden = !supportsFullscreen; fullscreenButton.textContent = telegram.isFullscreen === true ? "Exit fullscreen" : "Fullscreen";
  }
  function requestMaximumHeight() { if (!telegram || typeof telegram.expand !== "function" || telegram.isExpanded === true) return; try { telegram.expand(); } catch { showNotice("Telegram could not expand this Mini App.", true); } }
  function initializeTelegram() {
    if (!telegram) { syncViewport(); return; }
    telegram.ready?.(); applyTelegramTheme(); syncViewport(); requestMaximumHeight();
    telegram.onEvent?.("themeChanged", applyTelegramTheme); telegram.onEvent?.("viewportChanged", syncViewport); telegram.onEvent?.("safeAreaChanged", syncViewport); telegram.onEvent?.("contentSafeAreaChanged", syncViewport); telegram.onEvent?.("fullscreenChanged", syncViewport);
    telegram.onEvent?.("fullscreenFailed", () => { syncViewport(); showNotice("Telegram could not enter fullscreen mode.", true); });
    telegram.onEvent?.("activated", () => { syncViewport(); load(); });
  }

  function showNotice(message, error = false) { const notice = byId("notice"); notice.hidden = !message; notice.textContent = message || ""; notice.classList.toggle("error", error); }
  function setBadge(label, variant) { const badge = byId("connection-badge"); badge.dataset.variant = variant; badge.innerHTML = `<span class="connection-dot"></span>${label}`; }
  function formatDate(value) { if (!value) return "—"; const date = new Date(value); return Number.isNaN(date.valueOf()) ? "—" : new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" }).format(date); }
  function formatElapsed(seconds) { if (!Number.isFinite(seconds) || seconds < 0) return "—"; if (seconds < 60) return `${seconds} second${seconds === 1 ? "" : "s"}`; const minutes = Math.floor(seconds / 60); return `${minutes} minute${minutes === 1 ? "" : "s"}`; }
  function shortPath(value) { if (!value) return "Repository unavailable"; const parts = value.split(/[\\/]/).filter(Boolean); return parts.slice(-2).join("/") || value; }
  function authHeaders() { const headers = {}; if (telegram?.initData) headers["X-Telegram-Init-Data"] = telegram.initData; if (browserSessionToken) headers["X-Codex-Browser-Session"] = browserSessionToken; return headers; }
  function setPairingVisible(visible) { byId("browser-pairing-card").hidden = !visible; }
  function clearPairingPoll() { if (pairingPollTimer) clearTimeout(pairingPollTimer); pairingPollTimer = null; }
  async function pollBrowserPairing() {
    if (!browserPairingToken || pairingPollInFlight) return; pairingPollInFlight = true;
    try {
      const response = await fetch("/api/mini-app/pairing/status", { headers: { "X-Codex-Browser-Pairing": browserPairingToken }, credentials: "same-origin" });
      if (response.status === 401) throw new Error("This pairing code is no longer valid."); if (!response.ok) throw new Error("Pairing status is unavailable.");
      const status = await response.json();
      if (status.state === "approved" && status.sessionToken) { browserSessionToken = status.sessionToken; sessionStorage.setItem("codexTelegramBrowserSession", browserSessionToken); sessionStorage.removeItem("codexTelegramBrowserPairing"); browserPairingToken = null; clearPairingPoll(); setPairingVisible(false); await load(); return; }
      if (status.state === "expired") { browserPairingToken = null; sessionStorage.removeItem("codexTelegramBrowserPairing"); clearPairingPoll(); showNotice("That pairing code expired. Select Get a new code to try again.", true); return; }
      pairingPollTimer = setTimeout(pollBrowserPairing, 2000);
    } catch (error) { if (browserPairingToken) { showNotice(error.message || "Pairing status is unavailable.", true); pairingPollTimer = setTimeout(pollBrowserPairing, 5000); } }
    finally { pairingPollInFlight = false; }
  }
  async function beginBrowserPairing() {
    clearPairingPoll(); setPairingVisible(false); setBadge("Pairing", "warning");
    try {
      const response = await fetch("/api/mini-app/pairing/start", { credentials: "same-origin" }); if (response.status === 404) throw new Error("Open this Mini App from Telegram, or enable browser pairing on this host."); if (!response.ok) throw new Error("Browser pairing is unavailable.");
      const start = await response.json(); browserPairingToken = start.pairingToken; sessionStorage.setItem("codexTelegramBrowserPairing", browserPairingToken); byId("browser-pairing-code").textContent = start.pairingCode || "—"; byId("browser-pairing-instructions").textContent = `Send /pair ${start.pairingCode || "<code>"} to your bot in its private Telegram chat. This code expires at ${formatDate(start.expiresAtUtc)}.`; setPairingVisible(true); showNotice("Approve the pairing code in Telegram to connect this page."); pollBrowserPairing();
    } catch (error) { setBadge("Unavailable", "danger"); showNotice(error.message || "Browser pairing is unavailable.", true); }
  }

  function renderSession(data) {
    const current = data.currentSession;
    if (!current) { byId("session-screen").hidden = true; byId("empty-state").hidden = false; byId("repository-label").textContent = "Codex session"; return; }
    byId("empty-state").hidden = true; byId("session-screen").hidden = false; byId("repository-label").textContent = current.repository || "Codex session";
    const nameInput = byId("session-name-input"); if (document.activeElement !== nameInput) nameInput.value = current.name || "Unnamed session";
    byId("session-repository").lastElementChild.textContent = current.repository || "Repository unavailable"; byId("session-path").textContent = current.workingDirectory || "Working directory unavailable";
    const strip = byId("activity-strip"), status = current.activityStatus || "Disconnected"; strip.dataset.state = status === "Disconnected" ? "disconnected" : status === "Waiting for you" ? "waiting" : status === "Ready" ? "ready" : "working"; byId("activity-status").textContent = status;
    byId("activity-description").textContent = current.activityDescription || (status === "Ready" ? "Waiting for the next turn" : "Runtime activity is being observed");
    updateElapsed(current);
    renderGoal(current); renderSettings(current); renderUsage(data.usage); renderGlobalLink(data.globalInstructions); renderSessionSwitcher(data);
  }
  function updateElapsed(current) {
    const seconds = current.activityStartedAtUtc ? Math.max(current.activityElapsedSeconds || 0, Math.floor((Date.now() - new Date(current.activityStartedAtUtc).getTime()) / 1000)) : 0;
    byId("activity-elapsed").textContent = current.activityStatus === "Ready" || current.activityStatus === "Disconnected" ? "—" : formatElapsed(seconds);
  }
  function renderGoal(current) {
    const goal = current.goal; const input = byId("goal-input"); if (document.activeElement !== input) input.value = goal?.objective || "";
    const stateLabel = byId("goal-state"); stateLabel.textContent = goal ? goal.status || "Active" : "No goal"; stateLabel.dataset.state = (goal?.status || "").toLowerCase();
    const confirmation = byId("goal-confirmation"); confirmation.hidden = !goal; if (goal) confirmation.textContent = `${goal.status || "Active"} · accepted by Codex`;
    const statusButton = byId("goal-status-button"); statusButton.hidden = !goal || ["Complete", "completed", "Completed"].includes(goal.status); statusButton.textContent = String(goal.status).toLowerCase() === "paused" ? "Resume goal" : "Pause goal";
  }
  function renderSettings(current) {
    const modelSelect = byId("model-select"), effortSelect = byId("effort-select"), models = (current.models || []).filter(model => !model.hidden);
    modelSelect.replaceChildren();
    models.forEach(model => { const option = new Option(model.displayName || model.id, model.id); if (model.availabilityMessage) option.disabled = true; modelSelect.add(option); });
    if (!models.length) { modelSelect.add(new Option("Model catalog unavailable", "")); modelSelect.disabled = true; effortSelect.replaceChildren(); effortSelect.add(new Option("Thinking options unavailable", "")); effortSelect.disabled = true; }
    else { modelSelect.disabled = false; modelSelect.value = current.nextModel || current.currentModel || models[0].id; populateEfforts(current, modelSelect.value); }
    const active = current.activityStatus !== "Ready" && current.activityStatus !== "Disconnected";
    byId("settings-acceptance-text").textContent = active ? `Current turn: ${current.currentModel || "Existing model"} · ${current.currentThinkingEffort || "existing effort"}. Next turn: ${current.nextModel || current.currentModel || "selected model"} · ${current.nextThinkingEffort || current.currentThinkingEffort || "selected effort"}.` : "Idle session · effective selection";
    byId("settings-acceptance").dataset.active = String(active);
  }
  function populateEfforts(current, modelId) {
    const model = (current.models || []).find(candidate => candidate.id === modelId) || current.models?.[0]; const select = byId("effort-select"); select.replaceChildren();
    const choices = model?.supportedThinkingEfforts?.length ? model.supportedThinkingEfforts : model?.defaultThinkingEffort ? [model.defaultThinkingEffort] : [];
    choices.forEach(choice => select.add(new Option(choice, choice))); select.disabled = !choices.length; if (!choices.length) select.add(new Option("Runtime default", "")); select.value = current.nextThinkingEffort || current.currentThinkingEffort || model?.defaultThinkingEffort || choices[0] || "";
  }
  function renderGlobalLink(instructions) { const copy = byId("global-card-button").querySelector(".global-link-copy span"); if (copy) copy.textContent = instructions?.applicationState === "saved_next_safe_boundary" ? "Saved; this session adopts it at the next safe boundary." : "Applies to new sessions and the next turn."; }
  function renderSessionSwitcher(data) {
    const list = byId("session-switch-list"); list.replaceChildren(); const sessions = data.threads || [];
    if (!sessions.length) { list.append(Object.assign(document.createElement("div"), { className: "switch-option-meta", textContent: "No other sessions available" })); return; }
    sessions.slice(0, 12).forEach(session => { const button = document.createElement("button"); button.type = "button"; button.className = `switch-option${session.id === data.activeSessionId ? " current" : ""}`; button.innerHTML = `<span class="switch-option-copy"><span class="switch-option-name"></span><span class="switch-option-meta"></span></span><span aria-hidden="true">${session.id === data.activeSessionId ? "✓" : ""}</span>`; button.querySelector(".switch-option-name").textContent = session.name || "Unnamed session"; button.querySelector(".switch-option-meta").textContent = `${shortPath(session.workingDirectory)} · ${session.lifecycleState || "ready"}`; button.disabled = session.id === data.activeSessionId; button.addEventListener("click", () => selectSession(session.id)); list.append(button); });
  }

  function windowLabel(minutes) { if (!minutes) return "Quota window"; if (minutes % 10080 === 0) return `${minutes / 10080} week${minutes / 10080 === 1 ? "" : "s"}`; if (minutes % 1440 === 0) return `${minutes / 1440} day${minutes / 1440 === 1 ? "" : "s"}`; if (minutes % 60 === 0) return `${minutes / 60} hour${minutes / 60 === 1 ? "" : "s"}`; return `${minutes} minute${minutes === 1 ? "" : "s"}`; }
  function renderUsage(usage) {
    const list = byId("usage-list"); list.replaceChildren(); byId("usage-retrieved").textContent = usage?.retrievedAtUtc ? `Updated ${formatDate(usage.retrievedAtUtc)}` : "No confirmed snapshot";
    const rows = (usage?.rateLimits || []).flatMap(limit => [[limit, "primary", limit.primary], [limit, "secondary", limit.secondary]]).filter(([, , window]) => window);
    if (!rows.length) { list.append(Object.assign(document.createElement("div"), { className: "chart-empty", textContent: "Account usage is currently unavailable." })); return; }
    rows.forEach(([limit, kind, window]) => { const row = document.createElement("div"); row.className = "usage-row"; const copy = document.createElement("div"); const label = document.createElement("div"); label.className = "usage-label"; label.innerHTML = `<span></span><span class="usage-percent"></span>`; label.firstElementChild.textContent = windowLabel(window.windowDurationMinutes); label.lastElementChild.textContent = `${window.usedPercent}%`; const reset = document.createElement("div"); reset.className = "usage-reset"; reset.textContent = window.resetsAtUtc ? `Resets ${formatDate(window.resetsAtUtc)}` : "Reset time unavailable"; copy.append(label, reset); const chart = document.createElement("div"); chart.className = "usage-chart"; const samples = (usage.history || []).filter(sample => sample.window === kind && sample.limitId === limit.limitId); renderSparkline(chart, samples, window.resetsAtUtc); row.append(copy, chart); list.append(row); });
  }
  function renderSparkline(container, samples, resetAt) {
    if (samples.length < 2) { container.append(Object.assign(document.createElement("div"), { className: "chart-empty", textContent: "History is accumulating" })); return; }
    const sorted = samples.slice().sort((a, b) => new Date(a.observedAtUtc) - new Date(b.observedAtUtc)); const minTime = new Date(sorted[0].observedAtUtc).getTime(), maxTime = Math.max(new Date(sorted.at(-1).observedAtUtc).getTime(), resetAt ? new Date(resetAt).getTime() : 0), span = Math.max(1, maxTime - minTime), width = 420, height = 40;
    const svg = document.createElementNS("http://www.w3.org/2000/svg", "svg"); svg.setAttribute("viewBox", `0 0 ${width} ${height}`); svg.classList.add("sparkline"); const axis = document.createElementNS("http://www.w3.org/2000/svg", "line"); axis.setAttribute("x1", "0"); axis.setAttribute("x2", String(width)); axis.setAttribute("y1", "36"); axis.setAttribute("y2", "36"); axis.classList.add("sparkline-axis"); svg.append(axis);
    let segment = [];
    const flush = () => { if (segment.length < 2) { segment = []; return; } const line = document.createElementNS("http://www.w3.org/2000/svg", "polyline"); line.setAttribute("points", segment.map(point => `${point.x},${point.y}`).join(" ")); line.classList.add("sparkline-segment"); svg.append(line); segment = []; };
    sorted.forEach((sample, index) => { const time = new Date(sample.observedAtUtc).getTime(); const x = ((time - minTime) / span) * width; const y = 34 - (Math.max(0, Math.min(100, sample.usedPercent)) / 100) * 28; if (index > 0 && time - new Date(sorted[index - 1].observedAtUtc).getTime() > span / 3) flush(); segment.push({ x, y }); }); flush();
    if (resetAt) { const resetTime = new Date(resetAt).getTime(); if (resetTime >= minTime && resetTime <= maxTime) { const marker = document.createElementNS("http://www.w3.org/2000/svg", "line"); const x = ((resetTime - minTime) / span) * width; marker.setAttribute("x1", String(x)); marker.setAttribute("x2", String(x)); marker.setAttribute("y1", "2"); marker.setAttribute("y2", "37"); marker.classList.add("sparkline-reset"); svg.append(marker); const text = document.createElementNS("http://www.w3.org/2000/svg", "text"); text.setAttribute("x", String(Math.min(width - 28, x + 3))); text.setAttribute("y", "9"); text.classList.add("sparkline-reset-label"); text.textContent = "reset"; svg.append(text); } }
    container.append(svg); const times = document.createElement("div"); times.className = "chart-times"; [sorted[0], sorted.at(-1)].forEach(sample => { const time = document.createElement("span"); time.textContent = formatDate(sample.observedAtUtc); times.append(time); }); container.append(times);
  }

  async function postJson(url, body) {
    const response = await fetch(url, { method: "POST", headers: { ...authHeaders(), "Content-Type": "application/json" }, credentials: "same-origin", body: JSON.stringify(body) });
    if (response.status === 401) throw new Error("Open this Mini App from Telegram to connect your account.");
    let result = null; try { result = await response.json(); } catch { /* Empty error responses are handled below. */ }
    if (!response.ok) throw new Error(result?.error || "The requested change could not be completed."); return result;
  }
  async function updateSession(action, extra = {}) { const sessionId = state.data?.currentSession?.id; if (!sessionId) return; try { const result = await postJson("/api/mini-app/session/actions", { sessionId, action, ...extra }); showNotice(action === "update_model" ? "Next-turn settings saved and accepted by Codex." : result?.outcomeCode === "goal_cleared_by_codex" ? "Goal cleared by Codex." : "Change accepted by Codex."); await load(); } catch (error) { showNotice(error.message, true); } }
  async function selectSession(sessionId) { try { await postJson("/api/mini-app/session/actions", { sessionId, action: "select" }); byId("session-switch-list").hidden = true; byId("session-switch-button").setAttribute("aria-expanded", "false"); showNotice("Session selected for this Telegram conversation."); await load(); } catch (error) { showNotice(error.message, true); } }
  async function saveGoal(event) { event.preventDefault(); const objective = byId("goal-input").value.trim(); if (!objective) { showNotice("Enter a goal or use Clear to remove the current goal.", true); return; } await updateSession("set_goal", { objective }); }
  async function clearGoal() { if (!window.confirm("Clear the goal for this session?")) return; await updateSession("clear_goal"); }
  async function saveName(event) { event.preventDefault(); const name = byId("session-name-input").value.trim(); if (!name) { showNotice("Session name cannot be empty.", true); return; } await updateSession("rename", { name }); }
  async function saveSettings() { await updateSession("update_model", { model: byId("model-select").value || null, reasoningEffort: byId("effort-select").value || null }); }
  async function saveInstructions(clear = false) { try { const result = await postJson("/api/mini-app/instructions", { text: clear ? "" : byId("global-instructions-input").value }); byId("global-instructions-input").value = result.text || ""; byId("global-application-state").textContent = "Saved. New sessions receive this version; an active session adopts it at the next safe boundary."; showNotice("Global instructions saved. No prompt was sent and current work was not interrupted."); if (state.data) state.data.globalInstructions = result; renderGlobalLink(result); } catch (error) { showNotice(error.message, true); } }
  function openGlobalInstructions() { const drawer = byId("global-drawer"); drawer.hidden = false; byId("global-instructions-input").value = state.data?.globalInstructions?.text || ""; byId("global-instructions-input").focus(); }
  function closeGlobalInstructions() { byId("global-drawer").hidden = true; }

  function render(data) { state.data = data; byId("empty-state").hidden = !!data.currentSession; renderSession(data); const globalInput = byId("global-instructions-input"); if (document.activeElement !== globalInput) globalInput.value = data.globalInstructions?.text || ""; }
  function renderUnavailable() { byId("session-screen").hidden = true; byId("empty-state").hidden = false; byId("repository-label").textContent = "Codex session"; }
  async function load() {
    loadController?.abort(); loadController = new AbortController(); const { signal } = loadController; setBadge("Connecting", "neutral");
    if (!telegram && !browserSessionToken && !explicitPreview) { await beginBrowserPairing(); return; }
    try {
      const response = await fetch("/api/mini-app/bootstrap", { headers: authHeaders(), credentials: "same-origin", signal });
      if (response.status === 401 && !telegram && browserSessionToken) { browserSessionToken = null; sessionStorage.removeItem("codexTelegramBrowserSession"); await beginBrowserPairing(); return; }
      if (response.status === 401) throw new Error("Open this Mini App from Telegram to connect your account."); if (!response.ok) throw new Error(`Mini App is not enabled on this host (${response.status}).`);
      const data = await response.json(); if (signal.aborted) return; state.preview = false; state.stale = false; lastLiveData = data; setBadge("Live", "success"); render(data); showNotice("");
    } catch (error) {
      if (signal.aborted) return;
      if (explicitPreview) { state.preview = true; setBadge("Preview", "warning"); render(previewData); showNotice(`${error.message || "Live data is unavailable."} This is explicit local preview data.`, true); }
      else if (lastLiveData) { state.stale = true; setBadge("Stale", "warning"); render(lastLiveData); showNotice(`${error.message || "Live data is unavailable."} Showing the last confirmed session snapshot.`, true); }
      else { setBadge("Disconnected", "danger"); renderUnavailable(); showNotice(error.message || "The Mini App could not load live data.", true); }
    }
  }

  byId("fullscreen-button").addEventListener("click", () => { if (!telegram) return; try { if (telegram.isFullscreen === true) telegram.exitFullscreen?.(); else telegram.requestFullscreen?.(); } catch { showNotice("Fullscreen mode could not be changed.", true); } });
  byId("refresh-button").addEventListener("click", load); byId("browser-pairing-retry").addEventListener("click", beginBrowserPairing); byId("global-instructions-button").addEventListener("click", openGlobalInstructions); byId("empty-global-button").addEventListener("click", openGlobalInstructions); byId("global-card-button").addEventListener("click", openGlobalInstructions); byId("global-close-button").addEventListener("click", closeGlobalInstructions); byId("global-save-button").addEventListener("click", () => saveInstructions()); byId("global-clear-button").addEventListener("click", () => saveInstructions(true));
  byId("session-name-form").addEventListener("submit", saveName); byId("goal-form").addEventListener("submit", saveGoal); byId("goal-clear-button").addEventListener("click", clearGoal); byId("goal-status-button").addEventListener("click", () => updateSession("set_goal_status", { goalStatus: String(state.data?.currentSession?.goal?.status).toLowerCase() === "paused" ? "Active" : "Paused" }));
  byId("model-select").addEventListener("change", () => { if (state.data?.currentSession) { populateEfforts(state.data.currentSession, byId("model-select").value); saveSettings(); } }); byId("effort-select").addEventListener("change", saveSettings);
  byId("session-switch-button").addEventListener("click", () => { const list = byId("session-switch-list"); list.hidden = !list.hidden; byId("session-switch-button").setAttribute("aria-expanded", String(!list.hidden)); });
  document.addEventListener("click", event => { if (!event.target.closest(".session-switch-wrap")) { byId("session-switch-list").hidden = true; byId("session-switch-button").setAttribute("aria-expanded", "false"); } });
  window.addEventListener("resize", syncViewport); setInterval(() => { if (state.data?.currentSession) updateElapsed(state.data.currentSession); }, 1000); setInterval(() => { if (!state.preview) load(); }, 5000);
  initializeTelegram(); load();
})();
