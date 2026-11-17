import {appendLog} from "./log.js";

const connectButton = document.querySelector("#socket-connect");
const disconnectButton = document.querySelector("#socket-disconnect");
const textButton = document.querySelector("#socket-send-text");
const binaryButton = document.querySelector("#socket-send-binary");
const form = document.querySelector("#socket-form");
const messageInput = document.querySelector("#socket-message");
const status = document.querySelector("#socket-status");
const log = document.querySelector("#socket-log");
let socket = null;

function updateControls(message) {
    const open = socket?.readyState === WebSocket.OPEN;
    connectButton.disabled = socket !== null;
    disconnectButton.disabled = socket === null || socket.readyState >= WebSocket.CLOSING;
    textButton.disabled = !open;
    binaryButton.disabled = !open;
    status.textContent = message;
}

function connectSocket() {
    if (socket !== null) {
        return;
    }

    const url = new URL("/socket", window.location.href);
    url.protocol = url.protocol === "https:" ? "wss:" : "ws:";
    const connection = new WebSocket(url);
    connection.binaryType = "arraybuffer";
    socket = connection;
    updateControls("Connecting");

    connection.addEventListener("open", () => {
        updateControls("Connected");
        appendLog(log, "Connection opened");
    });

    connection.addEventListener("message", (event) => {
        if (typeof event.data === "string") {
            appendLog(log, `Received text: ${event.data}`);
        } else {
            const text = new TextDecoder().decode(event.data);
            appendLog(log, `Received binary (${event.data.byteLength} bytes): ${text}`);
        }
    });

    connection.addEventListener("error", () => {
        appendLog(log, "Connection error. Check that the sample server is running.");
    });

    connection.addEventListener("close", (event) => {
        socket = null;
        updateControls("Disconnected");
        appendLog(log, `Connection closed (${event.code})${event.reason ? ": " + event.reason : ""}`);
    });
}

function disconnectSocket() {
    socket?.close(1000, "Leaving the sample");
    updateControls(socket === null ? "Disconnected" : "Disconnecting");
}

function sendMessage(event) {
    event.preventDefault();
    if (socket?.readyState !== WebSocket.OPEN) {
        return;
    }

    // The same text can travel in either a WebSocket text message or a binary message.
    const text = messageInput.value;
    const binary = event.submitter === binaryButton;
    try {
        socket.send(binary ? new TextEncoder().encode(text) : text);
        appendLog(log, `Sent ${binary ? "binary" : "text"}: ${text}`);
    } catch (error) {
        appendLog(log, `Could not send: ${error.message}`);
    }
}

connectButton.addEventListener("click", connectSocket);
disconnectButton.addEventListener("click", disconnectSocket);
form.addEventListener("submit", sendMessage);
window.addEventListener("pagehide", disconnectSocket);
