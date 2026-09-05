import { QueryClientProvider } from "@tanstack/react-query";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { createBrowserRouter, RouterProvider } from "react-router";
import "./index.css";
import { createQueryClient } from "./api/query";
import { routeConfig } from "./routes";

// Named rather than asserted non-null: if the shell ever ships without its mount point, this says
// so instead of failing as a null dereference inside React.
const root = document.getElementById("root");
if (!root) throw new Error("The dashboard shell is missing its #root mount point.");

// Admin serves this SPA from its own origin with a browser-route fallback to index.html, so the
// router owns real paths rather than a hash.
const router = createBrowserRouter(routeConfig);

createRoot(root).render(
  <StrictMode>
    <QueryClientProvider client={createQueryClient()}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  </StrictMode>,
);
