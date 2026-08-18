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

  const MASTER_LIST = "https://raw.githubusercontent.com/HelgeSverre/fsharp-of-duty/main/servers.json";

  function initializeServerBrowser() {
    const serverBody = byId("server-body");
    const playerBody = byId("leaderboard-body");
    if (!serverBody || !playerBody) return;
    let servers = null;
    let rows = [];
    let selectedKey = null;

    const httpRoot = (ws) => ws.replace(/^ws/, "http").replace(/\/play\/?$/, "");

    async function loadServers() {
      const fallback = [{ name: "Official Fly.io", url: apiRoot || "https://fsharp-of-duty.fly.dev" }];
      try {
        const response = await fetch(MASTER_LIST, { cache: "no-store", signal: AbortSignal.timeout(3000) });
        const list = (await response.json()).servers
          .filter((server) => /^wss?:/.test(server.url))
          .map((server) => ({ name: server.name, url: httpRoot(server.url) }));
        return list.length ? list : fallback;
      } catch { return fallback; }
    }

    // One leaderboard GET per server yields ping, rooms, and players —
    // the same probe the in-game server browser performs.
    async function probe(server) {
      const started = performance.now();
      try {
        const response = await fetch(`${server.url}/api/leaderboard`, { cache: "no-store", signal: AbortSignal.timeout(4000) });
        if (!response.ok) throw new Error();
        const payload = await response.json();
        const ping = Math.round(performance.now() - started);
        return (payload.rooms ?? []).map((room) => ({
          key: `${server.url}|${room.mode}`,
          server: server.name,
          mode: room.mode === "FreeForAll" ? "Free For All" : "Team Deathmatch",
          phase: room.phase,
          players: Array.isArray(room.players) ? room.players : [],
          count: room.connectedPlayers ?? 0,
          capacity: payload.capacityPerRoom ?? 16,
          ping,
          online: true,
        }));
      } catch {
        return [{ key: server.url, server: server.name, mode: "—", phase: "OFFLINE", players: [], count: 0, capacity: 0, ping: null, online: false }];
      }
    }

    function renderServers() {
      serverBody.replaceChildren();
      rows.forEach((row) => {
        const tr = document.createElement("tr");
        if (!row.online) tr.className = "offline";
        if (row.key === selectedKey) tr.classList.add("selected");
        [row.server, row.mode, row.phase, row.online ? `${row.count} / ${row.capacity}` : "—", row.ping === null ? "—" : `${row.ping} ms`]
          .forEach((value) => { const cell = document.createElement("td"); text(cell, value); tr.append(cell); });
        tr.addEventListener("click", () => { selectedKey = row.key; renderServers(); renderPlayers(); });
        serverBody.append(tr);
      });
    }

    function renderPlayers() {
      const row = rows.find((candidate) => candidate.key === selectedKey);
      playerBody.replaceChildren();
      const players = row?.players ?? [];
      byId("empty-state").hidden = players.length > 0;
      players.forEach((player, index) => {
        const tr = document.createElement("tr");
        [index + 1, player.name, player.team, player.weapon, player.kills, player.deaths]
          .forEach((value) => { const cell = document.createElement("td"); text(cell, value); tr.append(cell); });
        const status = document.createElement("td");
        status.className = player.alive ? "status-alive" : "status-down";
        text(status, player.alive ? "ACTIVE" : "RESPAWNING");
        tr.append(status);
        playerBody.append(tr);
      });
    }

    async function refresh() {
      servers ??= await loadServers();
      const probed = await Promise.all(servers.map(probe));
      rows = probed.flat();
      if (!rows.some((row) => row.key === selectedKey)) {
        const preferred = rows.find((row) => row.online && row.players.length) ?? rows.find((row) => row.online) ?? rows[0];
        selectedKey = preferred?.key ?? null;
      }
      text(byId("server-status"), rows.some((row) => row.online) ? "LIVE" : "OFFLINE");
      text(byId("last-updated"), `Updated ${new Date().toLocaleTimeString()}`);
      renderServers();
      renderPlayers();
    }

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
      const heading = document.createElement("b"); text(heading, "TUNING VALUES");
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
        ? `${weapon.projectilesPerShot} independently traced projectiles per trigger pull. Maximum damage assumes every pellet hits.`
        : "One projectile is traced per trigger pull. Regional multipliers and penetration loss are resolved during impact.");
      panel.append(header, stats, note);
      grid.append(visual, panel);
      dossier.append(grid);
    }

    function loadPayload(payload, isOffline = false) {
      weapons = Array.isArray(payload.weapons) ? payload.weapons : [];
      text(byId("arsenal-status"), isOffline ? "BUNDLED DATA" : "LIVE DATA");
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
          text(byId("arsenal-status"), "DATA UNAVAILABLE");
          text(byId("arsenal-source"), "Could not load weapon data");
        }
      });
  }

  function initializeQuickStart() {
    const block = byId("quick-start");
    if (!block) return;
    const commands = block.textContent;
    block.addEventListener("click", async () => {
      try {
        await navigator.clipboard.writeText(commands);
        block.classList.add("copied");
        setTimeout(() => block.classList.remove("copied"), 1200);
      } catch { /* clipboard unavailable; leave the text selectable */ }
    });
  }

  initializeBackdrop();
  initializeQuickStart();
  initializeServerBrowser();
  initializeArsenal();
})();
