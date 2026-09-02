// Regenerates the typed API client from the Admin OpenAPI document.
//
// The document is emitted by the Admin build itself, so the client can never describe a contract
// the API does not serve. Nothing here starts a host: the build is given placeholder settings only
// so the service graph can be constructed, and they never reach a running deployment.
import { spawnSync } from "node:child_process";
import { mkdirSync, existsSync } from "node:fs";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const frontend = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const repoRoot = resolve(frontend, "../../..");
const adminProject = join(repoRoot, "src/Integrios.Admin/Integrios.Admin.csproj");
const document = join(repoRoot, "artifacts/openapi/Integrios.Admin.json");
const output = join(frontend, "src/api/schema.d.ts");

function run(command, args, options = {}) {
  const result = spawnSync(command, args, { stdio: "inherit", ...options });
  if (result.error) throw result.error;
  if (result.status !== 0)
    throw new Error(`${command} ${args.join(" ")} exited with ${result.status ?? "a signal"}`);
}

run("dotnet", ["build", adminProject, "-p:OpenApiGenerateDocumentsOnBuild=true"], {
  env: {
    ...process.env,
    // Placeholders that satisfy startup validation while the document is produced. They are not a
    // deployment configuration and are never written anywhere.
    Integrios__PublicIngestionBaseUri: "https://ingestion.invalid",
    ConnectionStrings__Postgres: "Host=openapi.invalid;Database=integrios;Username=none;Password=none",
    // An Authority is what turns on the OIDC-gated /auth endpoints (see IsOidcConfigured), so the
    // generated document and client describe them too instead of the frontend hand-maintaining a
    // mirror of a contract the API already serves.
    "Integrios__Admin__Oidc__Authority": "https://oidc.invalid",
    "Integrios__Admin__Oidc__ClientId": "openapi-placeholder",
  },
});

if (!existsSync(document))
  throw new Error(`The Admin build did not emit ${document}.`);

mkdirSync(dirname(output), { recursive: true });
// Run the locked generator through Node directly: no npx resolution, no shell, one version.
run(process.execPath, [join(frontend, "node_modules/openapi-typescript/bin/cli.js"), document, "-o", output]);
