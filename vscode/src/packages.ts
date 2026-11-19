// What the packages view and the package commands share. No vscode import, so the unit tests can load it.
import type { Package } from "./protocol";

// The same rules as the browser's packages panel (frontend/src/Packages.tsx).
const VALID_NAME = /^[A-Za-z0-9._-]+$/;
const VALID_VERSION = /^[A-Za-z0-9.+*\-[\](),]+$/;

/** Why a package name can't be used, or undefined when it can. */
export function packageNameError(name: string): string | undefined {
  const n = name.trim();
  if (!n) return "Enter a package name.";
  if (!VALID_NAME.test(n)) return "A package name can only have letters, digits, dots, dashes and underscores. Remove anything else.";
  return undefined;
}

/** Why a version can't be used for the package, or undefined when it can. */
export function packageVersionError(version: string, name: string): string | undefined {
  const v = version.trim();
  if (!v) return `Enter a version of ${name}, for example 2.14.1, or * for the latest.`;
  if (!VALID_VERSION.test(v)) return `${v} isn't a version of ${name} NuGet can read. Use a version like 2.14.1, a range like [1.0,2.0), or * for the latest.`;
  return undefined;
}

export interface PackageItem {
  label: string;
  description: string;
  contextValue: "reverb.package";
}

/** The rows of the packages view, in the order the notebook lists them. */
export function packageItems(packages: Package[]): PackageItem[] {
  return packages.map((p) => ({ label: p.name, description: p.version || "latest", contextValue: "reverb.package" }));
}
