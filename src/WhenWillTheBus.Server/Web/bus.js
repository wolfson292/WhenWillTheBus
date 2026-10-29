// SPDX-License-Identifier: GPL-3.0-or-later
//
// The app's Bus and Map tabs, on a laptop.
//
// Lays out what the worker decided (BusView.cs) and keeps the countdown
// moving between polls. Decides nothing else: the rules that choose a headline
// or say how sure it is live in C#, against the same engine as the app, so a
// laptop and a phone side by side cannot disagree.
//
// Never innerHTML: names, addresses and scan locations come from a third-party
// feed, and textContent cannot be talked into running anything.
"use strict";

// Every 20 s: the worker itself polls the bus every 30, and every request here
// shares a per-household rate limit with the phones.
const POLL_MS = 20000;
const $ = (id) => document.getElementById(id);

let skew = 0;          // worker clock minus this laptop's, in ms
let zone = "America/New_York";
let countdown = null;  // { at, ticking } while something is being counted to
let routesKey = null;  // which run's routes are drawn, so they load only when that changes
let framed = 0;        // 0 nothing, 1 the routes, 2 the bus and stop: each ONCE, never out from under someone panning

const map = L.map("map", { zoomControl: true, attributionControl: true }).setView([26.55, -81.87], 12);
L.tileLayer("https://tile.openstreetmap.org/{z}/{x}/{y}.png", {
  maxZoom: 19,
  attribution: '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a>',
}).addTo(map);
const routeLayer = L.layerGroup().addTo(map);
const pinLayer = L.layerGroup().addTo(map);

// MapPage.Heat: one blue, LIGHTER, thicker and more solid as a road gets more usual.
const HEAT = {
  most: { color: "#9CCBFF", weight: 7, opacity: 1 },
  often: { color: "rgb(79,143,230)", weight: 5, opacity: 0.95 },
  sometimes: { color: "rgb(59,94,148)", weight: 4, opacity: 0.85 },
  rarely: { color: "rgb(92,99,117)", weight: 3, opacity: 0.75 },
};

function show(el, visible) { el.hidden = !visible; }
function text(id, value) { $(id).textContent = value ?? ""; }
function colour(el, name, prefix = "c-") {
  el.className = el.className.split(" ").filter((c) => !c.startsWith(prefix)).join(" ");
  if (name) el.classList.add(prefix + name);
}

// BusPage.Countdown.
function formatCountdown({ at, ticking }) {
  const away = at * 1000 - (Date.now() + skew);
  if (away <= 0) return "due now";
  const seconds = Math.floor(away / 1000);
  if (ticking) return `in ${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`;
  if (away < 3600e3) return `in ${Math.round(away / 60e3)} min`;
  if (away < 20 * 3600e3) return `in ${Math.floor(seconds / 3600)}h ${Math.floor((seconds % 3600) / 60)}m`;
  return new Date(at * 1000).toLocaleString("en-US", {
    timeZone: zone, weekday: "long", hour: "numeric", minute: "2-digit",
  });
}

function tick() {
  if (countdown) text("countdown", formatCountdown(countdown));
}

function drawCard(rider) {
  text("rider", rider?.name ?? "Bus");
  text("chip", rider?.bus ? `Bus ${rider.bus}` : "");
  show($("chip"), !!rider?.bus);
  text("footer", rider?.footer);

  const card = rider?.card;
  countdown = null;
  text("stage", card?.stage ?? "No riders yet.");
  show($("estimate"), true);
  for (const id of ["time", "under", "track", "confidence"]) show($(id), !!card?.estimate);
  show($("facts"), !!card?.estimate);
  if (!card?.estimate) return;

  const head = card.headline;
  show($("arrival"), !!head);
  text("arrival", head?.text);
  show($("meridiem"), !!head?.meridiem);
  text("meridiem", head?.meridiem);

  const cd = card.countdown;
  const counter = $("countdown");
  show(counter, cd.mode !== "none");
  colour(counter, cd.colour);
  if (cd.mode === "to") {
    countdown = { at: cd.at, ticking: cd.ticking };
    tick();
  } else {
    text("countdown", cd.text);
  }

  show($("band"), !!card.band);
  text("band", card.band);

  const track = card.track;
  show($("track"), !!track);
  if (track) {
    $("fill").style.width = `${track.percent}%`;
    $("knob").style.left = `${track.percent}%`;
    colour($("fill"), track.colour, "bg-");
    colour($("knob"), track.colour, "bg-");
    text("left", track.left);
    text("right", track.right);
  }

  const c = card.confidence;
  document.querySelectorAll(".dots i").forEach((dot, i) => {
    dot.style.background = i < c.filled ? `var(--${c.colour})` : "";
  });
  text("basis", c.label);
  colour($("basis"), c.colour === "bus" ? "bus" : null);
  text("why", c.text);

  const f = card.facts;
  text("gps", f.gps);
  colour($("lamp"), f.lamp === "off" ? null : f.lamp);
  text("scans", f.scans);
  show($("schoolrow"), !!f.school);
  text("school", f.school);
}

function drawScans(days) {
  const holder = $("scandays");
  holder.replaceChildren();
  if (!days?.length) {
    const none = document.createElement("p");
    none.className = "dim";
    none.textContent = "No scans yet. The school's feed only ever returns today; everything older is kept by the worker.";
    holder.append(none);
    return;
  }

  for (const day of days) {
    const h = document.createElement("h3");
    h.textContent = day.day;
    holder.append(h);
    for (const scan of day.scans) {
      const p = document.createElement("p");
      const what = document.createElement("div");
      what.textContent = scan.what;
      const when = document.createElement("div");
      when.className = "when";
      when.textContent = `${scan.at} · ${scan.where}`;
      p.append(what, when);
      holder.append(p);
    }
  }
}

function pin(p, style, permanent) {
  if (!p) return null;
  const popup = document.createElement("div");
  const b = document.createElement("b");
  b.textContent = p.label;
  const d = document.createElement("span");
  d.textContent = p.detail;
  popup.append(b, d);

  const marker = L.circleMarker(p.at, style).bindPopup(popup);
  if (permanent) marker.bindTooltip(p.label, { permanent: true, direction: "right", offset: [10, 0], className: "pin-label" });
  return marker.addTo(pinLayer);
}

function drawMap(rider) {
  const m = rider?.map;
  text("caption", m?.caption ?? "Nothing to show yet.");
  pinLayer.clearLayers();
  if (!m) return;

  pin(m.school, { radius: 7, color: "#AEB9C9", weight: 3, fillColor: "#2E3746", fillOpacity: 1 });
  pin(m.stop, { radius: 8, color: "#EDF1F7", weight: 3, fillColor: "#0E1117", fillOpacity: 1 }, true);

  // The bus is yellow in the app; a pin that has stopped reporting loses it.
  pin(m.bus, { radius: 9, color: "#0E1117", weight: 3, fillColor: m.live ? "#F6C445" : "#8A96A9", fillOpacity: 1 }, true);

  if (framed < 2 && (m.bus || m.stop)) {
    const points = [m.bus?.at, m.stop?.at].filter(Boolean);
    if (points.length === 2) map.fitBounds(points, { padding: [60, 60], maxZoom: 16 });
    else map.setView(points[0], 15);
    framed = 2;
  }

  const key = `${rider.childId}/${m.run}/${m.journeys ?? 0}`;
  if (key !== routesKey) {
    routesKey = key;
    loadRoutes(rider.childId, m.run);
  }
}

async function loadRoutes(childId, run) {
  const response = await fetch(`routes?child=${encodeURIComponent(childId)}&run=${run}`, { cache: "no-store" });
  if (!response.ok) return;
  const data = await response.json();
  routeLayer.clearLayers();
  for (const segment of data.segments) {
    L.polyline(segment.path, { ...HEAT[segment.level], lineCap: "round", lineJoin: "round", interactive: false })
      .addTo(routeLayer);
  }

  // Between runs there is no bus and, after a restart, no stop yet either:
  // show the route itself rather than an arbitrary patch of map.
  if (framed === 0 && data.segments.length) {
    map.fitBounds(L.featureGroup(routeLayer.getLayers()).getBounds(), { padding: [30, 30] });
    framed = 1;
  }
}

async function poll() {
  try {
    const response = await fetch("state", { cache: "no-store" });
    if (!response.ok) throw new Error(response.status === 429 ? "Asked too often; trying again shortly." : `The worker answered ${response.status}.`);
    const state = await response.json();
    skew = state.now * 1000 - Date.now();
    zone = state.zone || zone;
    show($("problem"), false);

    // The first rider, as the app shows.
    const rider = state.riders[0];
    drawCard(rider);
    drawScans(rider?.scans);
    drawMap(rider);
  } catch (error) {
    // Keep what was last shown, and say it is no longer current.
    text("problem", error instanceof TypeError ? "Can't reach the worker." : error.message);
    show($("problem"), true);
    countdown = null;
  }
}

poll();
setInterval(poll, POLL_MS);
setInterval(tick, 1000);
