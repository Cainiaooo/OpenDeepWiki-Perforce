import type { Metadata } from "next";
import { getLocale } from "next-intl/server";
import RouteProviders from "@/app/route-providers";
import { getSiteUrl, SITE_DESCRIPTION, SITE_NAME } from "@/lib/repo-seo";
import "./globals.css";

export const dynamic = "force-dynamic";

export function generateMetadata(): Metadata {
  return {
    metadataBase: getSiteUrl(),
    applicationName: SITE_NAME,
    title: {
      default: SITE_NAME,
      template: `%s | ${SITE_NAME}`,
    },
    description: SITE_DESCRIPTION,
    icons: {
      icon: "/favicon.png",
    },
    openGraph: {
      title: SITE_NAME,
      description: SITE_DESCRIPTION,
      siteName: SITE_NAME,
      type: "website",
    },
  };
}

export default async function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  const locale = await getLocale();

  return (
    <html lang={locale} suppressHydrationWarning>
      <body className="antialiased">
        <RouteProviders>{children}</RouteProviders>
      </body>
    </html>
  );
}
