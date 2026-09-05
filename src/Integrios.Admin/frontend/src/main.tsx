import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "./App";
import "./index.css";

// Named rather than asserted non-null: if the shell ever ships without its mount point, this says
// so instead of failing as a null dereference inside React.
const root = document.getElementById("root");
if (!root) throw new Error("The dashboard shell is missing its #root mount point.");

createRoot(root).render(
  <StrictMode>
    <App />
  </StrictMode>,
);
