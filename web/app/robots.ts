import type { MetadataRoute } from "next";
import { absoluteUrl } from "@/lib/repo-seo";

export const dynamic = "force-dynamic";

export default function robots(): MetadataRoute.Robots {
  return {
    rules: [
      {
        userAgent: "*",
        allow: "/",
        disallow: [
          "/admin/",
          "/api/",
          "/auth",
          "/oauth/",
          "/private",
          "/settings",
          "/share/",
        ],
      },
    ],
    sitemap: absoluteUrl("/sitemap.xml"),
  };
}
