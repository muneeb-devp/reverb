// esbuild loads .css files as text for the renderer (see esbuild.mjs).
declare module "*.css" {
  const text: string;
  export default text;
}
