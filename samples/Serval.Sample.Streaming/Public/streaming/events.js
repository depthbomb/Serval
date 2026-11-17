import {appendLog} from "./log.js";

const startButton = document.querySelector("#events-start");
const stopButton = document.querySelector("#events-stop");
const status = document.querySelector("#events-status");
const log = document.querySelector("#events-log");
let source = null;

function stopEvents(message) {
    source?.close();
    source = null;
    startButton.disabled = false;
    stopButton.disabled = true;
    status.textContent = message;
}

function startEvents() {
    stopEvents("Connecting");
    log.replaceChildren();
    startButton.disabled = true;
    stopButton.disabled = false;

    const connection = new EventSource("/events?batch=3");
    source = connection;
    connection.addEventListener("open", () => {
        if (source !== connection) {
            return;
        }

        status.textContent = "Connected";
        appendLog(log, "Connection opened");
    });

    // Named SSE events use addEventListener rather than the default message handler.
    connection.addEventListener("tick", (event) => {
        if (source !== connection) {
            return;
        }

        appendLog(log, `${event.lastEventId}: ${event.data}`);
        if (event.lastEventId === "10") {
            stopEvents("Complete");
        }
    });

    connection.addEventListener("error", () => {
        if (source !== connection) {
            return;
        }

        if (connection.readyState === EventSource.CLOSED) {
            stopEvents("Closed");
            appendLog(log, "Stream closed. Start again to request a new sequence.");
            return;
        }

        // Keep this EventSource alive so the browser retains Last-Event-ID on reconnect.
        status.textContent = "Reconnecting";
        appendLog(log, "Connection ended; the browser will retry with the last event ID.");
    });
}

startButton.addEventListener("click", startEvents);
stopButton.addEventListener("click", () => stopEvents("Stopped"));
window.addEventListener("pagehide", () => stopEvents("Stopped"));
