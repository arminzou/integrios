import { describe, expect, it } from "vitest";
import { fieldError, formError, problemFrom } from "./problem";

describe("Problem Details", () => {
  it("attributes a validation message to its field whatever casing the server used", () => {
    const problem = problemFrom(
      { title: "One or more validation errors occurred.", errors: { Slug: ["Taken."] } },
      422,
    );
    expect(fieldError(problem, "slug")).toBe("Taken.");
  });

  it("does not repeat a field message as a form-level error", () => {
    const problem = problemFrom({ errors: { slug: ["Taken."] } }, 422);
    expect(formError(problem, ["slug"])).toBeUndefined();
  });

  it("surfaces a message attributed to a field the screen does not render", () => {
    const problem = problemFrom({ errors: { config: ["Unknown scheme."] } }, 422);
    expect(formError(problem, ["name"])).toContain("Unknown scheme.");
  });

  it("still reports a failure the server described with no message at all", () => {
    expect(formError(problemFrom(undefined, 409))).toBe("The request failed (409).");
  });

  it("prefers the server's own detail over its generic title", () => {
    const problem = problemFrom({ title: "Conflict", detail: "A Tenant with this slug exists." }, 409);
    expect(formError(problem)).toBe("A Tenant with this slug exists.");
  });
});
