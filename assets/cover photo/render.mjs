import { createRequire } from "node:module";
import { fileURLToPath, pathToFileURL } from "node:url";
import path from "node:path";
import fs from "node:fs";

const websitePackage = "D:/Photos/Portfolio/Home Website/package.json";
const websiteRequire = createRequire(websitePackage);
const { chromium } = websiteRequire("playwright");
const sharp = websiteRequire("sharp");

const here = path.dirname(fileURLToPath(import.meta.url));
const outDir = path.join(here, "exports");
fs.mkdirSync(outDir, { recursive: true });

const pageUrl = pathToFileURL(path.join(here, "cover.html")).href;

const variants = [
  {
    key: "16x9",
    variant: "wide",
    width: 1280,
    height: 720,
    scale: 2,
    file: "earguard-cover-16x9-2560x1440.png",
  },
  {
    key: "square",
    variant: "square",
    width: 1080,
    height: 1080,
    scale: 2,
    file: "earguard-cover-square-2160x2160.png",
  },
  {
    key: "github",
    variant: "strip",
    width: 1280,
    height: 640,
    scale: 2,
    file: "earguard-cover-github-2560x1280.png",
  },
  {
    key: "og",
    variant: "strip",
    width: 1200,
    height: 630,
    scale: 2,
    file: "earguard-cover-og-2400x1260.png",
  },
];

let browser;
try {
  browser = await chromium.launch({ channel: "chrome" });
} catch {
  browser = await chromium.launch({ channel: "msedge" });
}

for (const item of variants) {
  const page = await browser.newPage({
    viewport: { width: item.width, height: item.height },
    deviceScaleFactor: item.scale,
  });

  await page.goto(`${pageUrl}?variant=${item.variant}`, { waitUntil: "load" });
  await page.evaluate((variant) => {
    document.documentElement.dataset.variant = variant;
  }, item.variant);
  await page.evaluate(() => document.fonts.ready);
  await page.waitForFunction(() =>
    Array.from(document.images).every((img) => img.complete && img.naturalWidth > 0)
  );
  await page.waitForTimeout(150);

  const outPath = path.join(outDir, item.file);
  await page.screenshot({
    path: outPath,
    clip: { x: 0, y: 0, width: item.width, height: item.height },
  });

  const meta = await sharp(outPath).metadata();
  console.log(`${item.key.padEnd(7)} ${meta.width}x${meta.height}  ${item.file}`);
  await page.close();
}

await browser.close();

const jobs = [
  {
    src: "earguard-cover-16x9-2560x1440.png",
    out: "earguard-cover-16x9-1280x720.jpg",
    width: 1280,
    height: 720,
    format: "jpeg",
    quality: 90,
  },
  {
    src: "earguard-cover-16x9-2560x1440.png",
    out: "earguard-cover-16x9.webp",
    width: 1920,
    height: 1080,
    format: "webp",
    quality: 92,
  },
  {
    src: "earguard-cover-square-2160x2160.png",
    out: "earguard-cover-square-1080x1080.jpg",
    width: 1080,
    height: 1080,
    format: "jpeg",
    quality: 90,
  },
  {
    src: "earguard-cover-square-2160x2160.png",
    out: "earguard-cover-square-1080x1080.webp",
    width: 1080,
    height: 1080,
    format: "webp",
    quality: 92,
  },
  {
    src: "earguard-cover-github-2560x1280.png",
    out: "earguard-cover-github-1280x640.png",
    width: 1280,
    height: 640,
    format: "png",
  },
  {
    src: "earguard-cover-og-2400x1260.png",
    out: "earguard-cover-og-1200x630.jpg",
    width: 1200,
    height: 630,
    format: "jpeg",
    quality: 84,
  },
];

for (const job of jobs) {
  const input = path.join(outDir, job.src);
  const output = path.join(outDir, job.out);
  let pipeline = sharp(input).resize(job.width, job.height, {
    kernel: "lanczos3",
    fit: "fill",
  });

  if (job.format === "jpeg") {
    pipeline = pipeline.jpeg({
      quality: job.quality,
      chromaSubsampling: "4:4:4",
      mozjpeg: true,
    });
  } else if (job.format === "webp") {
    pipeline = pipeline.webp({ quality: job.quality, effort: 6 });
  } else {
    pipeline = pipeline.png({ compressionLevel: 9, palette: true });
  }

  await pipeline.toFile(output);
  const size = fs.statSync(output).size;
  console.log(`${job.out.padEnd(44)} ${(size / 1024).toFixed(1)} KB`);
}

console.log(`\nExports written to ${outDir}`);
