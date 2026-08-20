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
      const fallback = [{ name: "IRONSIGHT Official", url: apiRoot || "https://fsharp-of-duty.fly.dev" }];
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

  function weaponStyle(kind, mode) {
    const classes = [];
    if (kind === "SniperRifle") classes.push("sniper");
    if (kind === "Shotgun") classes.push("shotgun");
    if (kind === "Pistol") classes.push("pistol");
    if (mode === "FullAuto") classes.push("auto");
    return classes.join(" ");
  }

  // Section order follows the game's number keys. The names come with the
  // weapons, so there is no second table here to fall out of step.
  const sectionOrder = ["RIFLES", "AUTOMATICS", "SIDEARMS", "PRECISION", "HEAVY"];

  // A deployed server can be older than the site in front of it — the live one
  // is, right now — and older servers send no category at all. Falling back to
  // the weapon kind reproduces exactly how this page grouped before the field
  // existed, which is the right answer for the arsenal those servers have.
  const legacySections = {
    Rifle: "RIFLES", SniperRifle: "SNIPERS", Smg: "SUBMACHINE GUNS",
    Pistol: "PISTOLS", Shotgun: "SHOTGUNS", MachineGun: "MACHINE GUNS",
  };
  const sectionOf = (weapon) => weapon.category || legacySections[weapon.kind] || "OTHER";

  // Orbitable weapon models, drawn straight from the procedural meshes the
  // game builds. Painter's algorithm on a 2D canvas — the same approach
  // tools/GunPreview.fsx uses to draw its contact sheets, and no WebGL or
  // library for a few hundred flat-shaded triangles.
  const modelCache = new Map();

  function weaponSlug(name) {
    return name.toLowerCase().replace(/[^a-z0-9]/g, "-").replace(/^-+|-+$/g, "");
  }

  function mountWeaponModel(canvas, name) {
    const context = canvas.getContext("2d");
    if (!context) return () => {};
    // Spins on its own; hovering stops it so you can look, dragging turns it.
    let yaw = 0.9;
    let pitch = -0.28;
    let hovered = false;
    let dragging = false;
    let last = null;
    let model = null;
    let frame = 0;
    let previous = 0;

    const pointerDown = (event) => { dragging = true; last = event; canvas.setPointerCapture(event.pointerId); };
    const pointerUp = (event) => { dragging = false; last = null; if (canvas.hasPointerCapture(event.pointerId)) canvas.releasePointerCapture(event.pointerId); };
    const pointerMove = (event) => {
      if (!dragging || !last) return;
      yaw += (event.clientX - last.clientX) * 0.01;
      pitch = Math.max(-1.2, Math.min(1.2, pitch + (event.clientY - last.clientY) * 0.01));
      last = event;
    };
    canvas.addEventListener("pointerenter", () => { hovered = true; });
    canvas.addEventListener("pointerleave", () => { hovered = false; dragging = false; last = null; });
    canvas.addEventListener("pointerdown", pointerDown);
    canvas.addEventListener("pointerup", pointerUp);
    canvas.addEventListener("pointercancel", pointerUp);
    canvas.addEventListener("pointermove", pointerMove);

    function draw(now) {
      frame = requestAnimationFrame(draw);
      const delta = previous ? Math.min(0.05, (now - previous) / 1000) : 0;
      previous = now;
      if (!hovered && !dragging) yaw += delta * 0.55;
      if (!model) return;
      const ratio = Math.min(2, window.devicePixelRatio || 1);
      const width = canvas.clientWidth;
      const height = canvas.clientHeight;
      if (!width || !height) return;
      if (canvas.width !== width * ratio || canvas.height !== height * ratio) {
        canvas.width = width * ratio;
        canvas.height = height * ratio;
      }
      context.setTransform(ratio, 0, 0, ratio, 0, 0);
      context.clearRect(0, 0, width, height);

      const cosYaw = Math.cos(yaw), sinYaw = Math.sin(yaw);
      const cosPitch = Math.cos(pitch), sinPitch = Math.sin(pitch);
      const points = model.points;
      const rotated = new Float32Array(points.length);
      for (let i = 0; i < points.length; i += 3) {
        const x = points[i] - model.centre[0];
        const y = points[i + 1] - model.centre[1];
        const z = points[i + 2] - model.centre[2];
        const rx = x * cosYaw + z * sinYaw;
        const rz = z * cosYaw - x * sinYaw;
        rotated[i] = rx;
        rotated[i + 1] = y * cosPitch - rz * sinPitch;
        rotated[i + 2] = rz * cosPitch + y * sinPitch;
      }
      const scale = Math.min(width, height) / (model.radius * 2.15);
      const originX = width / 2;
      const originY = height / 2;
      // Depth sort, far to near. A few hundred triangles per weapon, so the
      // sort costs nothing and a real depth buffer would be overkill.
      const order = model.order;
      const tris = model.tris;
      for (let t = 0; t < order.length; t++) {
        const i = order[t] * 3;
        order[t] = order[t];
        model.depth[order[t]] =
          rotated[tris[i] * 3 + 2] + rotated[tris[i + 1] * 3 + 2] + rotated[tris[i + 2] * 3 + 2];
      }
      order.sort((a, b) => model.depth[a] - model.depth[b]);
      for (let t = 0; t < order.length; t++) {
        const triangle = order[t];
        const a = tris[triangle * 3] * 3, b = tris[triangle * 3 + 1] * 3, c = tris[triangle * 3 + 2] * 3;
        const ax = rotated[a], ay = rotated[a + 1], az = rotated[a + 2];
        const ux = rotated[b] - ax, uy = rotated[b + 1] - ay, uz = rotated[b + 2] - az;
        const vx = rotated[c] - ax, vy = rotated[c + 1] - ay, vz = rotated[c + 2] - az;
        const nx = uy * vz - uz * vy, ny = uz * vx - ux * vz, nz = ux * vy - uy * vx;
        if (nz <= 0) continue; // facing away
        const length = Math.hypot(nx, ny, nz) || 1;
        // The same key light and ambient floor GunPreview shades with, so a
        // weapon looks the same on the site as in the tool.
        const lambert = Math.max(0, (nx * 0.4 + ny * 0.8 + nz * 0.45) / (length * 0.9925));
        const shade = 0.35 + 0.65 * lambert;
        const colour = model.colors[model.mats[triangle]];
        context.fillStyle = `rgb(${Math.round(colour[0] * shade)} ${Math.round(colour[1] * shade)} ${Math.round(colour[2] * shade)})`;
        context.beginPath();
        context.moveTo(originX + ax * scale, originY - ay * scale);
        context.lineTo(originX + rotated[b] * scale, originY - rotated[b + 1] * scale);
        context.lineTo(originX + rotated[c] * scale, originY - rotated[c + 1] * scale);
        context.closePath();
        context.fill();
      }
    }

    const slug = weaponSlug(name);
    const load = modelCache.get(slug) || fetch(`models/${slug}.json`).then((response) => {
      if (!response.ok) throw new Error(response.statusText);
      return response.json();
    }).then((raw) => {
      const points = new Float32Array(raw.positions.length);
      const bounds = [Infinity, Infinity, Infinity, -Infinity, -Infinity, -Infinity];
      for (let i = 0; i < raw.positions.length; i++) {
        points[i] = raw.positions[i] * raw.scale;
        const axis = i % 3;
        bounds[axis] = Math.min(bounds[axis], points[i]);
        bounds[axis + 3] = Math.max(bounds[axis + 3], points[i]);
      }
      const centre = [0, 1, 2].map((axis) => (bounds[axis] + bounds[axis + 3]) / 2);
      const radius = Math.max(...[0, 1, 2].map((axis) => (bounds[axis + 3] - bounds[axis]) / 2)) || 1;
      const tris = new Uint16Array(raw.tris);
      return {
        points, centre, radius, tris,
        mats: raw.mats,
        colors: raw.colors.map((hex) => [1, 3, 5].map((offset) => parseInt(hex.slice(offset, offset + 2), 16))),
        order: Array.from({ length: raw.mats.length }, (_, index) => index),
        depth: new Float32Array(raw.mats.length),
      };
    });
    modelCache.set(slug, load);
    load.then((value) => { model = value; canvas.classList.add("ready"); }).catch(() => { canvas.remove(); });
    frame = requestAnimationFrame(draw);
    return () => cancelAnimationFrame(frame);
  }

  function initializeArsenal() {
    const dossier = byId("weapon-dossier");
    const tabs = byId("weapon-tabs");
    if (!dossier || !tabs) return;
    let weapons = [];
    let selected = 0;
    let disposeModel = null;

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
      tabs.querySelectorAll(".weapon-tab").forEach((button, index) => {
        button.classList.toggle("active", index === selected);
      });
      // The summary carries the selected weapon's name, so the closed dropdown
      // says what is on screen rather than just naming its category.
      tabs.querySelectorAll("details.weapon-group").forEach((group) => {
        const holds = selected >= Number(group.dataset.first) && selected <= Number(group.dataset.last);
        group.classList.toggle("holds-selection", holds);
        text(group.querySelector(".weapon-group-current"), holds ? weapon.name : "");
      });
      dossier.replaceChildren();
      const grid = document.createElement("div");
      grid.className = "dossier-grid";
      const visual = document.createElement("div");
      visual.className = "weapon-visual";
      visual.dataset.index = String(selected + 1).padStart(2, "0");
      const title = document.createElement("h2"); text(title, weapon.name);
      const silhouette = document.createElement("div");
      silhouette.className = `weapon-silhouette ${weaponStyle(weapon.kind, weapon.fireMode)}`;
      ["body", "long-barrel", "wood", "grip"].forEach((className) => { const part = document.createElement("i"); part.className = className; silhouette.append(part); });
      const availability = document.createElement("p"); text(availability, `${weapon.availability} // ${weapon.fireMode}`);
      // The CSS silhouette stays as the ground truth for "something is here":
      // the model is layered over it and removes itself if it cannot load, so a
      // failed fetch degrades to what the page has always shown.
      const model = document.createElement("canvas");
      model.className = "weapon-model";
      model.setAttribute("aria-hidden", "true");
      const stage = document.createElement("div");
      stage.className = "weapon-stage";
      stage.append(silhouette, model);
      visual.append(title, stage, availability);
      if (disposeModel) disposeModel();
      disposeModel = mountWeaponModel(model, weapon.name);

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
        stat("Hipfire spread", format(weapon.hipSpread, 3), "RAD", 100 - weapon.hipSpread * 900),
        stat("Penetration budget", format(weapon.penetration), "UNITS", weapon.penetration * 4),
        stat("Damage at range", format(weapon.minimumDamagePerProjectile), "HP", weapon.minimumDamagePerProjectile / 1.2)
      );
      const note = document.createElement("div"); note.className = "damage-note";
      const projectiles = weapon.projectilesPerShot > 1
        ? `${weapon.projectilesPerShot} independently traced projectiles per trigger pull. Maximum damage assumes every pellet hits.`
        : "One projectile is traced per trigger pull. Regional multipliers and penetration loss are resolved during impact.";
      const falloff = weapon.falloffEndMetres > 0
        ? ` Damage falls from ${format(weapon.damagePerProjectile)} HP at ${format(weapon.falloffStartMetres)} m to ${format(weapon.minimumDamagePerProjectile)} HP at ${format(weapon.falloffEndMetres)} m.`
        : " No distance falloff — full damage at any range.";
      text(note, projectiles + falloff);
      panel.append(header, stats, note);
      grid.append(visual, panel);
      dossier.append(grid);
    }

    function loadPayload(payload) {
      const loaded = Array.isArray(payload.weapons) ? payload.weapons : [];
      // Group into kind sections; `weapons` is rebuilt in section order so the
      // flat selection index stays aligned with the rendered buttons.
      const labels = [...new Set(loaded.map(sectionOf))]
        .sort((a, b) => (sectionOrder.indexOf(a) + 1 || 99) - (sectionOrder.indexOf(b) + 1 || 99));
      const sections = labels
        .map((label) => [label, loaded.filter((weapon) => sectionOf(weapon) === label)])
        .filter(([, members]) => members.length > 0);
      weapons = sections.flatMap(([, members]) => members);
      // One dropdown per category rather than every weapon at once: the flat
      // list ran the height of the viewport, so changing weapon happened too
      // far from the stats it changed to be noticed.
      let index = 0;
      for (const [label, members] of sections) {
        const group = document.createElement("details");
        group.className = "weapon-group";
        const summary = document.createElement("summary");
        const caption = document.createElement("span");
        caption.className = "weapon-group-label";
        text(caption, label);
        const current = document.createElement("b");
        current.className = "weapon-group-current";
        summary.append(caption, current);
        const menu = document.createElement("div");
        menu.className = "weapon-menu";
        const first = index;
        for (const weapon of members) {
          const at = index;
          const button = document.createElement("button");
          button.className = "weapon-tab";
          button.type = "button";
          text(button, weapon.name);
          button.addEventListener("click", () => {
            selected = at;
            group.open = false;
            render();
          });
          menu.append(button);
          index += 1;
        }
        group.dataset.first = String(first);
        group.dataset.last = String(index - 1);
        group.append(summary, menu);
        // One open at a time, and clicking anywhere else closes it.
        summary.addEventListener("click", () => {
          tabs.querySelectorAll("details[open]").forEach((other) => { if (other !== group) other.open = false; });
        });
        tabs.append(group);
      }
      document.addEventListener("click", (event) => {
        if (!tabs.contains(event.target)) tabs.querySelectorAll("details[open]").forEach((open) => { open.open = false; });
      });
      render();
    }

    fetch(`${apiRoot}/api/arsenal`)
      .then((response) => { if (!response.ok) throw new Error(); return response.json(); })
      .then((payload) => loadPayload(payload))
      .catch(() => {
        const fallback = byId("arsenal-fallback");
        try {
          loadPayload(JSON.parse(fallback?.textContent ?? "{}"));
        } catch {
          // The dossier keeps its loading placeholder if even the bundled
          // snapshot is unreadable.
        }
      });
  }

  initializeBackdrop();
  initializeServerBrowser();
  initializeArsenal();
})();
