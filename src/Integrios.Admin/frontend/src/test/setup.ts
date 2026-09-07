/// jsdom implements no pointer capture and no scrolling, and the vendored listbox calls both while
/// opening. These are the two shims that let a real interaction — press the trigger, choose an
/// option — be exercised here rather than only in a browser. They add no behaviour of their own: a
/// test that passed because of a shim would fail in Chromium too, which is what the browser layer
/// is for.
///
/// The browser suites run under the node environment in the same process, where there is no DOM at
/// all, so this does nothing there rather than failing on the first line.
if (typeof Element !== "undefined") {
  if (!Element.prototype.hasPointerCapture) {
    Element.prototype.hasPointerCapture = () => false;
    Element.prototype.setPointerCapture = () => {};
    Element.prototype.releasePointerCapture = () => {};
  }

  if (!Element.prototype.scrollIntoView) {
    Element.prototype.scrollIntoView = () => {};
  }
}

/// The listbox positions itself with a popper, which observes the size of the elements it sits
/// between. jsdom lays nothing out, so there is nothing to observe and nothing to report.
if (typeof globalThis.ResizeObserver === "undefined") {
  globalThis.ResizeObserver = class {
    observe() {}
    unobserve() {}
    disconnect() {}
  } as unknown as typeof ResizeObserver;
}
