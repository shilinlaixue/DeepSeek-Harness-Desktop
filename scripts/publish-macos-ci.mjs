#!/usr/bin/env node
/**
 * Publish this working tree to GitHub and build the macOS DMGs on Actions.
 *
 * Why: Apple's SwiftUI/WKWebView toolchain only exists on macOS. This script
 * commits the source through the GitHub Git Data API (no local git/network
 * needed), dispatches .github/workflows/build-macos.yml, waits for the matrix
 * build and downloads the DMG artifacts.
 *
 * Token: fine-grained PAT for the target repo with
 *   Contents: Read and write
 *   Workflows: Read and write
 *   Actions: Read and write
 * Put it in .build/github_token.txt (never commit that file) or GITHUB_TOKEN.
 *
 * Usage:
 *   node scripts/publish-macos-ci.mjs [owner/repo]
 */
import fs from "node:fs";
import path from "node:path";
import { spawnSync } from "node:child_process";

const root = process.cwd();
const repoArg = process.argv[2] || "XinXie-Condex/DeepSeek-Harness-Desktop";
const [owner, repo] = repoArg.split("/");
if (!owner || !repo) throw new Error("usage: publish-macos-ci.mjs owner/repo");

const tokenFile = path.join(root, ".build", "github_token.txt");
const token = (process.env.GITHUB_TOKEN || (fs.existsSync(tokenFile) ? fs.readFileSync(tokenFile, "utf8") : "")).trim();
if (!token) {
  console.error(`Missing token. Put a scoped PAT in ${tokenFile} or set GITHUB_TOKEN.`);
  process.exit(2);
}

const apiBase = `https://api.github.com/repos/${owner}/${repo}`;
const headers = {
  Authorization: `Bearer ${token}`,
  Accept: "application/vnd.github+json",
  "X-GitHub-Api-Version": "2022-11-28",
  "User-Agent": "deepseek-desktop-release",
};

async function api(url, options = {}) {
  const res = await fetch(url.startsWith("http") ? url : `${apiBase}${url}`, {
    ...options,
    headers: { ...headers, ...(options.headers || {}) },
  });
  const text = await res.text();
  let body;
  try { body = text ? JSON.parse(text) : null; } catch { body = text; }
  if (!res.ok) {
    const detail = typeof body === "string" ? body : JSON.stringify(body);
    throw new Error(`${options.method || "GET"} ${url} -> HTTP ${res.status}: ${detail}`);
  }
  return body;
}

function filesUnder(dir, out = []) {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    const rel = path.relative(root, full).replaceAll("\\", "/");
    if (
      rel === ".build" || rel.startsWith(".build/") ||
      rel === "dist" || rel.startsWith("dist/") ||
      rel === "build" || rel.startsWith("build/") ||
      rel === ".git" || rel.startsWith(".git/") ||
      rel.startsWith("windows/bin/") || rel.startsWith("windows/obj/") ||
      rel.startsWith("windows/publish/") || entry.name === ".DS_Store"
    ) continue;
    if (entry.isDirectory()) filesUnder(full, out);
    else if (entry.isFile()) out.push({ rel, full });
  }
  return out;
}

async function mapLimit(items, limit, fn) {
  const results = new Array(items.length);
  let next = 0;
  async function worker() {
    while (next < items.length) {
      const index = next++;
      results[index] = await fn(items[index], index);
    }
  }
  await Promise.all(Array.from({ length: Math.min(limit, items.length) }, worker));
  return results;
}

const files = filesUnder(root);
console.log(`source files: ${files.length}`);

const repoInfo = await api("");
const baseBranch = repoInfo.default_branch;
const baseRef = await api(`/git/ref/heads/${baseBranch}`);
const baseCommit = await api(`/git/commits/${baseRef.object.sha}`);
const baseTree = await api(`/git/trees/${baseCommit.tree.sha}?recursive=1`);
const basePaths = new Set((baseTree.tree || []).filter((e) => e.type === "blob").map((e) => e.path));

console.log("uploading blobs…");
const blobs = await mapLimit(files, 4, async (file) => {
  const content = fs.readFileSync(file.full).toString("base64");
  const blob = await api("/git/blobs", {
    method: "POST",
    body: JSON.stringify({ content, encoding: "base64" }),
  });
  return { ...file, sha: blob.sha };
});

const entries = blobs.map((file) => ({
  path: file.rel,
  mode: file.rel === "build.sh" ? "100755" : "100644",
  type: "blob",
  sha: file.sha,
}));
for (const p of basePaths) {
  if (!files.some((f) => f.rel === p)) {
    entries.push({ path: p, mode: "100644", type: "blob", sha: null });
  }
}

console.log("creating tree/commit…");
const tree = await api("/git/trees", {
  method: "POST",
  body: JSON.stringify({ base_tree: baseCommit.tree.sha, tree: entries }),
});
const commit = await api("/git/commits", {
  method: "POST",
  body: JSON.stringify({
    message: "Desktop 1.1.0: token-aware startup, self-contained Windows, macOS CI",
    tree: tree.sha,
    parents: [baseRef.object.sha],
  }),
});

const branch = `desktop/1.1.0-${new Date().toISOString().replace(/[-:TZ.]/g, "").slice(0, 14)}`;
await api("/git/refs", {
  method: "POST",
  body: JSON.stringify({ ref: `refs/heads/${branch}`, sha: commit.sha }),
});
console.log(`branch: ${branch}`);

console.log("dispatching Build macOS…");
await api("/actions/workflows/build-macos.yml/dispatches", {
  method: "POST",
  body: JSON.stringify({ ref: branch }),
});

const started = Date.now();
let run;
for (let i = 0; i < 180; i++) {
  await new Promise((r) => setTimeout(r, 5000));
  const runs = await api(`/actions/runs?branch=${encodeURIComponent(branch)}&event=workflow_dispatch&per_page=10`);
  run = (runs.workflow_runs || []).find((r) => new Date(r.created_at).getTime() >= started - 10_000);
  if (run && run.status !== "queued") break;
}
if (!run) throw new Error("workflow run was not created");
console.log(`run: ${run.html_url}`);

while (run.status !== "completed") {
  await new Promise((r) => setTimeout(r, 10_000));
  run = await api(`/actions/runs/${run.id}`);
  console.log(`  ${run.status}…`);
}
if (run.conclusion !== "success") {
  throw new Error(`workflow ${run.conclusion}: ${run.html_url}`);
}

const artifacts = await api(`/actions/runs/${run.id}/artifacts`);
const outDir = path.join(root, ".build", "mac-artifacts");
fs.rmSync(outDir, { recursive: true, force: true });
fs.mkdirSync(outDir, { recursive: true });

for (const artifact of artifacts.artifacts || []) {
  if (!artifact.name.startsWith("DeepSeek-macOS")) continue;
  console.log(`download ${artifact.name} (${(artifact.size_in_bytes / 1024 / 1024).toFixed(1)} MB)`);
  let res = await fetch(artifact.archive_download_url, { headers, redirect: "manual" });
  if (res.status >= 300 && res.status < 400) {
    // The signed storage URL must not receive the GitHub Authorization header.
    res = await fetch(res.headers.get("location"), { redirect: "follow" });
  }
  if (!res.ok) throw new Error(`artifact download failed: HTTP ${res.status}`);
  const zipPath = path.join(outDir, `${artifact.name}.zip`);
  fs.writeFileSync(zipPath, Buffer.from(await res.arrayBuffer()));
  const dest = path.join(outDir, artifact.name);
  fs.mkdirSync(dest, { recursive: true });
  const tar = spawnSync("tar.exe", ["-xf", zipPath, "-C", dest], { stdio: "inherit" });
  if (tar.status !== 0) throw new Error(`unzip failed: ${zipPath}`);
}

console.log(`\nDONE\nrun: ${run.html_url}\nartifacts: ${outDir}`);
