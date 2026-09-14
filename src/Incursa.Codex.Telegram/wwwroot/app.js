(() => {
  const telegram = window.Telegram?.WebApp;
  const state = { data: null, preview: false };
  let loadController = null;
  const byId = (id) => document.getElementById(id);
  const root = document.documentElement;

  const previewData = {
    user: { id: 0, firstName: "Preview", username: "local" },
    activeSessionId: "preview-session",
    activeProjectWorkingDirectory: "/workspace/codex-telegram",
    projects: [{ workingDirectory: "/workspace/codex-telegram", displayName: "codex-telegram", addedAt: new Date().toISOString() }],
    threads: [
      { id: "preview-session", name: "Mini App spike", preview: "Read-only dashboard surface", status: "idle", updatedAt: new Date().toISOString(), workingDirectory: "/workspace/codex-telegram" },
      { id: "preview-second", name: "UIKit integration", preview: "Published web component runtime", status: "idle", updatedAt: new Date(Date.now() - 86400000).toISOString(), workingDirectory: "/workspace/codex-telegram" }
    ],
    runtime: { initialized: true, serverName: "Preview Codex", serverVersion: "local" },
    usage: null
  };

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
    if (Number.isFinite(Number(value)) && Number(value) >= 0) {
      root.style.setProperty(name, `${Number(value)}px`);
    }
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
    if (Number.isFinite(viewportHeight) && viewportHeight > 0) {
      root.style.setProperty("--app-viewport-height", `${viewportHeight}px`);
    }
    if (Number.isFinite(stableHeight) && stableHeight > 0) {
      root.style.setProperty("--app-viewport-stable-height", `${stableHeight}px`);
    }

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

    const mode = telegram.isFullscreen === true
      ? "Full screen"
      : telegram.isExpanded === false
        ? "Compact"
        : "Full height";
    root.dataset.telegramDisplayMode = mode.toLowerCase().replace(" ", "-");
    const viewportBadge = byId("viewport-badge");
    viewportBadge.hidden = false;
    viewportBadge.textContent = mode;
    viewportBadge.setAttribute("variant", mode === "Compact" ? "warning" : mode === "Full screen" ? "success" : "info");

    const fullscreenButton = byId("fullscreen-button");
    const supportsFullscreen = telegram.isFullscreen === true
      ? typeof telegram.exitFullscreen === "function"
      : typeof telegram.requestFullscreen === "function";
    fullscreenButton.hidden = !supportsFullscreen;
    fullscreenButton.textContent = telegram.isFullscreen === true ? "Exit fullscreen" : "Fullscreen";
    fullscreenButton.setAttribute("aria-label", fullscreenButton.textContent);
    fullscreenButton.title = fullscreenButton.textContent;
  }

  function requestMaximumHeight() {
    if (!telegram || typeof telegram.expand !== "function" || telegram.isExpanded === true) return;
    try {
      telegram.expand();
    } catch (error) {
      showNotice(`Telegram could not expand this Mini App: ${error.message || "unsupported operation"}.`, true);
    }
  }

  function initializeTelegram() {
    if (!telegram) {
      root.dataset.telegramDisplayMode = "browser";
      syncViewport();
      return;
    }

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
      telegram.onEvent("fullscreenFailed", (event) => {
        syncViewport();
        showNotice(`Telegram could not enter fullscreen mode${event?.error ? ` (${event.error})` : ""}.`, true);
      });
      telegram.onEvent("activated", () => {
        syncViewport();
        load();
      });
      telegram.onEvent("deactivated", syncViewport);
    }
  }

  function showNotice(message, error = false) {
    const notice = byId("notice");
    notice.hidden = !message;
    notice.textContent = message || "";
    notice.classList.toggle("error", error);
  }

  function setBadge(label, variant) {
    const badge = byId("connection-badge");
    badge.setAttribute("variant", variant);
    badge.textContent = label;
  }

  function formatDate(value) {
    if (!value) return "—";
    return new Intl.DateTimeFormat(undefined, { month: "short", day: "numeric", hour: "numeric", minute: "2-digit" }).format(new Date(value));
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

  function renderSummary(data) {
    const [runtime, runtimeDetail] = runtimeLabel(data.runtime);
    byId("runtime-value").textContent = runtime;
    byId("runtime-detail").textContent = runtimeDetail;
    byId("session-value").textContent = data.activeSessionId ? "Selected" : "None";
    const active = data.threads?.find((thread) => thread.id === data.activeSessionId);
    byId("session-detail").textContent = active?.name || (data.activeSessionId ? data.activeSessionId : "No session selected");
    byId("project-value").textContent = String(data.projects?.length || 0);
    const [usage, usageDetail] = usageLabel(data.usage);
    byId("usage-value").textContent = usage;
    byId("usage-detail").textContent = usageDetail;
  }

  function renderSessions(data) {
    const list = byId("sessions-list");
    list.replaceChildren();
    if (!data.threads?.length) {
      const empty = document.createElement("div");
      empty.className = "empty-row";
      empty.textContent = "No Codex sessions are available yet.";
      list.append(empty);
      return;
    }
    data.threads.forEach((thread) => {
      const row = document.createElement("div");
      row.className = `session-row${thread.id === data.activeSessionId ? " active-row" : ""}`;
      row.tabIndex = 0;
      row.setAttribute("role", "button");
      const copy = document.createElement("div");
      copy.className = "session-copy";
      const name = document.createElement("div");
      name.className = "session-name";
      name.textContent = thread.name || "Unnamed session";
      const preview = document.createElement("div");
      preview.className = "session-preview";
      preview.textContent = thread.preview || shortPath(thread.workingDirectory);
      copy.append(name, preview);
      const meta = document.createElement("div");
      meta.className = "session-meta";
      const status = document.createElement("inc-badge");
      status.setAttribute("variant", thread.status === "error" ? "danger" : thread.status === "running" ? "success" : "neutral");
      const dot = document.createElement("span");
      dot.className = `status-dot ${thread.status}`;
      status.append(dot, document.createTextNode(thread.status || "idle"));
      const date = document.createElement("span");
      date.className = "session-date";
      date.textContent = formatDate(thread.updatedAt);
      meta.append(status, date);
      row.append(copy, meta);
      const showDetails = () => showThread(thread);
      row.addEventListener("click", showDetails);
      row.addEventListener("keydown", (event) => { if (event.key === "Enter" || event.key === " ") showDetails(); });
      list.append(row);
    });
  }

  function renderWorkspace(data) {
    const list = byId("workspace-list");
    list.replaceChildren();
    const projects = data.projects || [];
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
      path.textContent = shortPath(project.workingDirectory);
      copy.append(name, path);
      const badge = document.createElement("inc-badge");
      badge.setAttribute("variant", project.workingDirectory === data.activeProjectWorkingDirectory ? "success" : "neutral");
      badge.textContent = project.workingDirectory === data.activeProjectWorkingDirectory ? "Active" : "Saved";
      row.append(copy, badge);
      list.append(row);
    });
  }

  function showThread(thread) {
    byId("detail-card").hidden = false;
    byId("detail-title").textContent = thread.name || "Unnamed session";
    byId("detail-subtitle").textContent = `${thread.status || "idle"} · ${shortPath(thread.workingDirectory)}`;
    byId("detail-body").textContent = [
      `Thread: ${thread.id}`,
      `Updated: ${formatDate(thread.updatedAt)}`,
      `Provider: ${thread.modelProvider || "—"}`,
      "",
      thread.preview || "No preview available."
    ].join("\n");
    byId("detail-card").scrollIntoView({ behavior: "smooth", block: "nearest" });
  }

  function render(data) {
    state.data = data;
    renderSummary(data);
    renderSessions(data);
    renderWorkspace(data);
  }

  async function load() {
    loadController?.abort();
    loadController = new AbortController();
    const { signal } = loadController;
    setBadge("Connecting", "neutral");
    showNotice("");
    try {
      const headers = {};
      if (telegram?.initData) headers["X-Telegram-Init-Data"] = telegram.initData;
      const response = await fetch("/api/mini-app/bootstrap", { headers, credentials: "same-origin", signal });
      if (signal.aborted) return;
      if (response.status === 401) throw new Error("Open this Mini App from Telegram to connect your account.");
      if (!response.ok) throw new Error(`Mini App is not enabled on this host (${response.status}).`);
      state.preview = false;
      setBadge("Live", "success");
      render(await response.json());
    } catch (error) {
      if (signal.aborted) return;
      state.preview = true;
      setBadge("Preview", "warning");
      render(previewData);
      showNotice(`${error.message || "The Mini App could not load live data."} This screen is showing clearly marked local preview data.`, true);
    }
  }

  byId("fullscreen-button").addEventListener("click", () => {
    if (!telegram) return;
    try {
      if (telegram.isFullscreen === true && typeof telegram.exitFullscreen === "function") {
        telegram.exitFullscreen();
      } else if (typeof telegram.requestFullscreen === "function") {
        telegram.requestFullscreen();
      } else {
        showNotice("Fullscreen mode is not supported by this Telegram client.", true);
      }
    } catch (error) {
      showNotice(`Fullscreen mode could not be changed: ${error.message || "unsupported operation"}.`, true);
    }
  });
  byId("refresh-button").addEventListener("click", load);
  byId("close-detail-button").addEventListener("click", () => { byId("detail-card").hidden = true; });
  byId("open-chat-button").addEventListener("click", () => { if (telegram) telegram.close(); else showNotice("Return to the Telegram chat to send prompts and change context."); });
  window.addEventListener("resize", syncViewport);
  initializeTelegram();
  load();
})();
