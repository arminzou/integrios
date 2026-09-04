import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";

// Development runs the dashboard behind Vite's proxy so the browser sees one origin, exactly as
// production does when Admin serves these assets itself. That is what keeps the product free of a
// CORS policy: there is never a cross-origin request to allow.
const adminOrigin = process.env.INTEGRIOS_ADMIN_ORIGIN ?? "http://localhost:5150";

export default defineConfig({
  plugins: [react()],
  test: {
    environment: "jsdom",
    include: ["src/**/*.test.ts", "src/**/*.test.tsx", "tests/e2e/**/*.test.ts"],
    restoreMocks: true,
    unstubGlobals: true,
  },
  build: {
    // Published into the Admin host's static root.
    outDir: "../wwwroot",
    emptyOutDir: true,
  },
  server: {
    proxy: Object.fromEntries(
      ["/admin", "/auth"].map((path) => [
        path,
        { target: adminOrigin, changeOrigin: false, secure: false },
      ]),
    ),
  },
});
