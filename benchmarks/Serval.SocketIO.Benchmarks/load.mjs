import assert from "node:assert/strict";
import {createRequire} from "node:module";
import {performance} from "node:perf_hooks";

const require = createRequire(new URL("../../tests/Serval.SocketIO.Tests/package.json", import.meta.url));
const {io} = require("socket.io-client");
const url = process.argv[2] ?? "http://localhost:8120";
const results = [];

for (const transport of ["websocket", "polling"]) {
    for (const kind of ["json", "binary"]) {
        const sockets = [];
        try {
            for (let index = 0; index < 4; index++) {
                const socket = io(url, {transports: [transport], forceNew: true, reconnection: false, timeout: 5000});
                sockets.push(socket);
                await new Promise((resolve, reject) => {
                    socket.once("connect", resolve);
                    socket.once("connect_error", reject);
                });
            }
            const payload = kind === "json" ? {index: 42, text: "x".repeat(128)} : {data: Buffer.alloc(4096, 7)};

            async function round(count) {
                const latencies = [];
                const started = performance.now();
                await Promise.all(sockets.flatMap(socket => Array.from({length: 8}, async () => {
                    for (let index = 0; index < count; index++) {
                        const before = performance.now();
                        const reply = await socket.timeout(10000).emitWithAck(kind, payload);
                        latencies.push(performance.now() - before);
                        if (kind === "json") {
                            assert.deepEqual(reply, payload);
                        } else {
                            assert.deepEqual(Buffer.from(reply.data), payload.data);
                        }
                    }
                })));
                const elapsed = performance.now() - started;
                latencies.sort((a, b) => a - b);
                return {
                    perSecond: latencies.length * 1000 / elapsed,
                    p50: latencies[Math.floor(latencies.length * .5)],
                    p95: latencies[Math.floor(latencies.length * .95)]
                };
            }

            await round(20);
            const samples = [];
            for (let sample = 0; sample < 5; sample++) {
                samples.push(await round(100));
            }
            samples.sort((a, b) => a.perSecond - b.perSecond);
            results.push({transport, kind, ...samples[2], samples});
        } finally {
            sockets.forEach(socket => socket.disconnect());
        }
    }
}
console.log(JSON.stringify({clients: 4, inFlightPerClient: 8, messagesPerSample: 3200, results}));
