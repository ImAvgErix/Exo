/* OptiHub WebView SPA */
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
      setTimeout(() => toastEl.classList.add("hidden"), 320);
    }, 2800);
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
    // Settings always top-left on home; back on subpages (settings top-left hidden when not home)
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
      ctxLogo.src = logoUrl(m.logo);
      ctxLogo.classList.remove("hidden");
    } else {
      ctxLogo.classList.add("hidden");
      ctxLogo.removeAttribute("src");
    }
  }

  async function navigate(route, opts = {}) {
    if (state.navigating) return;
    state.navigating = true;
    const prev = view.querySelector(".page");
    if (prev) {
      prev.classList.add("exit");
      await wait(180);
    }
    state.route = route;
    setChrome(route);
    try {
      if (route === "home") await renderHome();
      else if (route === "settings") await renderSettings();
      else if (route === "discord" || route === "steam" || route === "nvidia")
        await renderOptimizer(route, opts);
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
    const name = String(rel).replace(/^.*[\\/]/, "");
    // Prefer embedded data URIs (always work under NavigateToString / strict CSP).
    if (window.__OPTIHUB_LOGO_DATA__ && window.__OPTIHUB_LOGO_DATA__[name]) {
      return window.__OPTIHUB_LOGO_DATA__[name];
    }
    if (window.__OPTIHUB_LOGOS__) {
      return window.__OPTIHUB_LOGOS__ + name;
    }
    return "logos/" + name;
  }

  async function renderHome() {
    view.innerHTML = `<div class="page"><div class="loading">Loading optimizers…</div></div>`;
    let cards = [];
    try {
      const boot = await post("getBootstrap");
      hydrateBoot(boot);
      cards = boot.cards || [];
    } catch (e) {
      view.innerHTML = `<div class="page"><div class="panel result bad">${esc(e.message)}</div></div>`;
      return;
    }

    const html = cards.map((c) => {
      const soon = c.comingSoon;
      return `<button type="button" class="card-btn" data-id="${esc(c.id)}" ${soon ? "disabled" : ""}>
        <div class="logo-wrap"><img src="${esc(logoUrl(c.logo))}" alt="" draggable="false" /></div>
        <span class="label">${esc(c.title)}</span>
        ${soon ? `<span class="soon">Soon</span>` : ""}
      </button>`;
    }).join("");

    view.innerHTML = `
      <div class="page">
        <div class="hero">
          <h1>Maximum performance.<br/>No compromise.</h1>
          <p>Pick a target. Live status is verified when you open it.</p>
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

  function hydrateBoot(boot) {
    applyTheme(boot.theme);
    state.appVersion = boot.appVersion;
    state.kitVersion = boot.kitVersion;
    state.autoUpdate = !!boot.autoUpdate;
    state.pwsh = boot.pwsh;
  }

  async function renderSettings() {
    view.innerHTML = `<div class="page"><div class="loading">Loading settings…</div></div>`;
    try {
      const boot = await post("getBootstrap");
      hydrateBoot(boot);
    } catch (e) {
      view.innerHTML = `<div class="page"><div class="panel result bad">${esc(e.message)}</div></div>`;
      return;
    }

    const dark = state.theme === "dark";
    const kits = String(state.kitVersion || "").split("·").map((s) => s.trim()).filter(Boolean);

    view.innerHTML = `
      <div class="page">
        <div class="settings-head">
          <h1>Settings</h1>
          <p>Theme, updates, and support — same language as the rest of OptiHub.</p>
        </div>

        <div class="grid-2">
          <div class="panel stack">
            <div class="section-label">Appearance</div>
            <p class="section-hint">AMOLED black or warm cream. Switches instantly.</p>
            <div class="theme-pills">
              <button type="button" class="theme-pill ${dark ? "on" : ""}" data-theme="dark">
                <span class="swatch dark"></span> Dark
              </button>
              <button type="button" class="theme-pill ${!dark ? "on" : ""}" data-theme="light">
                <span class="swatch light"></span> Light
              </button>
            </div>
          </div>

          <div class="panel stack">
            <div class="section-label">Support</div>
            <p class="section-hint">Logs for Discord, Steam, and NVIDIA applies.</p>
            <button type="button" class="btn quiet block" id="btnLogs" style="margin-top:auto">Open logs folder</button>
            <div class="pwsh-line">${esc(state.pwsh || "PowerShell 7 not detected")}</div>
          </div>
        </div>

        <div class="panel wide">
          <div class="section-label">Updates</div>
          <p class="section-hint" style="margin-bottom:8px">One release ships the app and matching optimizers.</p>
          <div class="version-block">
            <div>
              <div class="section-label" style="margin:0">Installed</div>
              <div class="num">${esc(state.appVersion)}</div>
            </div>
            <div class="meta">
              <div><strong>Ships with this build</strong></div>
              <div class="kit-chips">
                ${kits.map((k) => `<span class="chip">${esc(k)}</span>`).join("") || `<span class="chip">${esc(state.kitVersion)}</span>`}
              </div>
              <div style="margin-top:8px;color:var(--muted);font-size:12px">Discord · Steam · NVIDIA</div>
            </div>
          </div>
          <div class="toggle-row">
            <span>Check for updates on launch</span>
            <label class="switch">
              <input type="checkbox" id="autoUp" ${state.autoUpdate ? "checked" : ""} />
              <span class="track"><span class="thumb"></span></span>
            </label>
          </div>
          <button type="button" class="btn primary block" id="btnUpdate">Check for updates</button>
          <div class="status-well" id="updateStatus">You're set. Check anytime for a newer OptiHub.</div>
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
      try { await post("openLogs"); toast("Opened logs folder"); }
      catch (e) { toast(e.message); }
    };

    document.getElementById("autoUp").onchange = async (e) => {
      try { await post("setAutoUpdate", { enabled: e.target.checked }); }
      catch (err) { toast(err.message); }
    };

    document.getElementById("btnUpdate").onclick = async () => {
      const status = document.getElementById("updateStatus");
      const btn = document.getElementById("btnUpdate");
      btn.disabled = true;
      status.classList.add("busy");
      status.textContent = "Checking GitHub for a newer OptiHub…";
      try {
        const r = await post("checkUpdates");
        status.classList.remove("busy");
        status.textContent = r.message || "Done.";
        if (r.updateAvailable) {
          const ok = await confirmModal(
            "Install OptiHub update?",
            `Version ${r.remoteVersion} is available (you have ${r.localVersion}).\n\nThis release includes matching optimizers. OptiHub will close, install, and reopen.`
          );
          if (ok) {
            status.classList.add("busy");
            status.textContent = "Installing…";
            const inst = await post("installUpdate");
            status.classList.remove("busy");
            status.textContent = inst.message || "Install started.";
          } else {
            status.textContent = `v${r.remoteVersion} available — install skipped.`;
          }
        }
      } catch (e) {
        status.classList.remove("busy");
        status.textContent = e.message;
      } finally {
        btn.disabled = false;
      }
    };
  }

  async function renderOptimizer(kit) {
    state.kit = kit;
    const titles = { discord: "Discord", steam: "Steam", nvidia: "NVIDIA" };
    view.innerHTML = `
      <div class="page" id="optPage">
        <div class="opt-header">
          <h1>${titles[kit]}</h1>
          <div class="status-line">
            <span class="pill" id="statusPill"><span class="dot"></span><span id="statusPillText">Checking…</span></span>
          </div>
          <p class="opt-detail" id="optDetail">Live detection running.</p>
        </div>
        <div class="panel">
          <div class="section-label">Checklist</div>
          <div class="features" id="features"></div>
          ${kit === "nvidia" ? `<label class="gsync-row"><input type="checkbox" id="gsync" /> Use G-SYNC profile pack</label>` : ""}
          <div class="progress-wrap hidden" id="progWrap">
            <div class="progress-bar"><i id="progBar"></i></div>
            <div class="progress-status" id="progStatus">Starting…</div>
          </div>
          <div class="actions">
            <button type="button" class="btn primary" id="btnRun">Apply</button>
            <button type="button" class="btn quiet" id="btnRefresh">Refresh</button>
            <button type="button" class="btn ghost" id="btnRepair">Repair</button>
          </div>
          <div id="optResult"></div>
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
      const list = r.features || [];
      feats.innerHTML = list.map((f, i) => `
        <div class="feature ${f.active ? "on" : ""}" style="animation-delay:${i * 35}ms">
          <div class="dot"></div>
          <div class="meta">
            <div class="t">${esc(f.title)}</div>
            <div class="d">${esc(f.detail || "")}</div>
          </div>
        </div>`).join("");
      if (kit === "nvidia" && typeof r.gsync === "boolean") {
        const g = document.getElementById("gsync");
        if (g) g.checked = !!r.gsync;
      }
    } catch (e) {
      pillText.textContent = "Check failed";
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
      discord: "Aggressive Discord pass: closes Discord, applies Equicord/OpenASAR, kernel RAM reclaim, debloat, and quiet Windows integration. Admin required.\n\nUse Repair to restore a stock client.",
      steam: "Aggressive Steam pass: CEF quiet launcher, webhelper trim, Windows quiet, complete client debloat. Admin required.\n\nUse Repair to restore shortcuts and stock launch.",
      nvidia: "NVIDIA pass: series-aware driver check, Base + per-game profiles, display prefs, privacy debloat. Admin required.",
    };
    const ok = await confirmModal(`Confirm ${kit} optimizer`, warnings[kit] || "Continue?");
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
      if (r.success) toast("Apply finished");
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
    const ok = await confirmModal(
      `Repair ${kit}?`,
      "This undoes OptiHub-managed changes for this optimizer where possible."
    );
    if (!ok) return;
    state.busy = true;
    try {
      const r = await post("repair", { kit });
      toast(r.message || (r.success ? "Repair finished." : "Repair failed."));
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

  // Keyboard: Esc back home when not busy
  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape" && state.route !== "home" && !state.busy && !modal.classList.contains("hidden") === false) {
      // if modal open, ignore — handled separately
    }
    if (e.key === "Escape" && !modal.classList.contains("hidden")) return;
    if (e.key === "Escape" && state.route !== "home" && !state.busy) navigate("home");
  });

  // Boot
  Promise.resolve()
    .then(() => navigate("home"))
    .catch((e) => {
      view.innerHTML = `<div class="page"><div class="panel result bad">${esc(e && e.message ? e.message : e)}</div></div>`;
    });
})();
