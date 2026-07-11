/* OptiHub WebView SPA — minimal, no-scroll layouts */
(() => {
  const state = {
    theme: "dark",
    appVersion: "—",
    kitVersion: "—",
    autoUpdate: false,
    pwsh: null,
    route: "home",
    kit: null,
    busy: false,
    navigating: false,
  };

  const view = document.getElementById("view");
  const btnBack = document.getElementById("btnBack");
  const btnSettings = document.getElementById("btnSettings");
  const ctxLogo = document.getElementById("ctxLogo");
  const ctxTitle = document.getElementById("ctxTitle");
  const toastEl = document.getElementById("toast");
  const modal = document.getElementById("modal");

  let reqId = 0;
  const pending = new Map();
  let toastTimer;

  function post(method, params = {}) {
    return new Promise((resolve, reject) => {
      const id = String(++reqId);
      pending.set(id, { resolve, reject });
      const msg = JSON.stringify({ id, method, params });
      if (window.chrome?.webview) {
        window.chrome.webview.postMessage(msg);
      } else {
        pending.delete(id);
        reject(new Error("WebView host bridge unavailable"));
        return;
      }
      setTimeout(() => {
        if (pending.has(id)) {
          pending.delete(id);
          reject(new Error("Host timeout: " + method));
        }
      }, 300000);
    });
  }

  if (window.chrome?.webview) {
    window.chrome.webview.addEventListener("message", (e) => {
      let data = e.data;
      if (typeof data === "string") {
        try { data = JSON.parse(data); } catch { return; }
      }
      if (!data) return;
      if (data.type === "progress") { onProgress(data); return; }
      if (data.type === "theme") { applyTheme(data.theme); return; }
      if (data.id && pending.has(data.id)) {
        const p = pending.get(data.id);
        pending.delete(data.id);
        if (data.error) p.reject(new Error(data.error));
        else p.resolve(data.result);
      }
    });
  }

  function applyTheme(theme) {
    state.theme = theme === "Light" || theme === "light" ? "light" : "dark";
    document.documentElement.setAttribute("data-theme", state.theme);
  }

  function toast(msg) {
    toastEl.textContent = msg;
    toastEl.classList.remove("hidden");
    requestAnimationFrame(() => toastEl.classList.add("show"));
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => {
      toastEl.classList.remove("show");
      setTimeout(() => toastEl.classList.add("hidden"), 220);
    }, 2400);
  }

  function confirmModal(title, body) {
    return new Promise((resolve) => {
      document.getElementById("modalTitle").textContent = title;
      document.getElementById("modalBody").textContent = body;
      modal.classList.remove("hidden");
      const ok = document.getElementById("modalOk");
      const cancel = document.getElementById("modalCancel");
      const backdrop = document.getElementById("modalBackdrop");
      const done = (v) => {
        modal.classList.add("hidden");
        ok.onclick = cancel.onclick = backdrop.onclick = null;
        resolve(v);
      };
      ok.onclick = () => done(true);
      cancel.onclick = () => done(false);
      backdrop.onclick = () => done(false);
    });
  }

  function setChrome(route) {
    const home = route === "home";
    btnSettings.classList.toggle("hidden", !home);
    btnBack.classList.toggle("hidden", home);

    const map = {
      discord: { title: "Discord", logo: "logos/discord.png" },
      steam: { title: "Steam", logo: "logos/steam.png" },
      nvidia: { title: "NVIDIA", logo: "logos/nvidia.png" },
      settings: { title: "Settings", logo: null },
      home: { title: "", logo: null },
    };
    const m = map[route] || map.home;
    ctxTitle.textContent = m.title;
    if (m.logo) {
      let src = m.logo;
      if (window.__OPTIHUB_LOGOS__) {
        src = window.__OPTIHUB_LOGOS__ + m.logo.replace(/^logos\//, "");
      }
      ctxLogo.src = src;
      ctxLogo.classList.remove("hidden");
    } else {
      ctxLogo.classList.add("hidden");
      ctxLogo.removeAttribute("src");
    }
  }

  async function navigate(route) {
    if (state.navigating) return;
    state.navigating = true;
    const prev = view.querySelector(".page");
    if (prev) {
      prev.classList.add("exit");
      await wait(120);
    }
    state.route = route;
    setChrome(route);
    try {
      if (route === "home") await renderHome();
      else if (route === "settings") await renderSettings();
      else if (route === "discord" || route === "steam" || route === "nvidia")
        await renderOptimizer(route);
    } finally {
      state.navigating = false;
      view.focus({ preventScroll: true });
    }
  }

  function wait(ms) {
    return new Promise((r) => setTimeout(r, ms));
  }

  function logoUrl(rel) {
    if (!rel) return "";
    if (/^https?:|^file:|^data:/i.test(rel)) return rel;
    if (window.__OPTIHUB_LOGOS__) {
      return window.__OPTIHUB_LOGOS__ + String(rel).replace(/^logos\//, "");
    }
    return rel;
  }

  function hydrateBoot(boot) {
    applyTheme(boot.theme);
    state.appVersion = boot.appVersion;
    state.kitVersion = boot.kitVersion;
    state.autoUpdate = !!boot.autoUpdate;
    state.pwsh = boot.pwsh;
  }

  async function renderHome() {
    view.innerHTML = `<div class="page home"><div class="loading">Loading…</div></div>`;
    let cards = [];
    try {
      const boot = await post("getBootstrap");
      hydrateBoot(boot);
      cards = boot.cards || [];
    } catch (e) {
      view.innerHTML = `<div class="page"><div class="panel result bad">${esc(e.message)}</div></div>`;
      return;
    }

    // Prefer live optimizers first so the home grid stays clean (3 primary + rest).
    const primary = cards.filter((c) => !c.comingSoon);
    const soon = cards.filter((c) => c.comingSoon).slice(0, Math.max(0, 3 - primary.length));
    const show = [...primary, ...soon].slice(0, 6);

    const html = show.map((c) => {
      const soon = c.comingSoon;
      return `<button type="button" class="card-btn" data-id="${esc(c.id)}" ${soon ? "disabled" : ""}>
        <div class="logo-wrap"><img src="${esc(logoUrl(c.logo))}" alt="" draggable="false" /></div>
        <span class="label">${esc(c.title)}</span>
        ${soon ? `<span class="soon">Soon</span>` : ""}
      </button>`;
    }).join("");

    view.innerHTML = `
      <div class="page home">
        <div class="home-head">
          <h1>Maximum performance</h1>
          <p>Pick a target. Status is verified when you open it.</p>
        </div>
        <div class="cards">${html}</div>
      </div>`;

    view.querySelectorAll(".card-btn[data-id]").forEach((btn) => {
      btn.addEventListener("click", () => {
        const id = btn.getAttribute("data-id");
        if (id === "discord" || id === "steam" || id === "nvidia") navigate(id);
      });
    });
  }

  async function renderSettings() {
    view.innerHTML = `<div class="page settings"><div class="loading">Loading…</div></div>`;
    try {
      const boot = await post("getBootstrap");
      hydrateBoot(boot);
    } catch (e) {
      view.innerHTML = `<div class="page"><div class="panel result bad">${esc(e.message)}</div></div>`;
      return;
    }

    const dark = state.theme === "dark";
    const kits = String(state.kitVersion || "—").replace(/\s*·\s*/g, " · ");

    view.innerHTML = `
      <div class="page settings">
        <div class="settings-head">
          <h1>Settings</h1>
          <p>Theme, updates, and logs</p>
        </div>

        <div class="settings-grid">
          <div class="surface cell">
            <div class="lbl">Appearance</div>
            <p class="hint">AMOLED black or warm cream.</p>
            <div class="theme-row">
              <button type="button" class="theme-pill ${dark ? "on" : ""}" data-theme="dark">
                <span class="swatch dark"></span> Dark
              </button>
              <button type="button" class="theme-pill ${!dark ? "on" : ""}" data-theme="light">
                <span class="swatch light"></span> Light
              </button>
            </div>
          </div>

          <div class="surface cell">
            <div class="lbl">Support</div>
            <p class="hint">Optimizer apply logs on disk.</p>
            <div class="row-actions">
              <button type="button" class="btn quiet block" id="btnLogs">Open logs</button>
            </div>
          </div>

          <div class="surface cell span">
            <div class="lbl">Updates</div>
            <div class="updates">
              <div>
                <div class="ver-num">${esc(state.appVersion)}</div>
              </div>
              <div class="ver-meta">
                <div class="line">Installed · kits ship with this build</div>
                <div class="kits">${esc(kits)}</div>
              </div>
              <div class="updates-actions">
                <div class="toggle-line">
                  <span>Check on launch</span>
                  <label class="switch">
                    <input type="checkbox" id="autoUp" ${state.autoUpdate ? "checked" : ""} />
                    <span class="track"><span class="thumb"></span></span>
                  </label>
                </div>
                <button type="button" class="btn primary block" id="btnUpdate">Check for updates</button>
                <div class="status-line" id="updateStatus" role="status" aria-live="polite">Up to date</div>
              </div>
            </div>
          </div>
        </div>
      </div>`;

    view.querySelectorAll(".theme-pill").forEach((el) => {
      el.addEventListener("click", async () => {
        const t = el.getAttribute("data-theme") === "light" ? "Light" : "Dark";
        applyTheme(t);
        view.querySelectorAll(".theme-pill").forEach((p) => {
          p.classList.toggle("on", p.getAttribute("data-theme") === (t === "Light" ? "light" : "dark"));
        });
        try { await post("setTheme", { theme: t }); } catch (e) { toast(e.message); }
      });
    });

    document.getElementById("btnLogs").onclick = async () => {
      try { await post("openLogs"); toast("Opened logs"); }
      catch (e) { toast(e.message); }
    };

    document.getElementById("autoUp").onchange = async (e) => {
      try { await post("setAutoUpdate", { enabled: e.target.checked }); }
      catch (err) { toast(err.message); }
    };

    const setUpdateStatus = (text, busy = false) => {
      const el = document.getElementById("updateStatus");
      if (!el) return;
      el.classList.toggle("busy", !!busy);
      el.textContent = text;
    };

    document.getElementById("btnUpdate").onclick = async () => {
      const btn = document.getElementById("btnUpdate");
      btn.disabled = true;
      setUpdateStatus("Checking…", true);
      try {
        const r = await post("checkUpdates");
        const msg = (r.message || "Done.").replace(/^OptiHub is up to date \(v[\d.]+\).*$/i, "Up to date");
        setUpdateStatus(msg, false);
        if (r.updateAvailable) {
          const ok = await confirmModal(
            "Install update?",
            `v${r.remoteVersion} is ready (you have v${r.localVersion}).\n\nQuiet install, then restart.`
          );
          if (ok) {
            setUpdateStatus("Installing…", true);
            const inst = await post("installUpdate");
            setUpdateStatus(inst.message || "Restarting…", false);
          } else {
            setUpdateStatus(`v${r.remoteVersion} available`, false);
          }
        }
      } catch (e) {
        setUpdateStatus(e.message || "Check failed", false);
      } finally {
        btn.disabled = false;
      }
    };
  }

  async function renderOptimizer(kit) {
    state.kit = kit;
    const titles = { discord: "Discord", steam: "Steam", nvidia: "NVIDIA" };
    view.innerHTML = `
      <div class="page opt" id="optPage">
        <div class="opt-top">
          <h1>${titles[kit]}</h1>
          <span class="pill" id="statusPill"><span class="dot"></span><span id="statusPillText">Checking…</span></span>
        </div>
        <p class="opt-sub" id="optDetail">Live detection…</p>
        <div class="surface opt-body">
          <div class="features" id="features"></div>
          ${kit === "nvidia" ? `<label class="gsync-row"><input type="checkbox" id="gsync" /> G-SYNC profile pack</label>` : ""}
          <div class="progress-wrap hidden" id="progWrap">
            <div class="progress-bar"><i id="progBar"></i></div>
            <div class="progress-status" id="progStatus">Starting…</div>
          </div>
          <div id="optResult"></div>
          <div class="opt-actions">
            <button type="button" class="btn primary" id="btnRun">Apply</button>
            <button type="button" class="btn quiet" id="btnRefresh">Refresh</button>
            <button type="button" class="btn ghost" id="btnRepair">Repair</button>
          </div>
        </div>
      </div>`;

    document.getElementById("btnRefresh").onclick = () => loadDetect(kit);
    document.getElementById("btnRun").onclick = () => runApply(kit);
    document.getElementById("btnRepair").onclick = () => runRepair(kit);
    await loadDetect(kit);
  }

  async function loadDetect(kit) {
    const pillText = document.getElementById("statusPillText");
    const pill = document.getElementById("statusPill");
    const detail = document.getElementById("optDetail");
    const feats = document.getElementById("features");
    const btnRun = document.getElementById("btnRun");
    if (!pillText) return;
    pillText.textContent = "Checking…";
    pill.className = "pill";
    feats.innerHTML = "";
    try {
      const r = await post("detect", { kit });
      const applied = !!r.isApplied;
      pillText.textContent = r.statusText || (applied ? "Optimized" : "Ready");
      pill.className = "pill " + (applied ? "ok" : "warn");
      detail.textContent = r.detail || "";
      btnRun.textContent = applied ? "Reapply" : "Apply";
      // Compact title-only rows — fit without scrolling
      const list = (r.features || []).slice(0, 10);
      feats.innerHTML = list.map((f) => `
        <div class="feature ${f.active ? "on" : ""}" title="${esc(f.detail || f.title)}">
          <div class="dot"></div>
          <div class="t">${esc(f.title)}</div>
        </div>`).join("");
      if (kit === "nvidia" && typeof r.gsync === "boolean") {
        const g = document.getElementById("gsync");
        if (g) g.checked = !!r.gsync;
      }
    } catch (e) {
      pillText.textContent = "Failed";
      pill.className = "pill warn";
      detail.textContent = e.message;
    }
  }

  function onProgress(data) {
    if (data.kit !== state.kit) return;
    const wrap = document.getElementById("progWrap");
    const bar = document.getElementById("progBar");
    const st = document.getElementById("progStatus");
    if (!wrap) return;
    wrap.classList.remove("hidden");
    if (bar) {
      const next = Math.max(0, Math.min(100, data.percent || 0));
      const cur = parseFloat(bar.style.width) || 0;
      bar.style.width = Math.max(cur, next) + "%";
    }
    if (st) st.textContent = data.status || "";
  }

  async function runApply(kit) {
    if (state.busy) return;
    const warnings = {
      discord: "Closes Discord and applies aggressive client, memory, and quiet Windows changes. Admin required.\n\nUse Repair to undo.",
      steam: "Closes Steam and applies client, CEF, and quiet Windows changes. Admin required.\n\nUse Repair to undo.",
      nvidia: "Applies series-aware driver check, profiles, display prefs, and privacy debloat. Admin required.",
    };
    const ok = await confirmModal(`Apply ${kit}?`, warnings[kit] || "Continue?");
    if (!ok) return;

    state.busy = true;
    const btn = document.getElementById("btnRun");
    const result = document.getElementById("optResult");
    const wrap = document.getElementById("progWrap");
    if (btn) btn.disabled = true;
    if (wrap) {
      wrap.classList.remove("hidden");
      document.getElementById("progBar").style.width = "2%";
      document.getElementById("progStatus").textContent = "Preparing…";
    }
    if (result) result.innerHTML = "";

    const params = { kit };
    if (kit === "nvidia") {
      const g = document.getElementById("gsync");
      params.gsync = !!(g && g.checked);
    }

    try {
      const r = await post("apply", params);
      if (result) {
        result.innerHTML = `<div class="result ${r.success ? "ok" : "bad"}">${esc(r.message || (r.success ? "Done." : "Failed."))}</div>`;
      }
      if (r.success) toast("Done");
      await loadDetect(kit);
    } catch (e) {
      if (result) result.innerHTML = `<div class="result bad">${esc(e.message)}</div>`;
    } finally {
      state.busy = false;
      if (btn) btn.disabled = false;
    }
  }

  async function runRepair(kit) {
    if (state.busy) return;
    const ok = await confirmModal(`Repair ${kit}?`, "Undo OptiHub changes for this optimizer where possible.");
    if (!ok) return;
    state.busy = true;
    try {
      const r = await post("repair", { kit });
      toast(r.message || (r.success ? "Repaired." : "Repair failed."));
      await loadDetect(kit);
    } catch (e) {
      toast(e.message);
    } finally {
      state.busy = false;
    }
  }

  function esc(s) {
    return String(s ?? "")
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
  }

  btnBack.addEventListener("click", () => navigate("home"));
  btnSettings.addEventListener("click", () => navigate("settings"));

  if (window.__OPTIHUB_HOST_CHROME__) {
    const chrome = document.getElementById("chrome");
    if (chrome) chrome.style.display = "none";
    document.documentElement.classList.add("host-chrome");
  }

  window.__optihubNavigate = (route) => {
    try { navigate(String(route || "home")); } catch (e) { console.error(e); }
  };

  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape" && !modal.classList.contains("hidden")) return;
    if (e.key === "Escape" && state.route !== "home" && !state.busy) navigate("home");
  });

  Promise.resolve()
    .then(() => navigate("home"))
    .catch((e) => {
      view.innerHTML = `<div class="page"><div class="panel result bad">${esc(e && e.message ? e.message : e)}</div></div>`;
    });
})();
