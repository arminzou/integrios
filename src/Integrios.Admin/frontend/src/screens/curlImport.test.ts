import { describe, expect, it } from "vitest";
import { parseCurl } from "./curlImport";

/// A GitHub delivery as a request bin's "copy as cURL" exports it: one flag per line, and the body in
/// `$'…'` quoting because it holds newlines and an apostrophe.
const captured = String.raw`curl -X 'POST' 'https://webhook.site/5f0c6a3e-8a2b-4c1d-9e7f-2b3c4d5e6f70' \
  -H 'connection: close' \
  -H 'content-length: 98' \
  -H 'x-github-hook-installation-target-type: repository' \
  -H 'x-github-hook-installation-target-id: 812345678' \
  -H 'x-hub-signature-256: sha256=3f1c9a2b7d' \
  -H 'x-github-delivery: 72d3162e-cc78-11e3-81ab-4c9367dc0958' \
  -H 'x-github-event: issues' \
  -H 'content-type: application/json' \
  -H 'accept: */*' \
  -H 'user-agent: GitHub-Hookshot/044aadd' \
  -H 'host: webhook.site' \
  -d $'{\n  "action": "opened",\n  "issue": {\n    "title": "It\'s broken",\n    "labels": ["bug"]\n  }\n}'`;

describe("Importing a curl command", () => {
  it("reads every header and the body of a captured request exactly", () => {
    const request = parseCurl(captured);

    expect(request.headers).toHaveLength(11);
    expect(request.headers[0]).toEqual({ name: "connection", value: "close" });
    expect(request.headers).toContainEqual({ name: "x-github-event", value: "issues" });
    expect(request.headers).toContainEqual({ name: "x-hub-signature-256", value: "sha256=3f1c9a2b7d" });
    expect(request.headers).toContainEqual({ name: "accept", value: "*/*" });
    expect(request.body).toBe(
      '{\n  "action": "opened",\n  "issue": {\n    "title": "It\'s broken",\n    "labels": ["bug"]\n  }\n}',
    );
  });

  it("decodes $'…' escapes", () => {
    expect(parseCurl(String.raw`curl -d $'a\tb\x41é\\\'\"\q'`).body).toBe("a\tbAé\\'\"\\q");
  });

  it("reads double quotes, joined short flags, and long flags", () => {
    const request = parseCurl(
      String.raw`curl "https://x.test" --header "X-Name: say \"hi\" \$HOME" -HAccept:text/plain --data-raw "{}"`,
    );

    expect(request.headers).toEqual([
      { name: "X-Name", value: 'say "hi" $HOME' },
      { name: "Accept", value: "text/plain" },
    ]);
    expect(request.body).toBe("{}");
  });

  it("keeps a header's case, its repeats, and the spacing inside its value", () => {
    expect(parseCurl("curl -H 'X-Tag:  a  b' -H 'x-tag: c'").headers).toEqual([
      { name: "X-Tag", value: "a  b" },
      { name: "x-tag", value: "c" },
    ]);
  });

  it("joins repeated data options the way curl sends them", () => {
    expect(parseCurl("curl -d a=1 --data-binary b=2").body).toBe("a=1&b=2");
  });

  it("ignores every other flag", () => {
    expect(parseCurl("curl --compressed -X POST -u user:pass -k https://x.test")).toEqual({
      headers: [],
      body: null,
    });
  });

  it("refuses an unclosed quote", () => {
    expect(() => parseCurl("curl -H 'x: y")).toThrow("unclosed");
  });
});
