// Fails when documentation names code that no longer exists: identifiers and file paths in inline
// code spans are looked up in the tracked non-Markdown files. Names outside the code (third-party
// types, config keys derived from options) go into identifier-allowlist.txt.
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { basename } from "node:path";

const DOCS = [
  /^CLAUDE\.md$/,
  /^CONTRIBUTING\.md$/,
  /^src\/[^/]+\/CLAUDE\.md$/,
  /^docs\/design\/.+\.md$/,
  /^docs\/(architecture|project-status|security-invariants)\.md$/,
];
// Binary and generated files, whose names would hide one removed from the real source, and this tool itself.
const NOT_CORPUS =
  /^scripts\/check-docs\/|\.(md|png|jpe?g|gif|ico|webp|woff2?|ttf|otf|eot|pdf|zip|gz|db)$|package-lock\.json$|\/Generated\/|\/openapi\/|\.Designer\.cs$/;

const FENCE = /^\s*(```|~~~)/;
const SPAN = /(?<!`)(`+)(?!`)([^\n]*?)(?<!`)\1(?!`)/g;
const WORD = /[A-Za-z_][A-Za-z0-9_]*/g;
const CODE_LIKE = /[a-z][A-Z]|[A-Z]{2}[a-z]|[A-Za-z0-9]_[A-Za-z0-9]/;
// Environment variable bound to an option: CF_AGENT__DATADIRECTORY names AgentOptions.DataDirectory.
const OPTION_VARIABLE = /^CF_\w+__(?:\d+__)*(\w*?[A-Z]\w*?)(?:__\d+)*$/;
const PATH = /^(?:\.{0,2}\/)?[\w.@-]+(?:\/[\w.@-]+)*\/[\w.@-]+\.[A-Za-z]{1,6}$/;

function git(...args) {
  return execFileSync("git", args, { encoding: "utf8", maxBuffer: 1 << 28 });
}

export function checkIdentifiers(root) {
  const tracked = git("-C", root, "ls-files").split("\n").filter(Boolean);
  const words = new Set();
  const lowerWords = new Set();
  const add = (text) => {
    for (const [word] of text.matchAll(WORD)) {
      words.add(word);
      lowerWords.add(word.toLowerCase());
    }
  };
  for (const file of tracked) {
    if (NOT_CORPUS.test(file)) continue;
    add(basename(file));
    try {
      add(readFileSync(`${root}/${file}`, "utf8"));
    } catch {
      // A tracked file deleted in the working tree: its names are gone too.
    }
  }
  const exists = (word) => {
    const option = OPTION_VARIABLE.exec(word);
    return option ? lowerWords.has(option[1].toLowerCase()) : words.has(word);
  };
  const allowed = new Set(
    readFileSync(new URL("identifier-allowlist.txt", import.meta.url), "utf8")
      .split("\n")
      .map((line) => line.replace(/#.*/, "").trim())
      .filter(Boolean),
  );

  const problems = [];
  for (const doc of tracked.filter((file) => DOCS.some((re) => re.test(file)))) {
    let fenced = false;
    readFileSync(`${root}/${doc}`, "utf8")
      .split("\n")
      .forEach((line, index) => {
        if (FENCE.test(line)) fenced = !fenced;
        if (fenced) return;
        for (const [, , span] of line.matchAll(SPAN)) {
          const code = span.trim();
          if (PATH.test(code)) {
            const path = code.replace(/^\.{0,2}\//, "");
            if (!allowed.has(code) && !tracked.some((f) => f === path || f.endsWith(`/${path}`))) {
              problems.push(`${doc}:${index + 1}: no such file: ${code}`);
            }
            continue;
          }
          for (const [word] of code.matchAll(WORD)) {
            if (CODE_LIKE.test(word) && !exists(word) && !allowed.has(word)) {
              problems.push(`${doc}:${index + 1}: not found in code: ${word}`);
            }
          }
        }
      });
  }
  return problems;
}
