// Documentation checks that DocFX does not do. Run: npm ci && node check-docs.mjs (see CONTRIBUTING.md, "CI").
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { fileURLToPath } from "node:url";
import { JSDOM } from "jsdom";
import { checkIdentifiers } from "./check-identifiers.mjs";

const ROOT = fileURLToPath(new URL("../..", import.meta.url));
// The limit CLAUDE.md sets for itself: the file is loaded into every Claude Code session.
const CLAUDE_MD_MAX_LINES = 150;

function checkClaudeMdSize() {
  const lines = readFileSync(`${ROOT}/CLAUDE.md`, "utf8").trimEnd().split("\n").length;
  return lines > CLAUDE_MD_MAX_LINES
    ? [`CLAUDE.md: ${lines} lines, the limit is ${CLAUDE_MD_MAX_LINES}. Move what concerns one part of the project to its own document.`]
    : [];
}

async function checkMermaid() {
  // Mermaid expects a browser even for parsing, and reads these when it is imported.
  const { window } = new JSDOM("<!doctype html><body></body>");
  Object.assign(globalThis, { window, document: window.document, Element: window.Element });
  const { default: mermaid } = await import("mermaid");

  const problems = [];
  const files = execFileSync("git", ["-C", ROOT, "ls-files", "*.md"], { encoding: "utf8" }).split("\n").filter(Boolean);
  for (const file of files) {
    const text = readFileSync(`${ROOT}/${file}`, "utf8");
    for (const block of text.matchAll(/^[ \t]*```mermaid\n([\s\S]*?)^[ \t]*```/gm)) {
      try {
        await mermaid.parse(block[1]);
      } catch (error) {
        const line = text.slice(0, block.index).split("\n").length;
        problems.push(`${file}:${line}: Mermaid diagram does not parse: ${String(error.message).split("\n").slice(0, 3).join(" ")}`);
      }
    }
  }
  return problems;
}

const problems = [...checkClaudeMdSize(), ...checkIdentifiers(ROOT), ...(await checkMermaid())];
for (const problem of problems) console.error(problem);
if (problems.length > 0) {
  console.error(`\n${problems.length} problem(s). A name that is really external goes into scripts/check-docs/identifier-allowlist.txt.`);
  process.exit(1);
}
console.log("Documentation checks passed.");
