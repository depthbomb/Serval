let socket;
let joinedRoom;
const log = document.querySelector("#log");
const status = document.querySelector("#status");

function append(message) {
    const item = document.createElement("li");
    item.textContent = message;
    log.append(item);
    while (log.children.length > 100) {
        log.firstElementChild.remove();
    }
}

function update() {
    const connected = socket?.connected === true;
    document.querySelectorAll(".online").forEach(button => {
        button.disabled = !connected;
    });
    document.querySelector("#disconnect").disabled = !socket;
    document.querySelector("#connect").disabled = !!socket;
    status.textContent = connected ? `Connected to ${socket.nsp} using ${socket.io.engine.transport.name}` : "Disconnected";
}

async function emit(name, ...args) {
    try {
        const reply = await socket.timeout(5000).emitWithAck(name, ...args);
        append(`${name} acknowledgement: ${JSON.stringify(reply)}`);
        return reply;
    } catch (error) {
        append(`${name}: ${error.message}`);
    }
}

document.querySelector("#connection").addEventListener("submit", event => {
    event.preventDefault();
    const transport = document.querySelector("#transport").value;
    socket = io(document.querySelector("#namespace").value, {
        transports: transport === "upgrade" ? ["polling", "websocket"] : [transport]
    });
    socket.on("connect", () => {
        joinedRoom = undefined;
        append(`Connected: ${socket.id}. Join a room to chat.`);
        update();
        socket.io.engine.on("upgrade", update);
    });
    socket.on("disconnect", reason => {
        joinedRoom = undefined;
        append(`Disconnected: ${reason}`);
        update();
    });
    socket.on("connect_error", error => append(`Connection failed: ${error.message}`));
    socket.io.on("reconnect_attempt", attempt => append(`Reconnecting, attempt ${attempt}`));
    socket.on("chat", message => append(`[${message.room}] ${message.from.slice(0, 8)}: ${message.text}`));
    socket.on("binary", bytes => append(`Binary echo: ${new TextDecoder().decode(bytes)}`));
    socket.on("question", (question, acknowledge) => {
        append(`Server asks: ${question}`);
        acknowledge("Yes, the browser received your question.");
    });
    update();
});

document.querySelector("#disconnect").addEventListener("click", () => {
    socket?.disconnect();
    socket = undefined;
    joinedRoom = undefined;
    update();
});

document.querySelector("#join").addEventListener("submit", async event => {
    event.preventDefault();
    const current = socket;
    const reply = await emit("join", document.querySelector("#room").value);
    if (socket === current && socket.connected && reply?.room) {
        joinedRoom = reply.room;
    }
});

document.querySelector("#chat").addEventListener("submit", event => {
    event.preventDefault();
    if (!joinedRoom) {
        append("Join a room first.");
        return;
    }

    void emit("chat", joinedRoom, document.querySelector("#message").value);
});

document.querySelector("#binary").addEventListener("click", () => void emit("binary", new TextEncoder().encode("Hello, 世界")));
document.querySelector("#question").addEventListener("click", () => void emit("question"));
window.addEventListener("pagehide", () => socket?.disconnect());
