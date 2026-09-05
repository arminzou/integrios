import { transferableAbortController } from "node:util";
import { QueryClientProvider } from "@tanstack/react-query";
import { render } from "@testing-library/react";
import type { ReactElement } from "react";
import { createMemoryRouter, RouterProvider } from "react-router";
import { beforeEach, vi } from "vitest";
import { createQueryClient } from "../api/query";
import { routeConfig } from "../routes";

// jsdom replaces AbortController but supplies no Request. Keep the router's cancellation signal
// compatible with Node's Request, using Node's own controller rather than a fetch mock.
beforeEach(() => {
  vi.stubGlobal("AbortController", transferableAbortController().constructor);
});

/// A screen is rendered directly, with its own props, inside just enough router to satisfy the
/// links it contains. Its own behaviour is what the test is about; which URL resolved to it is
/// `routes.test.tsx`'s job.
export function renderScreen(element: ReactElement, initialPath = "/") {
  const router = createMemoryRouter([{ path: "*", element }], { initialEntries: [initialPath] });
  return { ...render(withQueries(<RouterProvider router={router} />)), router };
}

/// The whole application at one URL, exercising the real route table — the shell, the matched
/// screen, and the Not-found fallback alike.
export function renderApp(initialPath: string) {
  const router = createMemoryRouter(routeConfig, { initialEntries: [initialPath] });
  return { ...render(withQueries(<RouterProvider router={router} />)), router };
}

/// A cache of its own per test: what one test read must never answer another test's read.
function withQueries(children: ReactElement) {
  return <QueryClientProvider client={createQueryClient()}>{children}</QueryClientProvider>;
}
