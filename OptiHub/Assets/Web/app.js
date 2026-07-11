/* OptiHub WebView SPA — host bridge via chrome.webview */
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

  function post(method, params = {}) {
    return new Promise((resolve, reject) => {
      const id = String(++reqId);
      pending.set(id, { resolve, reject });
      const msg = JSON.stringify({ id, method, params });
      if (window.chrome?.webview) {
        window.chrome.webview.postMessage(msg);
      } else {
        reject(new Error("WebView host bridge unavailable"));
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

      if (data.type === "progress") {
        onProgress(data);
        return;
      }
      if (data.type === "theme") {
        applyTheme(data.theme);
        return;
      }

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
    setTimeout(() => {
      toastEl.classList.remove("show");
      setTimeout(() => toastEl.classList.add("hidden"), 300);
    }, 2800);
  }

  function confirmModal(title, body) {
    return new Promise((resolve) => {
      document.getElementById("modalTitle").textContent = title;
      document.getElementById("modalBody").textContent = body;
      modal.classList.remove("hidden");
      const ok = document.getElementById("modalOk");
      const cancel = document.getElementById("modalCancel");
      const done = (v) => {
        modal.classList.add("hidden");
        ok.onclick = null;
        cancel.onclick = null;
        resolve(v);
      };
      ok.onclick = () => done(true);
      cancel.onclick = () => done(false);
    });
  }

  function setChrome(route) {
    const home = route === "home";
    const settings = route === "settings";
    btnBack.classList.toggle("hidden", home);
    btnSettings.classList.toggle("hidden", !home);

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
      ctxLogo.src = m.logo;
      ctxLogo.classList.remove("hidden");
    } else {
      ctxLogo.classList.add("hidden");
      ctxLogo.removeAttribute("src");
    }
  }

  function navigate(route, opts = {}) {
    state.route = route;
    setChrome(route);
    if (route === "home") renderHome();
    else if (route === "settings") renderSettings();
    else if (route === "discord" || route === "steam" || route === "nvidia")
      renderOptimizer(route, opts);
  }

  async function renderHome() {
    view.innerHTML = `<div class="page"><div class="loading">Loading…</div></div>`;
    let cards = [];
    try {
      const boot = await post("getBootstrap");
      applyTheme(boot.theme);
      state.appVersion = boot.appVersion;
      state.kitVersion = boot.kitVersion;
      state.autoUpdate = !!boot.autoUpdate;
      state.pwsh = boot.pwsh;
      cards = boot.cards || [];
    } catch (e) {
      view.innerHTML = `<div class="page"><div class="panel result bad">${esc(e.message)}</div></div>`;
      return;
    }

    const html = cards.map((c) => {
      const soon = c.comingSoon;
      return `<button type="button" class="card-btn" data-id="${esc(c.id)}" ${soon ? "disabled" : ""}>
        <img src="${esc(c.logo)}" alt="" draggable="false" />
        <span>${esc(c.title)}</span>
        ${soon ? `<span class="soon">Coming soon</span>` : ""}
      </button>`;
    }).join("");

    view.innerHTML = `
      <div class="page">
        <div class="hero">
          <h1>Maximum performance. No compromise.</h1>
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

  async function renderSettings() {
    view.innerHTML = `<div class="page settings-page"><div class="loading">Loading…</div></div>`;
    try {
      const boot = await post("getBootstrap");
      applyTheme(boot.theme);
      state.appVersion = boot.appVersion;
      state.kitVersion = boot.kitVersion;
      state.autoUpdate = !!boot.autoUpdate;
      state.pwsh = boot.pwsh;
    } catch (e) {
      view.innerHTML = `<div class="page"><div class="panel result bad">${esc(e.message)}</div></div>`;
      return;
    }

    const dark = state.theme === "dark";
    view.innerHTML = `
      <div class="page settings-page">
        <h2>Settings</h2>
        <p class="sub">Theme, updates, and support — everything in one place.</p>
        <div class="grid-2">
          <div class="panel">
            <div class="section-label">APPEARANCE</div>
            <p style="color:var(--muted);font-size:12px">AMOLED black or warm cream for readability.</p>
            <div class="theme-row">
              <label><input type="radio" name="theme" value="dark" ${dark ? "checked" : ""}/> Dark</label>
              <label><input type="radio" name="theme" value="light" ${!dark ? "checked" : ""}/> Light</label>
            </div>
          </div>
          <div class="panel">
            <div class="section-label">SUPPORT</div>
            <p style="color:var(--muted);font-size:12px;margin-bottom:12px">Logs for Discord, Steam, and NVIDIA applies.</p>
            <button type="button" class="btn quiet" id="btnLogs">Open logs folder</button>
            <p style="margin-top:12px;font-size:11px;color:var(--muted)">PowerShell: ${esc(state.pwsh || "not found")}</p>
          </div>
        </div>
        <div class="panel" style="margin-top:16px">
          <div class="section-label">UPDATES</div>
          <div class="version-hero" style="margin:10px 0 14px">
            <div>
              <div style="font-size:11px;color:var(--muted);letter-spacing:.06em">INSTALLED</div>
              <div class="num">${esc(state.appVersion)}</div>
            </div>
            <div class="kits">
              <div>Ships with this build</div>
              <div style="font-weight:600;margin-top:4px">${esc(state.kitVersion)}</div>
              <div style="margin-top:4px;opacity:.75">Discord · Steam · NVIDIA</div>
            </div>
          </div>
          <div class="toggle">
            <span>Check for updates on launch</span>
            <input type="checkbox" id="autoUp" ${state.autoUpdate ? "checked" : ""} />
          </div>
          <button type="button" class="btn primary" id="btnUpdate">Check for updates</button>
          <div class="status-well" id="updateStatus">Ready.</div>
        </div>
      </div>`;

    view.querySelectorAll('input[name="theme"]').forEach((r) => {
      r.addEventListener("change", async () => {
        const t = r.value === "light" ? "Light" : "Dark";
        applyTheme(t);
        try { await post("setTheme", { theme: t }); } catch (e) { toast(e.message); }
      });
    });
    document.getElementById("btnLogs").onclick = async () => {
      try { await post("openLogs"); } catch (e) { toast(e.message); }
    };
    document.getElementById("autoUp").onchange = async (e) => {
      try { await post("setAutoUpdate", { enabled: e.target.checked }); } catch (err) { toast(err.message); }
    };
    document.getElementById("btnUpdate").onclick = async () => {
      const status = document.getElementById("updateStatus");
      const btn = document.getElementById("btnUpdate");
      btn.disabled = true;
      status.textContent = "Checking…";
      try {
        const r = await post("checkUpdates");
        status.textContent = r.message || "Done.";
        if (r.updateAvailable) {
          const ok = await confirmModal(
            "Install OptiHub update?",
            `Version ${r.remoteVersion} is available (you have ${r.localVersion}).\n\nThis release includes matching optimizers. OptiHub will close, install, and reopen.`
          );
          if (ok) {
            status.textContent = "Installing…";
            const inst = await post("installUpdate");
            status.textContent = inst.message || "Install started.";
          } else {
            status.textContent = `v${r.remoteVersion} available — install skipped.`;
          }
        }
      } catch (e) {
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
          <h2>${titles[kit]}</h2>
          <p id="optStatus">Checking status…</p>
        </div>
        <div class="panel">
          <div class="section-label">STATUS</div>
          <p id="optDetail" style="color:var(--text2);font-size:13px;line-height:1.45">Live detection running.</p>
          <div class="features" id="features"></div>
          ${kit === "nvidia" ? `<label class="gsync-row"><input type="checkbox" id="gsync" /> Use G-SYNC profile pack</label>` : ""}
          <div class="progress-wrap hidden" id="progWrap">
            <div class="progress-bar"><i id="progBar"></i></div>
            <div class="progress-status" id="progStatus">Starting…</div>
          </div>
          <div class="actions">
            <button type="button" class="btn primary" id="btnRun">Apply</button>
            <button type="button" class="btn quiet" id="btnRefresh">Refresh</button>
            <button type="button" class="btn quiet" id="btnRepair">Repair</button>
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
    const status = document.getElementById("optStatus");
    const detail = document.getElementById("optDetail");
    const feats = document.getElementById("features");
    const btnRun = document.getElementById("btnRun");
    if (!status) return;
    status.textContent = "Checking status…";
    feats.innerHTML = "";
    try {
      const r = await post("detect", { kit });
      status.textContent = r.statusText || (r.isApplied ? "Already optimized" : "Ready");
      detail.textContent = r.detail || "";
      btnRun.textContent = r.isApplied ? "Reapply" : "Apply";
      const list = r.features || [];
      feats.innerHTML = list.map((f, i) => `
        <div class="feature ${f.active ? "on" : ""}" style="animation-delay:${i * 40}ms">
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
      status.textContent = "Status check failed";
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
    if (bar) bar.style.width = Math.max(0, Math.min(100, data.percent || 0)) + "%";
    if (st) st.textContent = data.status || "";
  }

  async function runApply(kit) {
    if (state.busy) return;
    const warnings = {
      discord: "Aggressive Discord pass: closes Discord, applies Equicord/OpenASAR, kernel RAM reclaim, debloat, and quiet Windows integration. Admin required. Use Repair to restore stock client.",
      steam: "Aggressive Steam pass: CEF quiet launcher, webhelper trim, Windows quiet, complete client debloat. Admin required. Use Repair to restore shortcuts and stock launch.",
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

  // Boot
  navigate("home");
})();
