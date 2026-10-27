import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: {
      "/api": {
        // The address uvicorn is started on (--host 127.0.0.1); "localhost" can resolve
        // to ::1 first, where nothing listens.
        target: "http://127.0.0.1:8000",
        changeOrigin: true,
      },
    },
  },
});
