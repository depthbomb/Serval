import {defineConfig} from "vite";
import {fileURLToPath} from "node:url";

export default defineConfig(({command}) => ({
    // Keep Vite's modules and HMR socket separate from Serval's routes.
    base: command === "serve" ? "/_vite/" : "/",
    appType: "custom",
    server: {
        strictPort: true,
        proxy: {
            "^/(?!_vite(?:/|$))": {
                target: process.env.SERVAL_BACKEND_URL ?? "http://localhost:8114",
                changeOrigin: true,
            },
        },
    },
    build: {
        manifest: true,
        // Keep the logo as a file so the backend can look it up by its source key.
        assetsInlineLimit: 0,
        rolldownOptions: {
            input: fileURLToPath(new URL("./src/main.js", import.meta.url)),
        },
    },
}));
