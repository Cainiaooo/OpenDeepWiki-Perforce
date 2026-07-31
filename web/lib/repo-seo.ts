import type { Metadata } from "next";
import { buildRepoBasePath, buildRepoDocPath } from "@/lib/repo-route";

export const SITE_NAME = "OpenDeepWiki";
export const SITE_DESCRIPTION =
  "AI-powered code knowledge base for repository analysis and documentation generation";

const DEFAULT_SITE_URL = "http://localhost:3000";
const MAX_DESCRIPTION_LENGTH = 160;

export function getSiteUrl(): URL {
  const rawUrl =
    process.env.SITE_URL?.trim() ||
    process.env.NEXT_PUBLIC_SITE_URL?.trim() ||
    process.env.VERCEL_URL?.trim() ||
    DEFAULT_SITE_URL;

  const urlWithProtocol = /^https?:\/\//i.test(rawUrl) ? rawUrl : `https://${rawUrl}`;

  try {
    const url = new URL(urlWithProtocol);
    url.pathname = url.pathname.replace(/\/+$/, "");
    return url;
  } catch {
    return new URL(DEFAULT_SITE_URL);
  }
}

export function absoluteUrl(path: string): string {
  return new URL(path, getSiteUrl()).toString();
}

export function buildCanonicalPath(path: string, params?: Record<string, string | undefined | null>): string {
  const searchParams = new URLSearchParams();

  if (params) {
    Object.keys(params)
      .sort()
      .forEach((key) => {
        const value = params[key]?.trim();
        if (value) {
          searchParams.set(key, value);
        }
      });
  }

  const normalizedPath = path.startsWith("/") ? path : `/${path}`;
  const query = searchParams.toString();
  return query ? `${normalizedPath}?${query}` : normalizedPath;
}

function normalizeText(value: string): string {
  return value.replace(/\s+/g, " ").trim();
}

function stripInlineMarkdown(value: string): string {
  return normalizeText(
    value
      .replace(/`([^`]+)`/g, "$1")
      .replace(/!\[([^\]]*)\]\([^)]+\)/g, "$1")
      .replace(/\[([^\]]+)\]\([^)]+\)/g, "$1")
      .replace(/[*_~>#]/g, "")
      .replace(/<[^>]+>/g, "")
  );
}

export function stripMarkdown(markdown: string): string {
  return normalizeText(
    markdown
      .replace(/```[\s\S]*?```/g, " ")
      .replace(/~~~[\s\S]*?~~~/g, " ")
      .replace(/^---[\s\S]*?---/m, " ")
      .split(/\r?\n/)
      .map((line) => stripInlineMarkdown(line.replace(/^#{1,6}\s+/, "")))
      .filter(Boolean)
      .join(" ")
  );
}

export function extractMarkdownTitle(markdown: string, fallback: string): string {
  let inCodeBlock = false;

  for (const line of markdown.split(/\r?\n/)) {
    const trimmed = line.trim();
    if (trimmed.startsWith("```") || trimmed.startsWith("~~~")) {
      inCodeBlock = !inCodeBlock;
      continue;
    }

    if (inCodeBlock) {
      continue;
    }

    const heading = /^(#{1,3})\s+(.+)$/.exec(trimmed);
    if (heading?.[2]) {
      return stripInlineMarkdown(heading[2].replace(/#+$/, ""));
    }
  }

  return fallback;
}

export function createMarkdownDescription(markdown: string, fallback: string): string {
  const text = stripMarkdown(markdown) || fallback;
  if (text.length <= MAX_DESCRIPTION_LENGTH) {
    return text;
  }

  return `${text.slice(0, MAX_DESCRIPTION_LENGTH - 1).trimEnd()}...`;
}

export function repoTitle(owner: string, repo: string): string {
  return `${owner}/${repo}`;
}

export function repoCanonicalPath(owner: string, repo: string): string {
  return buildRepoBasePath(owner, repo);
}

export function docCanonicalPath(
  owner: string,
  repo: string,
  slug: string,
  branch?: string,
  lang?: string
): string {
  return buildCanonicalPath(buildRepoDocPath(owner, repo, slug), { branch, lang });
}

export function noIndexMetadata(title: string, description: string, canonicalPath?: string): Metadata {
  return {
    title,
    description,
    alternates: canonicalPath ? { canonical: canonicalPath } : undefined,
    robots: {
      index: false,
      follow: false,
      googleBot: {
        index: false,
        follow: false,
      },
    },
  };
}

export function indexableMetadata({
  title,
  description,
  canonicalPath,
  type = "website",
}: {
  title: string;
  description: string;
  canonicalPath: string;
  type?: "website" | "article";
}): Metadata {
  return {
    title,
    description,
    alternates: {
      canonical: canonicalPath,
    },
    openGraph: {
      title,
      description,
      url: canonicalPath,
      siteName: SITE_NAME,
      type,
    },
    twitter: {
      card: "summary",
      title,
      description,
    },
    robots: {
      index: true,
      follow: true,
      googleBot: {
        index: true,
        follow: true,
        "max-image-preview": "large",
        "max-snippet": -1,
        "max-video-preview": -1,
      },
    },
  };
}

export function createTechArticleJsonLd({
  title,
  description,
  canonicalPath,
  owner,
  repo,
  language,
}: {
  title: string;
  description: string;
  canonicalPath: string;
  owner: string;
  repo: string;
  language: string;
}) {
  return {
    "@context": "https://schema.org",
    "@type": "TechArticle",
    headline: title,
    description,
    url: absoluteUrl(canonicalPath),
    inLanguage: language,
    isPartOf: {
      "@type": "TechArticle",
      name: repoTitle(owner, repo),
      url: absoluteUrl(repoCanonicalPath(owner, repo)),
    },
    publisher: {
      "@type": "Organization",
      name: SITE_NAME,
    },
  };
}

export function safeJsonLd(data: unknown): string {
  return JSON.stringify(data).replace(/</g, "\\u003c");
}
