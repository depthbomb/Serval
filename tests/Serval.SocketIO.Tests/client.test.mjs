import assert from "node:assert/strict";
import {setTimeout as delay} from "node:timers/promises";
import test from "node:test";
import {io, Manager} from "socket.io-client";

const url = process.env.SERVAL_SOCKETIO_URL;
assert.ok(url, "Run this suite through dotnet test so its test server is started.");

function once(socket, event, timeout = 5000) {
    return new Promise((resolve, reject) => {
        const timer = setTimeout(() => {
            socket.off(event, listener);
            reject(new Error(`Timed out waiting for ${event}`));
        }, timeout);

        function listener(...args) {
            clearTimeout(timer);
            resolve(args);
        }

        socket.once(event, listener);
    });
}

async function connect(t, namespace = "/", options = {}) {
    const socket = io(url + (namespace === "/" ? "" : namespace), {
        forceNew: true,
        autoConnect: false,
        reconnection: false,
        timeout: 3000,
        ...options
    });
    t.after(() => socket.disconnect());
    const ready = new Promise((resolve, reject) => {
        socket.once("connect", resolve);
        socket.once("connect_error", reject);
    });
    socket.connect();
    await ready;
    return socket;
}

for (const transport of ["polling", "websocket", "upgrade"]) {
    test(`${transport}: events, binary, acknowledgements, scopes and heartbeat`, {timeout: 20000}, async t => {
        const socket = await connect(t, "/", {
            transports: transport === "upgrade" ? ["polling", "websocket"] : [transport],
            extraHeaders: {Authorization: "Bearer http-test"}
        });
        if (transport === "upgrade" && socket.io.engine.transport.name !== "websocket") {
            await once(socket.io.engine, "upgrade");
        }
        assert.equal(socket.io.engine.transport.name, transport === "polling" ? "polling" : "websocket");
        assert.equal(await socket.timeout(3000).emitWithAck("user"), "http-user");
        const values = await new Promise((resolve, reject) => {
            socket.timeout(3000).emit("echo", "Hello, 世界", {count: 7}, [true, null], (error, ...args) => error ? reject(error) : resolve(args));
        });
        assert.deepEqual(values, ["Hello, 世界", {count: 7}, [true, null]]);

        const binary = {data: Buffer.from([0, 1, 255]), nested: [Buffer.from([7, 8]), Buffer.alloc(0)]};
        const reply = await socket.timeout(3000).emitWithAck("binary", binary);
        assert.deepEqual(Buffer.from(reply.data), binary.data);
        assert.deepEqual(reply.nested.map(value => Buffer.from(value)), binary.nested);
        const pushed = once(socket, "binary-event");
        await socket.timeout(3000).emitWithAck("server-binary");
        assert.deepEqual(Buffer.from((await pushed)[0].data), binary.data);

        socket.on("question", (question, acknowledge) => {
            assert.equal(question, "answer me");
            acknowledge(binary);
        });
        const serverReply = await socket.timeout(3000).emitWithAck("ask");
        assert.deepEqual(Buffer.from(serverReply.data), binary.data);
        assert.equal(await socket.timeout(3000).emitWithAck("ack-timeout"), "timed out");

        const first = await socket.timeout(3000).emitWithAck("scope");
        const second = await socket.timeout(3000).emitWithAck("scope", first.id);
        assert.notEqual(first.id, second.id);
        assert.equal(second.previousDisposed, true);

        const bursts = await Promise.all(Array.from({length: 20}, (_, i) => socket.timeout(3000).emitWithAck("echo", i)));
        assert.deepEqual(bursts, Array.from({length: 20}, (_, i) => i));
        await delay(600);
        assert.equal(socket.connected, true);
        assert.equal(await socket.timeout(3000).emitWithAck("echo", "after heartbeat"), "after heartbeat");
    });
}

test("rooms are isolated by namespace and removed on leave or disconnect", {timeout: 15000}, async t => {
    const sender = await connect(t, "/chat");
    const receiver = await connect(t, "/chat");
    const outsider = await connect(t, "/chat");
    const otherNamespace = await connect(t, "/");
    await Promise.all([sender, receiver, otherNamespace].map(socket => socket.timeout(3000).emitWithAck("join", "room")));
    const received = [];
    let unintended = 0;
    receiver.on("broadcast", message => received.push(message));
    for (const socket of [sender, outsider, otherNamespace]) {
        socket.on("broadcast", () => unintended++);
    }
    const arrival = once(receiver, "broadcast");
    await sender.timeout(3000).emitWithAck("broadcast", "room", "hello room");
    assert.equal((await arrival)[0], "hello room");
    await receiver.timeout(3000).emitWithAck("leave", "room");
    await sender.timeout(3000).emitWithAck("broadcast", "room", "after leave");
    await delay(150);
    assert.deepEqual(received, ["hello room"]);
    assert.equal(unintended, 0);
});

test("binary events survive polling-to-WebSocket upgrade in flight", {timeout: 15000}, async t => {
    const socket = await connect(t, "/chat", {transports: ["polling", "websocket"]});
    const upgrading = socket.io.engine.transport.name === "websocket" ? Promise.resolve() : once(socket.io.engine, "upgrade");
    const values = await Promise.all(Array.from({length: 20}, (_, index) =>
        socket.timeout(5000).emitWithAck("binary", {
            data: Buffer.from([index]),
            nested: [Buffer.from([255 - index])]
        })));
    values.forEach((value, index) => {
        assert.deepEqual(Buffer.from(value.data), Buffer.from([index]));
        assert.deepEqual(Buffer.from(value.nested[0]), Buffer.from([255 - index]));
    });
    await upgrading;
    assert.equal(socket.io.engine.transport.name, "websocket");
});

test("namespace auth rejection and multiplexing preserve other namespaces", {timeout: 15000}, async t => {
    const manager = new Manager(url, {autoConnect: false, reconnection: false, transports: ["websocket"]});
    const root = manager.socket("/");
    const privateSocket = manager.socket("/private", {auth: {token: "wrong"}});
    t.after(() => {
        root.disconnect();
        privateSocket.disconnect();
    });
    const ready = once(root, "connect");
    root.connect();
    await ready;
    const rejected = once(privateSocket, "connect_error");
    privateSocket.connect();
    assert.equal((await rejected)[0].message, "Not authorized");
    assert.equal(root.connected, true);
    privateSocket.auth = {token: "namespace-test"};
    const accepted = once(privateSocket, "connect");
    privateSocket.connect();
    await accepted;
    assert.equal(root.io.engine.id, privateSocket.io.engine.id);
    assert.notEqual(root.id, privateSocket.id);
    assert.equal(await privateSocket.timeout(3000).emitWithAck("user"), "namespace-user");
    assert.equal(await root.timeout(3000).emitWithAck("user"), null);
    const disconnected = once(privateSocket, "disconnect");
    await privateSocket.timeout(3000).emitWithAck("close-namespace");
    assert.equal((await disconnected)[0], "io server disconnect");
    assert.equal(await root.timeout(3000).emitWithAck("echo", "still connected"), "still connected");
});

test("official client reconnects with a new socket and empty rooms", {timeout: 15000}, async t => {
    const socket = await connect(t, "/chat", {reconnection: true, reconnectionDelay: 50, reconnectionDelayMax: 100});
    await socket.timeout(3000).emitWithAck("join", "before-reconnect");
    const previous = socket.id;
    const disconnected = once(socket, "disconnect");
    socket.emit("close-transport");
    await disconnected;
    await once(socket, "connect");
    assert.notEqual(socket.id, previous);
    const rooms = await socket.timeout(3000).emitWithAck("join", "after-reconnect");
    assert.ok(rooms.includes(socket.id));
    assert.ok(!rooms.includes("before-reconnect"));
});

test("unknown namespaces return connect_error", {timeout: 5000}, async t => {
    const socket = io(url + "/missing", {autoConnect: false, reconnection: false});
    t.after(() => socket.disconnect());
    const rejected = once(socket, "connect_error");
    socket.connect();
    assert.equal((await rejected)[0].message, "Invalid namespace");
});

for (const transport of ["polling", "websocket"]) {
    test(`${transport}: delayed acknowledgements cannot cross a transport reconnect`, {timeout: 15000}, async t => {
        const socket = await connect(t, "/", {
            transports: [transport],
            reconnection: true,
            reconnectionDelay: 50,
            reconnectionDelayMax: 100
        });
        const previousEngine = socket.io.engine.id;
        const oldQuestion = once(socket, "question");
        socket.emit("ask");
        const [, staleAcknowledgement] = await oldQuestion;
        const reconnected = once(socket, "connect");
        socket.io.engine.close();
        await reconnected;
        assert.notEqual(socket.io.engine.id, previousEngine);

        const newQuestion = once(socket, "question");
        const reply = socket.timeout(3000).emitWithAck("ask");
        let completed = false;
        reply.then(() => {
            completed = true;
        }, () => {
            completed = true;
        });
        const [, acknowledge] = await newQuestion;
        staleAcknowledgement({data: Buffer.from([1]), nested: []});
        await delay(50);
        assert.equal(completed, false);
        acknowledge({data: Buffer.from([2]), nested: []});
        assert.deepEqual(Buffer.from((await reply).data), Buffer.from([2]));
    });

    test(`${transport}: delayed acknowledgements cannot satisfy a reconnected namespace`, {timeout: 15000}, async t => {
        const manager = new Manager(url, {autoConnect: false, reconnection: false, transports: [transport]});
        const root = manager.socket("/");
        const chat = manager.socket("/chat");
        t.after(() => {
            root.disconnect();
            chat.disconnect();
        });
        const ready = Promise.all([once(root, "connect"), once(chat, "connect")]);
        root.connect();
        chat.connect();
        await ready;
        const engineId = manager.engine.id;

        const oldQuestion = once(root, "question");
        root.emit("ask");
        const [, staleAcknowledgement] = await oldQuestion;
        root.disconnect();
        const reconnected = once(root, "connect");
        root.connect();
        await reconnected;
        assert.equal(manager.engine.id, engineId);

        const newQuestion = once(root, "question");
        const reply = root.timeout(3000).emitWithAck("ask");
        let completed = false;
        reply.then(() => {
            completed = true;
        }, () => {
            completed = true;
        });
        const [, acknowledge] = await newQuestion;
        staleAcknowledgement({data: Buffer.from([1]), nested: []});
        await delay(50);
        assert.equal(completed, false);
        acknowledge({data: Buffer.from([2]), nested: []});
        assert.deepEqual(Buffer.from((await reply).data), Buffer.from([2]));
        assert.equal(await chat.timeout(3000).emitWithAck("echo", "alive"), "alive");
    });
}
