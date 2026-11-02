/*
DataLogXE - A simple and high performance data logger.
Copyright (C) 2026 datalogxe@gmail.com

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

// A tooltip for elements with data-hint; the title attribute of the browser opens only after a second and that delay
// can not be changed
const OPEN_DELAY_MS = 300;
const GAP_PX = 6;

let tip: HTMLDivElement | null = null;
let target: HTMLElement | null = null;
let timer: number | undefined;
// An element can vanish under the mouse, e.g. when a dialog closes; its tooltip goes with it
let watch: number | undefined;
// The text can change while the tooltip is open, e.g. the rate of a sparkline
const observer = new MutationObserver(() => {
  if (target && tip) {
    tip.textContent = target.dataset.hint ?? "";
    place();
  }
});

function hintTarget(node: EventTarget | null): HTMLElement | null {
  return node instanceof Element ? node.closest<HTMLElement>("[data-hint]") : null;
}

function place() {
  if (!tip || !target) {
    return;
  }
  const anchor = target.getBoundingClientRect();
  const width = tip.offsetWidth;
  const height = tip.offsetHeight;
  // data-hint-side="right" keeps the tooltip beside the element, e.g. for the entries of a list
  if (target.dataset.hintSide === "right" && anchor.right + GAP_PX + width <= window.innerWidth - 4) {
    tip.style.left = `${Math.round(anchor.right + GAP_PX)}px`;
    tip.style.top = `${Math.round(anchor.top + anchor.height / 2 - height / 2)}px`;
    return;
  }
  const left = Math.max(4, Math.min(anchor.left + anchor.width / 2 - width / 2, window.innerWidth - width - 4));
  const below = anchor.bottom + GAP_PX;
  const top = below + height <= window.innerHeight - 4 ? below : anchor.top - GAP_PX - height;
  tip.style.left = `${Math.round(left)}px`;
  tip.style.top = `${Math.round(top)}px`;
}

function show() {
  if (!target?.isConnected || !target.dataset.hint) {
    return;
  }
  if (!tip) {
    tip = document.createElement("div");
    tip.className = "hint-tip";
    tip.setAttribute("role", "tooltip");
    document.body.appendChild(tip);
  }
  tip.textContent = target.dataset.hint;
  tip.hidden = false;
  place();
  observer.observe(target, { attributes: true, attributeFilter: ["data-hint"] });
  window.clearInterval(watch);
  watch = window.setInterval(() => {
    if (!target?.isConnected) {
      hide();
    }
  }, 250);
}

function hide() {
  window.clearTimeout(timer);
  timer = undefined;
  window.clearInterval(watch);
  watch = undefined;
  observer.disconnect();
  target = null;
  if (tip) {
    tip.hidden = true;
  }
}

function open(next: HTMLElement) {
  if (next === target) {
    return;
  }
  hide();
  target = next;
  timer = window.setTimeout(show, OPEN_DELAY_MS);
}

export function installHints() {
  document.addEventListener("pointerover", (event) => {
    const next = hintTarget(event.target);
    if (next) {
      open(next);
    } else if (target) {
      hide();
    }
  });
  document.addEventListener("focusin", (event) => {
    const next = hintTarget(event.target);
    if (next && next.matches(":focus-visible")) {
      open(next);
    }
  });
  document.addEventListener("focusout", hide);
  document.addEventListener("pointerdown", hide, true);
  document.addEventListener("keydown", hide, true);
  window.addEventListener("scroll", hide, true);
  window.addEventListener("blur", hide);
}
