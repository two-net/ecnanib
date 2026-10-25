// Applies a saved appearance preference to the document element at once (FR-UI-02: switch without a reload).
// App.razor renders the same attributes on the next request, so the page and the server never disagree.
export function apply(theme, motion) {
    const root = document.documentElement;
    if (theme) {
        root.dataset.theme = theme;
    } else {
        delete root.dataset.theme;
    }

    if (motion) {
        root.dataset.motion = motion;
    } else {
        delete root.dataset.motion;
    }
}
