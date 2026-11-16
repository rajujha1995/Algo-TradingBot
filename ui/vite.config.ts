import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";
import tailwindcss from "@tailwindcss/vite";
export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    proxy: {
      "/api": "http://127.0.0.1:5101",
      "/healthz": "http://127.0.0.1:5101",
      "/hubs": { target: "http://127.0.0.1:5101", ws: true },
    },
  },
  test: { include: ["src/**/*.test.ts"] },
});
