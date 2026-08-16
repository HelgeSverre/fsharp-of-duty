(() => {
  "use strict";

  const byId = (id) => document.getElementById(id);
  const text = (element, value) => { if (element) element.textContent = String(value); };
  const format = (value, digits = 0) => Number(value).toFixed(digits);
  const apiRoot = document.querySelector('meta[name="ironsight-api"]')?.content?.replace(/\/$/, "") ?? "";

  function initializeBackdrop() {
    const canvas = byId("battlefield");
    if (!canvas) return;
    const context = canvas.getContext("2d");
    const reducedMotion = matchMedia("(prefers-reduced-motion: reduce)").matches;
    let width = 0;
    let height = 0;
    let frame = 0;

    function resize() {
      const ratio = Math.min(devicePixelRatio || 1, 2);
      width = innerWidth;
      height = innerHeight;
      canvas.width = width * ratio;
      canvas.height = height * ratio;
      context.setTransform(ratio, 0, 0, ratio, 0, 0);
    }

    function draw(time = 0) {
      context.clearRect(0, 0, width, height);
      const horizon = height * .55;
      const sky = context.createLinearGradient(0, 0, 0, height);
      sky.addColorStop(0, "#273025");
      sky.addColorStop(.55, "#11160f");
      sky.addColorStop(1, "#070806");
      context.fillStyle = sky;
      context.fillRect(0, 0, width, height);
      context.strokeStyle = "rgba(141,149,99,.16)";
      context.lineWidth = 1;
      for (let x = -width; x < width * 2; x += 70) {
        context.beginPath();
        context.moveTo(width / 2, horizon);
        context.lineTo(x + ((time / 90) % 70), height);
        context.stroke();
      }
      for (let y = 0; y < 12; y += 1) {
        const p = y / 12;
        const rowY = horizon + (p * p) * (height - horizon);
        context.beginPath();
        context.moveTo(0, rowY);
        context.lineTo(width, rowY);
        context.stroke();
      }
      context.fillStyle = "rgba(6,8,6,.8)";
      context.beginPath();
      context.moveTo(0, horizon + 25);
      for (let x = 0; x <= width; x += 55) {
        const ridge = Math.sin(x * .018) * 18 + Math.sin(x * .006) * 30;
        context.lineTo(x, horizon - 22 + ridge);
      }
      context.lineTo(width, height);
      context.lineTo(0, height);
      context.fill();
      if (!reducedMotion) frame = requestAnimationFrame(draw);
    }

    addEventListener("resize", resize, { passive: true });
    resize();
    draw();
    addEventListener("pagehide", () => cancelAnimationFrame(frame), { once: true });
  }

  function initializeLeaderboard() {
    const body = byId("leaderboard-body");
    if (!body) return;
    let activeMode = "TeamDeathmatch";
    let latest = null;

    function render() {
      const room = latest?.rooms?.find((candidate) => candidate.mode === activeMode);
      body.replaceChildren();
      text(byId("match-phase"), room?.phase ?? "NO SIGNAL");
      text(byId("allies-score"), room?.alliesScore ?? 0);
      text(byId("axis-score"), room?.axisScore ?? 0);
      text(byId("population"), `${room?.connectedPlayers ?? 0} / ${latest?.capacityPerRoom ?? 16}`);
      document.querySelector(".tdm-score")?.toggleAttribute("hidden", activeMode !== "TeamDeathmatch");
      const players = Array.isArray(room?.players) ? room.players : [];
      byId("empty-state").hidden = players.length > 0;
      players.forEach((player, index) => {
        const row = document.createElement("tr");
        const values = [index + 1, player.name, player.team, player.weapon, player.kills, player.deaths];
        values.forEach((value) => { const cell = document.createElement("td"); text(cell, value); row.append(cell); });
        const status = document.createElement("td");
        status.className = player.alive ? "status-alive" : "status-down";
        text(status, player.alive ? "ACTIVE" : "RESPAWNING");
        row.append(status);
        body.append(row);
      });
    }

    async function refresh() {
      try {
        const response = await fetch(`${apiRoot}/api/leaderboard`, { cache: "no-store" });
        if (!response.ok) throw new Error(`HTTP ${response.status}`);
        latest = await response.json();
        text(byId("server-status"), "TELEMETRY LIVE");
        text(byId("persistence-note"), latest.persistence);
        text(byId("last-updated"), `Updated ${new Date(latest.generatedAt).toLocaleTimeString()}`);
        render();
      } catch {
        text(byId("server-status"), "TELEMETRY LOST");
        text(byId("last-updated"), "Command is reconsidering its network choices");
      }
    }

    document.querySelectorAll(".room-tab").forEach((tab) => tab.addEventListener("click", () => {
      activeMode = tab.dataset.room;
      document.querySelectorAll(".room-tab").forEach((candidate) => {
        const selected = candidate === tab;
        candidate.classList.toggle("active", selected);
        candidate.setAttribute("aria-selected", String(selected));
      });
      render();
    }));
    refresh();
    const timer = setInterval(refresh, 5000);
    addEventListener("pagehide", () => clearInterval(timer), { once: true });
    document.addEventListener("visibilitychange", () => { if (!document.hidden) refresh(); });
  }

  function weaponStyle(name, mode) {
    const classes = [];
    if (name.includes("Sniper")) classes.push("sniper");
    if (name.includes("Trench")) classes.push("shotgun");
    if (name.includes("M1911")) classes.push("pistol");
    if (mode === "FullAuto") classes.push("auto");
    return classes.join(" ");
  }

  function initializeArsenal() {
    const dossier = byId("weapon-dossier");
    const tabs = byId("weapon-tabs");
    if (!dossier || !tabs) return;
    let weapons = [];
    let selected = 0;

    function stat(label, value, unit, percentage) {
      const wrapper = document.createElement("div");
      wrapper.className = "stat";
      const caption = document.createElement("label");
      text(caption, label);
      const strong = document.createElement("strong");
      text(strong, value);
      if (unit) { const small = document.createElement("small"); text(small, ` ${unit}`); strong.append(small); }
      const meter = document.createElement("div");
      meter.className = "meter";
      const fill = document.createElement("i");
      fill.style.setProperty("--value", `${Math.max(2, Math.min(100, percentage))}%`);
      meter.append(fill);
      wrapper.append(caption, strong, meter);
      return wrapper;
    }

    function render() {
      const weapon = weapons[selected];
      if (!weapon) return;
      tabs.querySelectorAll("button").forEach((button, index) => {
        const active = index === selected;
        button.classList.toggle("active", active);
        button.setAttribute("aria-selected", String(active));
      });
      dossier.replaceChildren();
      const grid = document.createElement("div");
      grid.className = "dossier-grid";
      const visual = document.createElement("div");
      visual.className = "weapon-visual";
      visual.dataset.index = String(selected + 1).padStart(2, "0");
      const title = document.createElement("h2"); text(title, weapon.name);
      const silhouette = document.createElement("div");
      silhouette.className = `weapon-silhouette ${weaponStyle(weapon.name, weapon.fireMode)}`;
      ["body", "long-barrel", "wood", "grip"].forEach((className) => { const part = document.createElement("i"); part.className = className; silhouette.append(part); });
      const availability = document.createElement("p"); text(availability, `${weapon.availability} // ${weapon.fireMode}`);
      visual.append(title, silhouette, availability);

      const panel = document.createElement("div");
      panel.className = "stat-panel";
      const header = document.createElement("header");
      const heading = document.createElement("b"); text(heading, "CERTIFIED COMBAT VALUES");
      const identity = document.createElement("span"); text(identity, `ORD-${String(selected + 1).padStart(3, "0")} // LIVE TUNING`);
      header.append(heading, identity);
      const stats = document.createElement("div"); stats.className = "stat-grid";
      stats.append(
        stat("Damage / projectile", format(weapon.damagePerProjectile), "HP", weapon.damagePerProjectile / 1.2),
        stat("Maximum / trigger", format(weapon.maximumDamagePerShot), "HP", weapon.maximumDamagePerShot / 1.28),
        stat("Cyclic rate", format(weapon.roundsPerMinute), "RPM", weapon.roundsPerMinute / 9),
        stat("Magazine", format(weapon.magazineSize), "ROUNDS", weapon.magazineSize * 2),
        stat("Reload", format(weapon.reloadSeconds, 2), "SECONDS", 100 - weapon.reloadSeconds * 18),
        stat("Aim-down-sight", format(weapon.aimDownSightSeconds * 1000), "MS", 100 - weapon.aimDownSightSeconds * 350),
        stat("ADS spread", format(weapon.aimDownSightSpread, 5), "RAD", 100 - weapon.aimDownSightSpread * 2500),
        stat("Penetration budget", format(weapon.penetration), "UNITS", weapon.penetration * 4)
      );
      const note = document.createElement("div"); note.className = "damage-note";
      text(note, weapon.projectilesPerShot > 1
        ? `${weapon.projectilesPerShot} independently traced projectiles leave per trigger pull. Maximum damage assumes a diplomatic agreement among every pellet.`
        : "One projectile is traced per trigger pull. Regional multipliers and penetration loss are resolved during impact.");
      panel.append(header, stats, note);
      grid.append(visual, panel);
      dossier.append(grid);
    }

    function loadPayload(payload, isOffline = false) {
      weapons = Array.isArray(payload.weapons) ? payload.weapons : [];
      text(byId("arsenal-status"), isOffline ? "OFFLINE DOSSIER" : "BALLISTICS VERIFIED");
      text(byId("arsenal-source"), payload.generatedFrom);
      weapons.forEach((weapon, index) => {
        const button = document.createElement("button");
        button.className = "weapon-tab";
        button.setAttribute("role", "tab");
        button.setAttribute("aria-selected", String(index === 0));
        text(button, weapon.name);
        button.addEventListener("click", () => { selected = index; render(); });
        tabs.append(button);
      });
      render();
    }

    fetch(`${apiRoot}/api/arsenal`)
      .then((response) => { if (!response.ok) throw new Error(); return response.json(); })
      .then((payload) => loadPayload(payload))
      .catch(() => {
        const fallback = byId("arsenal-fallback");
        try {
          loadPayload(JSON.parse(fallback?.textContent ?? "{}"), true);
        } catch {
          text(byId("arsenal-status"), "QUARTERMASTER UNAVAILABLE");
          text(byId("arsenal-source"), "The statistics refused to deploy");
        }
      });
  }

  initializeBackdrop();
  initializeLeaderboard();
  initializeArsenal();
})();
