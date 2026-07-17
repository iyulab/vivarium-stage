// Extracts the fenced C# examples from docs/getting-started.md and executes
// them against the real library, exactly as a fresh consumer would: the
// fences are hoisted into one throwaway console project that references
// Vivarium.Stage (and, transitively, the sibling vivarium-changeset SDK).
// The fences are written to throw on failure, so this guards both against
// API drift and against examples that stop demonstrating what they claim.
//
// Usage: node tools/verify-docs-examples.ts

import { execFileSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");

function extractFences(markdownPath: string, language: string): string[] {
  const source = readFileSync(markdownPath, "utf8");
  const fences: string[] = [];
  const pattern = new RegExp("^```" + language + "\\r?\\n([\\s\\S]*?)^```", "gm");
  for (let match; (match = pattern.exec(source)) !== null; ) fences.push(match[1]);
  if (fences.length === 0) throw new Error(`no \`\`\`${language} fences in ${markdownPath}`);
  return fences;
}

const fences = extractFences(join(repoRoot, "docs", "getting-started.md"), "csharp");
const consumer = mkdtempSync(join(tmpdir(), "vivarium-stage-docs-"));
try {
  // C# requires using directives before top-level statements — hoist and dedupe.
  const usings = new Set<string>();
  const statements: string[] = [];
  for (const fence of fences) {
    for (const line of fence.split(/\r?\n/)) {
      if (/^using [A-Za-z][\w.]*;$/.test(line.trim())) usings.add(line.trim());
      else statements.push(line);
    }
  }
  writeFileSync(join(consumer, "consumer.csproj"), `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="${join(repoRoot, "src", "Vivarium.Stage", "Vivarium.Stage.csproj")}" />
  </ItemGroup>
</Project>
`);
  writeFileSync(join(consumer, "Program.cs"), [...usings, "", ...statements].join("\n"));
  execFileSync("dotnet", ["run", "--project", consumer], { cwd: consumer, stdio: "pipe" });
  console.log(`PASS docs — ${fences.length} fences executed as one consumer program`);
} catch (error: any) {
  console.error(`FAIL — ${error.message}`);
  if (error.stdout) console.error(String(error.stdout));
  if (error.stderr) console.error(String(error.stderr));
  process.exit(1);
} finally {
  rmSync(consumer, { recursive: true, force: true });
}
