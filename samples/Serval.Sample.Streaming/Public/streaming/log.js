export function appendLog(log, message) {
    const entry = document.createElement("li");
    entry.textContent = message;
    log.append(entry);
    if (log.childElementCount > 100) {
        log.firstElementChild.remove();
    }

    log.scrollTop = log.scrollHeight;
}
