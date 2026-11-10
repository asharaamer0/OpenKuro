"use strict";
const film = document.querySelector("#film");
const scrub = document.querySelector("#scrub");
const tracks = document.querySelector("#tracks");
const play = document.querySelector("#play");
const time = document.querySelector("#time");
const status = document.querySelector("#demo-status");
const title = document.querySelector(".film-title");
const footageClip = document.querySelector("#footage-clip");
const splitClip = document.querySelector("#split-clip");
const titleClip = document.querySelector("#title-clip");
const moves = [...document.querySelectorAll("[data-demo]")];
let mode = "";
function update(frame) {
  scrub.value = frame;
  tracks.style.setProperty("--playhead", Math.min(100, frame / Number(scrub.max) * 100) + "%");
  const seconds = Math.floor(frame / 30);
  time.textContent = "00:" + String(seconds).padStart(2, "0") + ":" + String(frame % 30).padStart(2, "0");
  scrub.setAttribute("aria-valuetext", seconds + " seconds, frame " + frame % 30);
  if (mode === "animate") {
    const progress = Math.min(1, Math.max(0, (frame - 25) / 35));
    title.style.opacity = progress;
    title.style.transform = "translateY(" + (1 - progress) * 24 + "px)";
  }
}
function seek(frame) {
  update(frame);
  if (Number.isFinite(film.duration)) film.currentTime = Math.min(film.duration, frame / 30);
}
scrub.addEventListener("input", () => { film.pause(); seek(Number(scrub.value)); });
film.addEventListener("timeupdate", () => { if (!film.paused || film.ended) update(Math.floor(film.currentTime * 30 + .0001)); });
film.addEventListener("loadedmetadata", () => { scrub.max = Math.ceil(film.duration * 30); seek(Number(scrub.value)); });
function playState() {
  play.textContent = film.paused ? "▶" : "Ⅱ";
  play.setAttribute("aria-label", film.paused ? "Play sample" : "Pause sample");
}
play.addEventListener("click", async () => {
  if (!film.paused) { film.pause(); return; }
  try { await film.play(); } catch { status.textContent = "Sample video could not start"; }
});
for (const event of ["play", "pause", "ended"]) film.addEventListener(event, playState);
function applyMode(next, frame) {
  mode = next;
  film.pause();
  const at = Math.min(Number(scrub.max) - 1, Math.max(1, frame));
  const percentage = at / Number(scrub.max) * 100;
  footageClip.style.width = next === "cut" ? percentage + "%" : "100%";
  splitClip.hidden = next !== "cut";
  splitClip.style.left = percentage + "%";
  splitClip.style.width = (100 - percentage) + "%";
  titleClip.textContent = next === "animate" ? "◆    First light    ◆" : "First light";
  title.style.opacity = "1";
  title.style.transform = "";
  film.volume = next === "mix" ? .35 : 1;
  film.muted = next !== "mix";
  for (const move of moves) move.setAttribute("aria-pressed", String(move.dataset.demo === next));
  seek(at);
  status.textContent = { cut: "Split at " + (at / 30).toFixed(2) + "s", animate: "Title motion · scrub between frames 25 and 60", mix: "Clip volume set to 35% · play to listen" }[next];
}
for (const move of moves) move.addEventListener("click", () => {
  const next = move.dataset.demo;
  applyMode(next, next === "cut" ? Number(scrub.value) || 120 : next === "animate" ? 37 : 60);
  document.querySelector(".demo-frame").scrollIntoView({ block: "nearest", behavior: "instant" });
});
document.querySelector("#run-agent").addEventListener("click", () => {
  applyMode("cut", 120);
  const result = document.querySelector("#agent-result");
  result.textContent = "✓ Applied split_clip at frame 120 (4.00s). ";
  const link = document.createElement("a");
  link.href = "#timeline-demo";
  link.textContent = "View timeline ↑";
  result.append(link);
});
update(0);
