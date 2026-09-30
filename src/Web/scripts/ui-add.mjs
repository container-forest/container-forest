// Adds shadcn/ui components and switches their icons to Material Symbols (src/components/icons.ts).
// Usage: npm run ui:add -- dialog table
import { execSync } from "node:child_process";
import { readdirSync, readFileSync, writeFileSync } from "node:fs";
import { join } from "node:path";

const components = process.argv.slice(2);
if (components.length === 0) {
  console.error("Usage: npm run ui:add -- <component> [...]");
  process.exit(1);
}

execSync(`npx shadcn@latest add ${components.join(" ")}`, { stdio: "inherit" });

let changed = 0;
for (const dir of ["src/components/ui", "src/hooks"]) {
  let files;
  try {
    files = readdirSync(dir).filter((name) => name.endsWith(".tsx") || name.endsWith(".ts"));
  } catch {
    continue;
  }

  for (const name of files) {
    const path = join(dir, name);
    const source = readFileSync(path, "utf8");
    const updated = source.replaceAll(/from ["']lucide-react["']/g, 'from "@/components/icons"');
    if (updated !== source) {
      writeFileSync(path, updated);
      changed++;
    }
  }
}

// shadcn installs lucide-react for the icons it expects; the panel does not use it.
const pkg = JSON.parse(readFileSync("package.json", "utf8"));
if (pkg.dependencies?.["lucide-react"] || pkg.devDependencies?.["lucide-react"]) {
  execSync("npm uninstall lucide-react", { stdio: "inherit" });
}

console.log(`Icons switched to Material Symbols in ${changed} file(s). Run npm run typecheck: a missing icon fails it.`);
