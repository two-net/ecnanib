// Whether motion should be reduced (NFR-USE-02): the in-app choice on the document element or the operating system's preference.
export function isReduced() {
    return document.documentElement.dataset.motion === "reduced" || window.matchMedia("(prefers-reduced-motion: reduce)").matches;
}
