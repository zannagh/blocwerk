/*
 * Asks before the tab is closed or reloaded while answers of the wall update's review are still on their way (or
 * could not be saved): they live only in this tab until the server has them.
 */
const ask = (e) => {
    e.preventDefault();
    e.returnValue = '';
    return '';
};

let on = false;

export function guard(enabled) {
    if (enabled === on) {
        return;
    }

    on = enabled;
    if (on) {
        window.addEventListener('beforeunload', ask);
    } else {
        window.removeEventListener('beforeunload', ask);
    }
}
