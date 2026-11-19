// Reading and changing a cell's attributes (`disabled`, `hide_code`, `name`), the same keys as
// src/Reverb.Format/Model.cs. No vscode import, so it can be unit tested in plain Node.
import type { CellAttributeData } from "./protocol";

export type CellFlag = "disabled" | "hide_code";

export function hasFlag(attrs: readonly CellAttributeData[] | undefined, key: string): boolean {
  return attrs?.some((a) => a.key === key) ?? false;
}

/** Turns a flag on or off. Turning on a flag that is already on changes nothing; the other attributes keep their order. */
export function withFlag(attrs: readonly CellAttributeData[] | undefined, key: CellFlag, on: boolean): CellAttributeData[] {
  const list = [...(attrs ?? [])];
  if (!on) return list.filter((a) => a.key !== key);
  return hasFlag(list, key) ? list : [...list, { key }];
}

/** The cell's name, or undefined when it has none or an empty one. */
export function nameOf(attrs: readonly CellAttributeData[] | undefined): string | undefined {
  return attrs?.find((a) => a.key === "name")?.value || undefined;
}

/** Sets the name where it already is, or at the end. An empty or undefined name removes it. */
export function withName(attrs: readonly CellAttributeData[] | undefined, name: string | undefined): CellAttributeData[] {
  const list = [...(attrs ?? [])];
  if (!name) return list.filter((a) => a.key !== "name");
  const at = list.findIndex((a) => a.key === "name");
  if (at < 0) return [...list, { key: "name", value: name }];
  list[at] = { key: "name", value: name };
  return list;
}
