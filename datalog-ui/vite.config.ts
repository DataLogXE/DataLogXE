/*
DataLogXE - A simple and high performance data logger.
Copyright (C) 2026 datalogxe@gmail.com

This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program.  If not, see <http://www.gnu.org/licenses/>.
*/

import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";
import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";

const root = dirname(fileURLToPath(import.meta.url));
const { version } = JSON.parse(readFileSync(join(root, "package.json"), "utf8")) as { version: string };

export default defineConfig({
  plugins: [vue()],
  define: {
    __APP_VERSION__: JSON.stringify(version)
  },
  server: {
    // The preview of the desktop app hands out a free port through PORT
    port: Number(process.env.PORT) || 5173,
    strictPort: Boolean(process.env.PORT),
    proxy: {
      "/api": {
        // API_TARGET points the dev server at another API, e.g. the local stack on :8084
        target: process.env.API_TARGET || "http://127.0.0.1:5080",
        changeOrigin: true,
        // The local stack on https://127.0.0.1:8443 serves a self-signed Caddy certificate
        secure: false
      }
    }
  }
});
