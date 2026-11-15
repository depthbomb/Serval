import assert from "node:assert/strict";
import {spawn} from "node:child_process";
import {once} from "node:events";
import {appendFile, cp, mkdtemp} from "node:fs/promises";
import {createServer as createTcpServer} from "node:net";
import {tmpdir} from "node:os";
import path from "node:path";
import {fileURLToPath, pathToFileURL} from "node:url";

const repository = fileURLToPath(new URL("../../../", import.meta.url));
const sample = path.join(repository, "samples/Serval.Sample.Vite");
const workspace = await mkdtemp(path.join(tmpdir(), "serval-vite-hmr-"));
const frontend = path.join(workspace, "frontend");
const artifacts = path.join(workspace, "artifacts");
await cp(path.join(sample, "ClientApp/src"), path.join(frontend, "src"), {recursive: true});

// Use the sample's installed Vite and proxy configuration, but edit disposable copies of its frontend files.
const {createServer} = await import(pathToFileURL(path.join(sample, "ClientApp/node_modules/vite/dist/node/index.js")));
await run("dotnet", ["build", path.join(sample, "Serval.Sample.Vite.csproj"), "-c", "Debug", "--artifacts-path", artifacts,
    "--disable-build-servers", "-p:UseSharedCompilation=false", "-p:GeneratePackageOnBuild=false", "-nodeReuse:false", "-p:ViteDevelopment=true",
    "-p:ViteBuildCommand=exit 99", `-p:ViteManifest=${path.join(workspace, "missing.json")}`]);

const reservation = createTcpServer();
reservation.listen(0, "127.0.0.1");
await once(reservation, "listening");
const backendUrl = `http://localhost:${reservation.address().port}`;
await new Promise((resolve, reject) => reservation.close(error => error ? reject(error) : resolve()));
const backend = spawn("dotnet", [path.join(artifacts, "bin/Serval.Sample.Vite/debug/Serval.Sample.Vite.dll"), "--port", new URL(backendUrl).port], {
    cwd: repository,
    windowsHide: true,
    stdio: ["ignore", "pipe", "pipe"],
});
let backendLog = "";
backend.stdout.on("data", chunk => {
    backendLog = (backendLog + chunk).slice(-16000);
});
backend.stderr.on("data", chunk => {
    backendLog = (backendLog + chunk).slice(-16000);
});
const backendExit = once(backend, "exit");
let vite;
let socket;
try {
    await waitForBackend();
    process.env.SERVAL_BACKEND_URL = backendUrl;
    const {default: configure} = await import(pathToFileURL(path.join(sample, "ClientApp/vite.config.js")));
    const config = configure({command: "serve", mode: "development"});
    vite = await createServer({
        ...config,
        configFile: false,
        root: frontend,
        cacheDir: path.join(workspace, "vite-cache"),
        server: {...config.server, host: "127.0.0.1", port: 0},
    });
    await vite.listen();
    const origin = new URL(vite.resolvedUrls.local[0]).origin;
    const page = await text(origin + "/");
    assert.match(page, /Vite serves the frontend/);
    assert.match(page, /src="\/_vite\/@vite\/client"/);
    assert.match(page, /src="\/_vite\/src\/main\.js\?import"/);
    assert.doesNotMatch(page, /\/assets\/main-/);
    assert.match(await text(origin + "/_vite/@vite/client"), /WebSocket/);
    assert.match(await text(origin + "/_vite/src/logo.svg"), /<svg/);

    const wsUrl = new URL(`/_vite/?token=${vite.config.webSocketToken}`, origin);
    wsUrl.protocol = "ws:";
    socket = new WebSocket(wsUrl, "vite-hmr");
    const messages = [];
    const waiters = new Set();
    socket.addEventListener("message", event => {
        const message = JSON.parse(event.data);
        messages.push(message);
        for (const notify of waiters) {
            notify();
        }
    });
    await nextMessage(message => message.type === "connected");
    assert.match(await text(origin + "/_vite/src/main.js?import"), /createHotContext/);
    assert.match(await text(origin + "/_vite/src/site.css?import"), /updateStyle/);

    for (const [file, marker] of [["site.css", "/* hmr-css-marker */"], ["main.js", "// hmr-js-marker"]]) {
        const update = nextMessage(message => message.type === "update" &&
            message.updates.some(item => item.acceptedPath.endsWith("/src/" + file)));
        await appendFile(path.join(frontend, "src", file), "\n" + marker + "\n");
        await update;
        assert.ok((await text(`${origin}/_vite/src/${file}?import&t=${Date.now()}`)).includes(marker));
    }
    assert.ok(!messages.some(message => message.type === "full-reload"), "CSS and accepted JS changes should use HMR");
    console.log("Vite development checks passed: clean build, backend proxy, source assets, CSS HMR, and JavaScript HMR.");

    function nextMessage(predicate) {
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => {
                waiters.delete(check);
                reject(new Error("Timed out waiting for HMR: " + JSON.stringify(messages)));
            }, 10000);

            function check() {
                const message = messages.find(predicate);
                if (message) {
                    clearTimeout(timer);
                    waiters.delete(check);
                    resolve(message);
                }
            }

            waiters.add(check);
            check();
        });
    }
} catch (error) {
    console.error(backendLog);
    throw error;
} finally {
    socket?.close();
    await vite?.close();
    if (backend.exitCode === null) {
        backend.kill();
        await backendExit;
    }
}

async function waitForBackend() {
    const deadline = Date.now() + 15000;
    while (Date.now() < deadline && backend.exitCode === null) {
        try {
            if ((await fetch(backendUrl, {signal: AbortSignal.timeout(1000)})).ok) {
                return;
            }
        } catch {
        }
        await new Promise(resolve => setTimeout(resolve, 100));
    }
    throw new Error("The sample backend did not start: " + backendLog);
}

async function text(url) {
    const response = await fetch(url, {signal: AbortSignal.timeout(10000)});
    assert.equal(response.status, 200, url);
    return response.text();
}

async function run(executable, args) {
    const child = spawn(executable, args, {cwd: repository, windowsHide: true, stdio: ["ignore", "pipe", "pipe"]});
    let output = "";
    child.stdout.on("data", chunk => {
        output = (output + chunk).slice(-32000);
    });
    child.stderr.on("data", chunk => {
        output = (output + chunk).slice(-32000);
    });
    const [code] = await once(child, "exit");
    assert.equal(code, 0, output);
}
